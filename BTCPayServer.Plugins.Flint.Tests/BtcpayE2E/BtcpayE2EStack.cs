using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using BTCPayServer.Plugins.Flint.Tests.LocalRegtest;
using Xunit;

namespace BTCPayServer.Plugins.Flint.Tests.BtcpayE2E;

/// <summary>
/// A live BTCPay Server running the Flint plugin against the local Spark stack, shared by the whole
/// <c>BtcpayE2E</c> suite and driven only over Greenfield.
/// </summary>
/// <remarks>
/// <para>
/// <b>What this adds over the LocalRegtest suite.</b> That suite builds the plugin's collaborators by hand
/// and connects an SDK instance of its own, which is enough to prove the money paths and the reconciler but
/// leaves out everything that only exists inside a host: BTCPay's invoice lifecycle and the transition to
/// <c>Settled</c>, the Lightning payment-method wiring the provisioner writes, the Greenfield surface's own
/// authorisation and store scoping, the reconciliation and sweep tasks running as hosted services on
/// BTCPay's schedule, and the plugin loading at all — as a directory under the data dir, out of the official
/// image, with Breez's native library resolved by BTCPay's own plugin load context. A green LocalRegtest run
/// says nothing about any of those.
/// </para>
/// <para>
/// <b>Nothing here reaches inside.</b> Every assertion is made through an HTTP call an operator could make,
/// or against the fixture's own bitcoind and LND. There is no in-process handle on the plugin: the point is
/// to observe it the way a host does.
/// </para>
/// <para>
/// <b>Gating.</b> Opt-in on <c>FLINT_BTCPAY_E2E</c> holding the path to the <c>btcpay.json</c> that
/// <c>e2e/btcpay/up.sh</c> writes. Absent, every test in the collection skips and this fixture starts
/// nothing: the stack takes minutes to bring up and needs the Spark fixture under it, so it is a deliberate
/// act and never a side effect of running the unit suite.
/// </para>
/// <para>
/// <b>Serialised.</b> One store, one wallet, three tests that each move its money, and one chain whose
/// height they all advance — hence a collection fixture with parallelisation disabled.
/// </para>
/// </remarks>
public sealed class BtcpayE2EStack : IAsyncLifetime
{
    public const string CollectionName = "Flint on a live BTCPay Server";

    /// <summary>Environment variable holding the path to <c>e2e/btcpay/btcpay.json</c>.</summary>
    public const string EnvironmentVariable = "FLINT_BTCPAY_E2E";

    /// <summary>
    /// The balance every test leaves behind for the others, in satoshis.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The tests do not run in a guaranteed order, so the sweep sizes itself to leave this much rather than
    /// draining — the same reasoning as the LocalRegtest suite's exit margin, and the reason
    /// <c>up.sh</c> configures the store with <c>enabled: false</c>: an automatic pass firing between two
    /// tests would move money nobody asked it to.
    /// </para>
    /// <para>
    /// <b>Small on purpose, and this number has a running cost.</b> It covers a 2,000-sat receive and a
    /// 3,000-sat send with room for their Lightning fees, and nothing more, because whatever is left when
    /// the containers go is <em>stranded</em>: a cooperative exit on this fixture costs around 20,000 sats,
    /// so a remainder under roughly 60,000 is refused by the fee guard — correctly — and stays in a wallet
    /// whose storage <c>down.sh</c> destroys. That leftover comes out of the SSP's own leaves. Measured: a
    /// 60,000-sat reserve took the fixture's SSP from 500,000 to 382,000 across two runs, which is about a
    /// dozen runs before its liquidity needs topping up. At 15,000 it is nearer forty.
    /// </para>
    /// </remarks>
    public const long ReserveForOtherTestsSats = 15_000;

    public static string SkipReason =>
        $"Set {EnvironmentVariable} to the path of the btcpay.json that e2e/btcpay/up.sh writes to run the "
        + "BTCPay end-to-end suite. See docs/testing.md: bring the Spark stack up with "
        + "e2e/local-regtest/up.sh, then e2e/btcpay/up.sh.";

    /// <summary>True when the variable names a file. The gate for the whole suite.</summary>
    public static bool IsEnabled =>
        Environment.GetEnvironmentVariable(EnvironmentVariable) is { } path
        && !string.IsNullOrWhiteSpace(path);

    private readonly CancellationTokenSource _setup = new(TimeSpan.FromMinutes(5));
    private HttpClient? _http;

    /// <summary>Greenfield, with the store's API key already attached. Only valid when enabled.</summary>
    public BtcpayGreenfield Api { get; private set; } = null!;

    /// <summary>bitcoind and LND, over <c>docker exec</c> — the same helper the LocalRegtest suite uses.</summary>
    public RegtestControl Control { get; private set; } = null!;

    /// <summary>The store <c>up.sh</c> provisioned.</summary>
    public string StoreId { get; private set; } = null!;

    /// <summary>The balance the store held when the suite started, for the run log.</summary>
    public long StartingBalanceSats { get; private set; }

