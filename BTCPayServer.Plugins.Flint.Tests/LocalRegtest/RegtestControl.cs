using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace BTCPayServer.Plugins.Flint.Tests.LocalRegtest;

/// <summary>
/// The fixture half of the local-regtest network descriptor: how to drive the stack's bitcoind and its
/// Lightning counterparties.
/// </summary>
/// <remarks>
/// Deliberately separate from <see cref="Sdk.SparkCustomNetwork"/>, which the plugin reads. Container names
/// and RPC credentials are the test suite's business and none of the plugin's, so the plugin ignores this
/// half of the file and it is modelled here instead.
/// </remarks>
public sealed record LocalRegtestFixtureInfo(
    string BitcoindContainer,
    string BitcoindRpcUser,
    string BitcoindRpcPassword,
    string LndContainer,
    string? ClnContainer,
    string? SspContainer,
    string? SspAdminToken);

/// <summary>
/// Drives the local regtest stack from outside: bitcoind through <c>bitcoin-cli</c> and LND through
/// <c>lncli</c>, both over <c>docker exec</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why docker exec rather than RPC over the published ports.</b> Both nodes speak to the host — bitcoind's
/// RPC port and LND's REST port are published — but reaching them needs credentials the fixture only ever
/// writes inside its own containers: LND's admin macaroon and its self-signed TLS certificate live in a bind
/// mount whose path is the fixture's business, and the Rust reference client only gets at them because it runs
/// inside the compose network with those directories mounted. From the host, <c>docker exec</c> is the one
/// route that needs nothing beyond the container name — and it is exactly what the fixture's own
/// <c>docker-scripts.sh</c> does (<c>bitcoin-cli-sim</c>, <c>lncli-sim</c>), so the invocations here are the
/// ones the fixture itself is known to work with.
/// </para>
/// <para>
/// <b>The invocations, and why each flag is there.</b>
/// </para>
/// <list type="bullet">
/// <item><description>
/// <c>docker exec &lt;bitcoind&gt; bitcoin-cli -regtest -rpcuser=… -rpcpassword=… -rpcwallet=cashu …</c> —
/// the wallet name is not optional. The fixture creates two wallets (<c>cashu</c> and
/// <c>ssp-withdrawals</c>), and with more than one loaded bitcoind refuses every wallet call that does not
/// name one. <c>cashu</c> is the funded, mining wallet.
/// </description></item>
/// <item><description>
/// <c>docker exec &lt;lnd&gt; lncli --network regtest --rpcserver=&lt;hostname&gt;:10009 …</c> — the
/// rpcserver is required and cannot be <c>localhost</c>: the fixture starts lnd with
/// <c>--rpclisten=lnd-1:10009</c>, so it is not listening on loopback at all. The hostname is read off the
/// container at startup rather than assumed, since it is the container's own compose hostname. No macaroon
/// or TLS flag is needed — lnd's data directory is the image default (<c>/root/.lnd</c>), which is where
/// lncli looks for <c>tls.cert</c> and <c>data/chain/bitcoin/regtest/admin.macaroon</c>.
/// </description></item>
/// </list>
/// <para>
/// Every call is bounded by a timeout and every failure carries the exit code and the captured stderr. A
/// silent hang or a bare "command failed" in the middle of a payment test costs far more to diagnose than
/// the plumbing costs to write.
/// </para>
/// </remarks>
public sealed class RegtestControl
{
    /// <summary>
    /// The bitcoind wallet the fixture funds and mines with.
    /// </summary>
    /// <remarks>
    /// From <c>docker-scripts.sh</c>'s <c>bitcoin-cli-sim</c>, which pins <c>-rpcwallet=cashu</c> for the same
    /// reason: <c>cashu-spark-fund-ssp</c> creates an <c>ssp-withdrawals</c> wallet alongside it, and bitcoind
    /// rejects unqualified wallet calls once two are loaded.
    /// </remarks>
    public const string BitcoindWallet = "cashu";

    /// <summary>LND's gRPC port inside the compose network.</summary>
    private const int LndRpcPort = 10009;

    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(90);

    private readonly LocalRegtestFixtureInfo _fixture;
    private string? _lndRpcServer;

    public RegtestControl(LocalRegtestFixtureInfo fixture) => _fixture = fixture;

