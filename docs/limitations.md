[← Docs index](README.md)

# Known limitations

- **LNURL and Lightning addresses are not strictly LUD-06 conformant.** The Spark SDK's receive
  request has no description-hash field, so when BTCPay asks for a description-hash-only invoice the
  plain description goes into the BOLT11 `d` tag and the `h` tag is left unset. Most wallets tolerate
  this; a strict one may reject the invoice. Pending an upstream Breez feature request.
- **Cancelling an invoice is local only.** Spark has no way to withdraw an invoice from the service
  provider, so a payment arriving for a cancelled — even a superseded — invoice still settles and
  credits the BTCPay invoice it was minted for. What cancellation actually buys is bookkeeping: the
  invoice is no longer offered to payers, and a late payment is attributed to it, not to its
  replacement. The plugin keeps reporting a cancelled invoice as unpaid (never expired) until it
  settles, so BTCPay's Lightning listener stays attached and can still deliver that credit — a listener
  drops an invoice whose status reads expired.

  **How that credit actually reaches BTCPay, since its listener cannot be relied on either.** BTCPay's
  Lightning listener only watches each invoice's *current* payment prompt. Replace BOLT11 X with
  BOLT11 Y — which BTCPay does whenever an LNURL invoice is re-quoted — and X leaves the watched set;
  after a restart it is never re-added, because that set is rebuilt from the prompts that exist now.
  So the plugin does not depend on notifying a listener. Every settlement is also written straight
  onto the BTCPay invoice the BOLT11 was minted for, found through BTCPay's own payment-hash index
  (`AddressInvoices`, which is insert-only and keeps the mint-time association forever) — and, for the
  hashes that index misses, through the plugin's own copy of the mint-time association kept from every
  prompt-mint event (LUD-21 off). The plugin records whether that credit landed and retries it on
  every reconciliation pass until it does, which
  also covers a payment that arrived while the server was down, a listening session that fell too far
  behind, and a crash between recording the settlement and telling BTCPay. The retry does not need the
  store's Spark wallet to be connected — crediting touches only BTCPay's own tables — so a store whose
  connection is broken still has its already-received money routed. The credit cannot happen
  twice: it is keyed on the payment hash, the same id BTCPay's own listener uses, so the two collide on
  BTCPay's payments primary key and exactly one of them records the money.

  **When the plugin gives up, and what it leaves behind.** Three bounds are worth knowing, and all three
  end the same way: one warning in BTCPay's log naming the amount and the payment hash, and a row marked
  as abandoned rather than as credited — so `SELECT` on the plugin's `InvoiceRecords` can always tell
  money that reached an invoice from money that did not.

  - A settlement whose payment hash BTCPay has **no invoice for** — a BOLT11 minted through this
    plugin's Greenfield endpoints, say — is retried for **seven days**, then reported once and given up
    on, because it can never be attributed. It is reported and marked on the first reconciliation pass
    after that seven days, not silently forgotten; the only settlements that escape the report are ones
    already older than a fortnight when this version was first installed, which are outside the walk
    entirely.
  - A BTCPay invoice with **no payment prompt** that could hold the payment is given up on immediately,
    since a prompt is never removed from an invoice once written and a retry would fail identically.
    This is very close to unreachable in practice — nothing in BTCPay deletes a prompt — and is listed
    because if it ever does happen the money is in the wallet, the invoice cannot be credited
    automatically, and only a human can reconcile it.
  - An LNURL prompt whose mint happened while this plugin was **not running**. The plugin keeps its own
    copy of the mint-time association (payment hash → BTCPay invoice) from every prompt-mint event, which
    BTCPay publishes whether or not LUD-21 is enabled — so a merchant who disables LUD-21 by hand does not
    lose the LNURL half of the index any more. The remaining gap is narrower and is about outages rather
    than settings: a prompt minted while the plugin itself was not running has no observer, and a late
    payment to it is reported as unattributable rather than guessed at. (The plugin forces LUD-21 on when
    it provisions a store; while that setting is on, core's own index covers the hash even through an
    outage.)

    The plugin's own copy of the association is also bounded, and that bound is worth knowing by name:
    rows are kept for **fourteen days** — the credit walk's own floor (seven days of credit retries plus the
    seven-day abandoned-reporting grace), the point past which nothing in the plugin will ever read the row
    again — and an hourly background pass deletes everything older, so this table, unlike `sdk.log` below,
    cannot grow without bound. On a server with no Flint store it never grows at all: prompt-mint recording
    is gated on at least one store being provisioned. The bound costs one case: a payment arriving **more
    than fourteen days after its prompt was minted** reaches an association this pass may already have
    deleted, and is reported unattributable — the same outcome as the outage gap above, and the same
    fourteen-day line as the bullet before it.

  One thing the plugin does *not* keep from BTCPay's own listener is worth naming as a non-limitation:
  the preimage is written onto the payment prompt as well as onto the payment, so LNURL
  proof-of-payment (LUD-21 `verify`) works for a Flint checkout exactly as it does for any other
  Lightning backend. The one case it is deliberately not written is a superseded BOLT11 — the prompt is
  offering the replacement by then, and its preimage is a different value.
