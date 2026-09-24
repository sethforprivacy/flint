using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Data;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.Flint.Payments;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Stores;

namespace BTCPayServer.Plugins.Flint.Services;

/// <summary>Whether a store offers USDC and USDT at checkout, and the switch that changes it.</summary>
public interface IStablecoinStoreConfig
{
    /// <summary>
    /// True when either payment method is configured and not switched off, false when neither is, and null when
    /// the store does not exist.
    /// </summary>
    Task<bool?> IsEnabledAsync(string storeId, CancellationToken cancellationToken = default);

    /// <summary>Configures both payment methods (and clears any exclusion), or removes both.</summary>
    Task<bool> SetEnabledAsync(string storeId, bool enabled, CancellationToken cancellationToken = default);
}

/// <summary>
/// The real <see cref="IStablecoinStoreConfig"/>, over the store's payment-method configuration.
/// </summary>
/// <remarks>
/// <para>
/// <b>One switch, two payment methods, and BTCPay as the only record of it.</b> The state is not duplicated into
/// the plugin's settings: a configured payment method is what BTCPay offers, so reading the configuration is what
/// makes the Flint page and the checkout agree by construction. A merchant who later switches one of the two off
/// through BTCPay's own checkout settings is respected — the page reports "on" while either is offered — and the
/// switch turning it on again clears that exclusion, as the Lightning wiring does.
/// </para>
/// <para>
/// The handler dictionary arrives as a factory for the reason given on
/// <see cref="BTCPayStoreLightningConfigStore"/>: this plugin's own handlers are in it.
/// </para>
/// </remarks>
public sealed class BTCPayStablecoinStoreConfig : IStablecoinStoreConfig
{
    private readonly StoreRepository _storeRepository;
    private readonly Func<PaymentMethodHandlerDictionary> _handlers;

    public BTCPayStablecoinStoreConfig(StoreRepository storeRepository, Func<PaymentMethodHandlerDictionary> handlers)
    {
        _storeRepository = storeRepository;
        _handlers = handlers;
    }

    public async Task<bool?> IsEnabledAsync(string storeId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(storeId);

        var store = await _storeRepository.FindStore(storeId).ConfigureAwait(false);
        return store is null ? null : IsEnabled(store);
    }

    /// <summary>The same answer, from a store row the caller already holds.</summary>
    public static bool IsEnabled(StoreData store)
    {
        ArgumentNullException.ThrowIfNull(store);

        var configs = store.GetPaymentMethodConfigs();
        var blob = store.GetStoreBlob();
        return StablecoinPayments.Assets.Any(asset =>
            configs.ContainsKey(asset.PaymentMethodId) && !blob.IsExcluded(asset.PaymentMethodId));
    }

    public async Task<bool> SetEnabledAsync(string storeId, bool enabled, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(storeId);

        var store = await _storeRepository.FindStore(storeId).ConfigureAwait(false);
        if (store is null)
            return false;

        var blob = store.GetStoreBlob();
        foreach (var asset in StablecoinPayments.Assets)
        {
            if (enabled)
            {
                store.SetPaymentMethodConfig(_handlers()[asset.PaymentMethodId], new StablecoinPaymentMethodConfig());
                blob.SetExcluded(asset.PaymentMethodId, false);
            }
            else
            {
                // Removed rather than excluded, so a store that never wants stablecoins carries no trace of them.
                store.SetPaymentMethodConfig(asset.PaymentMethodId, null);
            }
        }

        store.SetStoreBlob(blob);
        await _storeRepository.UpdateStore(store).ConfigureAwait(false);
        return true;
    }
}
