using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Threading.Channels;
using BTCPayServer.Plugins.Flint.Sdk;
using BTCPayServer.Plugins.Flint.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using NBitcoin;
using Xunit;
using SdkNetwork = Breez.Sdk.Spark.Network;

namespace BTCPayServer.Plugins.Flint.Tests.LocalRegtest;

/// <summary>
/// One freshly created, funded wallet on a <b>locally hosted</b> Spark network, shared by the whole
/// local-regtest suite.
/// </summary>
/// <remarks>
/// <para>
/// <b>What this adds over the funded suite.</b> <see cref="FundedRegtest.FundedRegtestWallet"/> runs against
/// Lightspark's hosted regtest, which gives real answers but withholds two things the plugin's behaviour
/// depends on. It has no external Lightning counterparty — a Flint invoice can only be paid by Flint itself,
/// so the plugin has never been observed settling a payment that came from somebody else's node — and it has
/// no control of the chain, so a cooperative exit confirms when it confirms. Here the whole stack is local:
/// three Spark operators, an open-ssp with its own LDK node, an Esplora, bitcoind, and LND and CLN nodes with
/// balanced channels to the SSP. Blocks are mined on demand and the counterparty is a real Lightning node
/// that is not this wallet.
/// </para>
/// <para>
/// <b>The wallet is created here, not supplied.</b> The funded suite is gated on a mnemonic held as a CI
/// secret because its money is real-ish and scarce; nothing about this stack is. A fresh 12-word mnemonic is
/// generated per run and funded from bitcoind, which is both simpler and safer: there is no long-lived secret
/// to leak, no shared balance to drain, and every run starts from an empty history. The mnemonic is never
/// printed — the tests still assert it did not reach the log, because the assertion is about the plugin's
/// scrubbing rather than about this particular wallet.
/// </para>
/// <para>
/// <b>Gating.</b> Opt-in on <c>SPARK_LOCAL_REGTEST_NETWORK</c> holding the path to the network descriptor
/// <c>e2e/local-regtest/write-network.sh</c> emits. Absent, every test in the collection skips and this
/// fixture connects nothing and starts no containers: the stack takes tens of minutes to build, so bringing
/// it up is a deliberate act and never a side effect of running the test suite.
/// </para>
/// <para>
/// <b>Serialised.</b> One wallet, three tests that each move its money, one chain whose height they all
/// advance. Hence a collection fixture with parallelisation disabled, as in the funded suite.
/// </para>
/// </remarks>
public sealed class LocalRegtestStack : IAsyncLifetime
{
    public const string CollectionName = "Spark local regtest";

    /// <summary>
    /// What the wallet is funded with, in satoshis.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Sized for the whole suite with headroom — a 5,000-sat receive, a 3,000-sat send, a 30,000-sat
    /// cooperative exit and every fee involved — and then deliberately kept well under what the SSP holds.
    /// </para>
    /// <para>
    /// <b>The ceiling is the SSP's, and it is not just about the deposit.</b> The fixture seeds open-ssp with
    /// a single 500,000-sat leaf, and every Spark payment out of this wallet needs the SSP to <em>split</em>
    /// leaves it can back. Funding this wallet with 200,000 was observed to succeed and then fail the first
    /// 3,000-sat Lightning send with <c>Tree service error: insufficient funds</c> once the SSP's own
    /// available balance had fallen below the wallet's — the deposit had eaten it. 150,000 against the SSP's
    /// 500,000 leaves it several times the wallet's balance, which is what keeps splitting possible.
    /// </para>
    /// <para>
    /// The upper end is also what the sweep test needs: it sizes a cooperative exit off a live fee quote, and
    /// on a chain whose estimator has drifted up to 100 sat/vB that means sweeping around 100,000 sats to
    /// keep the fee inside the engine's guards.
    /// </para>
    /// </remarks>
    public const long FundingSats = 150_000;

    /// <summary>How long the fixture waits for the funding deposit to be credited before failing.</summary>
    /// <remarks>
    /// Generous because the path is long — bitcoind must mine, Electrs must index, the SDK's chain service
    /// must see three confirmations, and only then does the claim go to the operators — and because the
    /// observed spread is wide: on the same stack the credit landed in 7 s on one run and 305 s on the next,
    /// which is the SDK's own deposit-claim worker choosing when to look rather than anything on the chain.
    /// The ceiling exists so a genuinely broken stack fails with an explanation instead of hanging until the
    /// CI job's own timeout kills the process and leaves no failure to read.
    /// </remarks>
    private static readonly TimeSpan FundingTimeout = TimeSpan.FromMinutes(10);

