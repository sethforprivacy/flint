using System;
using System.Globalization;
using NBitcoin;

namespace BTCPayServer.Plugins.Flint.Services;

/// <summary>
/// The on-chain key that pays a unilateral exit's fees: its address, its public half, and — for as long as one
/// build needs it — its private half.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the plugin holds an on-chain key at all.</b> The statechain's tree transactions are pre-signed and
/// cannot pay their own fees, so every one of them is bumped by CPFP from an ordinary confirmed UTXO the
/// operator supplies. Somebody has to sign that child, and the only seed the plugin has is the store's Spark
/// mnemonic — so the funding key is derived from it, at <c>m/84'/{coin}'/4607060'/0/{index}</c>. See
/// <see cref="Constants.UnilateralExitFundingAccount"/> for why the account index is deliberately absurd: on a
/// store provisioned from <see cref="SeedSource.HotWallet"/> the same seed is BTCPay's own hot wallet, and
/// deriving at BIP84 account 0 would put these addresses inside the merchant's tracked wallet where their own
/// coin selection could spend the funding UTXO out from under a half-broadcast exit.
/// </para>
/// <para>
/// <b>One address per exit, which is what the index is for.</b> A fixed address would collect the change of
/// every exit a store ever quotes, so a new exit would find another exit's leftovers sitting on the address it
/// just told the operator to fund — and a leftover large enough to satisfy the new requirement makes a build
/// succeed against money nobody just sent. The index comes from
/// <see cref="Data.UnilateralExitRecord.FundingKeyIndex"/> and is allocated once, at quote time.
/// </para>
/// <para>
/// <b>Disposable, and the reason is <see cref="Secret"/>.</b> The 32 bytes are the spending authority for the
/// funding output; they are needed only for the duration of one
/// <see cref="Sdk.ISparkSdkClient.UnilateralExitAsync"/> call and are zeroed on dispose. Quoting needs no secret
/// at all — only <see cref="Address"/> — and reading the page needs neither, only
/// <see cref="KeyPathFor(Network, uint)"/>, so nothing but a build ever derives. That is why this type caches
/// nothing per store: BIP39 seed derivation is PBKDF2 with 2048 iterations and measures around a millisecond,
/// which is affordable on the one path that needs it and not a reason to hold key material in a long-lived
/// field.
/// </para>
/// <para>
/// The mnemonic itself is never held here. It arrives already decrypted from
/// <see cref="SparkMnemonicProtector.TryUnprotect"/>, is consumed inside <see cref="TryDerive"/>, and nothing on
/// this type can echo it back. Neither the phrase nor <see cref="Secret"/> is ever logged, and neither appears
/// in <see cref="Data.UnilateralExitRecord"/> — the record carries the address and nothing else.
/// </para>
/// </remarks>
public sealed class SparkExitFundingKey : IDisposable
{
    private readonly byte[] _secret;
    private bool _disposed;

    private SparkExitFundingKey(string address, string pubkeyHex, byte[] secret)
    {
        Address = address;
        PubkeyHex = pubkeyHex;
        _secret = secret;
    }

    /// <summary>
    /// The native-SegWit (P2WPKH) address the operator sends funding to, for the network it was derived on.
    /// </summary>
    /// <remarks>
    /// P2WPKH and not P2TR because it is the one <c>CpfpFundingKind</c> the plugin asks the SDK for. The funding
    /// input's script type has to match what the quote was taken with, or the witness the SDK builds does not
    /// verify — so this is not a preference, and it is not a choice a merchant is offered.
    /// </remarks>
    public string Address { get; }

    /// <summary>The compressed public key, hex, as <see cref="Sdk.SparkExitFundingUtxo.PubkeyHex"/> wants it.</summary>
    public string PubkeyHex { get; }