    /// <summary>
    /// Proves the stack is reachable and resolves LND's rpcserver address.
    /// </summary>
    /// <remarks>
    /// Called before anything else in the fixture, so that a stack that is not running fails here — naming the
    /// container and the command — rather than several minutes later as an unexplained funding timeout.
    /// </remarks>
    public async Task InitialiseAsync(CancellationToken cancellationToken = default)
    {
        var hostname = (await RunAsync(
            ["docker", "inspect", "-f", "{{.Config.Hostname}}", _fixture.LndContainer],
            TimeSpan.FromSeconds(30),
            cancellationToken)).Trim();

        // A compose service gets its service name as its hostname, which is what the fixture's
        // --rpclisten binds to. An empty or malformed answer would otherwise become an lncli
        // "connection refused" with no hint about where the address came from.
        if (string.IsNullOrWhiteSpace(hostname))
        {
            throw new InvalidOperationException(
                $"could not read the compose hostname of the LND container '{_fixture.LndContainer}', so "
                + "there is no address to point lncli at.");
        }

        _lndRpcServer = $"{hostname}:{LndRpcPort.ToString(CultureInfo.InvariantCulture)}";

        // Two cheap round trips that fail loudly: one per node, each through the exact invocation the rest of
        // the suite uses.
        _ = await BitcoinCliJsonAsync(["getblockchaininfo"], cancellationToken);
        _ = await LncliJsonAsync(["getinfo"], cancellationToken);
    }

    #region bitcoind

    /// <summary>Runs <c>bitcoin-cli</c> against the fixture's funded wallet and returns raw stdout.</summary>
    public Task<string> BitcoinCliAsync(
        IEnumerable<string> args, CancellationToken cancellationToken = default) =>
        RunAsync(
            [
                "docker", "exec", _fixture.BitcoindContainer, "bitcoin-cli", "-regtest",
                $"-rpcuser={_fixture.BitcoindRpcUser}",
                $"-rpcpassword={_fixture.BitcoindRpcPassword}",
                $"-rpcwallet={BitcoindWallet}",
                .. args
            ],
            DefaultTimeout,
            cancellationToken);

    /// <summary>Runs <c>bitcoin-cli</c> and parses its answer as JSON.</summary>
    public async Task<JsonDocument> BitcoinCliJsonAsync(
        IEnumerable<string> args, CancellationToken cancellationToken = default) =>
        Parse(await BitcoinCliAsync(args, cancellationToken), "bitcoin-cli");

    /// <summary>A fresh address from the fixture's wallet.</summary>
    /// <param name="addressType">
    /// <c>bech32</c> by default. Not <c>bech32m</c>: a sweep destination goes through
    /// <c>SweepDestinationResolver</c>, and the point of these tests is the sweep, not NBitcoin's taproot
    /// parsing.
    /// </param>
    public async Task<string> NewAddressAsync(
        string label = "", string addressType = "bech32", CancellationToken cancellationToken = default) =>
        (await BitcoinCliAsync(["getnewaddress", label, addressType], cancellationToken)).Trim();

    /// <summary>
    /// Mines <paramref name="blocks"/> blocks to a fresh address of the fixture's wallet.
    /// </summary>
    /// <remarks>
    /// A fresh address rather than <c>-generate</c>'s implicit one purely so the coinbase outputs do not pile
    /// up on a single key; either would do. The address is minted per call because mining is what this suite
    /// uses to advance every timelock, so it happens dozens of times per run.
    /// </remarks>
    public async Task<int> MineAsync(int blocks, CancellationToken cancellationToken = default)
    {
        var address = await NewAddressAsync(cancellationToken: cancellationToken);
        _ = await BitcoinCliAsync(
            ["generatetoaddress", blocks.ToString(CultureInfo.InvariantCulture), address],
            cancellationToken);
        return await BlockHeightAsync(cancellationToken);
    }

    public async Task<int> BlockHeightAsync(CancellationToken cancellationToken = default) =>
        int.Parse(
            (await BitcoinCliAsync(["getblockcount"], cancellationToken)).Trim(),
            CultureInfo.InvariantCulture);

