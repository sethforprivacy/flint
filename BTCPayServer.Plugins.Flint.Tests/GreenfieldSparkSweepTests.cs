using BTCPayServer.Client.Models;
using BTCPayServer.Plugins.Flint.Data;
using BTCPayServer.Plugins.Flint.Models;
using BTCPayServer.Plugins.Flint.Sdk;
using BTCPayServer.Plugins.Flint.Services;
using BTCPayServer.Plugins.Flint.Tests.Fakes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace BTCPayServer.Plugins.Flint.Tests;

/// <summary>
/// Sweeping through the API: configuration parity with the form, and the engine as the only way money moves.
/// </summary>
public class GreenfieldSparkSweepTests
{
    private const string Store = SparkSurfaceHarness.AttackerStore;

    /// <summary>A valid regtest address the fake wallet does not hand out, for static-destination cases.</summary>
    private const string OwnAddress = "bcrt1qt8hufshrz62z5vj4q40uqx6c6ytlujy5s03gwm";

    #region Validation parity with the settings form

    /// <summary>
    /// The configurations the form refuses, and the reason it refuses each.
    /// </summary>
    /// <remarks>
    /// Every one of these is a configuration that looks reasonable field by field. The two cross-field cases are the
    /// ones a second, hand-written API validator would most plausibly omit: a threshold that can never clear its own
    /// minimum, and a fee-on-top policy with no reserve to charge the fee against.
    /// </remarks>
    public static TheoryData<string, SweepSettingsInput> RefusedConfigurations() => new()
    {
        {
            // The fee guard switched off: no percentage, no flat ceiling.
            "no fee ceiling at all",
            new SweepSettingsInput { MaxFeePercent = 0, MaxFeeFlatSats = null }
        },
        {
            // The other way of switching it off, which the form used to accept: clear the percentage and name a flat
            // ceiling larger than the smallest sweep allowed, so the fee may exceed what the sweep delivers.
            "a flat fee ceiling above the smallest sweep",
            new SweepSettingsInput { MaxFeePercent = 0, MinimumSweepSats = 100_000, MaxFeeFlatSats = 200_000 }
        },
        {
            "a negative threshold",
            new SweepSettingsInput { BalanceThresholdSats = -1 }
        },
        {
            "a negative reserve",
            new SweepSettingsInput { ReserveSats = -1 }
        },
        {
            "a minimum sweep below the on-chain dust floor",
            new SweepSettingsInput { MinimumSweepSats = 100 }
        },
        {
            "a percentage above 100",
            new SweepSettingsInput { MaxFeePercent = 101 }
        },
        {
            "a negative flat fee ceiling",
            new SweepSettingsInput { MaxFeeFlatSats = -1 }
        },
        {
            "a static destination that is not an address on this chain",
            new SweepSettingsInput
            {
                DestinationMode = SweepDestinationMode.StaticAddress,
                // A mainnet address on a regtest server.
                StaticAddress = "bc1qar0srrr7xfkvy5l643lydnw9re59gtzzwf5mdq"
            }
        },
        {
            "a fee-on-top policy with no reserve to pay the fee from",
            new SweepSettingsInput { Enabled = true, DrainWhenSweeping = false, ReserveSats = 0 }
        },
        {
            "a threshold that can never clear its own minimum",
            new SweepSettingsInput { Enabled = true, BalanceThresholdSats = 50_000, MinimumSweepSats = 100_000 }
        },
        {
            // Cross-chain sending is hard-gated to mainnet by the SDK: a connect carrying that configuration on
            // any other network throws, so storing this on a regtest server would not merely fail to sweep — it
            // would stop the store's wallet starting at all.
            "a cross-chain destination on a server that is not mainnet",
            new SweepSettingsInput
            {
                DestinationMode = SweepDestinationMode.EvmAddress,
                EvmAddress = "0x742d35Cc6634C0532925a3b844Bc454e4438f44e",
                EvmChain = "arbitrum",
                EvmAsset = "USDT"
            }
        }
    };

