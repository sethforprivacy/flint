using System;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.Flint.Data;
using BTCPayServer.Plugins.Flint.Payments;

namespace BTCPayServer.Plugins.Flint.Services;

/// <summary>What the stablecoin path needs to know about one BTCPay invoice.</summary>
/// <param name="Payable">
/// Open for payment: new, not expired, not an amountless top-up. A quote is only ever made for a payable invoice;
/// a <em>credit</em> is recorded whatever the invoice's state, because money that arrived late still arrived.
/// </param>
/// <param name="Due">
/// What remains to be paid, net of any network cost, in the prompt's currency and rounded up to
/// <see cref="StablecoinPayments.Divisibility"/>. Zero once paid.
/// </param>
/// <param name="Details">The prompt's networks and active quote; null when the invoice has no such prompt.</param>
public sealed record StablecoinInvoice(
    string InvoiceId,
    string StoreId,
    bool Payable,
    decimal Due,
    StablecoinPromptDetails? Details);

/// <summary>What recording a stablecoin payment on its BTCPay invoice did.</summary>
public enum StablecoinCreditOutcome
{
    /// <summary>This call recorded it.</summary>
    CreditedNow,

    /// <summary>A payment with this id is already on the invoice — an earlier pass, or a race with one.</summary>
    AlreadyRecorded,

    /// <summary>The invoice no longer exists.</summary>
    InvoiceGone,

    /// <summary>The invoice has no prompt for the payment method, or BTCPay no longer has the handler.</summary>
    PromptMissing,

    /// <summary>
    /// The invoice belongs to a different store than the quote. Unreachable by construction and refused anyway: a
    /// payment into one store's wallet must never credit another store's invoice.
    /// </summary>
    WrongStore
}

/// <param name="Value">What the payer sent, in the prompt's currency, rounded down.</param>
/// <param name="Fee">The quote's network cost, capped at <paramref name="Value"/>.</param>
public sealed record StablecoinCreditRequest(
    StablecoinQuote Quote,
    decimal Value,
    decimal Fee,
    StablecoinPaymentDetails Details,
    DateTimeOffset ReceivedAt);

/// <summary>
/// The BTCPay side of accepting USDC and USDT: reading an invoice, showing a quote on its prompt, and recording
/// the payment.
/// </summary>
/// <remarks>
/// Split from <see cref="StablecoinPaymentService"/> for the same reason <see cref="IInvoiceCreditGateway"/> is
/// split from the Lightning creditor: every decision lives in the service and is tested against a fake of this,
/// and this is only the translation into BTCPay's invoice repository and payment service.
/// </remarks>
public interface IStablecoinInvoiceGateway
{
    Task<StablecoinInvoice?> GetInvoiceAsync(
        string invoiceId,
        PaymentMethodId paymentMethodId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Points the invoice's prompt at a quote: its deposit address as the destination, its network cost as the
    /// prompt's fee, and the quote itself in the prompt details — then tells listening checkouts.
    /// </summary>
    Task ShowQuoteAsync(
        string invoiceId,
        PaymentMethodId paymentMethodId,
        StablecoinActiveQuote quote,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Records a settled payment on the quote's invoice, keyed on the Spark payment id so it can land only once.
    /// </summary>
    Task<StablecoinCreditOutcome> AddPaymentAsync(
        StablecoinCreditRequest request,
        CancellationToken cancellationToken = default);
}