    /// <summary>
    /// Sends to <paramref name="address"/> and returns the txid.
    /// </summary>
    /// <remarks>
    /// <c>-named</c> with an explicit <c>fee_rate</c>, exactly as the fixture's own funding helpers do: on a
    /// fresh regtest chain the fee estimator has no data, and an unqualified <c>sendtoaddress</c> fails with
    /// "Fee estimation failed" rather than picking a floor.
    /// </remarks>
    public async Task<string> SendToAddressAsync(
        string address, decimal amountBtc, int feeRateSatPerVb = 100,
        CancellationToken cancellationToken = default) =>
        (await BitcoinCliAsync(
            [
                "-named", "sendtoaddress",
                $"address={address}",
                $"amount={amountBtc.ToString("0.########", CultureInfo.InvariantCulture)}",
                $"fee_rate={feeRateSatPerVb.ToString(CultureInfo.InvariantCulture)}"
            ],
            cancellationToken)).Trim();

    /// <summary>The index of the output of <paramref name="txId"/> that pays <paramref name="address"/>.</summary>
    /// <remarks>
    /// Needed to claim a deposit by hand: the SDK addresses a deposit by outpoint, and a
    /// <c>sendtoaddress</c> transaction also carries a change output, so the vout is never reliably 0.
    /// </remarks>
    public async Task<uint?> FindVoutAsync(
        string txId, string address, CancellationToken cancellationToken = default)
    {
        using var tx = await BitcoinCliJsonAsync(["getrawtransaction", txId, "true"], cancellationToken);
        if (!tx.RootElement.TryGetProperty("vout", out var outputs))
            return null;

        foreach (var output in outputs.EnumerateArray())
        {
            if (output.TryGetProperty("scriptPubKey", out var script)
                && script.TryGetProperty("address", out var candidate)
                && string.Equals(candidate.GetString(), address, StringComparison.Ordinal)
                && output.TryGetProperty("n", out var n))
            {
                return n.GetUInt32();
            }
        }

        return null;
    }

    #endregion

    #region lnd

    /// <summary>Runs <c>lncli</c> inside the fixture's LND container and returns raw stdout.</summary>
    public Task<string> LncliAsync(IEnumerable<string> args, CancellationToken cancellationToken = default)
    {
        var rpcServer = _lndRpcServer
            ?? throw new InvalidOperationException(
                $"{nameof(RegtestControl)}.{nameof(InitialiseAsync)} has not run, so LND's rpcserver address "
                + "is unknown.");

        return RunAsync(
            [
                "docker", "exec", _fixture.LndContainer, "lncli",
                "--network", "regtest", $"--rpcserver={rpcServer}",
                .. args
            ],
            DefaultTimeout,
            cancellationToken);
    }

    /// <summary>Runs <c>lncli</c> and parses its answer as JSON.</summary>
    public async Task<JsonDocument> LncliJsonAsync(
        IEnumerable<string> args, CancellationToken cancellationToken = default) =>
        Parse(await LncliAsync(args, cancellationToken), "lncli");

    /// <summary>Mints an invoice on LND, for the plugin to pay.</summary>
    /// <returns>The BOLT11 request and the payment hash, as lowercase hex.</returns>
    public async Task<LndInvoice> LndAddInvoiceAsync(
        long amountSats, string memo, CancellationToken cancellationToken = default)
    {
        using var response = await LncliJsonAsync(
            [
                "addinvoice",
                "--amt", amountSats.ToString(CultureInfo.InvariantCulture),
                "--memo", memo,
                "--expiry", "900"
            ],
            cancellationToken);

        var bolt11 = response.RootElement.GetProperty("payment_request").GetString()
            ?? throw new InvalidOperationException("lncli addinvoice returned no payment_request.");
        var hash = HexOf(response.RootElement.GetProperty("r_hash"))
            ?? throw new InvalidOperationException("lncli addinvoice returned no r_hash.");
        return new LndInvoice(bolt11, hash);
    }

