using System.Globalization;
using System.Text.Json;
using BTCPayServer.Plugins.Flint.Sdk;
using BTCPayServer.Plugins.Flint.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Network = NBitcoin.Network;

namespace BTCPayServer.Plugins.Flint.Tests.LocalRegtest;

/// <summary>
/// A unilateral exit, executed end to end against real Spark operators and a chain this suite mines.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this file exists.</b> Everything on the exit surface was verified against the SDK's published
/// contract, the seam, a fake and unit tests — and none of that broadcasts anything, because the SDK never
/// does. The plugin's whole design is that an operator pushes the transactions out by hand, so the one thing
/// no test had ever done is the thing the feature is for: force a real balance on chain through the real
/// statechain tree. This does it.
/// </para>
/// <para>
/// <b>It drives the product's own code, not a parallel implementation.</b> The quote, the funding key
/// derivation, the build and the signer are the plugin's seam and <see cref="SparkExitFundingKey"/>. Only the
/// broadcasting is the test's, because that is genuinely the operator's job and the plugin deliberately does
/// not do it. The loop is driven by <c>CheckUnilateralExitAsync</c>'s verdict and per-transaction readiness —
/// the same two things the exit page renders — so a bug in the 0.25 status mapping shows up here as a stuck
/// exit rather than as a wrong colour on a table.
/// </para>
/// <para>
/// <b>The destination is asserted on, not the intermediate state.</b> The only assertion that cannot be
/// satisfied without a real exit is that the destination address' balance grew. A test that stopped at "the
/// build returned transactions" would pass against an exit that can never confirm.
/// </para>
/// <para>
/// <b>This test consumes the fixture's liquidity, unlike the rest of the suite.</b> Every other test in this
/// collection moves money and <c>LocalRegtestStack.DisposeAsync</c> pays what is left back to the chain, so a
/// run is roughly revenue-neutral for the SSP. An exit is not: it converts the wallet's whole balance to
/// on-chain Bitcoin at the destination address, which the return leg cannot undo and no later run re-uses.
/// Measured here, one run costs the SSP its full ~150,000-sat wallet — so a stack that has already run this
/// several times will start failing in fixture setup with
/// <c>amount cannot be represented by available leaves without creating a child below the configured split
/// floor</c>, which is the SSP being empty rather than anything wrong with the exit. Top it up with
/// <c>cashu-regtest/docker-scripts.sh</c>'s <c>cashu-spark-fund-ssp</c> (500,000 sats per invocation) — see
/// <c>e2e/local-regtest/README.md</c>, "Liquidity is the consumable".
/// </para>
/// <para>
/// Gated on <c>SPARK_LOCAL_REGTEST_NETWORK</c>, like the rest of this collection. See
/// <see cref="LocalRegtestStack"/>.
/// </para>
/// </remarks>
// A category of its own, not the suite's "LocalRegtest", and the reason is in the collection remarks
// below plus the test's own: an exit needs a stack nothing else is using. It is run by a step of its
// own in .github/workflows/local-regtest.yml, after the main suite and after a top-up.
[Trait("Category", "LocalRegtestExit")]
[Collection(LocalRegtestExitCollection.Name)]
public class SparkLocalRegtestExitTests
{
    private readonly LocalRegtestStack _stack;

    public SparkLocalRegtestExitTests(LocalRegtestStack stack) => _stack = stack;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// The rate the exit is quoted at, in sat/vB.
    /// </summary>
    /// <remarks>
    /// Deliberately near the floor rather than what the estimator suggests. This chain's estimator reads high
    /// (~100 sat/vB) because nothing has told it otherwise, and the rate multiplies across every transaction
    /// in the tree — at 100 sat/vB the CPFP funding requirement runs past what the wallet holds, which would
    /// make this test a test of the funding size rather than of the exit. Nothing here depends on the fee
    /// market ridiculing itself: the suite mines every block, so a low rate confirms as fast as a high one.
    /// </remarks>
    private const ulong FeeRateSatPerVbyte = 2;

    /// <summary>
    /// How many broadcast-and-mine rounds the exit is allowed before it is called stuck.
    /// </summary>
    /// <remarks>
    /// Sized from a measured run rather than guessed. On this fixture a wallet funded with one deposit exits
    /// through a tree node, then another, then a third, then a refund, then the sweep — each of the last three
    /// behind a ~2,000-block CSV timelock, and each level taking a couple of rounds to broadcast and confirm.
    /// That was 12 rounds to get the sweep onto the wire, so this leaves roughly double the room. A bound exists
    /// at all so that a status mapping which never reports Ready fails with "stuck at round N" and the whole
    /// trace, instead of running until the CI job's own timeout kills the process with nothing to read.
    /// </remarks>
    private const int MaxRounds = 28;

