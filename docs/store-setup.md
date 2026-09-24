[← Docs index](README.md)

# Setting a store up

Everything lives under **Plugins → Flint** in the store's navigation, or behind the **Set up Flint**
option on the store's *Connect to a Lightning node* screen. There is one page and no connection string to
copy: the plugin writes the store's `BTC-LN` and `BTC-LNURL` payment-method configuration itself, so LNURL
and Lightning addresses work through BTCPay core once setup finishes.

Setup asks one question — where the store's Spark seed comes from:

1. **Generate a new recovery phrase** *(default, recommended)*. A fresh 12-word BIP39 phrase, shown once
   on BTCPay's standard recovery-seed screen. Lightning and on-chain funds stay under separate keys.
2. **Reuse the store's on-chain wallet seed.** Offered only when the store's BTC wallet is a hot wallet
   that BTCPay holds a recovery phrase for; watch-only, hardware and xprv-imported wallets are steered to
   option 1. Two caveats are printed on the page and both matter:
   - One phrase then controls the store's Lightning **and** on-chain funds, and the Spark identity
     publicly links the two wallets.
   - **BIP39 passphrases are not stored by BTCPay.** Only the words can be read, and the words *alone*
     derive a different Spark wallet than the passphrase-protected wallet your backup describes — funds
     would arrive somewhere that backup cannot recover. Nothing can detect this from inside the plugin, so
     if the store's on-chain wallet might have a passphrase, generate a new phrase instead.
3. **Import an existing recovery phrase.** 12/15/18/21/24 words, validated (word list *and* checksum) and
   canonicalised before anything is written. Use one Spark wallet on one store at a time.

All three are behind BTCPay's own hot-wallet gate — a server admin, or the *Non-admins can create Hot
Wallets for their Store* policy — because Spark keeps keys on the server.

Two optional steps follow the seed question: automatic sweeping ([Sweeping the balance out](sweeping.md)), and,
on mainnet, **accepting USDC and USDT** from customers while the store receives and keeps bitcoin
([Accepting USDC and USDT](stablecoin-payments.md)). Both can be changed later from the status page.

The phrase is encrypted with `IDataProtector` before it is stored and is **never rendered back**: there is
no reveal-seed feature. That cuts both ways. The data-protection keyring lives in the BTCPay data
directory, so losing that directory makes the stored copy unreadable and your own backup the only
recovery path.

If the wallet cannot start, setup says so and rolls back rather than reporting success — the case worth
knowing about is **two stores on one server sharing a seed**, which the plugin refuses because two SDK
instances on one wallet corrupt its storage. Give each store its own phrase.

The **status page** then shows the wallet's balance (indicative only — see
[Known limitations](limitations.md)) and what the store's Lightning payment method currently points at,
with a repair button if it has drifted. If that repair would replace a real Lightning node, it asks for
confirmation first: a connection string carrying a macaroon or certificate cannot be recovered afterwards.
The wallet's Spark identity, the recovery phrase's provenance, and seed replacement live on the
**Advanced** page.

**Removing** Spark from a store destroys the server's copy of the
keys and clears the Lightning configuration the plugin wrote — but only if that configuration still points
at this store's Spark wallet, so a merchant who moved to their own node keeps it. The wallet's local SDK
database is deliberately left on disk, since it records payments already settled on a wallet whose phrase
the merchant may still hold.

## How settlement is guaranteed

Neither obvious mechanism is reliable on its own, so the plugin does not depend on either:

- **The SDK's event stream drops completions.** A completed receive has been observed emitting only
  `PaymentPending` and never `PaymentSucceeded`, with the completion visible solely from a later storage
  read. It also duplicates: the same `PaymentSucceeded` fired twice, 57 ms apart, on two threads.
- **BTCPay does not re-poll pending invoices.** It calls `GetInvoice` once per invoice when the invoice is
  created or activated, and once per invoice when a listening session starts. Its one-minute
  `_ListenPoller` timer only calls `CheckConnections()`, which expires stale entries and restarts a *dead*
  listening session — it polls no invoices, and this plugin's session never dies.

So the plugin runs its own reconciliation task (once a minute, and once at startup) that re-checks every
unpaid invoice against the Spark service, oldest first. Settlement itself is a database compare-and-set,
so a duplicated event, a `GetInvoice` lookup and a reconciliation pass racing each other still result in
exactly one credit and one notification.

The task keeps looking for **one hour past an invoice's expiry**, because the service provider accepts a
late payment and Spark has no way to withdraw an invoice from it — so a just-expired invoice can still
take real money that has to be recorded. Past that hour it stops, which is a deliberate bound: a payment
arriving later than that stays in the wallet balance without being attributed to an invoice.

### And the notification is not what credits the merchant

Notifying BTCPay's listener only works while that listener is watching the BOLT11 in question — and it
watches only each invoice's *current* payment prompt. Replace BOLT11 X with BOLT11 Y (BTCPay does this
whenever an LNURL invoice is re-quoted) and X leaves the watched set; after a restart it is never re-added,
because the set is rebuilt from the prompts that exist now. X is still payable on the service provider.

So every settlement is **also written directly onto the BTCPay invoice its BOLT11 was minted for**, found
through BTCPay's own payment-hash index, which keeps the mint-time association permanently. The plugin
records whether that credit landed and the reconciliation pass retries it until it does — covering a
payment that arrived while the server was down, a listening session that fell too far behind, and a crash
between recording the settlement and telling BTCPay. It cannot happen twice: the credit is keyed on the
payment hash, the same id BTCPay's own listener uses, so the two collide on BTCPay's payments primary key
and exactly one records the money.

That retry needs no Spark connection — it touches only BTCPay's own tables — so a store whose wallet is
disconnected, its key rotated, or its configuration removed still has money it already received routed
onto the right invoice.

A settlement whose payment hash BTCPay has no invoice for is retried for **seven days**. After that, the
next pass reports it once — with its amount and its payment hash, at warning level — and marks it as
abandoned, which is a different mark from credited: the plugin's records keep saying that this money never
reached a BTCPay invoice, so a wallet balance can be reconciled against them. Nothing is reported twice,
and nothing is retried forever.