    [Theory]
    [MemberData(nameof(RefusedConfigurations))]
    public async Task A_configuration_the_form_refuses_is_refused_by_the_API_with_the_same_message(
        string what,
        SweepSettingsInput input)
    {
        // Two harnesses rather than one, because a save that succeeded on the first surface would change what the
        // second surface is validating against.
        var page = SparkSurfaceHarness.Create(configureAttackerStore: true);
        var api = SparkSurfaceHarness.Create(configureAttackerStore: true);

        var pageResult = await page.Mvc.Sweep(
            Store, new SparkSweepViewModel { Settings = Clone(input) }, CancellationToken.None);
        var apiResult = await api.Api.UpdateSweepConfiguration(Store, Clone(input), CancellationToken.None);

        Assert.IsType<ViewResult>(pageResult);
        var apiErrors = AssertValidationError(apiResult);

        // The form prefixes its keys with the bound property's name and the API uses the JSON member name, so the
        // comparison is on the messages — which are the part a merchant reads and the part that must not diverge.
        var pageMessages = page.Mvc.ModelState
            .SelectMany(entry => entry.Value!.Errors.Select(e => e.ErrorMessage))
            .OrderBy(m => m, StringComparer.Ordinal)
            .ToList();
        var apiMessages = apiErrors
            .Select(e => e.Message)
            .OrderBy(m => m, StringComparer.Ordinal)
            .ToList();

        Assert.NotEmpty(pageMessages);
        Assert.Equal(pageMessages, apiMessages);

        // Neither surface wrote anything for {what}.
        Assert.Empty(page.Settings.Writes);
        Assert.Empty(api.Settings.Writes);
        Assert.False(string.IsNullOrEmpty(what));
    }

    [Theory]
    [MemberData(nameof(RefusedConfigurations))]
    public async Task Every_refused_configuration_names_a_field_the_caller_actually_sent(
        string what,
        SweepSettingsInput input)
    {
        var h = SparkSurfaceHarness.Create(configureAttackerStore: true);

        var errors = AssertValidationError(
            await h.Api.UpdateSweepConfiguration(Store, Clone(input), CancellationToken.None));

        // camelCase, because that is how BTCPay serialises the body the caller sent. A path of "MaxFeePercent"
        // would name a member that does not exist on the wire.
        var wireNames = typeof(SweepSettingsInput).GetProperties()
            .Select(p => char.ToLowerInvariant(p.Name[0]) + p.Name[1..])
            .ToHashSet(StringComparer.Ordinal);

        foreach (var error in errors)
        {
            Assert.Contains(error.Path, wireNames);
            Assert.NotEmpty(error.Message);
        }

        Assert.False(string.IsNullOrEmpty(what));
    }

    [Fact]
    public async Task A_settings_write_cannot_switch_the_fee_guard_off()
    {
        // The sharpest single case in this file. Sweeping is automatic, so there has to be a number above which the
        // plugin refuses to pay — and it must not be possible to remove it through the surface with no UI to warn
        // anyone.
        var h = SparkSurfaceHarness.Create(configureAttackerStore: true);
        h.Settings.Settings[Store]!.Sweep = new SweepSettings { MaxFeePercent = 3.0 };

        var errors = AssertValidationError(await h.Api.UpdateSweepConfiguration(
            Store,
            new SweepSettingsInput { Enabled = true, MaxFeePercent = 0, MaxFeeFlatSats = null },
            CancellationToken.None));

        Assert.Contains(errors, e => e.Path == "maxFeePercent");
        Assert.Empty(h.Settings.Writes);

        // The store keeps the ceiling it had.
        Assert.Equal(3.0, h.Settings.Settings[Store]!.Sweep.MaxFeePercent);
    }

    [Fact]
    public async Task A_valid_configuration_is_stored_and_leaves_the_seed_alone()
    {
        var h = SparkSurfaceHarness.Create(configureAttackerStore: true);
        h.Settings.Settings[Store]!.ApiKeyOverride = "merchant-key";
        var protectedBefore = h.Settings.Settings[Store]!.ProtectedMnemonic;

        var response = AssertOk<SparkSweepConfigurationData>(await h.Api.UpdateSweepConfiguration(
            Store,
            new SweepSettingsInput
            {
                Enabled = true,
                BalanceThresholdSats = 300_000,
                MinimumSweepSats = 100_000,
                MaxFeePercent = 2.5,
                ConfirmationSpeed = SweepConfirmationSpeed.Slow
            },
            CancellationToken.None));

        // The response is what is stored, re-read rather than echoed.
        Assert.True(response.Settings.Enabled);
        Assert.Equal(300_000, response.Settings.BalanceThresholdSats);
        Assert.Equal(2.5, response.Settings.MaxFeePercent);
        Assert.Equal(SweepConfirmationSpeed.Slow, response.Settings.ConfirmationSpeed);
        Assert.Equal("Regtest", response.Network);

        // The settings blob holds the protected mnemonic alongside the sweep configuration, and this endpoint must
        // never see or rewrite it. A read-modify-write that reconstructed the object would destroy the wallet.
        var stored = h.Settings.Settings[Store]!;
        Assert.Equal(protectedBefore, stored.ProtectedMnemonic);
        Assert.Equal("merchant-key", stored.ApiKeyOverride);
        Assert.Equal(SparkSurfaceHarness.VictimPaymentKey, stored.PaymentKey);
    }