- **Testnet and signet are unsupported**, because the SDK only offers mainnet and regtest.
- **The SDK's own log cannot be turned up to `trace`, on purpose.** The plugin installs the Rust SDK's
  logging subscriber, which writes `<DataDir>/Plugins/Flint/logs/sdk.log` *and* forwards every line into
  BTCPay's log. What it emits at each level was read line by line against a throwaway regtest wallet: at
  `info` and `debug` — the level Breez's production checklist asks for — nothing secret appears, but at
  `trace` the service provider's GraphQL **session token** is logged in full inside raw response bodies. That
  is a live bearer credential for the wallet, so a filter mentioning `trace` is refused and `debug` is used
  instead. Lines forwarded into BTCPay's log are additionally scrubbed of anything credential-shaped. The
  log directory is created owner-only; the SDK writes the file inside it at the process umask, which this
  plugin does not control. One gap worth knowing: the probe wallet was never paid, so the lines a completed
  payment produces are unaudited — the scrubbing is written against that gap rather than around it.
- **`sdk.log` is not rotated, and rotating it is yours to arrange.** The file at
  `<DataDir>/Plugins/Flint/logs/sdk.log` is opened and written by the Rust SDK, not by this plugin — nothing
  on the C# side can truncate, roll or size-cap it, so the plugin makes no promise about how large it gets.
  At the shipped `info` level it is not fast-growing, but it is unbounded and it grows for as long as the
  server runs. **Treat it as a file your host is responsible for**: point `logrotate` (or your container's
  log policy) at it, with the caveat that the SDK holds the handle open, so a rotation scheme that renames
  the file needs `copytruncate` rather than `create`. A ready-to-install `logrotate` configuration using
  `copytruncate` is at `scripts/flint-logrotate.conf`. It is safe to delete while the server is stopped.
  Nothing in the plugin reads it back; it exists for you and for a bug report.
- **Everything the plugin writes under the data directory is owner-only, and that is the only protection
  it has.** Each store's SDK state lives in `<DataDir>/Plugins/Flint/<storeId>/`, beside the log directory,
  and both are created `0700` — a directory without other-execute cannot be traversed to reach the files
  inside it, whatever mode the SDK gives them. The correction is applied on every start rather than only at
  creation, so an install laid down by an earlier version is hardened when it next runs. On a host where the
  mode cannot be set the plugin logs a warning and carries on, because a permission it could not tighten is
  not a reason to leave a merchant without a wallet. Note what this does **not** cover: anyone who can read
  the BTCPay data directory as the BTCPay user — a backup, a container volume, a misconfigured bind mount —
  has everything, including the data-protection keyring that decrypts every store's seed.
- **Run one BTCPay instance per data directory.** The SDK takes no lock of its own — a second connect to a
  storage directory an instance is already using was measured succeeding in 2 ms, silently — and its default
  storage is a single non-WAL SQLite file. Two BTCPay instances sharing one data directory would therefore
  put two writers on the database holding a funded wallet's record, which is a corruption risk rather than
  just the duplicate sweep it looks like. The plugin now takes an exclusive claim on each store's storage
  directory for as long as its wallet is running and **refuses to start that store's wallet** if another
  process already holds it, saying so loudly in the log. The store's Lightning is then unavailable on the
  second instance until one of them is stopped, which is the intended outcome: nothing else about a
  simultaneous second instance is safe either. Note the case this cannot see — two servers with *separate*
  data directories configured with the *same recovery phrase*. That is still two live instances on one
  wallet, and still able to sweep twice.
