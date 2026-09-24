using System.Collections.Concurrent;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.Flint.Data;
using BTCPayServer.Plugins.Flint.Payments;
using BTCPayServer.Plugins.Flint.Sdk;
using BTCPayServer.Plugins.Flint.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace BTCPayServer.Plugins.Flint.Tests.Fakes;

/// <summary>
/// <see cref="IStablecoinQuoteStore"/> in memory, with the same two guards the Postgres store enforces: the
/// settle compare-and-set, and one payment settling one quote.
/// </summary>
public sealed class InMemoryStablecoinQuoteStore : IStablecoinQuoteStore
{
    private readonly object _gate = new();
    private readonly WriteLog? _writeLog;

    public InMemoryStablecoinQuoteStore(WriteLog? writeLog = null)
    {
        _writeLog = writeLog;
    }

    public List<StablecoinQuote> Quotes { get; } = [];

    public Task AddAsync(StablecoinQuote quote, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (Quotes.Any(q => q.Id == quote.Id))
                throw new InvalidOperationException($"Duplicate quote id {quote.Id}");
            Quotes.Add(Copy(quote));
        }

        _writeLog?.Record("db:stablecoin-quote");
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<StablecoinQuote>> ListForInvoiceAsync(
        string invoiceId,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
            return Task.FromResult<IReadOnlyList<StablecoinQuote>>(Quotes
                .Where(q => q.InvoiceId == invoiceId)
                .OrderByDescending(q => q.CreatedAt)
                .Select(Copy)
                .ToList());
    }

    public Task<IReadOnlyList<StablecoinQuote>> ListOpenAsync(
        string storeId,
        DateTimeOffset expiredAfter,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
            return Task.FromResult<IReadOnlyList<StablecoinQuote>>(Quotes
                .Where(q => q.StoreId == storeId && q.SdkPaymentId is null && q.ExpiresAt > expiredAfter)
                .OrderBy(q => q.CreatedAt)
                .Select(Copy)
                .ToList());
    }

    public Task<IReadOnlyList<string>> ListStoresWithOpenQuotesAsync(
        DateTimeOffset expiredAfter,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
            return Task.FromResult<IReadOnlyList<string>>(Quotes
                .Where(q => q.SdkPaymentId is null && q.ExpiresAt > expiredAfter)
                .Select(q => q.StoreId)
                .Distinct()
                .ToList());
    }

    public Task<StablecoinQuote?> FindBySdkPaymentIdAsync(
        string storeId,
        string sdkPaymentId,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
            return Task.FromResult(Quotes
                .Where(q => q.StoreId == storeId && q.SdkPaymentId == sdkPaymentId)
                .Select(Copy)
                .FirstOrDefault());
    }

    public Task<bool> TrySettleAsync(
        string quoteId,
        StablecoinSettlement settlement,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var quote = Quotes.FirstOrDefault(q => q.Id == quoteId);
            if (quote is null || quote.SdkPaymentId is not null
                || Quotes.Any(q => q.SdkPaymentId == settlement.SdkPaymentId))
            {
                return Task.FromResult(false);
            }

            quote.SdkPaymentId = settlement.SdkPaymentId;
            quote.PaidBaseUnits = settlement.PaidBaseUnits?.ToString();
            quote.DeliveredBaseUnits = settlement.DeliveredBaseUnits?.ToString();
            quote.ExternalTxHash = settlement.ExternalTxHash;
            quote.ProviderOrderId = settlement.ProviderOrderId;
            quote.ProviderQuoteId = settlement.ProviderQuoteId;
            quote.SettledAt = settlement.SettledAt;
        }

