using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Client.Models;
using BTCPayServer.Data;
using BTCPayServer.Events;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.Flint.Payments;
using BTCPayServer.Services.Invoices;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.Flint.Services;

/// <summary>
/// The real <see cref="IStablecoinInvoiceGateway"/>, over BTCPay's invoice repository and payment service.
/// </summary>
/// <remarks>
/// <para>
/// <b>Exactly-once by primary key, as on the Lightning path.</b> The payment id is the inbound Spark payment's id,
/// so a second attempt to record the same arrival collides on BTCPay's <c>Payments</c> key
/// <c>(Id, PaymentMethodId)</c>, core's <c>AddPayment</c> answers null, and this reports it as already recorded.
/// </para>
/// <para>
/// <b>The payment's blob is written here rather than through core's <c>PaymentData.Set</c></b>, which copies the
/// destination and the fee from the invoice's <em>current</em> prompt. A payer can open a quote on one network,
/// switch to another, and then pay the first; the current prompt would then describe the wrong address and the
/// wrong fee, and the invoice would read as underpaid by the difference between the two networks' costs. The
/// payment records the quote it actually paid.
/// </para>
/// <para>
/// <see cref="PaymentService"/> and the handler dictionary arrive as factories for the reason given on
/// <see cref="BTCPayInvoiceCreditGateway"/>.
/// </para>
/// </remarks>
public sealed class BTCPayStablecoinInvoiceGateway : IStablecoinInvoiceGateway
{
    private readonly InvoiceRepository _invoiceRepository;
    private readonly Func<PaymentService> _paymentService;
    private readonly Func<PaymentMethodHandlerDictionary> _handlers;
    private readonly EventAggregator _eventAggregator;
    private readonly TimeProvider _time;
    private readonly ILogger<BTCPayStablecoinInvoiceGateway> _logger;

    public BTCPayStablecoinInvoiceGateway(
        InvoiceRepository invoiceRepository,
        Func<PaymentService> paymentService,
        Func<PaymentMethodHandlerDictionary> handlers,
        EventAggregator eventAggregator,
        TimeProvider time,
        ILogger<BTCPayStablecoinInvoiceGateway> logger)
    {
        _invoiceRepository = invoiceRepository;
        _paymentService = paymentService;
        _handlers = handlers;
        _eventAggregator = eventAggregator;
        _time = time;
        _logger = logger;
    }

    public async Task<StablecoinInvoice?> GetInvoiceAsync(
        string invoiceId,
        PaymentMethodId paymentMethodId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(invoiceId);
        ArgumentNullException.ThrowIfNull(paymentMethodId);

        var invoice = await _invoiceRepository.GetInvoice(invoiceId).ConfigureAwait(false);
        if (invoice is null)
            return null;

        var prompt = invoice.GetPaymentPrompt(paymentMethodId);
        if (prompt is null || !_handlers().TryGetValue(paymentMethodId, out var handler))
            return new StablecoinInvoice(invoice.Id, invoice.StoreId, false, 0m, null);

        var details = ParseDetails(handler, prompt);
        // Archived invoices are hidden from their payers by core's own checkout, so they are not quoted either.
        var payable = invoice.Status == InvoiceStatus.New
                      && !invoice.Archived
                      && invoice.ExpirationTime > _time.GetUtcNow()
                      && !invoice.IsUnsetTopUp();

        decimal due;
        try
        {
            // The net due, not the prompt's own Due: that one already includes whichever network's cost the
            // prompt is showing, and a quote for another network must not pay for it twice.
            // Rounded up, as core rounds a prompt's own due, so the quote never asks for a hair less than is owed.
            due = invoice.NetDue <= 0m
                ? 0m
                : BTCPayServer.Extensions.RoundUp(invoice.NetDue / prompt.Rate, StablecoinPayments.Divisibility);
        }
        catch (Exception ex)
        {
            // No rate for the prompt's currency is a prompt that cannot be quoted, not an error page.
            _logger.LogDebug(ex, "Invoice {InvoiceId}: no {PaymentMethodId} rate to compute a due", invoiceId, paymentMethodId);
            return new StablecoinInvoice(invoice.Id, invoice.StoreId, false, 0m, details);
        }

        return new StablecoinInvoice(invoice.Id, invoice.StoreId, payable, due, details);
    }