    public async ValueTask InitializeAsync()
    {
        if (Environment.GetEnvironmentVariable(EnvironmentVariable) is not { } path
            || string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path, _setup.Token));
        var root = document.RootElement;

        string Required(string name) =>
            root.TryGetProperty(name, out var value) && value.GetString() is { Length: > 0 } text
                ? text
                : throw new InvalidOperationException(
                    $"{path} is missing `{name}`. It was probably written by an older e2e/btcpay/up.sh; "
                    + "re-run e2e/btcpay/down.sh then e2e/btcpay/up.sh.");

        var baseUrl = Required("baseUrl");
        var apiKey = Required("apiKey");
        StoreId = Required("storeId");

        _http = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromMinutes(3) };
        // `token <key>`, BTCPay's own scheme for an API key. Not Bearer, and not Basic: the key is not a
        // password and BTCPay rejects it as one.
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("token", apiKey);
        Api = new BtcpayGreenfield(_http, StoreId);

        Control = new RegtestControl(ReadFixture(path, root));
        await Control.InitialiseAsync(_setup.Token);

        // Both halves proved before any test runs, because the two failures need different fixes and neither
        // is legible from a payment test that simply times out.
        using var health = await Api.GetAsync("/api/v1/health", _setup.Token);
        Assert.True(
            health.RootElement.TryGetProperty("synchronized", out _)
            || health.RootElement.ValueKind is JsonValueKind.Object,
            $"{baseUrl}/api/v1/health did not answer with a health document.");

        using var status = await Api.GetStoreAsync("/spark", _setup.Token);
        var configured = status.RootElement.GetProperty("configured").GetBoolean();
        var running = status.RootElement.GetProperty("walletRunning").GetBoolean();
        if (!configured || !running)
        {
            Assert.Fail(
                $"store {StoreId} on {baseUrl} does not have a running Flint wallet (configured: "
                + $"{configured}, walletRunning: {running}, walletError: "
                + $"{Text(status.RootElement, "walletError") ?? "none"}). Re-run e2e/btcpay/up.sh, and read "
                + "`docker compose -f e2e/btcpay/docker-compose.yml logs flint-e2e-btcpay`.");
        }

        StartingBalanceSats = status.RootElement.TryGetProperty("balanceSats", out var balance)
                              && balance.ValueKind is JsonValueKind.Number
            ? balance.GetInt64()
            : 0;

        Console.WriteLine(
            $"btcpay-e2e: store {StoreId} on {baseUrl}, wallet running, balance "
            + $"{StartingBalanceSats.ToString("N0", CultureInfo.InvariantCulture)} sats, Lightning wiring "
            + $"{Text(status.RootElement, "lightningWiring") ?? "unknown"}");
    }

    /// <summary>Fails with an explanation unless the store's balance can cover <paramref name="needSats"/>.</summary>
    /// <remarks>
    /// A drained wallet here means an earlier test in the run spent more than it was sized for, which is a
    /// bug in this suite rather than something a maintainer tops up — so it is an assertion and not a runbook
    /// step. The balance is read from the plugin's own status endpoint, which
    /// <c>docs/greenfield-api.md</c> warns is indicative: it lags settlement by around 20 s. That is
    /// tolerable for a floor check and is why nothing else in this suite reconciles against it.
    /// </remarks>
    public async Task RequireBalanceAsync(long needSats, string what, CancellationToken cancellationToken)
    {
        using var status = await Api.GetStoreAsync("/spark", cancellationToken);
        var balance = status.RootElement.TryGetProperty("balanceSats", out var value)
                      && value.ValueKind is JsonValueKind.Number
            ? value.GetInt64()
            : 0;

        if (balance < needSats)
        {
            Assert.Fail(
                $"{what} needs {needSats:N0} sats and the store holds {balance:N0}. It started this run with "
                + $"{StartingBalanceSats:N0}, so something in this run spent more than it should have; the "
                + $"amounts in {nameof(BtcpayE2ETests)} are sized to fit inside the funding together, and the "
                + $"sweep leaves {ReserveForOtherTestsSats:N0} behind on purpose.");
        }
    }

    public ValueTask DisposeAsync()
    {
        // Nothing to give back. Unlike the LocalRegtest fixture, this suite's wallet is not created per run:
        // it belongs to the store e2e/btcpay/up.sh provisioned, and e2e/btcpay/down.sh is what returns the
        // SSP's liquidity — by destroying the wallet's storage along with the containers. Sweeping here
        // instead would spend a cooperative-exit fee on every run for no gain.
        _http?.Dispose();
        _setup.Dispose();
        return ValueTask.CompletedTask;
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>
    /// Reads the <c>fixture</c> block, which <c>up.sh</c> copies verbatim out of the Spark descriptor.
    /// </summary>
    /// <remarks>
    /// The same shape <c>network.json</c> carries and the same reader shape the LocalRegtest fixture uses, so
    /// <see cref="RegtestControl"/> is reused unchanged rather than reimplemented against a second contract.
    /// </remarks>
    private static LocalRegtestFixtureInfo ReadFixture(string path, JsonElement root)
    {
        if (!root.TryGetProperty("fixture", out var fixture))
        {
            throw new InvalidOperationException(
                $"{path} has no `fixture` block, so there is no way to reach the stack's bitcoind or its "
                + "Lightning nodes. Regenerate it with e2e/btcpay/up.sh.");
        }

        string Required(string name) =>
            fixture.TryGetProperty(name, out var value) && value.GetString() is { Length: > 0 } text
                ? text
                : throw new InvalidOperationException($"{path}: the fixture block is missing {name}.");

        string? Optional(string name) =>
            fixture.TryGetProperty(name, out var value) ? value.GetString() : null;

        return new LocalRegtestFixtureInfo(
            Required("bitcoindContainer"),
            Required("bitcoindRpcUser"),
            Required("bitcoindRpcPassword"),
            Required("lndContainer"),
            Optional("clnContainer"),
            Optional("sspContainer"),
            Optional("sspAdminToken"));
    }
}

