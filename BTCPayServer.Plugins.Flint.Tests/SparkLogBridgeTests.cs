using Breez.Sdk.Spark;
using BTCPayServer.Plugins.Flint.Sdk;
using BTCPayServer.Plugins.Flint.Tests.Fakes;
using NBitcoin;
using Microsoft.Extensions.Logging;
using Xunit;
using BitcoinNetwork = NBitcoin.Network;

namespace BTCPayServer.Plugins.Flint.Tests;

/// <summary>
/// Nothing credential-shaped may reach the operator's log through the SDK's log bridge.
/// </summary>
/// <remarks>
/// <para>
/// The bridge forwards the Rust SDK's own lines into BTCPay's logger, and the same subscriber writes them to
/// <c>&lt;DataDir&gt;/Plugins/Spark/logs/sdk.log</c> — at the process umask, observed as 0644. What the SDK
/// emits was measured against a throwaway regtest wallet: at <c>info</c> and <c>debug</c> nothing secret
/// appeared, and at <c>trace</c> the service provider's GraphQL <c>session_token</c> did, in full, twice per
/// authentication.
/// </para>
/// <para>
/// The probe could not cover everything — an unfunded wallet never produces the lines a completed payment
/// would, which are the ones a preimage would ride on. So these tests are written against the lines that were
/// <em>not</em> observable as much as the ones that were, using the names the SDK's own SQLite schema uses.
/// </para>
/// </remarks>
public class SparkLogBridgeTests
{
    /// <summary>
    /// The SDK instance a log line names. 0.28 made it a required <c>LogEntry</c> parameter; the bridge never
    /// reads it, so any stable value stands in.
    /// </summary>
    private const string InstanceId = "flint-test-instance";

    /// <summary>
    /// The real leak, in the shape it was actually observed in.
    /// </summary>
    /// <remarks>
    /// Copied from the probe's own output rather than invented, minus the token itself. It is a live bearer
    /// credential for the merchant's wallet against the service provider.
    /// </remarks>
    [Fact]
    public void The_service_providers_session_token_never_reaches_the_log()
    {
        const string token = "eyJ1aWQiOiIwMTlmZDQ5Ny03Yjc3LTZlZDgifQ.anPdDQ.9noMLLWPhHNXSkl3YtayTfpEtMbvonj";
        var line =
            "raw response body: {\"data\": {\"verify_challenge\": {\"valid_until\": \"2026-08-06T01:12:05+00:00\", "
            + $"\"session_token\": \"{token}\"}}}}";

        var forwarded = Forward(line, "trace");

        Assert.DoesNotContain(token, forwarded);
        Assert.Contains(SparkLogScrubber.Redacted, forwarded);
    }

    /// <summary>
    /// A preimage, in the two shapes the SDK's own types would produce one in.
    /// </summary>
    /// <remarks>
    /// Unobserved rather than invented: <c>payment_details_lightning</c> has a <c>preimage</c> column and the
    /// Rust <c>Payment</c> type carries it, so a debug line dumping either would print it. The probe wallet
    /// was never paid, so this is the gap the redaction exists to cover.
    /// </remarks>
    [Theory]
    [InlineData("PaymentDetails { preimage: Some(\"PREIMAGE\"), payment_hash: \"abc\" }")]
    [InlineData("{\"preimage\": \"PREIMAGE\"}")]
    [InlineData("preimage=PREIMAGE")]
    [InlineData("payment_details_lightning row: invoice=lnbcrt1..., preimage: PREIMAGE")]
    // Rust's tracing can format a field through Display instead of as a key/value pair, which separates the
    // name from the value with a space rather than a colon. Requiring `:` or `=` let this shape through.
    [InlineData("received preimage PREIMAGE")]
    [InlineData("claiming with preimage PREIMAGE for payment 4b1f")]
    public void A_preimage_never_reaches_the_log(string template)
    {
        const string preimage = "9f2c1b7a4e8d0356f1a9c4b28e7d05316a4f9c2b8e7d0531f4a9c2b8e7d05314";

        var forwarded = Forward(template.Replace("PREIMAGE", preimage), "debug");

        Assert.DoesNotContain(preimage, forwarded);
    }