    [Fact]
    public async Task A_configuration_write_replaces_rather_than_merges()
    {
        // PUT semantics, asserted because a caller will otherwise assume a patch and quietly lose a setting they
        // did not resend. The documented behaviour is a full replacement, so an omitted field takes its default.
        var h = SparkSurfaceHarness.Create(configureAttackerStore: true);
        h.Settings.Settings[Store]!.Sweep = new SweepSettings
        {
            Enabled = true,
            ReserveSats = 50_000,
            MinimumSweepSats = 250_000,
            ConfirmationSpeed = SweepConfirmationSpeed.Fast,
            DestinationMode = SweepDestinationMode.StaticAddress,
            StaticAddress = OwnAddress
        };

        var response = AssertOk<SparkSweepConfigurationData>(
            await h.Api.UpdateSweepConfiguration(Store, new SweepSettingsInput(), CancellationToken.None));

        // Every field is back to the type's default, including the destination — and the static address is cleared
        // rather than kept, because an address the merchant has stopped naming is how one gets used by accident.
        Assert.False(response.Settings.Enabled);
        Assert.Equal(0, response.Settings.ReserveSats);
        Assert.Equal(SweepSettings.DefaultMinimumSweepSats, response.Settings.MinimumSweepSats);
        Assert.Equal(SweepSettings.DefaultBalanceThresholdSats, response.Settings.BalanceThresholdSats);
        Assert.Equal(SweepConfirmationSpeed.Medium, response.Settings.ConfirmationSpeed);
        Assert.Equal(SweepDestinationMode.StoreWallet, response.Settings.DestinationMode);
        Assert.Null(response.Settings.StaticAddress);
        Assert.Null(h.Settings.Settings[Store]!.Sweep.StaticAddress);
    }

    [Fact]
    public async Task A_store_in_store_wallet_mode_with_no_on_chain_wallet_is_refused()
    {
        var h = SparkSurfaceHarness.Create(configureAttackerStore: true);
        h.SweepAddresses.Result = SweepAddressResult.NoWallet("This store has no Bitcoin wallet.");

        var errors = AssertValidationError(await h.Api.UpdateSweepConfiguration(
            Store,
            new SweepSettingsInput { Enabled = true, DestinationMode = SweepDestinationMode.StoreWallet },
            CancellationToken.None));

        Assert.Contains(errors, e => e.Path == "destinationMode" && e.Message == "This store has no Bitcoin wallet.");
        Assert.Empty(h.Settings.Writes);

        // Validating a configuration must not consume an address from the merchant's wallet.
        Assert.All(h.SweepAddresses.Calls, call => Assert.False(call.Reserve));
    }

    [Fact]
    public async Task Sweep_endpoints_refuse_a_store_that_has_not_set_Spark_up()
    {
        var h = SparkSurfaceHarness.Create();

        foreach (var result in new[]
                 {
                     await h.Api.GetSweepConfiguration(Store, 0, 25, CancellationToken.None),
                     await h.Api.UpdateSweepConfiguration(Store, new SweepSettingsInput(), CancellationToken.None),
                     await h.Api.Sweep(Store, null, CancellationToken.None)
                 })
        {
            var objectResult = Assert.IsType<ObjectResult>(result);
            Assert.Equal(StatusCodes.Status404NotFound, objectResult.StatusCode);
            Assert.Equal(
                "spark-not-configured", Assert.IsType<GreenfieldAPIError>(objectResult.Value).Code);
        }

        Assert.Empty(h.Settings.Writes);
        Assert.Empty(h.SweepRecords.Records);
    }

    #endregion

    #region History