    public async Task ShowQuoteAsync(
        string invoiceId,
        PaymentMethodId paymentMethodId,
        StablecoinActiveQuote quote,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(invoiceId);
        ArgumentNullException.ThrowIfNull(paymentMethodId);
        ArgumentNullException.ThrowIfNull(quote);

        var invoice = await _invoiceRepository.GetInvoice(invoiceId).ConfigureAwait(false);
        if (invoice?.GetPaymentPrompt(paymentMethodId) is not { } prompt
            || !_handlers().TryGetValue(paymentMethodId, out var handler))
        {
            return;
        }

        var details = ParseDetails(handler, prompt) ?? new StablecoinPromptDetails();
        details.Quote = quote;

        prompt.Destination = quote.DepositAddress;
        // The network cost of the route on show: what BTCPay adds to the due and prints as "network cost". A payer
        // who switches network sees the new one's; a payment on the old one records its own (see AddPaymentAsync).
        prompt.PaymentMethodFee = quote.Fee;
        prompt.Details = JToken.FromObject(details, handler.Serializer);

        // Tracked as the prompt's destination, so the invoice is findable by the address a payer quotes back to
        // the merchant, and searchable by it in the invoice list.
        await _invoiceRepository
            .UpdatePrompt(invoiceId, prompt, [quote.DepositAddress])
            .ConfigureAwait(false);
        await _invoiceRepository.AddSearchTerms(invoiceId, [quote.DepositAddress]).ConfigureAwait(false);

        // What wakes a checkout listening on the invoice's websocket, so it refetches and shows the new due.
        _eventAggregator.Publish(new InvoiceNewPaymentDetailsEvent(invoiceId, details, paymentMethodId));
    }

    public async Task<StablecoinCreditOutcome> AddPaymentAsync(
        StablecoinCreditRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var quote = request.Quote;
        var paymentMethodId = PaymentMethodId.Parse(quote.PaymentMethodId);
        var paymentId = request.Details.SdkPaymentId;

        var invoice = await _invoiceRepository.GetInvoice(quote.InvoiceId).ConfigureAwait(false);
        if (invoice is null)
            return StablecoinCreditOutcome.InvoiceGone;

        if (!string.Equals(invoice.StoreId, quote.StoreId, StringComparison.Ordinal))
        {
            _logger.LogError(
                "Refusing to record Spark payment {SdkPaymentId} from store {QuoteStoreId}'s wallet on invoice "
                + "{InvoiceId}, which belongs to store {InvoiceStoreId}",
                paymentId, quote.StoreId, invoice.Id, invoice.StoreId);
            return StablecoinCreditOutcome.WrongStore;
        }

        // Both checks disambiguate AddPayment's null, which it also returns for a missing prompt or handler — a
        // payment never recorded must not be reported as already recorded.
        if (!_handlers().TryGetValue(paymentMethodId, out var handler)
            || invoice.GetPaymentPrompt(paymentMethodId) is not { } prompt)
        {
            return StablecoinCreditOutcome.PromptMissing;
        }

        if (invoice.GetPayments(false).Any(p => p.Id == paymentId && p.PaymentMethodId == paymentMethodId))
            return StablecoinCreditOutcome.AlreadyRecorded;

        var blob = new PaymentBlob
        {
            Destination = quote.DepositAddress,
            PaymentMethodFee = request.Fee,
            Divisibility = prompt.Divisibility
        }.SetDetails(handler, request.Details);

        var paymentData = new PaymentData
        {
            Id = paymentId,
            Created = request.ReceivedAt,
            Status = PaymentStatus.Settled,
            Currency = prompt.Currency,
            InvoiceDataId = invoice.Id,
            Amount = request.Value
        };
        paymentData.SetBlob(paymentMethodId, blob);

        var searchTerms = new HashSet<string>(StringComparer.Ordinal) { quote.DepositAddress };
        if (!string.IsNullOrEmpty(request.Details.ExternalTxHash))
            searchTerms.Add(request.Details.ExternalTxHash);

        var payment = await _paymentService().AddPayment(paymentData, searchTerms).ConfigureAwait(false);
        if (payment is null)
            return StablecoinCreditOutcome.AlreadyRecorded;

        // Re-read so subscribers compute the invoice's new state from one that includes this payment, exactly as
        // core's own listeners publish it.
        var credited = await _invoiceRepository.GetInvoice(invoice.Id).ConfigureAwait(false);
        if (credited is not null)
            _eventAggregator.Publish(new InvoiceEvent(credited, InvoiceEvent.ReceivedPayment) { Payment = payment });

        return StablecoinCreditOutcome.CreditedNow;
    }

    private static StablecoinPromptDetails? ParseDetails(IPaymentMethodHandler handler, PaymentPrompt prompt)
    {
        if (prompt.Details is null || prompt.Details.Type == JTokenType.Null)
            return null;
        try
        {
            return handler.ParsePaymentPromptDetails(prompt.Details) as StablecoinPromptDetails;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
