[← Docs index](README.md)

# Trust model

Spark is a 2-of-3 statechain system operated by Lightspark, Breez and Flashnet. Funds held on Spark
are not held in your own custody in the way an on-chain UTXO or a Lightning channel you own is:
every Lightning receive rides Lightspark's service provider, and unilateral exit is a multi-day last
resort that requires reachable operators and an external UTXO. Keeping the auto-sweep threshold low
is the best available mitigation, since it bounds how much is ever exposed on the L2.

The plugin performs **cooperative exits only** (owner decision). It offers no unilateral-exit path, so if the
Spark operators were to become unavailable or refuse to process an exit, recovering funds would mean using
the store's recovery phrase with another Spark wallet implementation. Set the sweep threshold according to
how much you are willing to have depend on those operators; sweeping is the only thing that reduces it.

**Stable Balance adds a second counterparty, and a different kind.** Holding the store's balance in USDB
means holding a token issued by a regulated stablecoin issuer whose metadata says it is **freezable**: the
issuer can freeze the balance, and if they do, this plugin cannot move it, sweep it or convert it back. That
is not the same risk as the statechain operators — it is a named party subject to a jurisdiction — and it is
in addition to them, not instead. The feature is off by default and cannot be enabled without acknowledging
it, on the settings page and through the API alike.

**A cross-chain sweep adds a bridge provider** for the duration of the send. The funds leave the Spark
wallet as an ordinary transfer to the provider's address and depend on the provider to settle on the far
side; until it does, they are neither on Spark nor at the destination. The plugin records the provider's own
quote id before sending and reports what it says it delivered, which is the most the SDK exposes.

**Accepting USDC and USDT puts a conversion provider between the payer and the store** — Orchestra, the same
provider a cross-chain sweep uses, in the other direction. The payer sends their stablecoin to the provider's
deposit address on their own chain; the provider converts it and pays bitcoin into the store's Spark wallet. Until
it does, the money is with the provider and is neither the payer's nor the store's. If the provider never
delivers, the invoice is simply not paid, and the store has received nothing it could refund from — the payer's
claim is against the provider, and the invoice records the provider's order id and the payer's transaction hash to
pursue it with. The coin's issuer, Circle or Tether, is the payer's counterparty up to that point and never the
store's: the store receives bitcoin. The one exception is a store holding its balance in USDB through Stable
Balance, which takes on the freezable-issuer risk described above for that balance. The feature is off by default
and mainnet only.

**The store's Lightning connection string is a bearer spend credential, store-bound at save time.**
Setup writes a `type=flint;store-id=…;key=…` string into the store's Lightning payment method. The
embedded store id binds the key to a wallet, and the plugin refuses to save the string on any *other*
store: `SparkLightningClient.Validate` runs inside every save request — the store's own Lightning settings
page and the Greenfield PUT alike — where core has placed the store being configured, and rejects a string
naming a different store. A cross-store configuration that predates or bypasses that check is cleared
at startup and about every half hour thereafter by the plugin's configuration sweep, which also
rotates the victim's payment key so every previously leaked copy of the victim's string stops
resolving. What this leaves: anyone who can *read* the
string still holds a live credential for the wallet it names — save it on the victim's own store and it
works — so it must still be treated as a secret; what is closed is the import onto another store through
any HTTP save path, which is exactly the cross-store drive this paragraph used to describe as open. The
caveats on the enforcement are that BTCPay's `ILightningConnectionStringHandler` is still never told which
store is being configured, so the three plugin layers (*save-time refusal* in `SparkLightningClient.Validate`,
the *render-time resolution to the authorised store* in the setup-tab partials, and the *startup sweep*)
carry the enforcement rather than the string itself — with the middle one meaning the Lightning settings
page can no longer be steered by its form-bound store id into rendering another store's string: those
partials resolve the store from what the request was authorised for, never from the form-bound model id,
render nothing when the request carries no authorised store, and key every lookup and link off the
authorised id alone — and that the plugin generated this credential for you rather
than you choosing to issue it. It **is rotated on every provision** — setting Spark up again (same seed or
a new one) mints a fresh key and rewrites the store's Lightning configuration with it, invalidating every
copy of the old string — so a leaked string is revoked by re-running setup, without waiting for a removal.
Between provisions it never expires. Treat it like a macaroon, and keep the sweep threshold low enough that
the balance it could reach is a balance you can afford to lose.

**The instance administrator is a counterparty on any server you do not operate.** Setup stores the
store's Spark seed encrypted in the store's settings blob, and the data-protection keys that decrypt it
live in the same data directory — so whoever operates the server holds both halves, can decrypt the
seed and can spend the store's Lightning funds without any cooperation from the Spark operators. That is
the product model rather than an accident: the Spark SDK must hold the seed in-process to receive, an
always-on Lightning wallet has nowhere to re-prompt for it at boot, and even moving the keyring off-host
would not stop a host admin reading the running process's memory. The merchant's own backup of the phrase
is a recovery path, not protection from the operator. On a self-hosted instance this party *is* the
merchant, which is what the rest of the docs assume; on a shared or public-registration instance it is
someone the tenant may never have met. The setup page names this party before a seed is created or
imported: the recovery phrase is stored on this server, whoever operates it can decrypt it and spend the
funds, and a tenant who does not control the server should not put a seed there.
