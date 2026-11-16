using System;
using System.Globalization;
using Breez.Sdk.Spark;

namespace BTCPayServer.Plugins.Flint.Sdk;

/// <summary>
/// Turns SDK exceptions into text fit for a merchant, and classifies the ones we act on.
/// </summary>
/// <remarks>
/// <para>
/// The SDK's exceptions are UniFFI-generated: the payload sits in a public field named <c>v1</c> and
/// <c>Message</c> is synthesised as <c>"@v1=Tree service error: insufficient funds"</c>, prefix and
/// all. Never surface <c>ex.Message</c> directly (spike notes §12).
/// </para>
/// <para>
/// Note also that not every failure is an <c>SdkException</c>: the C# binding layer itself throws
/// <c>ArgumentNullException</c> for a null description, so callers must catch <c>Exception</c> at the
/// <c>ILightningClient</c> boundary rather than <c>SdkException</c>.
/// </para>
/// </remarks>
public static class SparkErrors
{
    public static string Describe(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        // Scrubbed at the choke point. What leaves this method is free text from the SDK and its
        // service providers, and every one of its sinks is merchant-facing — TempData banners,
        // Greenfield validation bodies, stored `SweepRecord.Error`s, claim outcome messages —
        // none of them standing behind the log bridge's scrubbing. The rules that cover the
        // operator's log cover the merchant's error by standing here; scrubbing at each call
        // site instead would be forty chances to forget one.
        // Two trades taken knowingly: the scrubber's header-credential patterns eat to end-of-line
        // on an authorization/bearer/cookie word, and on into the next line when the word ends its
        // own (fail-closed — a truncated sentence beats a leaked token), and RedactPhrases may run
        // the Bip39English static ctor inside a catch handler (low risk: it only loads an embedded
        // wordlist).
        // Before the type switch, because the downgrade refusal arrives as whatever the SDK's storage layer
        // wraps it in — a StorageException today — and its own text ("rusqlite_migrate error:
        // MigrationDefinition(DatabaseTooFarAhead)") says nothing an operator can act on.
        if (IsStorageFromNewerVersion(exception))
            return StorageFromNewerVersion;

        var merchantFacing = exception switch
        {
            SdkException.InsufficientFunds => "Insufficient Spark balance.",
            SdkException.SparkException spark => Strip(spark.v1),
            SdkException.InvalidInput invalid => Strip(invalid.v1),
            SdkException.NetworkException network => $"Spark network error: {Strip(network.v1)}",
            SdkException.StorageException storage => $"Spark storage error: {Strip(storage.v1)}",
            SdkException.ChainServiceException chain => $"Bitcoin chain service error: {Strip(chain.v1)}",
            SdkException.LnurlException lnurl => $"LNURL error: {Strip(lnurl.v1)}",
            SdkException.Signer signer => $"Spark signer error: {Strip(signer.v1)}",
            SdkException.InvalidUuid uuid => $"Invalid identifier: {Strip(uuid.v1)}",
            SdkException.Generic generic => Strip(generic.v1),
            // The two typed cross-chain refusals (0.26) carry the provider's own reason in a named field rather
            // than v1, and the reason is the whole message: which bound an amount missed, or that a route is down.
            SdkException.CrossChainAmountOutOfRange outOfRange => Strip(outOfRange.reason),
            SdkException.CrossChainRouteUnavailable unavailable => Strip(unavailable.reason),
            SdkException.DepositClaimInProgress =>
                "A claim for this deposit is already in progress. Nothing more is needed; check the balance shortly.",
            SdkException.InsufficientCpfpFunds shortfall => DescribeCpfpShortfall(ToSats(shortfall.requiredSats)),
            // MissingUtxo and MaxDepositClaimFeeExceeded carry several named fields rather than a
            // single v1, so there is nothing better to do than strip the synthesised prefix.
            SdkException => Strip(exception.Message),
            ObjectDisposedException => "The Spark wallet for this store is no longer running.",
            _ => Strip(exception.Message)
        };
        // The total-redaction fallback is this sink's own sentence, not the log bridge's: a banner
        // quoting a failed request should not gain a stray clause about SDK log lines.
        return SparkLogScrubber.Scrub(merchantFacing, "Spark reported an error that could not be shown safely.");
    }

    /// <summary>
    /// What an operator is told when a wallet's storage was written by a newer SDK than the one installed.
    /// </summary>
    /// <remarks>
    /// Plain words, and each clause answers a question the raw SDK text leaves open: what happened (a newer
    /// version wrote the storage), what to do (install that version or a newer one), and whether the money is
    /// at risk (it is not — the refusal happens before anything in the storage is touched, and the funds are
    /// held by the Spark operators against the seed, not by the file).
    /// </remarks>
    internal const string StorageFromNewerVersion =
        "This store's Spark wallet storage was written by a newer version of the Flint plugin (and its Spark "
        + "SDK) than the one installed now, so this version cannot open it. Install that version of the plugin "
        + "or a newer one to bring the wallet back. The wallet's funds are not affected.";

