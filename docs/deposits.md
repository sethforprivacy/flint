[← Docs index](README.md)

# Funding the wallet on-chain

Under **Plugins → Flint → Advanced → Deposit address and unclaimed deposits**. Deposits have no navigation
entry of their own, because a Spark wallet is funded by customers paying invoices — a merchant never needs to
top it up for the plugin to work. A stuck deposit is flagged loudly on the status page, which links straight
to this page when there is actually something to claim.

The wallet has one **static Bitcoin address**. It does not rotate, so save it once and reuse it. Money sent
to it credits the store's Spark balance after **three confirmations**, at which point the SDK claims it —
budget half an hour or more, not seconds. It can land sooner: the Spark service provider will credit a deposit
before it matures for a spread, and the SDK takes that offer by itself whenever the spread fits the same claim
ceiling described below — so an early claim never costs more than the ceiling an ordinary one would be allowed.
A deposit credited that way is not listed as unclaimed while the provider finishes with it.

> **The failure worth understanding before you send anything.** Claiming a deposit costs an on-chain fee,
> and the SDK will only pay up to a configured ceiling. Above it the claim does not happen more cheaply —
> **it does not happen at all**, and the deposit sits unclaimed indefinitely. Spark's own default ceiling is
> a fixed **1 sat/vB**, which is below the mainnet floor essentially always: the plugin measured a market at
> `fastestFee=3, economyFee=2, minimumFee=1` and even there the default is under the half-hour rate. Left
> alone, an on-chain top-up simply never arrives, and nothing tells the merchant why.

So the plugin does three things instead:

1. **It never uses the default.** The ceiling is configured as *the network-recommended rate plus a leeway*
   (2 sat/vB by default, and never more than 100 whatever is stored) — the only form that tracks the
   mempool. A fixed rate low enough to be prudent today strands deposits in the next fee spike. Note that
   clearing the setting entirely is not an option the plugin offers, because in the SDK an absent ceiling
   means *automatic claiming is disabled*.
2. **It shows you what is stuck**, on the deposit page and on the status page, with the fee the claim
   actually needed.
3. **It gives you a one-click claim** at that fee — guarded by a per-store ceiling (25,000 sats by default)
   and by a backstop that refuses to spend more than half a deposit on claiming it, whatever the ceiling
   says. A rate-based cap knows nothing about the size of the deposit it comes out of; that is the only
   place the two are compared.

> **What that leaves uncovered, stated rather than implied.** The half-a-deposit backstop is on the claim
> *you* make. An **automatic** claim is Spark's own background worker: the plugin hands it a rate ceiling at
> startup and gets no say per deposit, and the SDK has no way to express a cap relative to the amount being
> claimed. So a small deposit maturing in the middle of a fee spike can still lose a meaningful share of
> itself to its own claim fee, and nothing will refuse it. Sending a large deposit rather than several small
> ones is the practical answer; the deposit page shows the rate in force and warns when it is already below
> what the mempool is asking. Neither the claim ceiling nor the leeway is editable from the UI or the API —
> both are operator-level values in the store's settings record.
