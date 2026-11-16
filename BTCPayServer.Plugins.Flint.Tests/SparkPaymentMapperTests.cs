using System.Numerics;
using Breez.Sdk.Spark;
using BTCPayServer.Plugins.Flint.Sdk;
using BTCPayServer.Plugins.Flint.Tests.Fakes;
using Xunit;
using SdkPaymentStatus = Breez.Sdk.Spark.PaymentStatus;

namespace BTCPayServer.Plugins.Flint.Tests;

/// <summary>
/// Where the payment hash comes from, and how the SDK's amounts and timestamps are narrowed.
/// </summary>
/// <remarks>
/// The SDK's records are plain managed classes, so they can be constructed directly here without any
/// native calls or a live wallet.
/// </remarks>
public class SparkPaymentMapperTests
{
    private const string Hash = "f003f7557bae8ffda088b42ee758edd048ae8689f87f7de41d9fb3b132341238";

    private static Payment LightningReceive(
        SparkHtlcDetails? htlc,
        string? invoice = "lnbcrt-one",
        BigInteger? amount = null,
        SdkPaymentStatus status = SdkPaymentStatus.Completed) =>
        new(
            id: "1f3c9a20-0000-4000-8000-000000000001",
            paymentType: PaymentType.Receive,
            status: status,
            amount: amount ?? new BigInteger(1000),
            fees: BigInteger.Zero,
            timestamp: 1_785_806_574,
            method: PaymentMethod.Lightning,
            details: new PaymentDetails.Lightning(
                description: "spike test a",
                invoice: invoice!,
                destinationPubkey: "02fe4b",
                htlcDetails: htlc!,
                lnurlPayInfo: null,
                lnurlWithdrawInfo: null,
                lnurlReceiveMetadata: null,
                conversionInfo: null),
            conversionDetails: null!);

    [Fact]
    public void The_payment_hash_comes_from_the_HTLC_details_when_present()
    {
        var parser = new StubBolt11Parser();
        var payment = LightningReceive(new SparkHtlcDetails(Hash, "AA03F7", 0, SparkHtlcStatus.PreimageShared));

        var mapped = SparkPaymentMapper.Map(payment, parser);

        Assert.Equal(Hash, mapped.PaymentHash);
        Assert.Equal("aa03f7", mapped.Preimage);
        // The invoice was never parsed: the HTLC details already had the hash.
        Assert.Empty(parser.Calls);
    }

    [Fact]
    public void The_payment_hash_is_normalised_to_lower_case()
    {
        // PaymentHash is the primary key of the invoice table and the id BTCPay joins on; a case mismatch
        // would look exactly like an unknown invoice.
        var payment = LightningReceive(new SparkHtlcDetails(Hash.ToUpperInvariant(), null!, 0,
            SparkHtlcStatus.PreimageShared));

        var mapped = SparkPaymentMapper.Map(payment, new StubBolt11Parser());

        Assert.Equal(Hash, mapped.PaymentHash);
    }

    [Fact]
    public void The_invoice_is_parsed_when_the_HTLC_details_are_missing()
    {
        // htlcDetails is nullable on PaymentDetails.Lightning, so the invoice string is the fallback.
        var parser = new StubBolt11Parser()
            .Register("lnbcrt-one", new Bolt11Info(Hash, DateTimeOffset.UnixEpoch, 1_000_000));

        var mapped = SparkPaymentMapper.Map(LightningReceive(htlc: null), parser);

        Assert.Equal(Hash, mapped.PaymentHash);
        Assert.Equal("lnbcrt-one", Assert.Single(parser.Calls));
    }

    [Fact]
    public void An_unparseable_invoice_with_no_HTLC_details_yields_no_hash()
    {
        var mapped = SparkPaymentMapper.Map(LightningReceive(htlc: null), new StubBolt11Parser());

        // Not an exception: the caller logs a warning and leaves the funds unattributed, which is the only
        // honest outcome.
        Assert.Null(mapped.PaymentHash);
    }

    [Fact]
    public void A_blank_HTLC_payment_hash_falls_back_to_the_invoice()
    {
        var parser = new StubBolt11Parser()
            .Register("lnbcrt-one", new Bolt11Info(Hash, DateTimeOffset.UnixEpoch, null));
        var payment = LightningReceive(new SparkHtlcDetails("", null!, 0, SparkHtlcStatus.WaitingForPreimage));

        var mapped = SparkPaymentMapper.Map(payment, parser);

        Assert.Equal(Hash, mapped.PaymentHash);
    }