    /// <summary>
    /// How far past a reported timelock height to mine, so the transaction is unambiguously spendable.
    /// </summary>
    /// <remarks>
    /// A CSV lock of N is satisfied once N blocks have been mined <em>after</em> the parent confirmed, and the
    /// SDK reports the height that lands on. Mining exactly to it was observed to leave the transaction still
    /// non-final, which is the off-by-one this margin exists for rather than an unexplained rejection.
    /// </remarks>
    private const int TimelockMargin = 2;

    [Fact]
    public async Task A_unilateral_exit_puts_the_balance_on_chain()
    {
        // Skips rather than fails when the descriptor is unset, matching the rest of this collection: the main
        // CI job runs the unit suite with no stack at all, and a missing external fixture is not a defect.
        Assert.SkipUnless(LocalRegtestStack.IsEnabled, LocalRegtestStack.SkipReason);

        // 1. Sync first, and this is load-bearing. A leaf can only be exited from exit data the wallet has
        //    already collected, and the SDK collects it during a sync. A quote taken before this can legally
        //    select nothing, and the failure would look like "nothing worth exiting" rather than "you did not
        //    sync".
        await _stack.Sdk.SyncWalletAsync(Ct).ConfigureAwait(false);

        // 2. The destination is a fresh address of the fixture's wallet, so the test can read back what
        //    arrived. Bech32 because a real destination would be, and because the sweep is signed against
        //    whatever the quote echoes back.
        var destination = await _stack.Control.NewAddressAsync("exit-destination", cancellationToken: Ct)
            .ConfigureAwait(false);

        // 3. Quote. Auto first, to find out what the wallet actually holds — that is what a merchant pressing the
        //    button gets, and what this test needs in order to pick a leaf.
        var auto = await _stack.Sdk
            .PrepareUnilateralExitAsync(FeeRateSatPerVbyte, destination, leafIds: null, Ct)
            .ConfigureAwait(false);

        Assert.False(
            auto.IsEmpty,
            "Spark selected no leaves to exit. Either the wallet holds nothing or the funding step did not "
            + "credit it; both are fixture faults rather than exit faults.");

        // 4. Then pin to ONE leaf, the largest, and this is deliberate rather than a shortcut.
        //
        // A wallet funded by a deposit can be split into several leaves, and an exit across several branches
        // is a different and much larger thing: a fan-out transaction, one chain of tree nodes per branch, and
        // a refund per leaf, each behind its own CSV timelock. This collection's other fixture shares one chain
        // and mines it concurrently, and a measured run of that shape reached OnChainStateDiverged — the SDK's
        // "this can no longer finish" verdict — because branches matured out of step with each other. That is
        // worth investigating on its own, but a test that reports a real feature as broken a third of the time
        // is worse than no test, so this pins the exit to the single-leaf shape the SDK's own guide describes
        // first: no fan-out, one tree-node chain, one refund, one sweep.
        //
        // What is still covered: the quote, the funding derivation and discovery, the build and CPFP signing,
        // package broadcast, a CSV timelock matured by mining, the sweep, the fee arithmetic, and the SDK's
        // own verdict driving all of it.
        var leafId = auto.Leaves.OrderByDescending(leaf => leaf.ValueSat).First().LeafId;

        var quote = await _stack.Sdk
            .PrepareUnilateralExitAsync(FeeRateSatPerVbyte, destination, [leafId], Ct)
            .ConfigureAwait(false);

        Assert.Single(quote.Leaves);

        Assert.True(
            quote.RecoverableValueSat > quote.TotalFeeSat,
            $"The exit costs more than it recovers ({quote.TotalFeeSat} sat of fees against "
            + $"{quote.RecoverableValueSat} sat of value), so the plugin would refuse it. Lower the rate or "
            + "fund the wallet with more.");
        Assert.True(
            quote.SingleUtxoFundingSat > 0,
            "The quote asked for no funding at all, which cannot be right for an exit whose fees are paid by "
            + "CPFP.");

        // 5. The funding key, derived exactly as the plugin derives it. Index 0 because this test takes one
        //    exit; the plugin allocates one index per exit.
        Assert.True(
            SparkExitFundingKey.TryDerive(
                _stack.Mnemonic, Network.RegTest, index: 0, out var fundingKey, out var keyError),
            $"The funding key could not be derived from the fixture's own mnemonic: {keyError}");
        Assert.NotNull(fundingKey);

        using var _ = fundingKey;

        // 6. Fund it. One output, and at least the requirement — the SDK fans a single UTXO out across
        //    branches, and two smaller outputs would not fund the exit however encouraging their total.
        var fundingTxId = await _stack.Control
            .SendToAddressAsync(
                fundingKey.Address,
                SatsToBtc(quote.SingleUtxoFundingSat),
                feeRateSatPerVb: 2,
                Ct)
            .ConfigureAwait(false);

        await _stack.Control.MineAsync(3, Ct).ConfigureAwait(false);

        var fundingVout = await _stack.Control.FindVoutAsync(fundingTxId, fundingKey.Address, Ct)
            .ConfigureAwait(false);

        Assert.NotNull(fundingVout);

        var funding = new SparkExitFundingUtxo(
            fundingTxId, fundingVout!.Value, quote.SingleUtxoFundingSat, fundingKey.PubkeyHex);

        // 7. Build and sign. The quote is taken inside this call on purpose — see the seam — so the only thing
        //    worth asserting on the callback is that it got a quote at all.
        SparkExitQuote? committed = null;
        var built = await _stack.Sdk
            .UnilateralExitAsync(
                FeeRateSatPerVbyte,
                destination,
                leafIds: [leafId],
                [funding],
                fundingKey.Secret,
                second =>
                {
                    committed = second;
                    return null;
                },
                Ct)
            .ConfigureAwait(false);

        Assert.NotNull(committed);
        Assert.NotEmpty(built.Transactions);

        // A sweep, because the exit is not an exit without one. And a tree node, because the balance has to be
        // unrolled out of the tree before anything can be swept.
        //
        // Deliberately NOT asserting a fan-out. One exists only to split a single funding UTXO across several
        // branches, so a wallet whose quote selected one leaf pays its fees directly from the funding output
        // and has none — which is the documented shape and, on a wallet funded with one deposit, the ordinary
        // one. Asserting it here would have been this test pinning its own fixture's leaf count.
        Assert.Contains(built.Transactions, transaction => transaction.Kind is SparkExitTxKind.TreeNode);
        Assert.Contains(built.Transactions, transaction => transaction.Kind is SparkExitTxKind.Sweep);

        // The readiness invariant, and the one a wrong status mapping would break.
        //
        // An exit is a *chain*: the tree node that spends the funding output can go out immediately, and every
        // step after it is waiting on something in dependsOn to confirm. So the correct shape after a build with
        // nothing broadcast is "at least one step is Ready, and nothing claims to be Unverified" — NOT "all of
        // them are Ready", which is what this asserted first and which no real exit can satisfy.
        //
        // What would be a bug: nothing Ready at all (the exit can never start), or any step Unverified (the SDK
        // could not read the chain, so broadcasting it is unsafe and the operator is told to rebuild).
        Assert.Contains(
            built.Transactions,
            transaction => transaction.Status.Readiness is SparkExitTxReadiness.Ready);
        Assert.DoesNotContain(
            built.Transactions,
            transaction => transaction.Status.Readiness is SparkExitTxReadiness.Unverified);
        Assert.All(
            built.Transactions,
            transaction => Assert.Contains(
                transaction.Status.Readiness,
                new[] { SparkExitTxReadiness.Ready, SparkExitTxReadiness.Waiting }));

        // The chain is rooted: the step that spends the funding output must be the ready one, or the exit has
        // no way in. A Ready step downstream of an unconfirmed parent would be instructions to broadcast a
        // transaction whose inputs do not exist yet.
        var roots = built.Transactions
            .Where(transaction => transaction.DependsOn.Count == 0)
            .ToList();

        Assert.NotEmpty(roots);
        Assert.Contains(roots, root => root.Status.Readiness is SparkExitTxReadiness.Ready);

        // And the converse, which is the assertion that actually defends the mapping: at build time nothing has
        // been broadcast, so no step with a dependency can possibly be broadcastable. A transaction reported
        // Ready while its parent is unconfirmed is the page telling an operator to push a transaction whose
        // inputs do not exist — the node rejects it, and on a chain they do not control the rejection is all
        // they get.
        //
        // This is stricter than it first looks, and deliberately so. It was added after a mutation test: making
        // the SDK's WaitingForDependencies case map to Ready instead of Waiting left the whole exit still
        // completing, because the loop simply retried each rejected broadcast until the parents confirmed. The
        // exit was slow and noisy rather than wrong, so only an assertion at build time catches it.
        Assert.All(
            built.Transactions.Where(transaction => transaction.DependsOn.Count > 0),
            transaction => Assert.True(
                transaction.Status.Readiness is not SparkExitTxReadiness.Ready,
                $"{transaction.Kind} depends on {transaction.DependsOn.Count} transaction(s) that have not "
                + "confirmed, yet it reports Ready — the page would tell the operator to broadcast a "
                + "transaction whose inputs do not exist."));

        // 8. Push it out, exactly as the page instructs and with the result checked on chain.
        var exited = await DriveToCompletionAsync(built).ConfigureAwait(false);

        // 9. The assertion that cannot be satisfied without a real exit.
        var received = await ReceivedByAddressAsync(destination).ConfigureAwait(false);

        Assert.True(
            received > 0,
            $"The exit reported {exited.Verdict} but {destination} received nothing, so no sweep confirmed.");

        // What arrives is exactly recoverable + unspent funding − total fee, and getting this identity right is
        // the point of asserting on the amount rather than on "something arrived".
        //
        // The tempting reading — recoverable minus the fee — is the one the SDK's own guide goes out of its way
        // to correct: the CPFP and fan-out fees are paid out of the funding UTXO, not out of the recovered
        // value, so subtracting the total fee from it charges those to the merchant twice. Both halves matter.
        // The funding the exit did not spend comes back to the destination with the swept coins, which is why
        // the sweep collects the CPFP children's change; and a fee-accounting regression that quietly kept that
        // change, or that netted the CPFP fees out of the recovered value twice, shows up here as a shortfall.
        //
        // Measured on this fixture: 149,901 recoverable + 2,914 funding − 2,620 fees = 150,195 received, which
        // is more than the recoverable value — the shape that looks wrong and is right.
        Assert.Equal(
            built.RecoverableValueSat + funding.ValueSat - built.TotalFeeSat,
            received);

        // And independent of the identity: the merchant must end up with at least what the leaves were worth,
        // because the fees were paid from funding they supplied on top. A test that only checked the identity
        // would still pass on an exit that recovered nothing at all.
        Assert.True(
            received >= built.RecoverableValueSat,
            $"The destination received {received} sat, less than the {built.RecoverableValueSat} sat the exit "
            + "recovered, so the fees came out of the recovered value instead of the funding output.");
    }