/// <summary>
/// The Greenfield calls this suite makes, and nothing else.
/// </summary>
/// <remarks>
/// A deliberately thin wrapper over <see cref="HttpClient"/> rather than BTCPay's own generated client. Two
/// reasons. The plugin's endpoints are not in that client at all — they are this plugin's, and driving them
/// the way <c>docs/greenfield-api.md</c> tells a merchant to drive them is part of what is under test. And a
/// failing call has to explain itself: every non-success answer here carries the method, the path, the status
/// code and BTCPay's own response body, which is where the reason actually is.
/// </remarks>
public sealed class BtcpayGreenfield
{
    private readonly HttpClient _http;
    private readonly string _storeId;

    public BtcpayGreenfield(HttpClient http, string storeId)
    {
        _http = http;
        _storeId = storeId;
    }

    /// <summary>A store-scoped path, e.g. <c>/spark/sweep</c> → <c>/api/v1/stores/{id}/spark/sweep</c>.</summary>
    public string StorePath(string suffix) => $"/api/v1/stores/{_storeId}{suffix}";

    public Task<JsonDocument> GetStoreAsync(string suffix, CancellationToken cancellationToken) =>
        GetAsync(StorePath(suffix), cancellationToken);

    public Task<JsonDocument> PostStoreAsync(
        string suffix, string? json, CancellationToken cancellationToken) =>
        PostAsync(StorePath(suffix), json, cancellationToken);

    public Task<JsonDocument> PutStoreAsync(
        string suffix, string json, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Put, StorePath(suffix), json, cancellationToken);

    public Task<JsonDocument> GetAsync(string path, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Get, path, null, cancellationToken);

    public Task<JsonDocument> PostAsync(string path, string? json, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Post, path, json, cancellationToken);

    private async Task<JsonDocument> SendAsync(
        HttpMethod method, string path, string? json, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, path);
        if (json is not null)
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        using var response = await _http.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new BtcpayGreenfieldException(
                method, path, (int)response.StatusCode, body);
        }

        if (string.IsNullOrWhiteSpace(body))
        {
            // 200 with no body — DELETE .../spark answers this way. Modelled as an empty object so callers
            // do not have to special-case it.
            return JsonDocument.Parse("{}");
        }

        try
        {
            return JsonDocument.Parse(body);
        }
        catch (JsonException ex)
        {
            throw new BtcpayGreenfieldException(
                method, path, (int)response.StatusCode,
                $"the response was not JSON ({ex.Message}): {Trim(body)}");
        }
    }

    internal static string Trim(string value) =>
        value.Length <= 4_000 ? value : value[..4_000] + "\n… (truncated)";
}

/// <summary>A Greenfield call that did not succeed, carrying BTCPay's own explanation.</summary>
/// <remarks>
/// The body is the whole point. A 403 from a missing API-key permission, a 422 from the plugin's own
/// validation and a 404 from a plugin that failed to load are three completely different problems that read
/// identically as a status code.
/// </remarks>
public sealed class BtcpayGreenfieldException : Exception
{
    public BtcpayGreenfieldException(HttpMethod method, string path, int statusCode, string body)
        : base($"{method} {path} answered {statusCode}."
               + (string.IsNullOrWhiteSpace(body) ? "" : $"\nbody:\n{BtcpayGreenfield.Trim(body).TrimEnd()}"))
    {
        Method = method;
        Path = path;
        StatusCode = statusCode;
        Body = body;
    }

    public HttpMethod Method { get; }

    public string Path { get; }

    public int StatusCode { get; }

    public string Body { get; }
}

/// <summary>
/// The collection every BtcpayE2E test joins, so they share one store and one chain, one at a time.
/// </summary>
[CollectionDefinition(BtcpayE2EStack.CollectionName, DisableParallelization = true)]
public sealed class BtcpayE2ECollection : ICollectionFixture<BtcpayE2EStack>;