    /// <summary>
    /// True when the SDK refused to open a store's storage because a newer SDK had already migrated it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is what a plugin downgrade looks like.</b> Once SDK 0.26 has opened a wallet's SQLite storage it
    /// has migrated the schema forward, and an older SDK's migration runner refuses the file with
    /// <c>rusqlite_migrate error: MigrationDefinition(DatabaseTooFarAhead)</c>, surfaced through the storage
    /// layer's generic "implementation error" wrapping. Without this, the connect failure reads as a broken
    /// configuration — "unavailable until the configuration is corrected" — and sends an operator hunting
    /// through settings that are fine, or worse, re-entering a seed.
    /// </para>
    /// <para>
    /// Matched on the marker text anywhere in the exception chain, whatever the exception type: the wrapping
    /// is an SDK detail that has moved between versions, the variant name is what the migration crate itself
    /// prints, and a type check would silently stop matching the day the SDK re-wraps it. It only ever helps
    /// a downgrade <em>from</em> a version that ships this check, which is the reason to ship it now.
    /// </para>
    /// </remarks>
    public static bool IsStorageFromNewerVersion(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        for (var current = exception; current is not null; current = current.InnerException)
        {
            var text = current switch
            {
                SdkException.StorageException storage => storage.v1,
                SdkException.Generic generic => generic.v1,
                SdkException.SparkException spark => spark.v1,
                _ => null
            };

            if (ContainsTooFarAhead(text) || ContainsTooFarAhead(current.Message))
                return true;
        }

        return false;

        static bool ContainsTooFarAhead(string? text) =>
            text?.Contains("DatabaseTooFarAhead", StringComparison.OrdinalIgnoreCase) is true;
    }

    /// <summary>
    /// True when the failure means "not enough sats".
    /// </summary>
    /// <remarks>
    /// The typed <c>SdkException.InsufficientFunds</c> variant exists but was never observed being
    /// thrown: an unfunded send surfaces as <c>SparkException: @v1=Tree service error: insufficient
    /// funds</c>. Both are matched, the second one by substring, because there is no alternative.
    /// </remarks>
    public static bool IsInsufficientFunds(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception is SdkException.InsufficientFunds ||
               (exception is SdkException.SparkException spark &&
                spark.v1?.Contains("insufficient funds", StringComparison.OrdinalIgnoreCase) is true);
    }

    /// <summary>
    /// True when the SDK rejected the request locally, before doing anything.
    /// </summary>
    /// <remarks>
    /// These are client-side validations that cost 0 ms and definitively mean nothing was sent — an amount
    /// below the 294-sat dust floor for the destination's script type, a malformed address, an unsupported
    /// payment method. Distinguishing them from a network failure is what lets a caller say "safe to retry
    /// differently" instead of "state unknown".
    /// </remarks>
    public static bool IsInvalidInput(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception is SdkException.InvalidInput;
    }

    /// <summary>
    /// True when a cooperative-exit fee quote has expired and the send must be re-prepared.
    /// </summary>
    /// <remarks>
    /// A prepared bitcoin-address response is valid for only ~60 seconds, so this is a normal condition
    /// rather than a failure to report: the caller re-prepares and tries again. It arrives as prose inside
    /// a <c>SparkException</c> ("The coop exit fee quote has expired, please request a new quote"), so
    /// there is no typed variant to match on. Used by the sweep path.
    /// </remarks>
    public static bool IsExpiredFeeQuote(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception is SdkException.SparkException spark &&
               spark.v1?.Contains("fee quote has expired", StringComparison.OrdinalIgnoreCase) is true;
    }

    /// <summary>
    /// True when a bridge provider refused an amount as below its own minimum.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A normal outcome, not a fault.</b> The cross-chain minimum is enforced server-side by the provider and
    /// the SDK exposes no getter for it — the spike had to binary-search it, finding a floor somewhere between
    /// 1,000 and 1,500 satoshi — so "too small" is only discoverable by attempting a prepare, and arrives as a
    /// <c>NetworkException</c> carrying the provider's own prose (<c>Amount too small (code: 400)</c>).
    /// </para>
    /// <para>
    /// Matched by substring because there is no typed variant and no error code on the C# side. A caller that
    /// treated this as a network failure would report the provider as unreachable to a merchant whose only
    /// problem is a small balance.
    /// </para>
    /// </remarks>
    public static bool IsAmountTooSmall(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception is SdkException.NetworkException network &&
               network.v1?.Contains("amount too small", StringComparison.OrdinalIgnoreCase) is true;
    }