    /// <summary>
    /// The private key, 32 bytes, for the one-shot CPFP signer.
    /// </summary>
    /// <exception cref="ObjectDisposedException">
    /// The key has been disposed and the bytes zeroed. Thrown rather than handing back a zeroed array, because a
    /// signer built over 32 zero bytes fails somewhere far away from the mistake.
    /// </exception>
    public byte[] Secret
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _secret;
        }
    }

    /// <summary>
    /// Derives the funding key for a store, or explains why it could not be derived.
    /// </summary>
    /// <param name="mnemonic">
    /// The store's decrypted BIP39 phrase. Null or unusable is the expected failure — a server whose
    /// data-protection keyring was replaced can no longer unprotect it — and it is reported rather than thrown,
    /// because the operator's fix is to re-enter their seed and not to read a stack trace.
    /// </param>
    /// <param name="network">
    /// The network the address is rendered for, which also picks the BIP44 coin type: 0 on mainnet, 1 everywhere
    /// else. Both halves matter — a mainnet-shaped address on regtest is unusable, and a coin type that differed
    /// between the quote and the build would derive a different key for the same exit.
    /// </param>
    /// <param name="index">
    /// The exit's own address index, from <see cref="Data.UnilateralExitRecord.FundingKeyIndex"/>. Must be the
    /// index the record was created with: derive at another one and the plugin holds no key for the output the
    /// operator funded.
    /// </param>
    /// <remarks>
    /// No BIP39 passphrase, matching how the mnemonic is handed to the SDK: an empty passphrase is the only value
    /// this plugin ever uses, and inventing one here would make the funding address unrecoverable by hand from
    /// the seed the merchant backed up. That recoverability is the point — an operator who abandons an exit with
    /// sats still on the funding address must be able to sweep them with any BIP84 wallet, given the path.
    /// </remarks>
    public static bool TryDerive(
        string? mnemonic,
        Network network,
        uint index,
        out SparkExitFundingKey? key,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(network);

        key = null;

        if (string.IsNullOrWhiteSpace(mnemonic))
        {
            error = "This store's Spark seed could not be read, so the exit funding address cannot be derived. "
                    + "Re-enter the store's recovery phrase on the Flint setup page.";
            return false;
        }

        ExtKey derived;
        try
        {
            var phrase = new Mnemonic(mnemonic.Trim());
            // Hardened at the account level, so the derived child cannot be reached from any xpub the seed's
            // other consumers publish.
            derived = phrase.DeriveExtKey().Derive(KeyPathFor(network, index));
        }
        catch (Exception)
        {
            // Swallowed whole, deliberately: NBitcoin's wording for a bad phrase names word lists and checksums,
            // and the only actionable half of it is that the stored seed is not usable.
            error = "This store's Spark seed is not a usable recovery phrase, so the exit funding address cannot "
                    + "be derived.";
            return false;
        }

        var privateKey = derived.PrivateKey;
        key = new SparkExitFundingKey(
            privateKey.PubKey.GetAddress(ScriptPubKeyType.Segwit, network).ToString(),
            privateKey.PubKey.ToHex(),
            privateKey.ToBytes());

        error = null;
        return true;
    }

    /// <summary>
    /// <c>m/84'/{coin}'/4607060'/0/{index}</c> for a network and an exit's address index.
    /// </summary>
    /// <remarks>
    /// Exposed, and shown on the exit page, because it is what makes funding left on the address recoverable
    /// outside this plugin: an operator who abandons an exit with sats still on its funding address sweeps them
    /// with any BIP84 wallet, given the seed and this path. Cheap enough to call on a read path — it derives
    /// nothing.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="index"/> is past <see cref="int.MaxValue"/>. BIP32 reserves the top bit of a child number
    /// for hardening, so an index above that is not an unhardened address index at all — and silently wrapping it
    /// into one would derive a key for a different address than the path printed on the page.
    /// </exception>
    public static KeyPath KeyPathFor(Network network, uint index)
    {
        ArgumentNullException.ThrowIfNull(network);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(index, (uint)int.MaxValue, nameof(index));

        // 0 on mainnet, 1 on everything else, as BIP44 registers them. Regtest is the only other network the SDK
        // supports, and it shares testnet's coin type.
        var coin = network == Network.Main ? 0 : 1;

        return KeyPath.Parse(string.Format(
            CultureInfo.InvariantCulture,
            "84'/{0}'/{1}'/0/{2}",
            coin,
            Constants.UnilateralExitFundingAccount,
            index));
    }

    /// <summary>Zeroes the private key. Safe to call twice.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Array.Clear(_secret);
    }
}