    /// <summary>
    /// A recovery phrase, both labelled and bare.
    /// </summary>
    /// <remarks>
    /// The bare case is why the wordlist check exists: a phrase can appear with no name attached to it — in a
    /// panic message, an error string, a struct printed without field names — and by shape alone twelve
    /// consecutive BIP39 words are unmistakable.
    /// </remarks>
    [Fact]
    public void A_recovery_phrase_never_reaches_the_log()
    {
        var mnemonic = new Mnemonic(Wordlist.English, WordCount.Twelve).ToString();
        var firstWord = mnemonic.Split(' ')[0];

        var labelled = Forward($"connecting with mnemonic=\"{mnemonic}\"", "debug");
        Assert.DoesNotContain(mnemonic, labelled);

        var bare = Forward($"failed to derive from {mnemonic} on regtest", "debug");
        Assert.DoesNotContain(mnemonic, bare);

        // Belt and braces: not merely reformatted somewhere in the middle.
        Assert.DoesNotContain($"{firstWord} ", bare[bare.IndexOf("from", StringComparison.Ordinal)..]);
    }

    [Fact]
    public void An_extended_private_key_never_reaches_the_log()
    {
        var xprv = new ExtKey().GetWif(BitcoinNetwork.Main).ToString();
        Assert.StartsWith("xprv", xprv);

        Assert.DoesNotContain(xprv, Forward($"restoring from {xprv}", "debug"));
    }

    [Theory]
    [InlineData("api_key=abcdef0123456789")]
    [InlineData("\"private_key\": \"abcdef0123456789\"")]
    [InlineData("signing_key: abcdef0123456789")]
    [InlineData("authorization: Bearer abcdef0123456789")]
    public void Other_credential_names_have_their_values_removed(string line)
    {
        Assert.DoesNotContain("abcdef0123456789", Forward(line, "debug"));
    }

    /// <summary>
    /// A header credential is redacted on whichever line of the text it is on, and the lines around it survive.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>HeaderCredential</c> ends in <c>$</c> without <c>Multiline</c>, which means the end of the text, so on its
    /// own it only ever matched on the last line: an <c>authorization: Bearer …</c> line with anything after it came
    /// through whole, token included, and a second trailing newline was enough on its own. Both of the scrubber's
    /// exits were open — this log, and every merchant-facing error <c>SparkErrors.Describe</c> relays — until
    /// <c>HeaderCredentialOnEveryLine</c> was added as the scrubber's last pass.
    /// </para>
    /// <para>
    /// Exact outputs rather than only "the token is gone", because the other half of the rule is that a redaction
    /// ends with its line. On <c>\r\n</c> text the carriage return goes with the redacted value — to <c>.</c> it is
    /// ordinary text, and one character too many is the direction the scrubber errs in — while the line break
    /// itself survives.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("authorization: Bearer TOKEN\nnext line", $"authorization: {SparkLogScrubber.Redacted}\nnext line")]
    [InlineData("authorization: Bearer TOKEN\r\nnext line", $"authorization: {SparkLogScrubber.Redacted}\nnext line")]
    [InlineData("authorization: Bearer TOKEN\n", $"authorization: {SparkLogScrubber.Redacted}\n")]
    [InlineData("authorization: Bearer TOKEN\n\n", $"authorization: {SparkLogScrubber.Redacted}\n\n")]
    [InlineData(
        "request headers:\ncookie: sid=TOKEN\nauthorization: Bearer TOKEN\ncontent-type: application/json",
        $"request headers:\ncookie: {SparkLogScrubber.Redacted}\nauthorization: {SparkLogScrubber.Redacted}\n"
        + "content-type: application/json")]
    [InlineData(
        "set-cookie: session=TOKEN; Path=/\r\ncontent-length: 0\r\n",
        $"set-cookie: {SparkLogScrubber.Redacted}\ncontent-length: 0\r\n")]
    public void A_header_credential_is_redacted_on_whichever_line_it_is_on(string template, string expected)
    {
        const string token = "eyJ1aWQiOiIwMTlmZDQ5Ny03Yjc3LTZlZDgifQ.anPdDQ.9noMLLWPhHNXSkl3YtayTfpEtMbvonj";

        var forwarded = Forward(template.Replace("TOKEN", token), "debug");

        Assert.DoesNotContain(token, forwarded);
        Assert.Equal(expected, forwarded);
    }