    [Fact]
    public async Task History_pages_and_clamps_the_same_way_the_page_does()
    {
        var h = SparkSurfaceHarness.Create(configureAttackerStore: true);
        await SeedRefusals(h, 30);

        var first = AssertOk<SparkSweepConfigurationData>(
            await h.Api.GetSweepConfiguration(Store, 0, 10, CancellationToken.None));
        Assert.Equal(30, first.Total);
        Assert.Equal(10, first.History.Count);
        Assert.Equal(0, first.Skip);

        var second = AssertOk<SparkSweepConfigurationData>(
            await h.Api.GetSweepConfiguration(Store, 10, 10, CancellationToken.None));
        Assert.Equal(10, second.Skip);
        Assert.Empty(first.History.Select(r => r.IdempotencyKey)
            .Intersect(second.History.Select(r => r.IdempotencyKey)));

        // Nonsense bounds produce a sensible page rather than an error, and a caller cannot make the server read
        // ten thousand rows.
        var clamped = AssertOk<SparkSweepConfigurationData>(
            await h.Api.GetSweepConfiguration(Store, -5, 10_000, CancellationToken.None));
        Assert.Equal(0, clamped.Skip);
        Assert.Equal(SparkSweepSettingsService.MaxHistoryPageSize, clamped.Count);
        Assert.Equal(30, clamped.History.Count);
    }

    [Fact]
    public async Task A_refusal_in_the_history_carries_its_code_reason_and_attempt_count()
    {
        var h = SparkSurfaceHarness.Create(configureAttackerStore: true);

        // Two automatic passes on one unchanged cause, which the engine folds onto a single row.
        h.Settings.Settings[Store]!.Sweep = new SweepSettings { Enabled = true, BalanceThresholdSats = 1 };
        h.WalletOf(Store).BalanceSats = 1_000;
        var engine = h.SweepEngine;
        await engine.RunAsync(Store, SweepTrigger.Automatic, cancellationToken: CancellationToken.None);
        await engine.RunAsync(Store, SweepTrigger.Automatic, cancellationToken: CancellationToken.None);

        var configuration = AssertOk<SparkSweepConfigurationData>(
            await h.Api.GetSweepConfiguration(Store, 0, 25, CancellationToken.None));

        var record = Assert.Single(configuration.History);
        Assert.Equal(SweepRecordStatus.Refused, record.Status);
        Assert.Equal(SweepRefusalCode.BelowMinimumSweep, record.RefusalCode);
        Assert.Equal(2, record.AttemptCount);
        Assert.NotNull(record.LastSeenAt);
        Assert.NotNull(record.Error);
        Assert.Contains("minimum", record.Error!, StringComparison.OrdinalIgnoreCase);
    }

    #endregion

    #region Manual sweep

    [Fact]
    public async Task A_manual_sweep_persists_its_record_before_the_send()
    {
        // The crash-safety primitive, asserted as an ordering on the shared monotonic write log. Two independent
        // call counts would pass just as happily with the writes reversed, and a send issued before its record
        // exists is a sweep that cannot be resolved after a crash.
        var h = SparkSurfaceHarness.Create(configureAttackerStore: true);

        var result = AssertOk<SparkSweepResultData>(
            await h.Api.Sweep(Store, null, CancellationToken.None));

        Assert.Equal(SweepOutcomeKind.Swept, result.Outcome);
        var key = result.Record!.IdempotencyKey;

        var added = h.WriteLog.Entries.IndexOf($"sweep:add:{key}");
        var sent = h.WriteLog.Entries.IndexOf($"sdk:send:{key}");

        Assert.True(added >= 0, $"no record was written for {key}: {string.Join(" -> ", h.WriteLog.Entries)}");
        Assert.True(sent >= 0, $"no send was issued for {key}: {string.Join(" -> ", h.WriteLog.Entries)}");
        Assert.True(
            added < sent,
            "the cooperative exit was sent before its record existed: " + string.Join(" -> ", h.WriteLog.Entries));

        // The engine's own ordering too: a threshold decision made against an unsynced balance is a sweep of the
        // wrong size, so the sync precedes the read.
        var syncIndex = h.WriteLog.Entries.IndexOf("sdk:sync");
        var readIndex = h.WriteLog.Entries.IndexOf("sdk:getinfo:synced");
        Assert.True(syncIndex >= 0 && readIndex > syncIndex, string.Join(" -> ", h.WriteLog.Entries));
    }

