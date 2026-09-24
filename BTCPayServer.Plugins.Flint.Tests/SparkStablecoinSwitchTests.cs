using BTCPayServer.Plugins.Flint.Controllers;
using BTCPayServer.Plugins.Flint.Models;
using BTCPayServer.Plugins.Flint.Tests.Fakes;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace BTCPayServer.Plugins.Flint.Tests;

/// <summary>
/// The merchant's two ways to turn USDC and USDT on: the question at setup, and the switch on the status page.
/// </summary>
public class SparkStablecoinSwitchTests
{
    private const string Store = SparkSurfaceHarness.AttackerStore;

    [Fact]
    public async Task The_status_page_switch_turns_both_coins_on_and_off()
    {
        var h = SparkSurfaceHarness.Create(configureAttackerStore: true, mainnet: true);

        var on = await h.Mvc.Stablecoins(Store, enabled: true, CancellationToken.None);
        Assert.IsType<RedirectToActionResult>(on);
        Assert.Contains(Store, h.Stablecoins.StoreConfig.Enabled);
        Assert.Contains("USDC and USDT", Assert.IsType<string>(h.Mvc.TempData["SuccessMessage"]));

        var off = await h.Mvc.Stablecoins(Store, enabled: false, CancellationToken.None);
        Assert.IsType<RedirectToActionResult>(off);
        Assert.DoesNotContain(Store, h.Stablecoins.StoreConfig.Enabled);
    }

    [Fact]
    public async Task The_switch_refuses_to_turn_on_off_mainnet_and_says_why()
    {
        var h = SparkSurfaceHarness.Create(configureAttackerStore: true, mainnet: false);

        await h.Mvc.Stablecoins(Store, enabled: true, CancellationToken.None);

        Assert.Empty(h.Stablecoins.StoreConfig.Enabled);
        Assert.Contains("mainnet", Assert.IsType<string>(h.Mvc.TempData["ErrorMessage"]));
    }

    [Fact]
    public async Task The_switch_cannot_reach_another_store()
    {
        var h = SparkSurfaceHarness.Create(configureAttackerStore: true, mainnet: true);

        var result = await h.Mvc.Stablecoins(SparkSurfaceHarness.VictimStore, enabled: true, CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
        Assert.Empty(h.Stablecoins.StoreConfig.Enabled);
    }

    [Fact]
    public async Task A_store_without_flint_is_sent_to_setup_rather_than_switched()
    {
        var h = SparkSurfaceHarness.Create(configureAttackerStore: false, mainnet: true);

        await h.Mvc.Stablecoins(Store, enabled: true, CancellationToken.None);

        Assert.Empty(h.Stablecoins.StoreConfig.Enabled);
    }

    [Fact]
    public async Task Setup_turns_usdc_and_usdt_on_when_the_merchant_asked_for_it()
    {
        var h = SparkSurfaceHarness.Create(mainnet: true);

        var result = await h.Mvc.Setup(
            Store,
            new SparkSetupViewModel
            {
                SeedSource = SeedSource.Imported,
                ImportedMnemonic = SparkSurfaceHarness.ValidMnemonic,
                EnableStablecoins = true
            },
            CancellationToken.None);

        Assert.IsType<RedirectToActionResult>(result);
        Assert.Contains(Store, h.Stablecoins.StoreConfig.Enabled);
    }

    [Fact]
    public async Task Setup_leaves_them_off_unless_asked_and_off_mainnet_whatever_was_posted()
    {
        var unasked = SparkSurfaceHarness.Create(mainnet: true);
        await unasked.Mvc.Setup(
            Store,
            new SparkSetupViewModel { SeedSource = SeedSource.Imported, ImportedMnemonic = SparkSurfaceHarness.ValidMnemonic },
            CancellationToken.None);
        Assert.Empty(unasked.Stablecoins.StoreConfig.Enabled);

        var regtest = SparkSurfaceHarness.Create(mainnet: false);
        await regtest.Mvc.Setup(
            Store,
            new SparkSetupViewModel
            {
                SeedSource = SeedSource.Imported,
                ImportedMnemonic = SparkSurfaceHarness.ValidMnemonic,
                EnableStablecoins = true
            },
            CancellationToken.None);
        Assert.Empty(regtest.Stablecoins.StoreConfig.Enabled);
        Assert.NotNull(regtest.Settings.Settings[Store]);
    }

    [Fact]
    public async Task The_status_page_reports_whether_they_are_on()
    {
        var h = SparkSurfaceHarness.Create(configureAttackerStore: true, mainnet: true);
        h.Stablecoins.StoreConfig.Enabled.Add(Store);

        var view = Assert.IsType<ViewResult>(await h.Mvc.Status(Store, CancellationToken.None));
        var model = Assert.IsType<SparkStatusViewModel>(view.Model);

        Assert.True(model.StablecoinsAvailable);
        Assert.True(model.StablecoinsEnabled);
    }
}