    /// <summary>
    /// A line that ends in a credential name runs on into the next line, which is its value.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberate. A folded header or a YAML-style dump puts the value on the line below the name — below the header
    /// name, or below the <c>Bearer</c> that starts the value — and stopping at the line break would forward it. So
    /// when nothing but spaces and a colon follow the last credential name on a line, the redaction carries on to the
    /// next line with anything on it, as many times as that holds; a line with anything else after its last name
    /// ends the redaction there. The fold after <c>Bearer</c> is the one a simpler fix gets wrong: the anchored
    /// pattern caught it on the last lines of the text, and "the rest of the line" alone would let the token through.
    /// </para>
    /// <para>
    /// The last case is the price, pinned so that it is paid knowingly: a sentence ending in one of these words loses
    /// the line after it. It is the trade <c>SparkErrors.Describe</c> already names for this pattern — a truncated
    /// sentence beats a leaked token.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("authorization:\n    Bearer TOKEN\nnext line", $"authorization: {SparkLogScrubber.Redacted}\nnext line")]
    [InlineData("authorization: Bearer\n    TOKEN\nnext line", $"authorization: {SparkLogScrubber.Redacted}\nnext line")]
    [InlineData("authorization: Bearer\r\n    TOKEN\r\nnext line", $"authorization: {SparkLogScrubber.Redacted}\nnext line")]
    [InlineData("authorization:\n  Bearer\n  TOKEN\nnext line", $"authorization: {SparkLogScrubber.Redacted}\nnext line")]
    [InlineData("the service rejected the bearer\nretrying in 5s", $"the service rejected the bearer: {SparkLogScrubber.Redacted}")]
    public void A_line_that_ends_in_a_credential_name_runs_on_into_the_next_line(string template, string expected)
    {
        const string token = "eyJ1aWQiOiIwMTlmZDQ5Ny03Yjc3LTZlZDgifQ.anPdDQ.9noMLLWPhHNXSkl3YtayTfpEtMbvonj";

        var forwarded = Forward(template.Replace("TOKEN", token), "debug");

        Assert.DoesNotContain(token, forwarded);
        Assert.Equal(expected, forwarded);
    }

    /// <summary>
    /// A recovery phrase broken across a header line is redacted whole.
    /// </summary>
    /// <remarks>
    /// Pins why <c>HeaderCredentialOnEveryLine</c> runs last instead of taking <c>HeaderCredential</c>'s place at the
    /// front. Every pass works on what the ones before it left: redact the header line first and the phrase's first
    /// half goes with it, leaving six words the phrase scan no longer recognises as a phrase — half a wallet's recovery
    /// phrase in the log, where before the fix the scan had caught all twelve across the line break. Run last, the
    /// scan sees the phrase whole, and the header's value goes after. Twelve fixed wordlist words rather than a
    /// generated phrase, so a failure reproduces; the scan checks membership, not the checksum.
    /// </remarks>
    [Fact]
    public void A_recovery_phrase_broken_across_a_header_line_is_redacted_whole()
    {
        const string line = "cookie: session=1; abandon ability able about above absent\n"
                            + "absorb abstract absurd abuse access accident";

        Assert.Equal($"cookie: {SparkLogScrubber.Redacted}", Forward(line, "debug"));
    }