    [Fact]
    public async Task A_manual_sweep_is_recorded_as_manual_and_uses_the_stores_own_configuration()
    {
        var h = SparkSurfaceHarness.Create(configureAttackerStore: true);
        h.Settings.Settings[Store]!.Sweep = new SweepSettings
        {
            // Off: a manual sweep relaxes the automatic switch and the balance threshold, and nothing else.
            Enabled = false,
            BalanceThresholdSats = 100_000_000,
            MinimumSweepSats = 100_000,
            ConfirmationSpeed = SweepConfirmationSpeed.Slow,
            DrainWhenSweeping = true,
            MaxFeePercent = 3.0
        };

        var result = AssertOk<SparkSweepResultData>(await h.Api.Sweep(Store, null, CancellationToken.None));

        Assert.Equal(SweepOutcomeKind.Swept, result.Outcome);
        Assert.Equal(SweepTrigger.Manual, result.Record!.Trigger);
        Assert.Equal(SweepConfirmationSpeed.Slow, result.Record.ConfirmationSpeed);
        Assert.True(result.Record.FeesIncluded);

        // The tier the store configured, not a default the request could have chosen — nothing in the request body
        // names an amount, a destination or a speed.
        var send = Assert.Single(h.WalletOf(Store).OnchainSendCalls);
        Assert.Equal(SparkOnchainSpeed.Slow, send.Speed);
        Assert.Equal(500_000, send.AmountSats);

        // A fresh, reserved address from the store's own wallet.
        Assert.Equal(1, h.SweepAddresses.ReservedCount);
        Assert.Equal(FakeSweepAddressSource.RegtestAddresses[0], send.Address);
    }

    [Fact]
    public async Task A_preview_reserves_no_address_and_writes_no_record()
    {
        var h = SparkSurfaceHarness.Create(configureAttackerStore: true);

        var preview = AssertOk<SparkSweepPreviewData>(
            await h.Api.Sweep(Store, new SparkSweepRequest { Preview = true }, CancellationToken.None));

        Assert.True(preview.CanSweep);
        Assert.Null(preview.RefusalReason);
        Assert.Equal(500_000, preview.BalanceSats);
        Assert.Equal(500_000, preview.SweepableSats);
        Assert.Equal(500_000, preview.AmountSats);

        // The live quote, at every tier, with the store's own tier selected.
        Assert.NotNull(preview.Quote);
        Assert.Equal(1950, preview.Quote!.SlowFeeSats);
        Assert.Equal(2190, preview.Quote.MediumFeeSats);
        Assert.Equal(2430, preview.Quote.FastFeeSats);
        Assert.Equal(2190, preview.Quote.FeeSats);
        Assert.Equal(500_000 - 2190, preview.RecipientAmountSats);

        // Where it would go, and that a real sweep would rotate the address rather than reuse this one.
        Assert.NotNull(preview.Destination);
        Assert.Equal(SweepDestinationMode.StoreWallet, preview.Destination!.Mode);
        Assert.True(preview.Destination.Rotates);

        // Nothing was consumed and nothing was sent.
        Assert.Equal(0, h.SweepAddresses.ReservedCount);
        Assert.All(h.SweepAddresses.Calls, call => Assert.False(call.Reserve));
        Assert.Empty(h.SweepRecords.Records);
        Assert.Empty(h.WalletOf(Store).OnchainSendCalls);
        Assert.Equal(500_000, h.WalletOf(Store).BalanceSats);
    }

    [Fact]
    public async Task A_preview_reports_the_refusal_a_real_sweep_would_hit_without_recording_one()
    {
        var h = SparkSurfaceHarness.Create(configureAttackerStore: true);
        h.Settings.Settings[Store]!.Sweep = new SweepSettings { MinimumSweepSats = 1_000_000 };

        var preview = AssertOk<SparkSweepPreviewData>(
            await h.Api.Sweep(Store, new SparkSweepRequest { Preview = true }, CancellationToken.None));

        Assert.False(preview.CanSweep);
        Assert.NotNull(preview.RefusalReason);
        Assert.Null(preview.Quote);
        // A preview never files a row, not even for a refusal — the history is what the engine did, not what a
        // caller asked about.
        Assert.Empty(h.SweepRecords.Records);
    }

