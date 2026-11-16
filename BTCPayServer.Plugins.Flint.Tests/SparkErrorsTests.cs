using Breez.Sdk.Spark;
using BTCPayServer.Plugins.Flint.Sdk;
using Xunit;

namespace BTCPayServer.Plugins.Flint.Tests;

/// <summary>
/// Error translation. The SDK's exceptions are UniFFI-generated, with the payload in a public field named
/// <c>v1</c> and <c>Message</c> synthesised as <c>"@v1=…"</c>. None of that may reach a merchant.
/// </summary>
public class SparkErrorsTests
{
    [Fact]
    public void Describe_strips_the_UniFFI_prefix()
    {
        var described = SparkErrors.Describe(new SdkException.SparkException("@v1=Tree service error: no funds"));

        Assert.Equal("Tree service error: no funds", described);
    }

    [Fact]
    public void Describe_never_returns_a_prefixed_message_for_any_SDK_error()
    {
        Exception[] errors =
        [
            new SdkException.SparkException("@v1=spark"),
            new SdkException.InvalidInput("@v1=bad input"),
            new SdkException.NetworkException("@v1=offline"),
            new SdkException.StorageException("@v1=no rows"),
            new SdkException.ChainServiceException("@v1=chain"),
            new SdkException.LnurlException("@v1=lnurl"),
            new SdkException.Signer("@v1=signer"),
            new SdkException.InvalidUuid("@v1=uuid"),
            new SdkException.Generic("@v1=generic"),
            // tokenIdentifier is null for a sat shortfall, set only when a token balance is short.
            new SdkException.InsufficientFunds(null),
            new SdkException.MissingUtxo("tx", 0),
            new SdkException.OptimizationAlreadyRunning()
        ];

        foreach (var error in errors)
        {
            var described = SparkErrors.Describe(error);
            Assert.False(string.IsNullOrWhiteSpace(described));
            Assert.DoesNotContain("@v1=", described);
        }
    }

    /// <summary>The exit's CPFP shortfall names the amount the funding address needs, in sats.</summary>
    /// <remarks>
    /// The variant carries the amount in a named field rather than <c>v1</c>, so without its own arm it would fall
    /// through to the synthesised message and the operator would lose the one number they have to act on.
    /// </remarks>
    [Fact]
    public void Describe_gives_the_CPFP_shortfall_as_the_sats_the_funding_address_needs()
    {
        // SDK 0.28 added a trailing docsUrl to this variant; the plugin's description reads only the shortfall,
        // so null is the truthful value here.
        var described = SparkErrors.Describe(new SdkException.InsufficientCpfpFunds(1_234, docsUrl: null));

        Assert.Contains("at least 1,234 sat", described);
        Assert.DoesNotContain("@v1=", described);
    }

    /// <summary>A disposed SDK instance is described in plain words, not with the SDK's own text.</summary>
    /// <remarks>
    /// This once failed now and then in a full parallel run, with "Spark reported an error that could not be shown
    /// safely." in place of the message: the scrubber's patterns carried a wall-clock match timeout, and a busy
    /// machine tripped it. <c>SparkLogScrubberTests</c> is where that regression is reproduced and pinned.
    /// </remarks>
    [Fact]
    public void Describe_gives_a_plain_message_for_a_disposed_instance()
    {
        var described = SparkErrors.Describe(new ObjectDisposedException("BreezSdk"));

        Assert.Contains("no longer running", described);
    }

    [Fact]
    public void Describe_handles_a_null_payload()
    {
        Assert.False(string.IsNullOrWhiteSpace(SparkErrors.Describe(new SdkException.SparkException(null!))));
    }

    [Fact]
    public void IsInsufficientFunds_matches_the_typed_variant_and_the_string_form()
    {
        // The typed variant was never actually observed being thrown; an unfunded send arrives as a
        // SparkException whose text contains "insufficient funds".
        Assert.True(SparkErrors.IsInsufficientFunds(new SdkException.InsufficientFunds(null)));
        // Since SDK 0.22.0 the variant carries the identifier of whichever balance was short; a token
        // shortfall must classify the same way, because the plugin only ever spends sats and would
        // otherwise report a token error as "state unknown".
        Assert.True(SparkErrors.IsInsufficientFunds(new SdkException.InsufficientFunds("btkn1exampletoken")));
        Assert.True(SparkErrors.IsInsufficientFunds(
            new SdkException.SparkException("@v1=Tree service error: insufficient funds")));
        Assert.False(SparkErrors.IsInsufficientFunds(new SdkException.SparkException("@v1=Invalid network")));
        Assert.False(SparkErrors.IsInsufficientFunds(new InvalidOperationException("insufficient funds")));
    }