    /// <summary>
    /// The other exit an SDK payload has: error text a merchant is shown.
    /// </summary>
    /// <remarks>
    /// <c>SparkErrors.Describe</c> relays the SDK's own payload to banners, Greenfield validation
    /// bodies, stored sweep errors and claim outcome messages — none of which stand in front of
    /// the bridge above. The scrubbing therefore also sits at <c>Describe</c>'s choke point, and
    /// this is what pins that it does: an SDK error whose payload carries the shape the trace-level
    /// leak was observed in must come out with the value replaced and the diagnosis intact.
    /// </remarks>
    [Fact]
    public void An_SDK_error_relayed_to_a_merchant_is_scrubbed()
    {
        const string token = "eyJ1aWQiOiIwMTlmZDQ5Ny03Yjc3LTZlZDgifQ.anPdDQ.9noMLLWPhHNXSkl3YtayTfpEtMbvonj";

        var described = SparkErrors.Describe(new SdkException.SparkException(
            $"@v1=Tree service error: verify_challenge rejected session_token: \"{token}\""));

        Assert.DoesNotContain(token, described);
        Assert.Contains(SparkLogScrubber.Redacted, described);

        // Not merely redacted: still the error the merchant needs to see, prefix stripped.
        Assert.Contains("Tree service error: verify_challenge rejected session_token:", described);
        Assert.DoesNotContain("@v1=", described);
    }

    /// <summary>
    /// The same exit, with a header credential on a line that is not the last.
    /// </summary>
    /// <remarks>
    /// SDK error text is free text from the SDK and its service providers: nothing keeps it to one line, and nothing
    /// between it and a merchant's banner splits it into lines. So the rule the bridge above pins for the log holds
    /// here too — the credential's value goes, and the lines on either side of it are the diagnosis and stay.
    /// </remarks>
    [Fact]
    public void A_multi_line_SDK_error_relayed_to_a_merchant_has_its_header_credential_redacted()
    {
        const string token = "eyJ1aWQiOiIwMTlmZDQ5Ny03Yjc3LTZlZDgifQ.anPdDQ.9noMLLWPhHNXSkl3YtayTfpEtMbvonj";

        var described = SparkErrors.Describe(new SdkException.NetworkException(
            $"@v1=request failed\nauthorization: Bearer {token}\nstatus: 401 Unauthorized"));

        Assert.DoesNotContain(token, described);
        Assert.Equal(
            $"Spark network error: request failed\nauthorization: {SparkLogScrubber.Redacted}\nstatus: 401 Unauthorized",
            described);
    }

    /// <summary>
    /// The other half: the lines that carry the diagnostic value must survive intact.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Without this, "redact everything" would pass every test above, and the operator would be left with a
    /// log that cannot answer the question it exists for. All four are real lines from the probe.
    /// </para>
    /// <para>
    /// The DDL cases matter specifically: the SDK's migrations <em>mention</em> <c>preimage</c> — as a column
    /// name and inside a <c>json_extract</c> path — and a redaction keyed on the word rather than on an
    /// assignment to it would blank them.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("Running: ALTER TABLE lnurl_receive_metadata ADD COLUMN preimage TEXT;")]
    [InlineData("json_extract(details, '$.Lightning.preimage')")]
    [InlineData("Created new connection to operator: https://0.spark.lightspark.com")]
    [InlineData("Calling query_nodes with request: QueryNodesRequest { include_parents: false, limit: 100 }")]
    public void Ordinary_diagnostic_lines_are_forwarded_unchanged(string line)
    {
        Assert.Equal(line, Forward(line, "debug"));
    }

    /// <summary>
    /// A 64-hex run with no sensitive name in front of it survives, whatever separator precedes it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This pins the boundary that makes <c>SensitiveHexValue</c> safe to have at all. Accepting whitespace
    /// between a sensitive name and a 64-hex value is <em>name-keyed</em>; widening it to the shape alone is
    /// the rule <c>SparkLogScrubber</c>'s remarks reject at length, because a preimage, a payment hash, a txid
    /// and a public key's x-coordinate are all 32 bytes of hex and only one of them is a secret.
    /// </para>
    /// <para>
    /// It is a test rather than a comment because the two regexes differ by one token, and the wider one
    /// passes every other test in this file. Without this, someone tidying the pattern into a single shape
    /// rule would blank the txid a sweep sitting at <em>Sent</em> carries as the only handle on funds in
    /// flight, and nothing would object.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("broadcast txid e9946fb8351db1e27bba015f3f3e099ad3de46e91678482222f5a238cb654bca0")]
    [InlineData("payment_hash: 84e9d10684e9d10684e9d10684e9d10684e9d10684e9d10684e9d10684e9d106")]
    [InlineData("identity 02b79ba94ef9bc75b657c57c49781e57cb63ee2bd831297ef684b1bdb58de1bd")]
    public void A_hex_identifier_with_no_sensitive_name_is_left_alone(string line)
    {
        Assert.Equal(line, Forward(line, "debug"));
    }