    [Theory]
    [InlineData(1_000, SweepRefusalCode.BelowMinimumSweep)]
    public async Task A_refused_sweep_surfaces_its_reason_and_code(long balance, SweepRefusalCode expected)
    {
        var h = SparkSurfaceHarness.Create(configureAttackerStore: true);
        h.WalletOf(Store).BalanceSats = balance;

        var result = AssertOk<SparkSweepResultData>(await h.Api.Sweep(Store, null, CancellationToken.None));

        // 200 with an explicit outcome: a refusal is a decision the engine reached, not a malformed request, and
        // folding it into a 4xx would discard the code and the record.
        Assert.Equal(SweepOutcomeKind.Refused, result.Outcome);
        Assert.False(result.Succeeded);
        Assert.Equal(expected, result.RefusalCode);
        Assert.NotEmpty(result.Reason);

        // Recorded, so a merchant whose sweeps have stopped can see why without reading the server log.
        Assert.Equal(SweepRecordStatus.Refused, result.Record!.Status);
        Assert.Equal(expected, result.Record.RefusalCode);
        Assert.Empty(h.WalletOf(Store).OnchainSendCalls);
    }

    [Fact]
    public async Task A_sweep_is_refused_when_the_fee_moves_above_the_limit_between_the_quote_and_the_send()
    {
        // The guard that matters runs inside the send, against the quote actually being committed to. A refusal
        // there means nothing was sent, and the record says so.
        var h = SparkSurfaceHarness.Create(configureAttackerStore: true);
        h.Settings.Settings[Store]!.Sweep = new SweepSettings { MinimumSweepSats = 100_000, MaxFeePercent = 1.0 };

        var wallet = (FakeSparkSdkClient)h.Runtime.Clients[Store];
        wallet.OnchainTiersAtSend = new SparkOnchainFeeQuote(
            "quote", DateTimeOffset.UtcNow.AddMinutes(1), 40_000, 50_000, 60_000);

        var result = AssertOk<SparkSweepResultData>(await h.Api.Sweep(Store, null, CancellationToken.None));

        Assert.Equal(SweepOutcomeKind.Refused, result.Outcome);
        Assert.Equal(SweepRefusalCode.FeeAboveLimit, result.RefusalCode);
        // The send was reached and vetoed, so nothing left the wallet.
        Assert.Single(wallet.OnchainSendCalls);
        Assert.Equal(500_000, wallet.BalanceSats);
    }

    [Fact]
    public async Task Both_surfaces_sweep_through_the_same_engine_path()
    {
        // Parity for the endpoint that moves money: the same stored configuration produces the same record shape,
        // the same trigger and the same idempotency-key-before-send ordering on both surfaces.
        var page = SparkSurfaceHarness.Create(configureAttackerStore: true);
        var api = SparkSurfaceHarness.Create(configureAttackerStore: true);

        Assert.IsType<RedirectToActionResult>(await page.Mvc.SweepNow(Store, CancellationToken.None));
        AssertOk<SparkSweepResultData>(await api.Api.Sweep(Store, null, CancellationToken.None));

        var pageRecord = Assert.Single(page.SweepRecords.Records).Value;
        var apiRecord = Assert.Single(api.SweepRecords.Records).Value;

        Assert.Equal(pageRecord.Trigger, apiRecord.Trigger);
        Assert.Equal(pageRecord.AmountSats, apiRecord.AmountSats);
        Assert.Equal(pageRecord.DestinationAddress, apiRecord.DestinationAddress);
        Assert.Equal(pageRecord.QuotedFeeSats, apiRecord.QuotedFeeSats);
        Assert.Equal(pageRecord.Status, apiRecord.Status);
        Assert.Equal(pageRecord.FeesIncluded, apiRecord.FeesIncluded);
    }

    [Fact]
    public async Task A_static_destination_configured_through_the_API_is_where_the_sweep_goes()
    {
        // End to end through both endpoints: configure, then sweep, and check the money went where the API said.
        var h = SparkSurfaceHarness.Create(configureAttackerStore: true);

        AssertOk<SparkSweepConfigurationData>(await h.Api.UpdateSweepConfiguration(
            Store,
            new SweepSettingsInput
            {
                Enabled = true,
                BalanceThresholdSats = 200_000,
                MinimumSweepSats = 100_000,
                DestinationMode = SweepDestinationMode.StaticAddress,
                StaticAddress = OwnAddress
            },
            CancellationToken.None));

        var result = AssertOk<SparkSweepResultData>(await h.Api.Sweep(Store, null, CancellationToken.None));

        Assert.Equal(SweepOutcomeKind.Swept, result.Outcome);
        Assert.Equal(OwnAddress, result.Record!.DestinationAddress);
        Assert.Equal(SweepDestinationMode.StaticAddress, result.Record.DestinationMode);
        // A fixed destination is not rotated, so no address was consumed from the store's wallet.
        Assert.Equal(0, h.SweepAddresses.ReservedCount);
    }