    [Fact]
    public void A_direct_Spark_transfer_uses_its_own_HTLC_details()
    {
        // A BOLT11 receive can settle as a Spark transfer, in which case there is no invoice string at all.
        var payment = new Payment(
            id: "spark-1",
            paymentType: PaymentType.Receive,
            status: SdkPaymentStatus.Completed,
            amount: new BigInteger(500),
            fees: BigInteger.Zero,
            timestamp: 1_785_806_574,
            method: PaymentMethod.Spark,
            details: new PaymentDetails.Spark(
                invoiceDetails: null,
                htlcDetails: new SparkHtlcDetails(Hash, "beef", 0, SparkHtlcStatus.PreimageShared),
                conversionInfo: null),
            conversionDetails: null!);

        var mapped = SparkPaymentMapper.Map(payment, new StubBolt11Parser());

        Assert.Equal(Hash, mapped.PaymentHash);
        Assert.Null(mapped.Bolt11);
    }

    [Fact]
    public void Amounts_are_in_satoshi_and_converted_to_millisatoshi_explicitly()
    {
        var mapped = SparkPaymentMapper.Map(
            LightningReceive(htlc: null, amount: new BigInteger(1234)), new StubBolt11Parser());

        Assert.Equal(1234, mapped.AmountSats);
        Assert.Equal(1_234_000, mapped.AmountMsat);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(21_000_000_00000000, 21_000_000_00000000)]
    public void ToSats_passes_representable_values_through(long input, long expected)
    {
        Assert.Equal(expected, SparkPaymentMapper.ToSats(new BigInteger(input)));
    }

    [Fact]
    public void ToSats_clamps_rather_than_throwing()
    {
        // A u128 that does not fit in a signed 64-bit satoshi value cannot be a real amount. Clamping keeps
        // one nonsensical row from killing the event consumer loop for every store.
        Assert.Equal(long.MaxValue, SparkPaymentMapper.ToSats(BigInteger.Pow(2, 100)));
        Assert.Equal(0, SparkPaymentMapper.ToSats(new BigInteger(-5)));
    }

    [Fact]
    public void Timestamps_are_read_as_seconds_or_milliseconds()
    {
        Assert.Equal(
            DateTimeOffset.FromUnixTimeSeconds(1_785_806_574),
            SparkPaymentMapper.ToTimestamp(1_785_806_574));
        Assert.Equal(
            DateTimeOffset.FromUnixTimeMilliseconds(1_785_806_574_000),
            SparkPaymentMapper.ToTimestamp(1_785_806_574_000));
    }

    [Fact]
    public void A_zero_timestamp_becomes_now_rather_than_1970()
    {
        var mapped = SparkPaymentMapper.ToTimestamp(0);

        Assert.True(mapped > DateTimeOffset.UtcNow.AddMinutes(-1));
    }

    [Theory]
    [InlineData(SdkPaymentStatus.Completed, SparkPaymentStatus.Completed)]
    [InlineData(SdkPaymentStatus.Pending, SparkPaymentStatus.Pending)]
    [InlineData(SdkPaymentStatus.Failed, SparkPaymentStatus.Failed)]
    public void Status_maps_one_to_one(SdkPaymentStatus sdkStatus, SparkPaymentStatus expected)
    {
        var mapped = SparkPaymentMapper.Map(
            LightningReceive(htlc: null, status: sdkStatus), new StubBolt11Parser());

        Assert.Equal(expected, mapped.Status);
    }

    [Theory]
    [InlineData(PaymentMethod.Lightning, SparkPaymentMethod.Lightning)]
    [InlineData(PaymentMethod.Spark, SparkPaymentMethod.Spark)]
    [InlineData(PaymentMethod.Token, SparkPaymentMethod.Token)]
    [InlineData(PaymentMethod.Deposit, SparkPaymentMethod.Deposit)]
    [InlineData(PaymentMethod.Withdraw, SparkPaymentMethod.Withdraw)]
    [InlineData(PaymentMethod.Unknown, SparkPaymentMethod.Unknown)]
    public void The_payment_method_is_carried_through(PaymentMethod sdkMethod, SparkPaymentMethod expected)
    {
        // Needed to tell an unattributable Lightning receive — worth a warning — from an on-chain deposit
        // claim, which has no payment hash by nature and is entirely normal.
        var payment = new Payment(
            id: "p1",
            paymentType: PaymentType.Receive,
            status: SdkPaymentStatus.Completed,
            amount: new BigInteger(1000),
            fees: BigInteger.Zero,
            timestamp: 1_785_806_574,
            method: sdkMethod,
            details: null!,
            conversionDetails: null!);

        Assert.Equal(expected, SparkPaymentMapper.Map(payment, new StubBolt11Parser()).Method);
    }