    /// <summary>
    /// An English sentence of twelve short words is not a recovery phrase.
    /// </summary>
    /// <remarks>
    /// The wordlist check earns its keep here. Redacting on the twelve-lowercase-words pattern alone would
    /// silently eat prose, and a scrubber that eats prose is one an operator turns off.
    /// </remarks>
    [Fact]
    public void A_long_sentence_of_ordinary_words_is_not_mistaken_for_a_phrase()
    {
        const string line =
            "the node did not send the data that the peer had asked for and then the link went away again";

        Assert.Equal(line, Forward(line, "debug"));
    }

    [Fact]
    public void A_line_the_SDK_reports_with_no_text_does_not_throw()
    {
        // Log is a UniFFI callback: throwing back into it is how the process deadlocks.
        var log = new CapturingLogger<SparkLogBridgeTests>();
        var bridge = new SparkLogBridge(log);

        bridge.Log(new LogEntry(null!, "debug", InstanceId));
        bridge.Log(new LogEntry("", null!, InstanceId));

        Assert.Equal(2, log.Lines.Count);
    }

    /// <summary>
    /// A filter that would turn the Rust subscriber up to trace is refused.
    /// </summary>
    /// <remarks>
    /// This is the guard that actually protects the <em>file</em>. Scrubbing happens on the C# bridge, but
    /// <c>sdk.log</c> is written by the Rust subscriber and never passes through it, so the only way to keep
    /// a session token out of it is to never ask for the level that emits one.
    /// </remarks>
    [Theory]
    [InlineData("trace")]
    [InlineData("TRACE")]
    [InlineData("info,breez_sdk_spark=trace")]
    [InlineData("spark::ssp=trace,info")]
    public void A_trace_filter_is_refused(string requested)
    {
        var log = new CapturingLogger<SparkLogBridgeTests>();

        var applied = SparkLogging.ClampFilter(requested, log);

        Assert.Equal(SparkLogging.MaxFilter, applied);
        Assert.Contains("session token", log.AllText);
    }

    [Theory]
    [InlineData("info")]
    [InlineData("debug")]
    [InlineData("warn")]
    [InlineData("info,breez_sdk_spark=debug")]
    public void A_filter_that_does_not_reach_trace_is_left_alone(string requested)
    {
        var log = new CapturingLogger<SparkLogBridgeTests>();

        Assert.Equal(requested, SparkLogging.ClampFilter(requested, log));
        Assert.Empty(log.Lines);
    }

    /// <summary>
    /// A filter nobody supplied falls back to the quiet level, not to the clamp's ceiling.
    /// </summary>
    /// <remarks>
    /// The clamp exists because verbosity is what put a live session token on disk, so resolving "no filter
    /// was asked for" into the most verbose level the plugin tolerates is the one direction the fallback must
    /// not lean. Production is unaffected either way — <c>Constants.SdkLogFilter</c> is always passed — which
    /// is exactly why this needs a test rather than a reviewer noticing.
    /// </remarks>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void A_filter_that_was_never_supplied_falls_back_to_the_quiet_level(string? requested)
    {
        var log = new CapturingLogger<SparkLogBridgeTests>();

        var applied = SparkLogging.ClampFilter(requested, log);

        Assert.Equal(SparkLogging.DefaultFilter, applied);
        Assert.NotEqual(SparkLogging.MaxFilter, applied);
    }