    [Fact]
    public void IsInvalidInput_identifies_a_locally_rejected_request()
    {
        // Distinguishes "nothing was sent, safe to retry differently" from "state unknown".
        Assert.True(SparkErrors.IsInvalidInput(new SdkException.InvalidInput(
            "@v1=Amount is below the minimum of 294 sats required for this address")));
        Assert.False(SparkErrors.IsInvalidInput(new SdkException.NetworkException("@v1=timeout")));
        Assert.False(SparkErrors.IsInvalidInput(new TimeoutException()));
    }

    [Fact]
    public void IsExpiredFeeQuote_identifies_a_stale_coop_exit_quote()
    {
        // A ~60-second TTL makes this a routine condition to re-prepare through, not a failure to report.
        // It arrives as prose, so there is nothing typed to match on.
        Assert.True(SparkErrors.IsExpiredFeeQuote(new SdkException.SparkException(
            "@v1=Service error: service provider error: graphql error: The coop exit fee quote has expired, " +
            "please request a new quote.")));
        Assert.False(SparkErrors.IsExpiredFeeQuote(
            new SdkException.SparkException("@v1=Tree service error: insufficient funds")));
    }

    [Fact]
    public void IsNotFound_matches_the_no_rows_storage_error()
    {
        // GetPayment on an unknown id throws rather than returning null, and this is the only way to tell
        // "not found" from a genuine storage failure.
        Assert.True(SparkErrors.IsNotFound(
            new SdkException.StorageException("@v1=Underlying implementation error: Query returned no rows")));
        Assert.False(SparkErrors.IsNotFound(new SdkException.StorageException("@v1=Connection error")));
        Assert.False(SparkErrors.IsNotFound(new SdkException.SparkException("@v1=no rows")));
    }

    /// <summary>
    /// A downgrade's refusal to open storage a newer SDK migrated is described as exactly that.
    /// </summary>
    /// <remarks>
    /// The raw text — the migration crate's variant name inside the storage layer's wrapping — is what an
    /// operator saw before, beside a log line blaming the store's configuration. What they need instead is
    /// what happened, what to install, and that the money is safe; all three are pinned, and the SDK's own
    /// wording is pinned out.
    /// </remarks>
    [Fact]
    public void Describe_explains_storage_written_by_a_newer_version_in_plain_words()
    {
        var refusal = new SdkException.StorageException(
            "@v1=Underlying implementation error: rusqlite_migrate error: MigrationDefinition(DatabaseTooFarAhead)");

        Assert.True(SparkErrors.IsStorageFromNewerVersion(refusal));

        var described = SparkErrors.Describe(refusal);
        Assert.Contains("newer version", described);
        Assert.Contains("Install that version", described);
        Assert.Contains("funds are not affected", described);
        Assert.DoesNotContain("rusqlite", described);
        Assert.DoesNotContain("DatabaseTooFarAhead", described);
    }

    /// <summary>
    /// The marker is found whatever the SDK wraps it in, and nothing else is mistaken for it.
    /// </summary>
    [Fact]
    public void IsStorageFromNewerVersion_matches_the_marker_in_any_wrapping_and_nothing_else()
    {
        Assert.True(SparkErrors.IsStorageFromNewerVersion(
            new SdkException.Generic("@v1=rusqlite_migrate error: MigrationDefinition(DatabaseTooFarAhead)")));
        Assert.True(SparkErrors.IsStorageFromNewerVersion(
            new InvalidOperationException("connect failed",
                new SdkException.StorageException("@v1=MigrationDefinition(DatabaseTooFarAhead)"))));

        Assert.False(SparkErrors.IsStorageFromNewerVersion(
            new SdkException.StorageException("@v1=Underlying implementation error: Query returned no rows")));
        Assert.False(SparkErrors.IsStorageFromNewerVersion(
            new InvalidOperationException("the SDK refused this seed")));
    }
}