    [Fact]
    public void A_claimed_deposit_maps_with_its_outpoint_and_no_payment_hash()
    {
        // The claim fee is netted out by the SDK: a 100 000 sat deposit arrived as amount 99 901, fees 99.
        // Credit the amount, never the gross deposit.
        var payment = new Payment(
            id: "019fccc9-fba5-7cc9-865f-7e03030a3605",
            paymentType: PaymentType.Receive,
            status: SdkPaymentStatus.Completed,
            amount: new BigInteger(99_901),
            fees: new BigInteger(99),
            timestamp: 1_785_847_217,
            method: PaymentMethod.Deposit,
            details: new PaymentDetails.Deposit("e2e11469", 1),
            conversionDetails: null!);

        var mapped = SparkPaymentMapper.Map(payment, new StubBolt11Parser());

        Assert.Equal(SparkPaymentMethod.Deposit, mapped.Method);
        Assert.Null(mapped.PaymentHash);
        Assert.Equal(99_901, mapped.AmountSats);
        Assert.Equal(99, mapped.FeeSats);
        Assert.Contains("e2e11469:1", mapped.Description);
    }

    [Fact]
    public void A_cooperative_exit_maps_with_its_L1_txid()
    {
        // The txid is present from the first Pending event onwards, so a sweep record can display it without
        // waiting for completion.
        var payment = new Payment(
            id: "bb45fc96-285e-4fa8-bc3c-cc3d655b0beb",
            paymentType: PaymentType.Send,
            status: SdkPaymentStatus.Pending,
            amount: new BigInteger(24_972),
            fees: new BigInteger(2_190),
            timestamp: 1_785_848_062,
            method: PaymentMethod.Withdraw,
            // SDK 0.28 added the vout and the destination address to this variant. The mapper reads neither —
            // the L1 txid is the whole point of the arm — so a zero index and a placeholder address stand in.
            details: new PaymentDetails.Withdraw("8808985e", 0, null!),
            conversionDetails: null!);

        var mapped = SparkPaymentMapper.Map(payment, new StubBolt11Parser());

        Assert.Equal(SparkPaymentMethod.Withdraw, mapped.Method);
        Assert.Equal(SparkPaymentDirection.Send, mapped.Direction);
        Assert.Contains("8808985e", mapped.Description);
    }

    [Fact]
    public void Both_legs_of_a_self_payment_share_a_hash_but_differ_in_direction_and_fee()
    {
        // Documenting the shape callers have to defend against: matching on payment hash alone finds both.
        var htlc = new SparkHtlcDetails(Hash, "4ec10d84", 0, SparkHtlcStatus.PreimageShared);
        var receive = LightningReceive(htlc, amount: new BigInteger(500));
        var send = new Payment(
            id: "d7bb8be5-b182-4031-8310-8d11d6470ae4",
            paymentType: PaymentType.Send,
            status: SdkPaymentStatus.Completed,
            amount: new BigInteger(500),
            fees: new BigInteger(3),
            timestamp: 1_785_806_574,
            method: PaymentMethod.Lightning,
            details: new PaymentDetails.Lightning("d", "lnbcrt-one", "02", htlc, null, null, null, null),
            conversionDetails: null!);

        var mappedReceive = SparkPaymentMapper.Map(receive, new StubBolt11Parser());
        var mappedSend = SparkPaymentMapper.Map(send, new StubBolt11Parser());

        Assert.Equal(mappedReceive.PaymentHash, mappedSend.PaymentHash);
        Assert.NotEqual(mappedReceive.SdkPaymentId, mappedSend.SdkPaymentId);
        Assert.Equal(0, mappedReceive.FeeSats);
        Assert.Equal(3, mappedSend.FeeSats);
    }

    [Fact]
    public void Sends_are_distinguished_from_receives()
    {
        var send = new Payment(
            id: "send-1",
            paymentType: PaymentType.Send,
            status: SdkPaymentStatus.Completed,
            amount: new BigInteger(1000),
            fees: new BigInteger(4),
            timestamp: 1_785_806_574,
            method: PaymentMethod.Lightning,
            details: new PaymentDetails.Lightning("d", "lnbcrt-one", "02", null!, null, null, null, null),
            conversionDetails: null!);

        var mapped = SparkPaymentMapper.Map(send, new StubBolt11Parser());

        Assert.Equal(SparkPaymentDirection.Send, mapped.Direction);
        Assert.Equal(4, mapped.FeeSats);
    }
}