        _writeLog?.Record("db:stablecoin-settle");
        return Task.FromResult(true);
    }

    public Task<bool> TryMarkCreditedAsync(
        string quoteId,
        DateTimeOffset creditedAt,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var quote = Quotes.FirstOrDefault(q => q.Id == quoteId);
            if (quote is null || quote.SdkPaymentId is null || quote.CreditedAt is not null)
                return Task.FromResult(false);
            quote.CreditedAt = creditedAt;
            return Task.FromResult(true);
        }
    }

    public Task<IReadOnlyList<StablecoinQuote>> ListUncreditedAsync(
        string storeId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
            return Task.FromResult<IReadOnlyList<StablecoinQuote>>(Quotes
                .Where(q => q.StoreId == storeId && q.SdkPaymentId is not null && q.CreditedAt is null)
                .OrderBy(q => q.SettledAt)
                .Take(limit)
                .Select(Copy)
                .ToList());
    }

    public Task<IReadOnlyList<string>> ListStoresAwaitingCreditAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
            return Task.FromResult<IReadOnlyList<string>>(Quotes
                .Where(q => q.SdkPaymentId is not null && q.CreditedAt is null)
                .Select(q => q.StoreId)
                .Distinct()
                .ToList());
    }

    public Task<int> DeleteFinishedAsync(DateTimeOffset before, CancellationToken cancellationToken = default)
    {
        lock (_gate)
            return Task.FromResult(Quotes.RemoveAll(q =>
                (q.CreditedAt is { } credited && credited < before)
                || (q.SdkPaymentId is null && q.ExpiresAt < before)));
    }

    /// <summary>
    /// Handed out as copies, as rows read from a database are: a caller mutating what it read must not be editing
    /// the store behind the store's back.
    /// </summary>
    private static StablecoinQuote Copy(StablecoinQuote quote) => new()
    {
        Id = quote.Id,
        StoreId = quote.StoreId,
        InvoiceId = quote.InvoiceId,
        PaymentMethodId = quote.PaymentMethodId,
        Chain = quote.Chain,
        ChainId = quote.ChainId,
        Asset = quote.Asset,
        ContractAddress = quote.ContractAddress,
        Decimals = quote.Decimals,
        DepositAddress = quote.DepositAddress,
        DepositBaseUnits = quote.DepositBaseUnits,
        AskedBaseUnits = quote.AskedBaseUnits,
        PaymentRequest = quote.PaymentRequest,
        DueAmount = quote.DueAmount,
        FeeAmount = quote.FeeAmount,
        ExpectedReceivedBaseUnits = quote.ExpectedReceivedBaseUnits,
        DestinationAsset = quote.DestinationAsset,
        ServiceFeeBaseUnits = quote.ServiceFeeBaseUnits,
        ServiceFeeAsset = quote.ServiceFeeAsset,
        CreatedAt = quote.CreatedAt,
        ExpiresAt = quote.ExpiresAt,
        SdkPaymentId = quote.SdkPaymentId,
        PaidBaseUnits = quote.PaidBaseUnits,
        DeliveredBaseUnits = quote.DeliveredBaseUnits,
        ExternalTxHash = quote.ExternalTxHash,
        ProviderOrderId = quote.ProviderOrderId,
        ProviderQuoteId = quote.ProviderQuoteId,
        SettledAt = quote.SettledAt,
        CreditedAt = quote.CreditedAt
    };
}

/// <summary>
/// <see cref="IStablecoinInvoiceGateway"/> over invoices a test declares, recording what the service showed and
/// credited.
/// </summary>
public sealed class FakeStablecoinInvoiceGateway : IStablecoinInvoiceGateway
{
    public sealed class Invoice
    {
        public required string Id { get; init; }
        public required string StoreId { get; init; }
        public bool Payable { get; set; } = true;
        public decimal Due { get; set; }
        public Dictionary<PaymentMethodId, StablecoinPromptDetails> Prompts { get; } = [];
        public Dictionary<PaymentMethodId, (string? Destination, decimal Fee)> Shown { get; } = [];
        public List<StablecoinCreditRequest> Payments { get; } = [];
    }

    public ConcurrentDictionary<string, Invoice> Invoices { get; } = new();

    public Exception? FailPaymentsWith { get; set; }

    /// <summary>Thrown by <see cref="GetInvoiceAsync"/>: an invoice store that is down.</summary>
    public Exception? FailLookupsWith { get; set; }

    public Invoice Add(
        string invoiceId,
        string storeId,
        decimal due,
        params (StablecoinAsset Asset, string[] Chains)[] prompts)
    {
        var invoice = new Invoice { Id = invoiceId, StoreId = storeId, Due = due };
        foreach (var (asset, chains) in prompts)
        {
            invoice.Prompts[asset.PaymentMethodId] = new StablecoinPromptDetails
            {
                Networks = chains
                    .Select(chain => new StablecoinNetworkOption { Chain = chain, Name = StablecoinPayments.ChainName(chain) })
                    .ToList()
            };
        }

        Invoices[invoiceId] = invoice;
        return invoice;
    }

    /// <summary>Gives a declared network the contract its route carries, as invoice creation would have.</summary>
    public void SetContracts(string invoiceId, StablecoinAsset asset, IEnumerable<SparkCrossChainReceiveRoute> routes)
    {
        foreach (var network in Invoices[invoiceId].Prompts[asset.PaymentMethodId].Networks)
        {
            var route = routes.First(r => r.Chain == network.Chain && r.Asset == asset.Symbol
                                          && r.Provider is SparkCrossChainProvider.Orchestra);
            network.ContractAddress = route.ContractAddress;
            network.ChainId = route.ChainId;
        }
    }