    #endregion

    #region Cross-chain configuration, which only mainnet can hold

    /// <summary>
    /// Switching away from a cross-chain destination clears the EVM address.
    /// </summary>
    /// <remarks>
    /// The same rule a static Bitcoin address follows, and it matters more here: a leftover EVM address is a
    /// destination on a chain this plugin cannot claw anything back from. Keeping it is how it gets used again
    /// by accident after a merchant has stopped intending to.
    /// </remarks>
    [Fact]
    public async Task Switching_away_from_a_cross_chain_destination_clears_the_address()
    {
        var h = SparkSurfaceHarness.Create(configureAttackerStore: true, mainnet: true);
        h.Settings.Settings[Store]!.Sweep = new SweepSettings
        {
            DestinationMode = SweepDestinationMode.EvmAddress,
            EvmAddress = "0x742d35Cc6634C0532925a3b844Bc454e4438f44e",
            EvmChain = "arbitrum",
            EvmAsset = "USDT"
        };

        AssertOk<SparkSweepConfigurationData>(await h.Api.UpdateSweepConfiguration(
            Store,
            new SweepSettingsInput
            {
                DestinationMode = SweepDestinationMode.StaticAddress,
                StaticAddress = MainnetAddress,
                // Still carrying the EVM fields, because the settings form posts every field it renders —
                // including the collapsed panel the merchant has just switched away from. This is the only
                // shape in which "cleared" and "kept" differ, and it is the shape that actually arrives.
                EvmAddress = "0x742d35Cc6634C0532925a3b844Bc454e4438f44e",
                EvmChain = "arbitrum",
                EvmAsset = "USDT"
            },
            CancellationToken.None));

        var stored = h.Settings.Settings[Store]!.Sweep;
        Assert.Null(stored.EvmAddress);
        Assert.Null(stored.EvmChain);
        Assert.Null(stored.EvmAsset);
    }

    /// <summary>
    /// A cross-chain destination cannot be saved with an economic floor the cost curve makes absurd.
    /// </summary>
    /// <remarks>
    /// The provider's fee has a fixed component of roughly $0.025, so the smallest send it will accept costs
    /// about 3.3% while a 50,000-sat one costs about 0.34%. The protocol would allow far less than that; the
    /// plugin should not, and refusing at save time is the only point at which a merchant is looking.
    /// </remarks>
    [Fact]
    public async Task A_cross_chain_destination_cannot_be_saved_below_its_economic_floor()
    {
        var h = SparkSurfaceHarness.Create(configureAttackerStore: true, mainnet: true);

        var errors = AssertValidationError(await h.Api.UpdateSweepConfiguration(
            Store,
            new SweepSettingsInput
            {
                Enabled = true,
                BalanceThresholdSats = 200_000,
                // Fine for a cooperative exit, punishing for a bridge.
                MinimumSweepSats = 1_000,
                DestinationMode = SweepDestinationMode.EvmAddress,
                EvmAddress = "0x742d35Cc6634C0532925a3b844Bc454e4438f44e",
                EvmChain = "arbitrum",
                EvmAsset = "USDT"
            },
            CancellationToken.None));

        Assert.Contains(errors, e => e.Path == "minimumSweepSats");
        Assert.Empty(h.Settings.Writes);
    }

    /// <summary>
    /// A mistyped EVM address is refused on save.
    /// </summary>
    /// <remarks>
    /// The address below is 42 characters of valid hex and differs from a real one by a transposition — the
    /// mistake a human eye slides over. Its EIP-55 checksum does not match, which is the only local signal that
    /// anything is wrong, and delivery to the wrong EVM address is irreversible.
    /// </remarks>
    [Fact]
    public async Task A_mistyped_EVM_address_is_refused_on_save()
    {
        var h = SparkSurfaceHarness.Create(configureAttackerStore: true, mainnet: true);

        var errors = AssertValidationError(await h.Api.UpdateSweepConfiguration(
            Store,
            new SweepSettingsInput
            {
                Enabled = true,
                BalanceThresholdSats = 200_000,
                MinimumSweepSats = SweepSettings.DefaultCrossChainMinimumSweepSats,
                DestinationMode = SweepDestinationMode.EvmAddress,
                // 0x742d35Cc… with the 4 and 2 transposed.
                EvmAddress = "0x724d35Cc6634C0532925a3b844Bc454e4438f44e",
                EvmChain = "arbitrum",
                EvmAsset = "USDT"
            },
            CancellationToken.None));

        Assert.Contains(errors, e => e.Path == "evmAddress");
        Assert.Empty(h.Settings.Writes);
    }