    /// <summary>
    /// Pays a BOLT11 invoice from LND, then reads the settled payment back out of LND's own history.
    /// </summary>
    /// <remarks>
    /// The result is read from <c>listpayments</c> rather than scraped out of <c>payinvoice</c>'s output on
    /// purpose: <c>payinvoice</c> renders a live-updating table whose shape has changed across lnd releases,
    /// while <c>listpayments</c> is machine JSON. Passing the hash in means the lookup needs no parsing of the
    /// payer's progress output at all.
    /// </remarks>
    /// <param name="expectedPaymentHash">
    /// The hash the invoice commits to, used to find the payment afterwards.
    /// </param>
    public async Task<LndPayment> LndPayInvoiceAsync(
        string bolt11,
        string expectedPaymentHash,
        long feeLimitSats = 1_000,
        CancellationToken cancellationToken = default)
    {
        // --force skips the interactive confirmation, which would otherwise block forever on a
        // non-tty stdin. --json asks for machine output where the lnd build supports it; where it
        // does not, the flag is rejected and the payment is retried without it, since the answer is
        // read from listpayments either way.
        string payOutput;
        try
        {
            payOutput = await LncliAsync(
                [
                    "payinvoice", "--force", "--json",
                    "--timeout", "120s",
                    "--fee_limit", feeLimitSats.ToString(CultureInfo.InvariantCulture),
                    bolt11
                ],
                cancellationToken);
        }
        catch (RegtestCommandException ex) when (ex.StdErr.Contains("--json", StringComparison.Ordinal)
                                                 || ex.StdErr.Contains("flag provided but not defined",
                                                     StringComparison.Ordinal))
        {
            payOutput = await LncliAsync(
                [
                    "payinvoice", "--force",
                    "--timeout", "120s",
                    "--fee_limit", feeLimitSats.ToString(CultureInfo.InvariantCulture),
                    bolt11
                ],
                cancellationToken);
        }

        var payment = await FindPaymentAsync(expectedPaymentHash, cancellationToken);
        if (payment is null)
        {
            throw new InvalidOperationException(
                $"lncli paid {expectedPaymentHash} but no matching payment appeared in listpayments. "
                + $"payinvoice said:\n{payOutput}");
        }

        return payment;
    }

    /// <summary>One payment in LND's history, by hash, or null when LND has no record of it.</summary>
    public async Task<LndPayment?> FindPaymentAsync(
        string paymentHash, CancellationToken cancellationToken = default)
    {
        // No --reversed flag: lncli rejects it ("flag provided but not defined") because reversed
        // pagination — newest first — is already the default, which is the order this needs.
        using var response = await LncliJsonAsync(
            ["listpayments", "--include_incomplete", "--max_payments", "50"],
            cancellationToken);

        if (!response.RootElement.TryGetProperty("payments", out var payments))
            return null;

        foreach (var payment in payments.EnumerateArray())
        {
            var hash = payment.TryGetProperty("payment_hash", out var h) ? HexOf(h) : null;
            if (!string.Equals(hash, paymentHash, StringComparison.OrdinalIgnoreCase))
                continue;

            return new LndPayment(
                hash!,
                payment.TryGetProperty("status", out var status) ? status.GetString() ?? "" : "",
                payment.TryGetProperty("payment_preimage", out var preimage) ? HexOf(preimage) : null,
                payment.TryGetProperty("value_sat", out var value) ? LongOf(value) : null,
                payment.TryGetProperty("fee_sat", out var fee) ? LongOf(fee) : null);
        }

        return null;
    }

    /// <summary>An invoice's state on LND, by payment hash.</summary>
    public async Task<LndInvoiceState> LndLookupInvoiceAsync(
        string paymentHash, CancellationToken cancellationToken = default)
    {
        using var response = await LncliJsonAsync(["lookupinvoice", paymentHash], cancellationToken);
        var root = response.RootElement;
        var state = root.TryGetProperty("state", out var s) ? s.GetString() ?? "" : "";
        return new LndInvoiceState(
            state,
            root.TryGetProperty("settled", out var settled) && settled.ValueKind == JsonValueKind.True
                || string.Equals(state, "SETTLED", StringComparison.Ordinal),
            root.TryGetProperty("r_preimage", out var preimage) ? HexOf(preimage) : null,
            root.TryGetProperty("amt_paid_sat", out var paid) ? LongOf(paid) : null);
    }

    #endregion

    /// <summary>
    /// Normalises one of lnd's <c>bytes</c> fields to lowercase hex.
    /// </summary>
    /// <remarks>
    /// lncli renders them as hex and lnd's REST gateway renders the same fields as base64, so both are
    /// accepted — the Rust reference client's <c>bytes_hex</c> makes the same allowance for the same reason.
    /// Anything else returns null rather than throwing, so a shape change surfaces as a named assertion in the
    /// test rather than as a parse error in the plumbing.
    /// </remarks>
    internal static string? HexOf(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.String)
            return null;

        var value = element.GetString();
        if (string.IsNullOrWhiteSpace(value))
            return null;