    public static string SkipReason =>
        $"Set {SparkCustomNetworkFile.EnvironmentVariable} to the path of a local Spark regtest network "
        + "descriptor to run the local-regtest suite. See docs/testing.md and e2e/local-regtest/README.md: "
        + "bring the stack up with e2e/local-regtest/up.sh, then write the descriptor with "
        + "e2e/local-regtest/write-network.sh.";

    private static SparkCustomNetwork? TryLoadNetwork()
    {
        try
        {
            return SparkCustomNetworkFile.TryLoadFromEnvironment();
        }
        catch (Exception ex)
        {
            // A descriptor that exists but does not parse is a fixture fault, not a reason to skip: skipping
            // would report the same green run as "the stack is not up", which is how a broken write-network.sh
            // stays broken.
            throw new InvalidOperationException(
                $"{SparkCustomNetworkFile.EnvironmentVariable} is set but the descriptor could not be read: "
                + ex.Message, ex);
        }
    }

    /// <summary>True when the descriptor variable names a file. The gate for the whole suite.</summary>
    public static bool IsEnabled =>
        Environment.GetEnvironmentVariable(SparkCustomNetworkFile.EnvironmentVariable) is { } path
        && !string.IsNullOrWhiteSpace(path);

    /// <summary>
    /// The deadline for everything this fixture does, including funding.
    /// </summary>
    /// <remarks>
    /// A token of its own rather than <c>TestContext.Current.CancellationToken</c>: the ambient token
    /// belongs to a test, and fixture setup runs outside one. Bounded anyway, because a fixture that hangs
    /// reports nothing at all — the CI job's own timeout kills the process and the run has no failure to
    /// read.
    /// </remarks>
    private readonly CancellationTokenSource _setup = new(TimeSpan.FromMinutes(15));

    private string? _mnemonic;
    private string? _storageDirectory;
    private string? _logDirectory;

    /// <summary>The connected wallet. Only valid when <see cref="IsEnabled"/>.</summary>
    public ISparkSdkClient Sdk { get; private set; } = null!;

    /// <summary>bitcoind and LND, over <c>docker exec</c>.</summary>
    public RegtestControl Control { get; private set; } = null!;

    /// <summary>The network the wallet is connected to, as the descriptor described it.</summary>
    public SparkCustomNetwork Network { get; private set; } = null!;

    /// <summary>A store id for the plugin-side collaborators. Not a real BTCPay store.</summary>
    public string StoreId => "local-regtest";

    /// <summary>The payment key the plugin-side collaborators are built with.</summary>
    public string PaymentKey => "local-regtest-key";

    /// <summary>Everything the SDK's Rust subscriber forwarded through <see cref="SparkLogBridge"/>, scrubbed.</summary>
    public CapturingLogger<SparkLogBridge> ForwardedLog { get; } = new();

    /// <summary>The wallet's static Bitcoin deposit address.</summary>
    public string DepositAddress { get; private set; } = null!;

    /// <summary>The balance observed once the funding deposit had been credited.</summary>
    public long FundedBalanceSats { get; private set; }

    /// <summary>What the SSP charged to claim the funding deposit. Reported, not asserted.</summary>
    public long DepositClaimFeeSats { get; private set; }

    /// <summary>How long the funding deposit took to be credited, for the record in the run log.</summary>
    public TimeSpan DepositClaimLatency { get; private set; }

    /// <summary>The seed, for the tests that must prove it was not logged. Never write this anywhere.</summary>
    public string Mnemonic => _mnemonic
        ?? throw new InvalidOperationException("The local regtest wallet is not enabled.");