    /// <summary>
    /// The fallback and the filter the plugin actually ships are the same level.
    /// </summary>
    /// <remarks>
    /// Two constants describing one intention drift apart silently otherwise: lowering
    /// <c>Constants.SdkLogFilter</c> without lowering the fallback would leave the forgotten-argument path
    /// louder than the configured one, which is the whole defect this pins.
    /// </remarks>
    [Fact]
    public void The_fallback_is_the_level_the_plugin_ships()
    {
        Assert.Equal(Constants.SdkLogFilter, SparkLogging.DefaultFilter);
    }

    /// <summary>
    /// Initialisation applies the clamp, rather than merely having one available.
    /// </summary>
    /// <remarks>
    /// Separate from the <c>ClampFilter</c> tests above on purpose: a clamp nothing calls is not a guard, and
    /// the difference between the two is exactly one deleted line.
    /// </remarks>
    [Fact]
    public void Initialisation_installs_the_clamped_filter_and_not_the_requested_one()
    {
        using var dir = new TempDirectory();
        var log = new CapturingLogger<SparkLogBridgeTests>();
        string? installed = null;

        var ok = SparkLogging.Initialise(
            dir.Path, log, "info,breez_sdk_spark=trace", (_, _, filter) => installed = filter);

        Assert.True(ok);
        Assert.Equal(SparkLogging.MaxFilter, installed);
    }

    // The log directory's mode is asserted in SparkStorageDirectoryPermissionTests, beside the per-store
    // storage directory's. They were separate once, and that is precisely how storage came to be the one
    // directory nobody hardened.

    [Fact]
    public void The_shipped_filter_is_one_that_survives_the_clamp()
    {
        // Pins the constant against the guard rather than restating it: bumping SdkLogFilter to something the
        // plugin would then refuse should be a failure here, not a surprise on a live server.
        var log = new CapturingLogger<SparkLogBridgeTests>();

        Assert.Equal(Constants.SdkLogFilter, SparkLogging.ClampFilter(Constants.SdkLogFilter, log));
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "spark-log-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>Puts a line through the real bridge and returns what the logger received.</summary>
    private static string Forward(string line, string level)
    {
        var log = new CapturingLogger<SparkLogBridgeTests>();
        new SparkLogBridge(log).Log(new LogEntry(line, level, InstanceId));

        var entry = Assert.Single(log.Lines);
        var marker = "spark: ";
        return entry[(entry.IndexOf(marker, StringComparison.Ordinal) + marker.Length)..];
    }

    /// <summary>
    /// The cost check, and its redaction corollary: a line the logger would discard must not be paid for
    /// (six scrub regexes on an SDK callback thread), and the lines that ARE emitted must still scrub —
    /// the gate must not become a way to skip redaction for anything that actually reaches the log.
    /// </summary>
    [Fact]
    public void Lines_below_the_effective_level_are_dropped_before_the_scrub()
    {
        var log = new ThresholdLogger(LogLevel.Warning);
        var bridge = new SparkLogBridge(log);

        // SDK "info" → Information, "debug" → Debug, "trace" → Trace: all below the threshold.
        bridge.Log(new LogEntry("sync completed", "info", InstanceId));
        bridge.Log(new LogEntry("dumping payment row with preimage abc", "debug", InstanceId));
        bridge.Log(new LogEntry("graphql session_token: deadbeef", "trace", InstanceId));
        Assert.Empty(log.Lines);

        // "warn" stays Warning, and "error" maps DOWN to Warning — both at or above the threshold, so
        // both are forwarded, and the secret among them is scrubbed on the way.
        bridge.Log(new LogEntry("retrying connect", "warn", InstanceId));
        bridge.Log(new LogEntry("session_token: deadbeef", "error", InstanceId));
        Assert.Equal(2, log.Lines.Count);
        Assert.Contains("retrying connect", log.AllText);
        Assert.DoesNotContain("deadbeef", log.AllText);
        Assert.Contains(SparkLogScrubber.Redacted, log.AllText);
    }

    private sealed class ThresholdLogger(LogLevel minimum) : ILogger
    {
        private readonly List<string> _lines = [];

        public IReadOnlyList<string> Lines => _lines;
        public string AllText => string.Join('\n', _lines);

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= minimum;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) => _lines.Add(formatter(state, exception));

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