    public Task<StablecoinInvoice?> GetInvoiceAsync(
        string invoiceId,
        PaymentMethodId paymentMethodId,
        CancellationToken cancellationToken = default)
    {
        if (FailLookupsWith is not null)
            throw FailLookupsWith;
        if (!Invoices.TryGetValue(invoiceId, out var invoice))
            return Task.FromResult<StablecoinInvoice?>(null);

        invoice.Prompts.TryGetValue(paymentMethodId, out var details);
        return Task.FromResult<StablecoinInvoice?>(new StablecoinInvoice(
            invoice.Id, invoice.StoreId, invoice.Payable && details is not null, invoice.Due, details));
    }

    public Task ShowQuoteAsync(
        string invoiceId,
        PaymentMethodId paymentMethodId,
        StablecoinActiveQuote quote,
        CancellationToken cancellationToken = default)
    {
        var invoice = Invoices[invoiceId];
        invoice.Prompts[paymentMethodId].Quote = quote;
        invoice.Shown[paymentMethodId] = (quote.DepositAddress, quote.Fee);
        return Task.CompletedTask;
    }

    public Task<StablecoinCreditOutcome> AddPaymentAsync(
        StablecoinCreditRequest request,
        CancellationToken cancellationToken = default)
    {
        if (FailPaymentsWith is not null)
            throw FailPaymentsWith;
        if (!Invoices.TryGetValue(request.Quote.InvoiceId, out var invoice))
            return Task.FromResult(StablecoinCreditOutcome.InvoiceGone);
        if (invoice.StoreId != request.Quote.StoreId)
            return Task.FromResult(StablecoinCreditOutcome.WrongStore);

        lock (invoice)
        {
            // BTCPay's payments key is (id, payment method): the same arrival cannot be recorded twice.
            if (invoice.Payments.Any(p => p.Details.SdkPaymentId == request.Details.SdkPaymentId
                                          && p.Quote.PaymentMethodId == request.Quote.PaymentMethodId))
            {
                return Task.FromResult(StablecoinCreditOutcome.AlreadyRecorded);
            }

            invoice.Payments.Add(request);
        }

        return Task.FromResult(StablecoinCreditOutcome.CreditedNow);
    }
}

/// <summary><see cref="IStablecoinStoreConfig"/> as a set of stores with the switch on.</summary>
public sealed class FakeStablecoinStoreConfig : IStablecoinStoreConfig
{
    public HashSet<string> KnownStores { get; } = [];
    public HashSet<string> Enabled { get; } = [];

    public Task<bool?> IsEnabledAsync(string storeId, CancellationToken cancellationToken = default) =>
        Task.FromResult<bool?>(KnownStores.Count > 0 && !KnownStores.Contains(storeId) ? null : Enabled.Contains(storeId));

    public Task<bool> SetEnabledAsync(string storeId, bool enabled, CancellationToken cancellationToken = default)
    {
        if (KnownStores.Count > 0 && !KnownStores.Contains(storeId))
            return Task.FromResult(false);
        if (enabled)
            Enabled.Add(storeId);
        else
            Enabled.Remove(storeId);
        return Task.FromResult(true);
    }
}

/// <summary>A <see cref="StablecoinPaymentService"/> over the fakes above.</summary>
public sealed class StablecoinHarness
{
    public StablecoinHarness(
        ISparkStoreRuntime runtime,
        bool available = true,
        TimeProvider? time = null,
        WriteLog? writeLog = null)
    {
        Time = time ?? TimeProvider.System;
        Quotes = new InMemoryStablecoinQuoteStore(writeLog);
        Invoices = new FakeStablecoinInvoiceGateway();
        StoreConfig = new FakeStablecoinStoreConfig();
        Log = new CapturingLogger<StablecoinPaymentService>();
        Routes = new StablecoinRouteCache(Time, NullLogger<StablecoinRouteCache>.Instance);
        Service = new StablecoinPaymentService(
            Quotes, Invoices, StoreConfig, runtime, Routes, Time, available, Log);
    }

    public TimeProvider Time { get; }
    public InMemoryStablecoinQuoteStore Quotes { get; }
    public FakeStablecoinInvoiceGateway Invoices { get; }
    public FakeStablecoinStoreConfig StoreConfig { get; }
    public StablecoinRouteCache Routes { get; }
    public CapturingLogger<StablecoinPaymentService> Log { get; }
    public StablecoinPaymentService Service { get; }
}