    public async ValueTask InitializeAsync()
    {
        if (TryLoadNetwork() is not { } network)
            return;

        Network = network;
        Control = new RegtestControl(ReadFixture(
            Environment.GetEnvironmentVariable(SparkCustomNetworkFile.EnvironmentVariable)!));
        await Control.InitialiseAsync(_setup.Token);

        // Generated, not configured. See the class remarks: there is no secret to hold, so there is no reason
        // to hold one, and a fresh wallet per run means no test can be perturbed by an earlier run's history.
        _mnemonic = new Mnemonic(Wordlist.English, WordCount.Twelve).ToString();

        var root = Path.Combine(Path.GetTempPath(), "spark-local-regtest", Guid.NewGuid().ToString("N"));
        _storageDirectory = Path.Combine(root, "storage");
        _logDirectory = Path.Combine(root, "logs");
        Directory.CreateDirectory(_storageDirectory);
        Directory.CreateDirectory(_logDirectory);

        // Installed before the connect, because the SDK's subscriber is process-global and one-shot. "debug"
        // for the same reason the funded suite uses it: that is the level the plugin's log audit was performed
        // at, so it is the level the receive test's preimage assertion has to hold at. ClampFilter still
        // refuses trace, where the SSP session token lives.
        SparkLogging.TryInitialise(_logDirectory, ForwardedLog, "debug");

        var events = Channel.CreateBounded<SparkEventEnvelope>(new BoundedChannelOptions(256));
        var factory = new SparkSdkClientFactory(
            new FixedStorageProvider(_storageDirectory),
            new NBitcoinBolt11Parser(NBitcoin.Network.RegTest, NullLogger<NBitcoinBolt11Parser>.Instance),
            NullLoggerFactory.Instance);

        Sdk = await factory.ConnectAsync(
            new SparkConnectOptions(
                StoreId,
                _mnemonic,
                passphrase: null,
                apiKey: null,
                SdkNetwork.Regtest,
                // A ceiling generous enough that the SDK's own background worker claims the funding deposit
                // without help. The SDK's default of Rate(1 sat/vB) is a cap rather than a bid and strands
                // deposits, and this suite cannot start until one is credited.
                //
                // 1,000 sat/vB rather than the funded suite's 50, because this stack's idea of the fee
                // market is set by the fixture itself: every funding transaction it sends specifies
                // fee_rate=100, so the SSP quoted a claim requiring 100 sat/vB (9,900 sats) and refused both
                // the SDK's auto-claim and an explicit claim at a 50 sat/vB ceiling — the whole suite then
                // failed at startup with a zero balance. Nothing here is real money, so the ceiling is set
                // far above anything the fixture can ask for rather than tuned to it.
                maxDepositClaimFee: new SparkMaxFee.Rate(1_000),
                stableBalance: null,
                customNetwork: network),
            events.Writer);

        DepositAddress = await Sdk.GetBitcoinDepositAddressAsync(_setup.Token);
        await FundAsync(_setup.Token);
    }

    /// <summary>
    /// Puts <see cref="FundingSats"/> into the wallet from bitcoind and waits until the SDK has credited it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The route is the one the Rust reference client uses and the only one that exists: pay the wallet's
    /// static deposit address on-chain, mine past the SDK's three-confirmation maturity rule, and let the
    /// wallet claim it. Nothing here talks to the SSP's admin API — a deposit claimed out of band would prove
    /// nothing about the plugin's own configuration of the SDK.
    /// </para>
    /// <para>
    /// Claiming is expected to happen on the SDK's own background worker, which is why
    /// <c>maxDepositClaimFee</c> is set. The explicit claim in the loop is a fallback rather than the plan: if
    /// the worker has not acted by the time a deposit is mature, the suite claims it by outpoint at the same
    /// ceiling instead of failing. Both routes are the plugin's own code, so either one keeps the test honest;
    /// which one fired is reported so a change in the SDK's behaviour is visible in the run log rather than
    /// silently absorbed.
    /// </para>
    /// <para>
    /// A block is mined on every pass, not just at the start. Electrs and the SDK's chain service both follow
    /// the tip, and on an idle regtest chain nothing else advances it.
    /// </para>
    /// </remarks>
    private async Task FundAsync(CancellationToken cancellationToken)
    {
        var started = Stopwatch.StartNew();
        var amountBtc = FundingSats / 100_000_000m;
        var txId = await Control.SendToAddressAsync(DepositAddress, amountBtc,
            cancellationToken: cancellationToken);
        // Three, because that is the SDK's maturity rule for a deposit; the loop below keeps mining anyway.
        await Control.MineAsync(3, cancellationToken);

        var vout = await Control.FindVoutAsync(txId, DepositAddress, cancellationToken);
        var claimedExplicitly = false;
        var deadline = DateTimeOffset.UtcNow + FundingTimeout;
        var lastError = "";

        while (DateTimeOffset.UtcNow < deadline)
        {
            long balance;
            try
            {
                balance = await SyncBalanceAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                // The operators and the SSP are all coming up alongside this; a sync that fails while the
                // chain service catches up is expected, and the deadline is the judge.
                lastError = $"the last wallet sync failed: {ex.Message}";
                await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
                continue;
            }

            if (balance > 0)
            {
                FundedBalanceSats = balance;
                DepositClaimFeeSats = FundingSats - balance;
                DepositClaimLatency = started.Elapsed;
                Console.WriteLine(
                    $"local-regtest: funded {FundingSats:N0} sats, credited {balance:N0} "
                    + $"({DepositClaimFeeSats:N0} sats of claim fee) in "
                    + $"{DepositClaimLatency.TotalSeconds:0.0} s "
                    + $"({(claimedExplicitly ? "claimed explicitly" : "auto-claimed by the SDK")}); "
                    + $"deposit {txId}");
                return;
            }

            if (!claimedExplicitly && vout is { } outpoint)
            {
                try
                {
                    var deposits = await Sdk.ListUnclaimedDepositsAsync(cancellationToken);
                    var mine = deposits.FirstOrDefault(d =>
                        string.Equals(d.TxId, txId, StringComparison.OrdinalIgnoreCase));
                    if (mine is { IsMature: true })
                    {
                        var claim = await Sdk.ClaimDepositAsync(
                            txId, outpoint, new SparkMaxFee.Rate(1_000), cancellationToken);
                        if (claim.Succeeded)
                            claimedExplicitly = true;
                        else
                            lastError = $"the explicit claim was refused: {claim.Error}";
                    }
                    else if (mine is null)
                    {
                        lastError = "the SDK does not yet know about the funding deposit";
                    }
                    else
                    {
                        lastError = "the funding deposit is not mature yet";
                    }
                }
                catch (Exception ex)
                {
                    lastError = $"the last claim attempt threw: {ex.Message}";
                }
            }

            await Control.MineAsync(1, cancellationToken);
            await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
        }

        Assert.Fail(
            $"the local regtest wallet was never funded: {FundingSats:N0} sats were sent to its deposit "
            + $"address in {txId} (vout {vout?.ToString(CultureInfo.InvariantCulture) ?? "not found"}) and "
            + $"mined, but the SDK's balance was still 0 after {FundingTimeout.TotalMinutes:0} minutes"
            + (lastError.Length == 0 ? "." : $"; {lastError}.")
            + " Check that the stack's Spark operators, open-ssp and Esplora are all healthy — "
            + "e2e/local-regtest/README.md says how — and that open-ssp reports Spark liquidity, since a "
            + "deposit is credited out of the SSP's own leaves.");
    }