        if (value.Length == 64 && value.All(Uri.IsHexDigit))
            return value.ToLowerInvariant();

        try
        {
            return Convert.ToHexString(Convert.FromBase64String(value)).ToLowerInvariant();
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>lnd renders 64-bit numbers as JSON strings; both forms are read here.</summary>
    internal static long? LongOf(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Number => element.GetInt64(),
        JsonValueKind.String when long.TryParse(
            element.GetString(), CultureInfo.InvariantCulture, out var parsed) => parsed,
        _ => null
    };

    private static JsonDocument Parse(string output, string what)
    {
        try
        {
            return JsonDocument.Parse(output);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"{what} did not answer with JSON: {ex.Message}. It said:\n{Trim(output)}", ex);
        }
    }

    /// <summary>
    /// Runs one process to completion, with a timeout, and returns stdout.
    /// </summary>
    /// <remarks>
    /// <see cref="ProcessStartInfo.ArgumentList"/> rather than a command string: several arguments here carry
    /// values from the descriptor and from the tests (memos, addresses, BOLT11 requests), and a shell-quoting
    /// bug in a test fixture that drives a wallet is not a class of bug worth having. On a timeout the child is
    /// killed with its whole tree — <c>docker exec</c> otherwise leaves the in-container command running.
    /// </remarks>
    private static async Task<string> RunAsync(
        IReadOnlyList<string> command, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo(command[0])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false
        };
        foreach (var argument in command.Skip(1))
            info.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = info };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
                stdout.AppendLine(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
                stderr.AppendLine(e.Data);
        };

        if (!process.Start())
            throw new InvalidOperationException($"could not start {command[0]}.");

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        // Closed immediately: lncli prompts for confirmation on some commands, and a child holding an open
        // stdin it will never receive anything on is a hang rather than an error.
        process.StandardInput.Close();

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(deadline.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            throw new RegtestCommandException(
                command,
                exitCode: null,
                stdout.ToString(),
                stderr.ToString(),
                $"timed out after {timeout.TotalSeconds:0} s");
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        if (process.ExitCode != 0)
        {
            throw new RegtestCommandException(
                command, process.ExitCode, stdout.ToString(), stderr.ToString(),
                $"exited {process.ExitCode.ToString(CultureInfo.InvariantCulture)}");
        }

        return stdout.ToString();
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
        catch (NotSupportedException)
        {
        }
    }

    private static string Trim(string value) =>
        value.Length <= 4_000 ? value : value[..4_000] + "\n… (truncated)";

    /// <summary>An invoice minted on LND.</summary>
    public sealed record LndInvoice(string Bolt11, string PaymentHash);

    /// <summary>An LND invoice's settlement state.</summary>
    public sealed record LndInvoiceState(string State, bool Settled, string? Preimage, long? AmountPaidSats);

    /// <summary>An outgoing payment as LND recorded it.</summary>
    public sealed record LndPayment(
        string PaymentHash, string Status, string? Preimage, long? ValueSats, long? FeeSats)
    {
        public bool Succeeded => string.Equals(Status, "SUCCEEDED", StringComparison.Ordinal);
    }
}

/// <summary>
/// A <c>docker exec</c> that failed, carrying everything needed to work out why.
/// </summary>
/// <remarks>
/// The captured stderr is the whole point. bitcoind and lncli both explain themselves there — an unloaded
/// wallet, a missing macaroon, a wrong rpcserver — and without it every one of those failures reads
/// identically as a non-zero exit code from a docker command.
/// </remarks>
public sealed class RegtestCommandException : Exception
{
    public RegtestCommandException(
        IReadOnlyList<string> command, int? exitCode, string stdOut, string stdErr, string what)
        : base($"`{string.Join(' ', command)}` {what}."
               + (string.IsNullOrWhiteSpace(stdErr) ? "" : $"\nstderr:\n{stdErr.TrimEnd()}")
               + (string.IsNullOrWhiteSpace(stdOut) ? "" : $"\nstdout:\n{stdOut.TrimEnd()}"))
    {
        Command = command;
        ExitCode = exitCode;
        StdOut = stdOut;
        StdErr = stdErr;
    }

    public IReadOnlyList<string> Command { get; }

    public int? ExitCode { get; }

    public string StdOut { get; }

    public string StdErr { get; }
}
