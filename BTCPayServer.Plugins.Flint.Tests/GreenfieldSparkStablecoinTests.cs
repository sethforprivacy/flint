using BTCPayServer.Client.Models;
using BTCPayServer.Plugins.Flint.Models;
using BTCPayServer.Plugins.Flint.Tests.Fakes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace BTCPayServer.Plugins.Flint.Tests;

/// <summary>
/// The USDC and USDT switch through the API: the same switch as the Flint page, with the same refusal off mainnet.
/// </summary>
public class GreenfieldSparkStablecoinTests
{
    private const string Store = SparkSurfaceHarness.AttackerStore;

    [Fact]
    public async Task A_store_reads_whether_its_checkout_takes_usdc_and_usdt()
    {
        var h = SparkSurfaceHarness.Create(configureAttackerStore: true, mainnet: true);

        var data = AssertOk(await h.Api.GetStablecoins(Store, CancellationToken.None));

        Assert.True(data.Available);
        Assert.False(data.Enabled);
        Assert.Equal(["USDC-FLINT", "USDT-FLINT"], data.PaymentMethodIds);
    }

    [Fact]
    public async Task Turning_it_on_offers_both_coins_and_an_empty_body_turns_it_off()
    {
        var h = SparkSurfaceHarness.Create(configureAttackerStore: true, mainnet: true);

        var on = AssertOk(await h.Api.UpdateStablecoins(
            Store, new SparkStablecoinsInput { Enabled = true }, CancellationToken.None));
        Assert.True(on.Enabled);
        Assert.Contains(Store, h.Stablecoins.StoreConfig.Enabled);

        // A full replacement, so an omitted body is "off" — which, unlike Stable Balance's default, moves no money.
        var off = AssertOk(await h.Api.UpdateStablecoins(Store, null, CancellationToken.None));
        Assert.False(off.Enabled);
        Assert.DoesNotContain(Store, h.Stablecoins.StoreConfig.Enabled);
    }

    [Fact]
    public async Task Enabling_off_mainnet_is_refused_and_nothing_is_stored()
    {
        var h = SparkSurfaceHarness.Create(configureAttackerStore: true, mainnet: false);

        var result = await h.Api.UpdateStablecoins(
            Store, new SparkStablecoinsInput { Enabled = true }, CancellationToken.None);

        var unprocessable = Assert.IsType<UnprocessableEntityObjectResult>(result);
        var error = Assert.Single(Assert.IsType<List<GreenfieldValidationError>>(unprocessable.Value));
        Assert.Equal("enabled", error.Path);
        Assert.Contains("mainnet", error.Message);
        Assert.Empty(h.Stablecoins.StoreConfig.Enabled);

        var data = AssertOk(await h.Api.GetStablecoins(Store, CancellationToken.None));
        Assert.False(data.Available);
    }

    [Fact]
    public async Task A_store_without_Spark_is_told_to_set_it_up_first()
    {
        var h = SparkSurfaceHarness.Create(mainnet: true);

        foreach (var result in new[]
                 {
                     await h.Api.GetStablecoins(Store, CancellationToken.None),
                     await h.Api.UpdateStablecoins(Store, new SparkStablecoinsInput { Enabled = true }, CancellationToken.None)
                 })
        {
            var objectResult = Assert.IsType<ObjectResult>(result);
            Assert.Equal(StatusCodes.Status404NotFound, objectResult.StatusCode);
            Assert.Equal("spark-not-configured", Assert.IsType<GreenfieldAPIError>(objectResult.Value).Code);
        }

        Assert.Empty(h.Stablecoins.StoreConfig.Enabled);
    }

    private static SparkStablecoinsData AssertOk(IActionResult result) =>
        Assert.IsType<SparkStablecoinsData>(Assert.IsType<OkObjectResult>(result).Value);
}