    /// <summary>
    /// Forces a sync and returns the balance.
    /// </summary>
    /// <remarks>
    /// The sync is not optional, for the reason <see cref="ISparkSdkClient.SyncWalletAsync"/> gives: the
    /// balance was observed lagging settlement by ~20 s on the hosted regtest even through
    /// <c>GetInfo(ensureSynced: true)</c>. The local stack syncs every two seconds, so the lag is much
    /// smaller here, but nothing in this suite is measuring that and everything in it needs the current
    /// number.
    /// </remarks>
    public async Task<long> SyncBalanceAsync(CancellationToken cancellationToken = default)
    {
        await Sdk.SyncWalletAsync(cancellationToken);
        var info = await Sdk.GetInfoAsync(ensureSynced: true, cancellationToken);
        return info.BalanceSats;
    }

    /// <summary>Fails with an explanation unless the wallet can cover <paramref name="needSats"/>.</summary>
    /// <remarks>
    /// Unlike the funded suite's equivalent this is not a runbook step — a drained wallet here means an
    /// earlier test in the run spent more than it was supposed to, which is a bug in the suite rather than
    /// something a maintainer tops up.
    /// </remarks>
    public async Task RequireBalanceAsync(
        long needSats, string what, CancellationToken cancellationToken = default)
    {
        var balance = await SyncBalanceAsync(cancellationToken);
        if (balance < needSats)
        {
            Assert.Fail(
                $"{what} needs {needSats:N0} sats and the local regtest wallet holds {balance:N0}. It was "
                + $"funded with {FundedBalanceSats:N0}, so something in this run spent more than it should "
                + "have; the amounts in SparkLocalRegtestTests are sized to fit inside the funding together.");
        }
    }

    /// <summary>A snapshot of how many lines the bridge has forwarded, to bound a later slice.</summary>
    public int ForwardedLineCount => ForwardedLog.Lines.Count;

    /// <summary>The lines forwarded since <paramref name="from"/>.</summary>
    public IReadOnlyList<string> ForwardedSince(int from)
    {
        var lines = ForwardedLog.Lines;
        return from >= lines.Count ? [] : lines.Skip(from).ToList();
    }

    public async ValueTask DisposeAsync()
    {
        if (_mnemonic is null)
            return;

        // Before disconnecting, give the money back. See ReturnFundsAsync.
        await ReturnFundsAsync();

        try
        {
            if (Sdk is not null)
            {
                // Disconnect then Dispose: after Disconnect alone the instance still serves the network.
                await Sdk.DisconnectAsync();
                Sdk.Dispose();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"local-regtest: could not disconnect cleanly: {ex}");
        }

        TryDeleteStorage();
        _setup.Dispose();
    }

