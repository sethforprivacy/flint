[← Docs index](README.md)

# Accepting USDC and USDT, received as bitcoin

Under **Plugins → Flint**, in the *USDC and USDT* section of the status page — or as optional step 3 of setup.
Off until a merchant turns it on, and **mainnet only**.

Turning it on lets a customer pay any of the store's invoices in **USDC or USDT**, from whichever network they hold
it on. The payment is converted on the way in: **the store's Spark wallet receives bitcoin, and the store never
holds the stablecoin.** A merchant who wants bitcoin, with customers who would rather pay in dollars, no longer has
to choose between them, run a second plugin, or keep a token they do not want.

> The one exception is a store that has chosen to hold its balance in dollars with
> [Stable Balance](stable-balance.md). There, a USDC or USDT payment lands as USDB with the rest of the balance.
> The status page says which of the two applies.

## What the customer sees

Two more payment methods beside Lightning, **USDC** and **USDT**. Choosing one lists the networks it can be sent
from, each with its icon; choosing a network fetches a quote from the conversion provider and shows:

- a QR code with the network's icon in the middle — and, on EVM chains, a *Pay in wallet* button whose link fills
  in the token, the amount and the address;
- the exact amount to send, which includes that network's cost;
- the deposit address, labelled with the network and its icon.

The token contract is behind *More details*, with no copy button and a warning never to send to it. It is there so
a payer can check their wallet is sending the right token; pasted as the destination, it would burn the payment.

**Only networks the plugin can show an icon for are offered**: Ethereum, Solana, Tron, Base, Arbitrum, Polygon,
BNB Chain and Avalanche, wherever the provider serves that coin. The icon is the payer's check that they are
sending on the right network, which is the one mistake that loses a payment outright. The provider serves more —
Optimism, HyperCore, HyperEVM, Monad, Tempo, Plasma — and each can be added with its icon.

## What it costs, and who pays

The payer. The provider's cost for the chosen network is added to what the customer pays and shown as the
invoice's *Network Cost*, the way BTCPay shows an on-chain fee — so an invoice paid exactly settles exactly, and the
store receives the invoice's value. Measured on mainnet in September 2026:

| Invoice | Paid with | Network cost |
|---|---|---|
| $3 | USDC on Solana | 0.03 USDC (0.9%) |
| $3 | USDC on Base | 0.06 USDC (2.0%) |
| $10 | USDT on Tron | 3.12 USDT (31%) |
| $25 | USDT on Tron | 3.23 USDT (13%) |

Tron's cost is nearly all fixed, so a small invoice cannot be paid there at all (the provider refused $3). The
plugin also refuses any network whose cost is more than half of what is due. Either way the payer is told to choose
another network rather than shown the provider's error.

## Timing

- **A quote holds its price for about two minutes, but its address stays good.** The provider reprices a deposit
  that arrives later, and the SDK keeps watching an unpaid quote for a day. So the checkout keeps the same address
  on screen, and hands it out again for the same network, for an hour past the price's expiry, and only then asks
  the payer to fetch a fresh one. A reprice changes neither what the payer sends nor what the invoice is credited
  with — only how much bitcoin reaches the wallet, which is the exposure any crypto invoice carries between its rate
  and its payment.
- **A payment takes a few minutes to show.** The payer's transaction has to confirm on its own chain, and the
  provider has to deliver on Spark, before the invoice is credited.
- **A payment that arrives after the invoice expired is still credited to it**, the way BTCPay records any late
  payment.

## How a payment finds its invoice

The provider pays every conversion into the same Spark wallet, and nothing in what arrives names an invoice. So the
plugin records every quote it shows, and matches each arrival to its quote by the figures the provider fixed when it
made that quote. Two live quotes on one network are never asked for exactly the same amount — a millionth is added
where one would collide — so an exact payment is always attributable.

What that asks of the payer: **send exactly the amount shown, in one transaction, on the network shown.** A
different amount is still credited, with what actually arrived, whenever its quote can be told apart from the
others. One that cannot — two identical quotes and an inexact payment, say — stays in the wallet and is reported
once in BTCPay's log for a human to reconcile, rather than guessed at.

The invoice's page then lists each USDC/USDT payment with its network, the payer's own transaction hash (what a
merchant needs to chase a payment on the other chain), and what reached the wallet.

## Turning it on and off

The switch turns both coins on or off together. A store that wants only one can switch the other off in BTCPay's
own checkout settings, which the plugin respects. Turning it off stops new invoices offering them; every quote
already shown still settles. The same switch is on the API:
[`GET`/`PUT /api/v1/stores/{storeId}/spark/stablecoins`](greenfield-api.md).

## Before you turn it on

- **The conversion provider holds the payer's coin while it converts.** See [Trust model](trust-model.md).
- **A custom rate script needs USDC and USDT rules.** The plugin registers default rules — `USDC_USD = 1` and
  `USDT_USD = 1`, crossed through bitcoin for other currencies — and a store with its own rate script must carry
  equivalents, or the payment methods cannot be priced.
- The rest is in [Known limitations](limitations.md).