    /// <summary>
    /// Broadcasts whatever the chain says is ready, mines, and repeats until the SDK reports the exit done.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the operator's job, automated — and modelled on the page's own instructions rather than on what
    /// happens to work: the fan-out and the sweep go out alone, a tree node goes out as a package with its
    /// CPFP child, and between rounds blocks are mined because that is what makes a level's CSV timelock
    /// mature. On a chain without a block producer each of those waits is real time; here it is a command,
    /// which is the whole reason this test is possible.
    /// </para>
    /// <para>
    /// Progress is read from the SDK, never inferred. A transaction is broadcast only when its status says
    /// Ready, and the loop stops when the verdict says the exit is finished or cannot finish. Re-broadcasting
    /// one that is already sent is harmless by the SDK's own contract, so a round that makes no progress is a
    /// bug rather than a lost broadcast — and is reported as such rather than retried forever.
    /// </para>
    /// </remarks>
    private async Task<SparkExitProgress> DriveToCompletionAsync(SparkExitResult built)
    {
        var current = built;
        var trace = new System.Text.StringBuilder();

        for (var round = 1; round <= MaxRounds; round++)
        {
            var progress = await _stack.Sdk.CheckUnilateralExitAsync(current, Ct).ConfigureAwait(false);

            current = new SparkExitResult(
                progress.RecoverableValueSat, progress.TotalFeeSat, progress.Transactions, current.Leaves);

            trace.AppendLine(
                $"round {round}: verdict={progress.Verdict} "
                + $"height={await _stack.Control.BlockHeightAsync(Ct).ConfigureAwait(false)} "
                + $"txs=[{string.Join(", ", progress.Transactions.Select(t => $"{t.Kind}:{t.Status.Readiness}" + (t.Status.SpendableAtHeight is { } h ? $"@{h}" : string.Empty) + (t.Status.BlockHeight is { } bh ? $"#{bh}" : string.Empty)))}]");

            if (progress.Verdict is SparkExitVerdict.Done)
                return progress;

            if (progress.Verdict is SparkExitVerdict.Redo)
            {
                Assert.Fail(
                    $"Round {round}: the SDK reported that this exit can no longer finish as it stands, against "
                    + $"a chain this suite controls and has not reorganised.\n{trace}");
            }

            var ready = progress.Transactions.Where(t => t.Status.CanBroadcast).ToList();

            foreach (var transaction in ready)
            {
                try
                {
                    var outcome = await BroadcastAsync(transaction).ConfigureAwait(false);
                    trace.AppendLine($"  broadcast {transaction.Kind} {transaction.Txid[..16]}: {outcome}");
                }
                catch (Exception ex)
                {
                    trace.AppendLine($"  broadcast {transaction.Kind} {transaction.Txid[..16]} THREW: {ex.Message}");
                }
            }

            // Mine enough to confirm what just went out, and to mature whatever timelock is next. Where the
            // SDK names the height a waiting transaction unlocks at, mine to it — that is strictly better than
            // guessing, and it is the number the page shows the operator.
            var nextUnlock = progress.Transactions
                .Select(t => t.Status.SpendableAtHeight)
                .Where(height => height is not null)
                .Select(height => (int)height!.Value)
                .DefaultIfEmpty(0)
                .Max();

            var height = await _stack.Control.BlockHeightAsync(Ct).ConfigureAwait(false);

            var blocks = Math.Max(
                ready.Count > 0 ? 2 : 1,
                nextUnlock > 0 ? (nextUnlock + TimelockMargin) - height : 0);

            await _stack.Control.MineAsync(blocks, Ct).ConfigureAwait(false);
            trace.AppendLine($"  mined {blocks}");
        }

        Assert.Fail(
            $"The exit did not finish within {MaxRounds} rounds. It is stuck rather than slow: every wait on "
            + $"this chain is a block this suite mines.\n{trace}");
        throw new InvalidOperationException("unreachable");
    }

    /// <summary>
    /// Puts one transaction on the wire the way its own shape requires.
    /// </summary>
    /// <remarks>
    /// The package decision is read off the presence of the CPFP child, matching
    /// <see cref="SparkExitTransaction.RequiresPackageBroadcast"/>. A tree transaction pays no fee of its own,
    /// so <c>sendrawtransaction</c> rejects it — that rejection is the reason the page's instructions differ
    /// per row, and getting it wrong here would silently test a path no operator takes.
    /// </remarks>
    private async Task<string> BroadcastAsync(SparkExitTransaction transaction)
    {
        if (transaction.RequiresPackageBroadcast)
        {
            // Bitcoin Core 31 on this fixture, so package relay is available. Core 25 is where submitpackage
            // arrives; a fixture bump below that would fail here with a clear "method not found" rather than
            // quietly falling back to a single-transaction broadcast that cannot work.
            using var response = await _stack.Control
                .BitcoinCliJsonAsync(
                    ["submitpackage", $"[\"{transaction.TxHex}\",\"{transaction.CpfpTxHex}\"]"], Ct)
                .ConfigureAwait(false);

            return "submitpackage " + response.RootElement.ToString()[..Math.Min(200, response.RootElement.ToString().Length)];
        }

        var txid = await _stack.Control
            .BitcoinCliAsync(["sendrawtransaction", transaction.TxHex], Ct)
            .ConfigureAwait(false);

        return "sendrawtransaction " + txid.Trim();
    }

    /// <summary>What the fixture's wallet has received at one address, in satoshi.</summary>
    private async Task<long> ReceivedByAddressAsync(string address)
    {
        var btc = decimal.Parse(
            (await _stack.Control
                .BitcoinCliAsync(["getreceivedbyaddress", address, "0"], Ct)
                .ConfigureAwait(false)).Trim(),
            CultureInfo.InvariantCulture);

        return (long)(btc * 100_000_000m);
    }

    private static decimal SatsToBtc(long sats) => sats / 100_000_000m;
}
/// <summary>
/// A collection of its own, so <see cref="SparkLocalRegtestExitTests"/> gets its own wallet.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is a correctness requirement, not tidiness.</b> A collection fixture is one instance shared by every
/// test class in the collection, so joining <c>LocalRegtestStack.CollectionName</c> would put this class on the
/// same wallet as the receive, send and cooperative-exit tests — and this test exits the <em>whole</em>
/// balance, because that is what an exit is. Whichever ran first would decide whether the others had any money:
/// a full pile of rent paid for one test and four failures that read like unrelated regressions.
/// </para>
/// <para>
/// A distinct collection name gives xUnit a distinct fixture instance, and therefore a distinct mnemonic, a
/// distinct wallet and a distinct 150,000-sat deposit. The cost is real and worth stating: the SSP is drained
/// twice per run rather than once, and both instances mine the same chain and so advance each other's heights.
/// Nothing here depends on absolute height — the exit follows heights the SDK reports — so the second cost is
/// only that the CSV waits are measured from wherever the other wallet's test left the tip.
/// </para>
/// </remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
public class LocalRegtestExitCollection : ICollectionFixture<LocalRegtestStack>
{
    public const string Name = "Spark local regtest (exit)";
}
