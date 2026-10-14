using System.Security.Claims;
using BTCPayServer.Plugins.Flint.Sdk;
using BTCPayServer.Plugins.Flint.Services;
using BTCPayServer.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.ViewFeatures;

namespace BTCPayServer.Plugins.Flint.Tests.Fakes;

/// <summary>
/// The small collaborators <c>SparkController</c> needs that have nothing to do with what is under test.
/// </summary>
/// <remarks>
/// The controller was deliberately given interface dependencies rather than the concrete
/// <c>SparkService</c> so it can be constructed here at all — <c>SparkService</c> needs a store repository, a
/// database and the SDK's native library. That is what makes its store-scoping guard testable.
/// </remarks>
public sealed class FakeSparkStoreRuntime : ISparkStoreRuntime
{
    public Dictionary<string, ISparkSdkClient> Clients { get; } = [];

    public string GetStorageDirectory(string storeId) => $"/tmp/spark/{storeId}";

    public Task<ISparkSdkClient?> GetSdkClientAsync(string storeId) =>
        Task.FromResult(Clients.TryGetValue(storeId, out var client) ? client : null);

    /// <summary>Each running store's wallet tag; a store without one here runs <c>wallet-of-{storeId}</c>.</summary>
    public Dictionary<string, string> WalletTags { get; } = [];

    public string? GetWalletTag(string storeId, ISparkSdkClient? sdk = null) =>
        Clients.TryGetValue(storeId, out var client) && (sdk is null || ReferenceEquals(client, sdk))
            ? WalletTags.GetValueOrDefault(storeId, $"wallet-of-{storeId}")
            : null;

    /// <summary>What an import of the queue reports. Nothing queued, unless a test says otherwise.</summary>
    public ExitStateImportReport NextImportReport { get; set; } = ExitStateImportReport.NothingPending;

    /// <summary>Every store an import of the queue was asked for, in order.</summary>
    public List<string> ImportRequests { get; } = [];

    public Task<ExitStateImportReport> ImportPendingExitStateAsync(
        string storeId, CancellationToken cancellationToken = default)
    {
        ImportRequests.Add(storeId);
        return Task.FromResult(NextImportReport);
    }

    public async Task<ExitStateExportResult> ExportExitStateAsync(
        string storeId, CancellationToken cancellationToken = default)
    {
        if (!Clients.TryGetValue(storeId, out var client))
            return new ExitStateExportResult(null, false, "This store's Spark wallet is not running.");

        try
        {
            // Never stored: the production rules for when an export is stored live in SparkService and are
            // tested there.
            var exported = await client.ExportUnilateralExitStateAsync(cancellationToken);
            return new ExitStateExportResult(exported, false);
        }
        catch (Exception ex)
        {
            return new ExitStateExportResult(null, false,
                "Spark could not export this wallet's exit data: " + SparkErrors.Describe(ex));
        }
    }
}

/// <summary>Returns a fixed hot-wallet seed answer.</summary>
public sealed class FakeHotWalletSeedReader : IHotWalletSeedReader
{
    private readonly HotWalletSeedResult _result;

    public FakeHotWalletSeedReader(HotWalletSeedResult? result = null)
    {
        _result = result ?? HotWalletSeedResult.NotAvailable(
            HotWalletSeedStatus.NotAHotWallet, "This store has no Bitcoin hot wallet.");
    }

    /// <summary>Store ids this was asked about, so a test can prove it was never asked about a victim.</summary>
    public List<string> Reads { get; } = [];

    public Task<HotWalletSeedResult> ReadAsync(
        ClaimsPrincipal user,
        string storeId,
        CancellationToken cancellationToken = default)
    {
        Reads.Add(storeId);
        return Task.FromResult(_result);
    }
}

/// <summary>Reports a fixed network status.</summary>
public sealed class FakeSparkNetworkStatusProbe : ISparkNetworkStatusProbe
{
    public SparkNetworkStatus? Status { get; set; }

    public Task<SparkNetworkStatus?> TryGetAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(Status);
}

/// <summary>
/// Grants every policy.
/// </summary>
/// <remarks>
/// Granting on purpose. The store-scoping tests need authorisation to <em>succeed</em>, because the hole being
/// tested is precisely the one where authorisation succeeds against the caller's own store and the action then
/// operates on a different one. A fake that refused would make those tests pass for the wrong reason.
/// </remarks>
public sealed class FakeAuthorizationService : IAuthorizationService
{
    public Task<AuthorizationResult> AuthorizeAsync(
        ClaimsPrincipal user,
        object? resource,
        IEnumerable<IAuthorizationRequirement> requirements) =>
        Task.FromResult(AuthorizationResult.Success());

    public Task<AuthorizationResult> AuthorizeAsync(ClaimsPrincipal user, object? resource, string policyName) =>
        Task.FromResult(AuthorizationResult.Success());
}

/// <summary>
/// A fixed <see cref="ISettingsAccessor{T}"/>, standing in for core's database-backed one.
/// </summary>
/// <remarks>
/// Core's real accessor is populated by a startup task and throws until it has been, which is why the plugin takes
/// the accessor rather than the settings object. This one is always populated.
/// </remarks>
public sealed class FakeSettingsAccessor<T> : ISettingsAccessor<T>
{
    public FakeSettingsAccessor(T settings)
    {
        Settings = settings;
    }

    public T Settings { get; set; }
}

/// <summary>Discards TempData so redirect-with-message actions can run outside a request pipeline.</summary>
public sealed class NullTempDataProvider : ITempDataProvider
{
    public IDictionary<string, object> LoadTempData(Microsoft.AspNetCore.Http.HttpContext context) =>
        new Dictionary<string, object>();

    public void SaveTempData(
        Microsoft.AspNetCore.Http.HttpContext context,
        IDictionary<string, object> values)
    {
    }
}

/// <summary>
/// A price that is present and sane, unless a test says otherwise.
/// </summary>
/// <remarks>
/// <para>
/// Defaults to the rate the spike's live quotes implied — BTC ≈ $64,620 — so a fake quote priced from
/// <see cref="FakeSparkSdkClient.CrossChainRate"/> lands squarely inside the value guard's band and no test
/// trips over it by accident.
/// </para>
/// <para>
/// <see cref="Unavailable"/> is the case worth modelling: BTCPay's rate providers are third-party HTTP, so "no
/// rate right now" is ordinary rather than exotic, and it decides whether the guard is one a hiccup can bypass.
/// </para>
/// </remarks>
public sealed class FakeCrossChainValueOracle : ICrossChainValueOracle
{
    /// <summary>US dollars per bitcoin. The rate the spike's own quotes implied.</summary>
    public decimal BtcUsd { get; set; } = 64_620m;

    /// <summary>When true, no rate can be obtained — which must refuse the sweep, not wave it through.</summary>
    public bool Unavailable { get; set; }

    public List<(string StoreId, string Currency)> Calls { get; } = [];

    public Task<decimal?> TryGetSatoshiValueAsync(
        string storeId,
        string currencyCode,
        CancellationToken cancellationToken = default)
    {
        Calls.Add((storeId, currencyCode));
        return Task.FromResult<decimal?>(Unavailable ? null : BtcUsd / 100_000_000m);
    }

    /// <summary>The same <see cref="BtcUsd"/>: the fake has no spread, so bid and mid are one price.</summary>
    public Task<decimal?> TryGetSatoshiMidValueAsync(
        string storeId,
        string currencyCode,
        CancellationToken cancellationToken = default) =>
        TryGetSatoshiValueAsync(storeId, currencyCode, cancellationToken);
}