    /// <summary>
    /// Cooperatively exits whatever is left in the wallet back on-chain, best effort.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why a teardown step spends money.</b> Every run of this suite funds a <em>fresh</em> wallet out of
    /// the SSP's own Spark leaves, and a wallet that is simply abandoned keeps them: the SSP's available
    /// balance falls by the funded amount per run and never recovers. Two or three runs against one stack
    /// were enough to drain the fixture's 500,000-sat seed to the point where the next run's cooperative-exit
    /// quote failed with <c>Tree service error: insufficient funds</c> — a failure that names this wallet and
    /// means the SSP. Handing the balance back is what makes the suite re-runnable against a stack that takes
    /// tens of minutes to build.
    /// </para>
    /// <para>
    /// Best effort, and silent about it beyond a console line: this runs after the tests have their verdicts,
    /// and a fixture that turned a green run red while tidying up would be worse than a stack that needs a
    /// top-up. The fixture's own acceptance test ends the same way, for the same reason.
    /// </para>
    /// </remarks>
    private async Task ReturnFundsAsync()
    {
        try
        {
            var balance = await SyncBalanceAsync();
            if (balance <= 0)
                return;

            var destination = await Control.NewAddressAsync("flint-local-regtest-return");
            var quote = await Sdk.QuoteOnchainSendAsync(destination, balance, feesIncluded: true);
            if (quote.SlowFeeSats <= 0 || quote.SlowFeeSats >= balance)
            {
                Console.WriteLine(
                    $"local-regtest: leaving {balance:N0} sats in the wallet — the exit fee "
                    + $"({quote.SlowFeeSats:N0} sats) is not worth paying. The SSP's liquidity will need a "
                    + "top-up sooner.");
                return;
            }

            var send = await Sdk.SendToBitcoinAddressAsync(
                destination,
                balance,
                SparkOnchainSpeed.Slow,
                feesIncluded: true,
                Guid.NewGuid().ToString(),
                approveQuote: _ => null);

            if (send.RejectedReason is null)
            {
                // Mined so the SSP's own wallet sees the exit confirm rather than holding it in flight.
                await Control.MineAsync(3);
                Console.WriteLine(
                    $"local-regtest: returned {balance:N0} sats to the chain ({send.Payment?.TxId}), so the "
                    + "SSP's Spark liquidity comes back for the next run.");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"local-regtest: could not return the wallet's balance ({ex.Message}).");
        }
    }

    private void TryDeleteStorage()
    {
        if (_storageDirectory is null)
            return;
        try
        {
            // The whole run directory, storage and SDK logs together. Nothing here is an artefact: the
            // wallet is disposable and its log is asserted on in-process, so leaving either behind would only
            // leave a directory holding a wallet's storage on a developer's machine.
            Directory.Delete(Path.GetDirectoryName(_storageDirectory)!, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Reads the descriptor's <c>fixture</c> block — the half the plugin ignores.
    /// </summary>
    /// <remarks>
    /// Read straight from the file rather than through <see cref="SparkCustomNetworkFile"/> on purpose. That
    /// loader models only what the SDK needs and deliberately ignores this block so a fixture-side change
    /// cannot break the plugin; keeping the two readers separate is what preserves that.
    /// </remarks>
    private static LocalRegtestFixtureInfo ReadFixture(string descriptorPath)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(descriptorPath));
        if (!document.RootElement.TryGetProperty("fixture", out var fixture))
        {
            throw new InvalidOperationException(
                $"{descriptorPath} has no `fixture` block, so there is no way to reach the stack's bitcoind "
                + "or its Lightning nodes. It was probably written by hand or by an older "
                + "write-network.sh; regenerate it with e2e/local-regtest/write-network.sh.");
        }

        string Required(string name) =>
            fixture.TryGetProperty(name, out var value) && value.GetString() is { Length: > 0 } text
                ? text
                : throw new InvalidOperationException(
                    $"{descriptorPath}: the fixture block is missing {name}.");

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

    private sealed class FixedStorageProvider : ISparkStorageProvider
    {
        private readonly string _path;

        public FixedStorageProvider(string path) => _path = path;

        public SparkStorageTarget GetTarget(string storeId) => new SparkStorageTarget.Directory(_path);
    }
}

/// <summary>
/// The collection every local-regtest test joins, so they share one wallet and one chain, one at a time.
/// </summary>
[CollectionDefinition(LocalRegtestStack.CollectionName, DisableParallelization = true)]
public sealed class LocalRegtestCollection : ICollectionFixture<LocalRegtestStack>;