    /// <summary>
    /// A valid cross-chain configuration is accepted on mainnet, so the refusals above are not blanket.
    /// </summary>
    [Fact]
    public async Task A_valid_cross_chain_configuration_is_stored_on_mainnet()
    {
        var h = SparkSurfaceHarness.Create(configureAttackerStore: true, mainnet: true);

        var response = AssertOk<SparkSweepConfigurationData>(await h.Api.UpdateSweepConfiguration(
            Store,
            new SweepSettingsInput
            {
                Enabled = true,
                BalanceThresholdSats = 200_000,
                MinimumSweepSats = SweepSettings.DefaultCrossChainMinimumSweepSats,
                DestinationMode = SweepDestinationMode.EvmAddress,
                EvmAddress = "0x742d35Cc6634C0532925a3b844Bc454e4438f44e",
                EvmChain = "arbitrum",
                EvmAsset = "USDT",
                CrossChainSlippageBps = 50
            },
            CancellationToken.None));

        Assert.Equal(SweepDestinationMode.EvmAddress, response.Settings.DestinationMode);
        Assert.Equal("0x742d35Cc6634C0532925a3b844Bc454e4438f44e", response.Settings.EvmAddress);
        Assert.Equal("arbitrum", response.Settings.EvmChain);
        Assert.Equal(50u, response.Settings.CrossChainSlippageBps);
    }

    #endregion

    /// <summary>A valid mainnet address, for the mainnet-harness cases above.</summary>
    private const string MainnetAddress = "bc1qar0srrr7xfkvy5l643lydnw9re59gtzzwf5mdq";

    private static async Task SeedRefusals(SparkSurfaceHarness h, int count)
    {
        for (var i = 0; i < count; i++)
        {
            await h.SweepRecords.AddAsync(
                new SweepRecord
                {
                    IdempotencyKey = Guid.NewGuid().ToString(),
                    StoreId = Store,
                    Status = SweepRecordStatus.Refused,
                    RefusalCode = SweepRefusalCode.FeeAboveLimit,
                    Trigger = SweepTrigger.Automatic,
                    CreatedAt = DateTimeOffset.UnixEpoch.AddMinutes(i),
                    Error = "The exit fee is above the limit this store allows."
                },
                CancellationToken.None);
        }
    }

    /// <summary>
    /// An independent copy, so the two surfaces in a parity test cannot be handed the same mutable instance.
    /// </summary>
    private static SweepSettingsInput Clone(SweepSettingsInput input) => new()
    {
        Enabled = input.Enabled,
        BalanceThresholdSats = input.BalanceThresholdSats,
        ReserveSats = input.ReserveSats,
        MinimumSweepSats = input.MinimumSweepSats,
        ConfirmationSpeed = input.ConfirmationSpeed,
        MaxFeePercent = input.MaxFeePercent,
        MaxFeeFlatSats = input.MaxFeeFlatSats,
        DrainWhenSweeping = input.DrainWhenSweeping,
        DestinationMode = input.DestinationMode,
        StaticAddress = input.StaticAddress,
        EvmAddress = input.EvmAddress,
        EvmChain = input.EvmChain,
        EvmAsset = input.EvmAsset,
        CrossChainSlippageBps = input.CrossChainSlippageBps,
        CrossChainMinimumStableUnits = input.CrossChainMinimumStableUnits
    };

    private static T AssertOk<T>(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        return Assert.IsType<T>(ok.Value);
    }

    private static List<GreenfieldValidationError> AssertValidationError(IActionResult result)
    {
        var unprocessable = Assert.IsType<UnprocessableEntityObjectResult>(result);
        Assert.Equal(StatusCodes.Status422UnprocessableEntity, unprocessable.StatusCode);
        return Assert.IsType<List<GreenfieldValidationError>>(unprocessable.Value);
    }
}
