[← Docs index](README.md)

# Trust model

Spark is a 2-of-3 statechain system operated by Lightspark, Breez and Flashnet. Funds held on Spark
are not held in your own custody in the way an on-chain UTXO or a Lightning channel you own is:
every Lightning receive rides Lightspark's service provider, and unilateral exit is a multi-day last
resort that requires reachable operators and an external UTXO. Keeping the auto-sweep threshold low
is the best available mitigation, since it bounds how much is ever exposed on the L2.

Every sweep this plugin makes is a **cooperative exit**, and that is the only automated path off Spark: the
operators build and broadcast one Bitcoin transaction for a flat fee, and it lands in seconds. Set the sweep
threshold according to how much you are willing to have depend on those operators; sweeping is the only thing
that reduces it.

There is now an **experimental unilateral exit**, and it is deliberately hard to reach: it appears on the
store's Advanced page only when the server operator sets `FLINT_EXPERIMENTAL_UNILATERAL_EXIT=1` in BTCPay's
environment, and without that variable the page shows nothing and the routes do not exist. Read what it is
before counting on it:

- **The plugin never broadcasts.** It asks the SDK to build and sign the statechain's timelocked transaction
  tree and then shows you the raw transactions; pushing them, in dependency order, with `submitpackage` where
  a transaction and its fee-bumping child go together, is your job.
- **It still needs the operators reachable.** On the pinned SDK, preparing an exit talks to them — so the
  scenario you most want this for, operators gone for good, is the one it cannot serve yet. That changes when
  the SDK ships exit-from-local-state.
- **You have to fund it on-chain first.** The tree transactions cannot pay their own fees, so the exit is
  bumped by CPFP from a native-SegWit UTXO you send to an address the plugin derives from the store's seed at
  its own hardened account. Too little there and nothing gets built.
- **It settles in days, not seconds.** The outputs are behind CSV timelocks measured in blocks; the money is
  spendable when the last one expires, not when the transactions are signed.

So it is a last resort that costs days and attention, not a second sweep destination. If the operators
became unavailable and this path did not get you out, recovering funds still means using the store's recovery
phrase with another Spark wallet implementation.

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

**The store's Lightning connection string is a bearer spend credential.** Setup writes a
`type=breezspark;store-id=…;key=…` string into the store's Lightning payment method. Anyone who can read it
— any principal with `CanModifyStoreSettings` on the store, plus anything that string was ever pasted into —
can save it on *another* store on the same server and drive this store's wallet from there: receive into it
and spend from it. The embedded store id binds the key to a wallet; it does not bind the string to the store
it was saved on, because BTCPay's `ILightningConnectionStringHandler` never tells a handler which store is
being configured. This is the same property an LND macaroon has, with one difference worth knowing: the
plugin generated this credential for you rather than you choosing to issue it, and it is **not rotated** when
Spark is re-provisioned, so it outlives the access of whoever saw it and is invalidated only by removing
Spark from the store and setting it up again. Treat it like a macaroon, and keep the sweep threshold low
enough that the balance it could reach is a balance you can afford to lose.
