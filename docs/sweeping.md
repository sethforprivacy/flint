[← Docs index](README.md)

# Sweeping the balance out

Spark holds the store's balance on an L2. Sweeping is the only thing that reduces
how much is exposed there, which is why the threshold is the most important setting on the plugin —
see [Trust model](trust-model.md). Two destinations are covered here: Bitcoin, and a stablecoin on an
EVM chain.

## Sweeping to Bitcoin

Under **Plugins → Flint → Sweeps** (also offered as optional step 2 of setup). Off
until a merchant turns it on. The reserve ("Leave behind") and the fee policy ("Take the exit fee out of
the swept amount") live on the **Advanced** page, because their defaults — no reserve, fee taken out of
the swept amount — are right for almost every store.

**Every sweep is a cooperative exit** — the plugin asks Spark's service providers to build and broadcast a
Bitcoin transaction. Sweeping never performs a unilateral exit — the only unilateral path in the plugin is
the experimental, environment-gated flow on the Advanced page, which nothing here can reach or trigger.
"Take the exit fee out of the swept amount" is the SDK's `FeesIncluded` fee policy on a cooperative exit
and nothing more.

**Exits cost a flat fee, whatever they carry.** Regtest measurements: 750 sats of service fee plus
1,200/1,440/1,680 sats of L1 broadcast fee by tier — 1,950/2,190/2,430 total — *identical* for every amount
from 294 to 99,901 sats. So a 5,000-sat sweep loses ~39% of itself and a 500,000-sat sweep loses ~0.4%. The
defaults encode that: sweep at 200,000 sats (~1%), never below 100,000 (~2.4% worst case), and cap the fee
as a **percentage** rather than a flat number, because a flat cap set today refuses every sweep the first
time mainnet broadcast fees rise past it.

> **On mainnet, expect to need roughly an order of magnitude more than these defaults.** The figures above
> come from a regtest chain at 1 sat/vB. At realistic mainnet fee levels the L1 broadcast component alone
> runs to tens of thousands of sats, so a sweep has to be well into the millions to stay under a 3%
> ceiling — which means a default-configured store on mainnet will sit on the fee-guard refusal until its
> threshold and minimum are raised. That is the intended failure direction (refuse rather than overpay),
> and the sweep page says so, but it does mean the defaults are a starting point and not a
> recommendation. Re-measure before relying on them.

The fee guard cannot be switched off. Clearing the percentage falls back to the default rather than
allowing anything; the form refuses a flat ceiling above the smallest sweep you allow; and whatever is
configured, the plugin will not pay more than half of what a sweep delivers.

**Destinations** default to a fresh, labelled address from the store's own BTC derivation scheme, reserved
and therefore rotated on every sweep, so sweeps are not all linked to one address on the blockchain. Once a
sweep's transaction id is known, the transaction itself is also labelled `flint-sweep` in the store's BTCPay
wallet — the same mechanism core uses for payouts — so the wallet's transactions list says where the money
came from. A store
with no on-chain wallet in BTCPay can set one fixed address instead, validated against this server's chain
both on save and again before every send. A store set to sweep into its own wallet that has no wallet is
**refused with a reason** — it never falls back to an address left over from an earlier configuration.

Every refusal is logged and recorded, so the history page answers "why has nothing swept?" rather than
leaving it in the server log. A recurring automatic refusal folds onto **one** row that carries how many
times and how recently it has happened, keyed on the *kind* of refusal rather than on its wording — the
wording contains live figures including a balance that drifts by a few sats, so a store parked on a
refusal would otherwise accumulate a row every couple of minutes forever. Coalescing is bounded to a day,
so a condition that stopped and came back reads as two episodes.

**Crash safety.** The SDK adopts the idempotency key given to `SendPayment` as its own `Payment.id`, verified
on real cooperative exits. So the plugin writes a `SweepRecord` carrying a fresh UUID *before* it calls the
SDK, and the next pass resolves anything unresolved with `GetPayment(key)` — a definitive answer about
whether the exit happened. Nothing is ever retried blind, and no new sweep starts while an earlier one's fate
is unknown. A record the SDK has never heard of after five minutes is written off as never sent; a send whose
outcome is genuinely unknown (a network failure) stays unresolved for the next pass rather than being guessed
at either way.

The pass runs every two minutes. Sweep *frequency* does not change what sweeping costs, because the trigger
is a balance threshold rather than a clock; the interval only bounds how long a balance sits above the
threshold and how long a crashed sweep stays unresolved.

**"Sweep now"** goes through the identical engine. It relaxes exactly two things — the automatic switch and
the balance threshold, both of which answer "should I be looking?" — and applies the minimum, the fee limits,
the dust floor and the destination rules unchanged, server-side. It shows a live quote first, then re-quotes
on confirm and re-checks the fee limit against the new number, because a Spark exit quote is only valid for
about a minute.
## Sweeping to a stablecoin on another chain

A third sweep destination, alongside the store's Bitcoin wallet and a fixed Bitcoin address: **an address
you control on an EVM chain**, delivered as a stablecoin through a bridge provider. **Mainnet only** — the
SDK refuses to start a wallet configured for cross-chain sending on any other network, so the option is
disabled elsewhere rather than saved and broken.

It is still a cooperative path. At the point money leaves the wallet it is an ordinary Spark transfer to the
provider's own Spark address, and the provider settles on the far side; there is no unilateral exit here
either.

What to expect:

- **The quote debits more than the amount being sent.** The source leg is overpaid to absorb the provider's
  fee and slippage — around `max(50 bps, ~50 sats)` in testing. The plugin leaves a 1% margin for it and
  still checks the quote, so a sweep is *refused* rather than sent when the balance only just covers the
  amount.
- **Small sends are poor value.** The fee has a fixed component of roughly $0.025 plus about 0.29%, so the
  smallest send the provider accepts costs about **3.3%** while a 50,000-sat one costs about **0.34%**. The
  minimum defaults to 50,000 sats for that reason, and the settings page refuses to save less.
- **There is no arrival estimate.** The SDK exposes none, so no page can show one — and nothing announces
  the delivery either, so the sweep history updates when the plugin next polls rather than the moment it
  lands. A sweep shows as *Sent* with the amount the provider expects to deliver, and gains the delivered
  figure later.
- **`USDT0` is not `USDT`.** The asset is matched exactly; the plugin will not substitute the LayerZero
  token for Tether.
- **The quote is sanity-checked against a real price, not just against itself.** A provider's stated spread is
  two numbers from the same quote in the same asset, so it says nothing about the *rate* applied — a quote
  offering $100 of USDT for $320 of bitcoin states a spread under half a percent. The plugin compares what
  would arrive against your store's own exchange rates and refuses a sweep losing more than 10% of its value.
  When no rate is available the sweep is refused rather than sent unchecked; sweeping is automatic, so the next
  pass simply tries again.
- **A mistyped address is caught when it can be.** An EVM address written in mixed case carries an EIP-55
  checksum, and the plugin verifies it — two transposed digits are otherwise 42 characters of perfectly valid
  hex, and delivery is irreversible. An address written entirely in lower or upper case carries no checksum and
  cannot be checked.
- **Only one provider works today.** Orchestra. Every Boltz route currently fails when the send is
  prepared, so the plugin filters them out and reports a Boltz-only destination as having no route — rather
  than attempting it and failing after a record exists. Which provider carried a sweep is recorded and
  shown.
- **An address carries no chain.** Nothing about `0x…` says which network it is on, so the chain is a
  separate choice, and getting it wrong sends money somewhere unreachable. Nothing verifies you control the
  address either.
- **The chain and asset are chosen from a list the provider publishes.** The plugin reads the orchestrator's
  public route table, keeps the EVM destinations reachable from Spark, and caches that for hours in the
  background — a settings page never waits on it, and a server that cannot reach the internet falls back to a
  small built-in list and still renders and saves. Either way the list is a convenience, not the authority:
  the live route table is read through the SDK before every send and is what decides whether a sweep goes. So
  a destination the list omits is not thereby forbidden — the Greenfield API takes both as free text, and a
  store already configured with one keeps it, including when it edits other settings on the page.
- **EVM chains only, for now.** Spark also reaches Solana, Tron, TON, XRP and Zcash, but the destination field
  on this page is an EVM address and is validated as one, so those are left out rather than offered and then
  refused at save.

When [Stable Balance](stable-balance.md) is on, a cross-chain sweep is funded from the **stablecoin** balance and its amount is
in dollars rather than satoshi — which is why that path has its own minimum, in whole units.
