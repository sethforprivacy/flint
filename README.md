<img src="assets/logo.png" alt="" width="72" height="72" align="right">

# Flint — nodeless Lightning for BTCPay Server

A [BTCPay Server](https://github.com/btcpayserver/btcpayserver) plugin that lets a store receive
Lightning payments without running a Lightning node. It runs the
[Breez Spark SDK](https://sdk-doc-spark.breez.technology/) in-process, gives each store its own Spark
wallet from a recovery phrase you hold, and wires that wallet into the store's Lightning and LNURL
payment methods for you — there is no connection string to copy and nothing to keep online but BTCPay
itself. The balance can then be swept automatically, on a threshold, to the store's own BTCPay on-chain
wallet, to a fixed Bitcoin address, or cross-chain to a stablecoin at an address you control on an EVM
chain; and it can be held in USDB between sweeps.

> [!WARNING]
> Flint is still in development and thinly proven in production. Use it with caution and amounts you
> can afford to lose, and read [Known limitations](docs/limitations.md) before putting money through it.

## Trust model

**What you are trusting, in one paragraph.** Spark is a 2-of-3 statechain operated by Lightspark, Breez
and Flashnet. A balance sitting on it is not in your sole custody the way an on-chain UTXO or a channel
you own is: every Lightning receive rides Lightspark's service provider, and every automated flow in this plugin performs
**cooperative exits only** — the sole unilateral-exit path is an experimental, environment-gated flow on the
Advanced page whose transactions the operator broadcasts by hand. Sweeping is
the only thing that reduces that exposure, which is why the sweep threshold is the most important setting
on the plugin. Stable Balance and cross-chain sweeps each add a further counterparty of a different kind:
a regulated stablecoin issuer whose token metadata says it can **freeze** the balance, and a bridge
provider that holds the funds between the two chains. Both are off by default.

Read the full **[Trust model](docs/trust-model.md)** — every party, what each can do, and what recourse
exists — before you configure anything.

## Requirements

What a server needs to *run* this plugin:

- **BTCPay Server 2.4.1 or newer.** That is the declared support floor, and BTCPay refuses to load the
  plugin on anything older. It is compiled against 2.4.1 as well.
- **A supported platform.** The Breez Spark SDK ships around 200 MB of native libraries for
  `linux-x64`, `linux-arm64`, `osx-x64`, `osx-arm64`, `win-x64` and `win-x86`. There is **no**
  `linux-musl` (Alpine) or `win-arm64` payload, so the plugin will not load on those platforms.
  Standard BTCPay Docker images (Debian-based) are fine.
- **Mainnet or regtest.** The SDK offers no testnet or signet, so neither is supported. Stable Balance
  and cross-chain sweeps are mainnet-only even on a supported network.
- **Disk for the SDK's per-store state**, under `<DataDir>/Plugins/Spark/`, plus an unrotated
  `sdk.log` you are expected to point `logrotate` at — see [Known limitations](docs/limitations.md).
- **Server-admin rights, or the *Non-admins can create Hot Wallets for their Store* policy**, to set a
  store up. Spark keeps keys on the server, so the plugin sits behind BTCPay's own hot-wallet gate.

## Installing

Requires **BTCPay Server 2.4.1 or newer** on a host the Breez SDK has native libraries for — see
[Requirements](#requirements) before you start, because Alpine-based images are not among them.

**From the BTCPay plugin registry** *(once this plugin is listed there — it is not yet)*: in your BTCPay
Server, go to **Server settings → Plugins**, find **Spark**, and install it. BTCPay restarts itself to
complete the install.

**From a release artifact**, which is the route available today: download
`BTCPayServer.Plugins.Flint.btcpay` from the [releases page](https://github.com/sethforprivacy/flint/releases),
then **Server settings → Plugins → Upload plugin** and select the file. BTCPay restarts to complete the
install. Every release artifact is signed; the releases page carries `SHA256SUMS` and the commands to
check both the hash and the signature before you upload anything. Verify them — this plugin holds
Lightning keys on your server.

Once installed, the plugin appears per-store under **Plugins → Flint**, and as a **Set up Flint** option
on the store's *Connect to a Lightning node* screen. It does nothing at all until a store is set up.

**Uninstalling the plugin is not the same as removing Spark from a store.** *Server settings → Plugins →
Uninstall* takes the code away; it does not touch a store's encrypted seed, its SDK storage under the data
directory, or the Lightning payment-method configuration the plugin wrote — so a store left configured
simply loses its Lightning provider. Use the per-store **Remove** page first if that is what you meant;
see [Setting a store up](docs/store-setup.md) for exactly what that destroys.

## Getting a store running

1. **[Set the store up](docs/store-setup.md)** — one page under **Plugins → Flint**, one question (where
   the seed comes from). LNURL and Lightning addresses work through BTCPay core the moment it finishes.
2. **[Turn sweeping on](docs/sweeping.md)** — off until you do, and it is the only thing that bounds how
   much of the store's money depends on the Spark operators. Set the threshold deliberately; the shipped
   fee defaults are regtest measurements and need raising for mainnet.
3. **[Fund the wallet on-chain](docs/deposits.md)**, if you want to start with a balance rather than wait
   for receives. Read the fee-ceiling warning first.
4. Optionally **[hold the balance in dollars](docs/stable-balance.md)** between sweeps, and drive the
   whole lot from a script with the **[Greenfield API](docs/greenfield-api.md)**.

## Documentation

**Running a store on it:** [setting a store up](docs/store-setup.md) ·
[sweeping the balance out](docs/sweeping.md) · [funding the wallet on-chain](docs/deposits.md) ·
[holding the balance in dollars](docs/stable-balance.md) · [known limitations](docs/limitations.md) ·
[trust model](docs/trust-model.md) · [automating it with the Greenfield API](docs/greenfield-api.md)

**Working on it:** [building](docs/building.md) · [tests](docs/testing.md) ·
[local development and migrations](docs/development.md) ·
[CI, releases & upstream updates](docs/ci-and-releases.md)

Full index: **[docs/](docs/README.md)**.

- [CHANGELOG.md](CHANGELOG.md) — what is in this release, and what its maturity actually is.

**Reviewing this for security?** Start with the [trust model](docs/trust-model.md) and
[known limitations](docs/limitations.md), which state the attack surface and the accepted risks plainly.
The reasoning behind each guard is written where the guard is, in the doc comments — every one of them names
the failure it exists to prevent, and several name a bug this project has already shipped once.

## AI disclosure

**This codebase is written by AI.** Every line of code and documentation in this repository is
AI-authored, produced under human direction and review. It is not casually generated, though:
changes are heavily peer-reviewed by other AI models before they land, and **no release is
published until it has passed a full security audit by a quorum of three independent models —
Kimi K3, GLM 5.3 and Grok 4.6** — each reviewing the release separately.

None of this is a substitute for your own judgement. Hold this plugin to the same standard you
would hold any code that touches money: read the [trust model](docs/trust-model.md), verify the
release signatures, and start with amounts you can afford to lose.

## License and attribution

MIT — see [`LICENSE`](LICENSE). Copyright (c) 2026 Seth For Privacy.

This plugin is greenfield, but the BTCPay plugin patterns it follows (per-store Lightning client
lifecycle, connection-string handler, LN setup-tab UI extensions, plugin-owned EF schema) descend
from Kukks' MIT-licensed
[BTCPayServerPlugins](https://github.com/Kukks/BTCPayServerPlugins) — specifically the Breez, Blink
and MicroNode plugins — which are gratefully acknowledged.

See [`NOTICE`](NOTICE) for the full third-party notices, including the sweep settings/UI shape
(harvested, with attribution to Kukks as the upstream copyright holder, from the prior-art Spark
plugin's treasury pages — its sweep *execution* logic was deliberately not used) and the sweep
destination pattern taken from Boltz's Liquid plugin.
