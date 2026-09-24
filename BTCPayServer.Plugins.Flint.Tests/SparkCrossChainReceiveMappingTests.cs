using System.Numerics;
using Breez.Sdk.Spark;
using BTCPayServer.Plugins.Flint.Sdk;
using BTCPayServer.Plugins.Flint.Services;
using BTCPayServer.Plugins.Flint.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using SdkPaymentStatus = Breez.Sdk.Spark.PaymentStatus;

namespace BTCPayServer.Plugins.Flint.Tests;

/// <summary>
/// The SDK's cross-chain receive shapes as the plugin reads them, and the network list's cache.
/// </summary>
public class SparkCrossChainReceiveMappingTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static CrossChainRoutePair Pair(
        string chain = "base",
        string? chainId = "8453",
        string asset = "USDC",
        string? contract = "0x833589fCD6eDb6E08f4c7C32D4f71b54bdA02913",
        byte decimals = 6,
        params CrossChainAcceptedAsset[] accepted) =>
        new(CrossChainProvider.Orchestra, chain, chainId!, asset, contract!, decimals, false,
            accepted.Length == 0 ? [new CrossChainAcceptedAsset(new SparkAsset.Bitcoin(), null!)] : accepted, []);

    [Fact]
    public void A_receive_route_carries_where_it_can_land_and_the_bitcoin_band()
    {
        var route = SparkSdkClient.MapReceiveRoute(Pair(accepted:
        [
            new CrossChainAcceptedAsset(new SparkAsset.Bitcoin(), new CrossChainRouteLimits(null, null, 100, 1_000_000)),
            new CrossChainAcceptedAsset(new SparkAsset.Token("btkn1usdb"), null!)
        ]));

        Assert.Equal(SparkCrossChainProvider.Orchestra, route.Provider);
        Assert.Equal("base", route.Chain);
        Assert.Equal(6u, route.Decimals);
        Assert.True(route.LandsAsBitcoin);
        Assert.True(route.LandsAsToken);
        Assert.Equal(100UL, route.BitcoinLimits!.MinUsdCents);
        Assert.True(route.BitcoinLimits.AdmitsUsd(10m));
        Assert.False(route.BitcoinLimits.AdmitsUsd(0.5m));
        Assert.IsType<CrossChainRoutePair>(route.Handle);
    }

    [Fact]
    public void A_token_only_route_says_so_and_publishes_no_bitcoin_band()
    {
        var route = SparkSdkClient.MapReceiveRoute(Pair(
            chain: "arc", accepted: [new CrossChainAcceptedAsset(new SparkAsset.Token("btkn1usdb"), null!)]));

        Assert.False(route.LandsAsBitcoin);
        Assert.True(route.LandsAsToken);
        Assert.Null(route.BitcoinLimits);
    }

    [Fact]
    public void A_band_with_no_bounds_admits_every_positive_amount()
    {
        var limits = new SparkCrossChainLimits(null, null, null, null);

        Assert.True(limits.AdmitsUsd(0.01m));
        Assert.True(limits.AdmitsUsd(1_000_000m));
        Assert.False(limits.AdmitsUsd(0m));
    }

    [Theory]
    [InlineData("0x00000000000000000000000000000000000000aa", "0x833589fCD6eDb6E08f4c7C32D4f71b54bdA02913", "8453", true)]
    // HyperCore's "contract" is a 16-byte token id, not an EVM address: no EIP-681 for it.
    [InlineData("0x00000000000000000000000000000000000000aa", "0x00000000000000000000000000000000", "1337", false)]
    [InlineData("TFake00000000000000000000000000001", "TR7NHqjeKQxGTCi8q8ZY4pL8otSzgjLj6t", "728126428", false)]
    [InlineData("So1Fake0000000000000000000000000000000000001", "EPjFWdd5AufqSSqeM2qN1xzybapC8G4wEGGkZwyTDt1v", "solana:5eykt4UsFv8P8NJdTREpY1vzqKqZKvdp", false)]
    public void Only_an_evm_token_transfer_gets_a_payment_uri(string deposit, string contract, string chainId, bool uri)
    {
        var route = FakeSparkSdkClient.ReceiveRoute("x", chainId, "USDC", contract, 6);

        var request = StablecoinPaymentService.PaymentRequestFor(route, deposit, new BigInteger(10_080_000));

        if (uri)
            Assert.Equal($"ethereum:{contract}@{chainId}/transfer?address={deposit}&uint256=10080000", request);
        else
            Assert.Equal(deposit, request);
    }

    [Fact]
    public void A_receives_conversion_details_carry_the_quotes_fingerprint_and_the_payers_transaction()
    {
        var payment = new Payment(
            id: "spark-1",
            paymentType: PaymentType.Receive,
            status: SdkPaymentStatus.Completed,
            amount: new BigInteger(10_000),
            fees: BigInteger.Zero,
            timestamp: 1_785_806_574,
            method: PaymentMethod.Spark,
            details: new PaymentDetails.Spark(null!, null!, new ConversionInfo.Orchestra(
                "order-1", "quote-1", null!, "base", "8453", "USDC", "spark1wallet",
                new BigInteger(10_080_000), new BigInteger(10_000), new BigInteger(10_004), "0xpayer",
                ConversionStatus.Completed, new BigInteger(76_000), new BigInteger(80_000), "USDC", 6, 6,
                "0x833589fcd6edb6e08f4c7c32d4f71b54bda02913")),
            conversionDetails: null!);

        var mapped = SparkPaymentMapper.Map(payment, new StubBolt11Parser());

        Assert.Null(mapped.PaymentHash);
        var conversion = mapped.Conversion!;
        Assert.Equal(SparkCrossChainProvider.Orchestra, conversion.Provider);
        Assert.Equal(SparkConversionStatus.Completed, conversion.Status);
        Assert.Equal(new BigInteger(10_080_000), conversion.AssetAmountIn);
        Assert.Equal(new BigInteger(10_000), conversion.EstimatedOut);
        Assert.Equal(new BigInteger(80_000), conversion.ServiceFeeAmount);
        Assert.Equal("0xpayer", conversion.ExternalTxHash);
        Assert.Equal("8453", conversion.ChainId);
        Assert.Equal("0x833589fcd6edb6e08f4c7c32d4f71b54bda02913", conversion.AssetContract);
    }

    #region The network list's cache

    [Fact]
    public async Task The_network_list_is_read_once_and_reused()
    {
        var sdk = new FakeSparkSdkClient(new WriteLog());
        var time = new StubTimeProvider(DateTimeOffset.UtcNow);
        var cache = new StablecoinRouteCache(time, NullLogger<StablecoinRouteCache>.Instance);

        var first = await cache.GetAsync("store-1", sdk, TimeSpan.FromSeconds(5), Ct);
        var second = await cache.GetAsync("store-1", sdk, TimeSpan.FromSeconds(5), Ct);

        Assert.NotNull(first);
        Assert.Same(first, second);

        time.Advance(StablecoinPayments.RouteCacheTtl + TimeSpan.FromSeconds(1));
        var third = await cache.GetAsync("store-1", sdk, TimeSpan.FromSeconds(5), Ct);
        Assert.NotSame(first, third);
    }

    [Fact]
    public async Task A_reconnected_wallet_reads_its_own_list()
    {
        var time = new StubTimeProvider(DateTimeOffset.UtcNow);
        var cache = new StablecoinRouteCache(time, NullLogger<StablecoinRouteCache>.Instance);
        var before = new FakeSparkSdkClient();
        var after = new FakeSparkSdkClient();
        after.CrossChainReceiveRoutes.RemoveAll(r => r.Chain != "tron");

        await cache.GetAsync("store-1", before, TimeSpan.FromSeconds(5), Ct);
        var routes = await cache.GetAsync("store-1", after, TimeSpan.FromSeconds(5), Ct);

        Assert.Equal("tron", Assert.Single(routes!).Chain);
    }

    [Fact]
    public async Task A_failed_read_keeps_the_last_list_and_is_not_retried_for_a_minute()
    {
        var writes = new WriteLog();
        var sdk = new FakeSparkSdkClient(writes);
        var time = new StubTimeProvider(DateTimeOffset.UtcNow);
        var cache = new StablecoinRouteCache(time, NullLogger<StablecoinRouteCache>.Instance);
        var fresh = await cache.GetAsync("store-1", sdk, TimeSpan.FromSeconds(5), Ct);

        time.Advance(StablecoinPayments.RouteCacheTtl + TimeSpan.FromSeconds(1));
        sdk.FailCrossChainReceiveRoutesWith = new SdkException.NetworkException("@v1=unreachable");
        var stale = await cache.GetAsync("store-1", sdk, TimeSpan.FromSeconds(5), Ct);
        var again = await cache.GetAsync("store-1", sdk, TimeSpan.FromSeconds(5), Ct);

        Assert.Same(fresh, stale);
        Assert.Same(fresh, again);
        // One read on the way in, one failed refresh, and no stampede after it.
        Assert.Equal(2, writes.Entries.Count(e => e == "sdk:cc-receive-routes"));
    }

    #endregion
}