    /// <summary>
    /// The typed cross-chain amount refusal (0.26), read into the plugin's own terms; null for anything else.
    /// </summary>
    /// <remarks>
    /// The SDK carries which bound was missed and the bound itself as fields, so nothing here parses the
    /// provider's prose — whose wording ("Increase the input amount") is addressed to the integrator in any case.
    /// </remarks>
    public static SparkAmountOutOfRange? AmountOutOfRange(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception is SdkException.CrossChainAmountOutOfRange outOfRange
            ? new SparkAmountOutOfRange(outOfRange.tooSmall, outOfRange.boundAmount, outOfRange.boundUsdCents)
            : null;
    }

    /// <summary>
    /// For the typed "provider won't serve this route" refusal (0.26): whether the provider expects the route back
    /// shortly. Null for anything else.
    /// </summary>
    public static bool? RouteUnavailable(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception is SdkException.CrossChainRouteUnavailable unavailable ? unavailable.temporary : null;
    }

    /// <summary>
    /// True when the SDK reported "no such row", which is how it reports "not found".
    /// </summary>
    /// <remarks>
    /// <c>GetPayment</c> on an unknown id throws <c>StorageException: @v1=Underlying implementation
    /// error: Query returned no rows</c> rather than returning null (spike notes §6).
    /// </remarks>
    public static bool IsNotFound(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception is SdkException.StorageException storage &&
               storage.v1?.Contains("no rows", StringComparison.OrdinalIgnoreCase) is true;
    }

    /// <summary>
    /// Turns the unilateral-exit-specific SDK error into a typed plugin exception, or returns null when the
    /// failure is something else.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>There is now exactly one of these, and there used to be two.</b> SDK 0.25 removed
    /// <c>SdkException.FundingUtxoConflict</c> along with the build shape that could produce it: a conflict is
    /// no longer reported as an error at all, because an earlier attempt's spent funding is followed to
    /// whatever it became rather than rejected, and fresh funding can be passed alongside the old. What
    /// remains is the shortfall, which a caller still has to act on and which needs the number the SDK
    /// provides — the figure to put in front of an operator who has to top a funding address up.
    /// </para>
    /// <para>
    /// Returns null rather than the original exception so a call site can use it as an exception filter and let
    /// everything else escape unchanged, with its original stack.
    /// </para>
    /// </remarks>
    public static Exception? TranslateUnilateralExit(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception switch
        {
            SdkException.InsufficientCpfpFunds shortfall =>
                new SparkExitFundingShortfallException(ToSats(shortfall.requiredSats), shortfall),
            _ => null
        };
    }

    internal static string DescribeCpfpShortfall(long requiredSat) => string.Format(
        CultureInfo.InvariantCulture,
        "There is not enough confirmed Bitcoin on the exit funding address to pay the exit's on-chain fees. "
        + "Spark needs at least {0:N0} sat available there, as a single confirmed output.",
        requiredSat);

    /// <remarks>
    /// Every amount on the exit surface is a <c>u64</c> of satoshi — no tokens, no base units, no
    /// <c>BigInteger</c> — so the only conversion hazard is the width, and it is clamped rather than wrapped:
    /// an absurd value must not come out the other side as a negative fee.
    /// </remarks>
    private static long ToSats(ulong value) => (long)Math.Min(value, long.MaxValue);

    private static string Strip(string? message)
    {
        if (string.IsNullOrEmpty(message))
            return "Unknown Spark error.";
        return message.StartsWith("@v1=", StringComparison.Ordinal) ? message[4..] : message;
    }
}

/// <summary>
/// Raised when a unilateral exit could not be built because its funding outputs do not cover the fees.
/// </summary>
/// <remarks>
/// <para>
/// <b>Recoverable, and the fix is a number.</b> A unilateral exit pays every one of its own on-chain fees from a
/// separate confirmed UTXO the operator supplies, because the coins being recovered are locked behind timelocks
/// and cannot pay for their own release. Under-funding it therefore fails the build rather than producing a
/// cheaper exit — and the SDK says what would have been enough, which is carried here so an operator is told
/// how much to add instead of being told to guess.
/// </para>
/// <para>
/// Nothing was built, signed or broadcast, so retrying after topping the address up is safe.
/// </para>
/// </remarks>
public sealed class SparkExitFundingShortfallException : InvalidOperationException
{
    public SparkExitFundingShortfallException(long requiredSat, Exception? innerException = null)
        : base(SparkErrors.DescribeCpfpShortfall(requiredSat), innerException)
    {
        RequiredSat = requiredSat;
    }

    /// <summary>What the SDK said the exit needs, in satoshi, as a single confirmed output.</summary>
    public long RequiredSat { get; }
}