- **Do not run BTCPay itself in more than one place, and do not move the data-protection keyring into the
  database to try.** BTCPay has no cross-instance coordination of any kind — no advisory locks, no leases, no
  leader election. Two instances against one database double-pay Lightning payouts, double-broadcast on-chain
  payouts, and send every webhook and email twice, entirely independently of this plugin, so no amount of
  hardening here makes that deployment safe. Today a second instance cannot even start a Spark wallet on a
  shared store: the seed is in the database but the key that decrypts it is in the data directory, so the
  second instance reads the row and fails to decrypt it. **Persisting the ASP.NET data-protection keyring to
  shared storage removes that accidental protection** — for every store at once, with no warning — which is
  the specific thing not to do.
- **The displayed balance is indicative.** Spark's reported balance lagged a settled payment by ~20 s in
  testing and drifts by a few sats while the SDK reorganises the wallet's leaves. Nothing in the plugin
  derives settlement or any accounting figure from it. The sweep engine forces a wallet sync before reading
  it, which is the only thing that makes it current.
- **A sweep is not tracked to confirmation.** The service provider hands back the L1 transaction id
  immediately, which the plugin records and displays, but the SDK exposes no confirmation count — so a sweep
  the SDK stops reporting on is shown as *Sent* with its txid rather than promoted to *Confirmed*. Look the
  transaction up on-chain.
- **All sweep fee figures are regtest measurements.** They are used only for guidance text; every decision
  uses a live quote. Re-measure before relying on the defaults on mainnet.
- **An automatic deposit claim is bounded by fee *rate* and by nothing else.** The plugin's refusal to spend
  more than half a deposit on claiming it applies to a claim made by hand; automatic claiming happens inside
  the SDK, which offers no cap relative to the amount being claimed and no hook to refuse one. See [Funding
  the wallet on-chain](deposits.md). Closing this properly would mean the plugin doing all the claiming itself,
  which trades an overpayment risk for a worse one — a claim loop that stops means no deposit ever arrives —
  and is not something to ship without mainnet evidence behind it.
- **Neither post-MVP feature can be tested off mainnet.** Cross-chain sending is hard-gated — the SDK throws
  at connect on any other network — and Stable Balance is *accepted* on regtest and then never converts,
  because USDB does not exist there. So the unit tests run against a fake built to model the real SDK's
  hazards, and there is no CI coverage of either path against a live service. Budget mainnet sats.
- **A cross-chain sweep funded from a stablecoin balance cannot be retried.** The SDK rejects an idempotency
  key on any send with a token leg, so there is no key to deduplicate a second attempt. The plugin records
  the provider's quote id *before* sending and, after a crash, searches the payment history for it rather
  than sending again — and if it cannot find it, the sweep is written off with a message that says plainly
  that this is the strongest available evidence and not proof. Nothing is ever re-sent on that path. For the
  same reason, **a stablecoin-funded sweep whose quote arrives without a reference of its own is refused
  rather than sent**: with no idempotency key and no quote id there would be nothing to identify the payment
  by afterwards, and a payment that can never be traced is worse than one that never happened. A sats-funded
  sweep to the same destination is unaffected, because its idempotency key is the handle.
- **Conversions and cross-chain deliveries are poll-only.** None of the SDK's nine event variants concerns
  a conversion or a delivery, so their state changes only when the plugin's own reconciliation pass looks —
  which is the same pass that already runs before every sweep, extended rather than duplicated.
- **One SDK error classification is not covered by an automated test.** The live regtest test pins how the
  SDK reports insufficient funds, a malformed destination and a wrong-network address, because the sweep
  engine's decisions rest on telling those apart. It does **not** pin `IsExpiredFeeQuote`, which would need
  a deliberate >60-second gap between preparing and sending; that classification is matched on prose
  ("fee quote has expired") and a re-wording upstream would silently turn a normal re-quote into an
  unknown outcome that blocks the store's sweeps for five minutes. Note also that the classification test
  runs in a `continue-on-error` CI job — an SDK wording change will show as a failed job in the run
  summary but will not turn CI red, so it has to be looked at rather than waited for.
