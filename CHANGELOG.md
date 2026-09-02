# Changelog

All notable changes to this plugin are recorded here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.1.8.0] - 2026-08-24

### Added

- **Plugin update webhook.** A new server-level setting (`PUT /api/v1/server/spark`) accepts an
  `updateWebhookUrl`. Once set, Flint checks the BTCPay plugin registry daily and POSTs a
  `plugin.update-available` event payload when a newer version is found. The payload includes
  `pluginIdentifier`, `installedVersion`, and `availableVersion`. Each available version is
  notified at most once. Read the current setting with `GET /api/v1/server/spark`. Both endpoints
  require `canModifyServerSettings`.


## [0.1.7.0] - 2026-08-24

### Added

- **GET sweep record by idempotency key.** New endpoint
  `GET /api/v1/stores/{storeId}/spark/sweep/{idempotencyKey}` returns the full sweep record for a
  given key. Requires `canViewStoreSettings`. Returns 404 with code `sweep-record-not-found` when no
  record matches.

- **Destination address override in POST sweep.** The `POST .../spark/sweep` request body now accepts
  an optional `destinationAddress` field. When supplied, the sweep sends to that Bitcoin address
  instead of the store's configured sweep destination. The address is validated against the server's
  chain before the engine runs. Not supported for EVM cross-chain sweep mode (returns 422).

- **Force flag in POST sweep.** The request body now accepts an optional `force` boolean (default
  false). When true, the sweep proceeds even if the Spark balance is below the store's configured
  minimum sweep amount. The absolute on-chain protocol minimum (`Constants.MinimumOnchainSendSats`)
  still applies and cannot be bypassed.


## [0.1.6.2] - 2026-08-24

### Added

- **Sweep webhook fires on failed sweeps.** When a sweep is attempted and fails (Spark rejects it or
  reports it as failed), Flint now POSTs an `event: "sweep.failed"` notification to the configured
  webhook URL. The payload includes `storeId`, `trigger`, `reason`, and — when a sweep record was
  created before the failure — `idempotencyKey`, `amountSats`, `destination`, and `destinationMode`.
  Same retry logic as the success notification (3 retries with exponential backoff).

### Changed

- **Success webhook payload includes `event: "sweep.swept"`.** Added to let receivers distinguish
  success from failure notifications on the same endpoint without inspecting other fields.


## [0.1.6.1] - 2026-08-24

### Changed

- **Sweep webhook delivery retries on transient failures.** Flint now retries a failed webhook POST up
  to three times with exponential backoff (2 s, 4 s, 8 s) before giving up. 5xx responses and network
  errors are retried; 4xx responses are not (they indicate a permanent client-side rejection and will not
  resolve on their own).

## [0.1.6.0] - 2026-08-23

### Added

- **`POST /api/v1/stores/{storeId}/spark/sync` forces a wallet sync and returns the current balance.**
  The balance returned by other endpoints is read from the SDK cache without forcing a sync and may lag
  settlement by up to 20 seconds. The new endpoint forces an explicit sync before reading, so the
  returned `balanceSats` is current at call time. Requires `btcpay.store.canmodifystoresettings`.
  Response: `{ "walletRunning": bool, "balanceSats": long, "syncedAt": timestamp }`.
- **Sweep webhook.** Setting `sweepWebhookUrl` in the sweep configuration causes Flint to POST a JSON
  payload to that URL after each successful sweep. The payload includes `storeId`, `idempotencyKey`,
  `txId`, `amountSats`, `feeSats`, `destination`, `destinationMode`, `trigger`, and `completedAt`.
  Delivery failures are logged as warnings; the sweep record is the authoritative source. The field is
  included in `SweepSettings.Clone()` so it survives a seed change.
- **Sweep configuration warnings.** `GET /api/v1/stores/{storeId}/spark/sweep` now includes a `warnings`
  array. On mainnet, entries appear when the balance threshold or minimum sweep amount are below the
  recommended defaults (which were measured on regtest and may not hold on mainnet).
- **`scripts/setup-stores.sh`** - bash script for headless provisioning of one or more stores via the
  Greenfield API. Saves each store's recovery phrase (optionally GPG-encrypted) immediately on
  provisioning, since the API returns it exactly once.
- **`scripts/flint-logrotate.conf`** - logrotate configuration for `sdk.log`. Uses `copytruncate`
  because the Rust SDK holds the file handle open; a rename-based rotation leaves the SDK writing to
  the renamed file.
- **`docs/railway.md`** - deployment guide for Railway: persistent volume requirements, environment
  variables, log rotation options, and a post-deploy verification script.

## [1.0.4] — 2026-09-02

### Security

- **A store owner can no longer read another store's connection string through the setup page's
  extension points.** The two Spark setup-tab partials resolved everything from the form-bound
  `Model.StoreId`, and BTCPay's Lightning-setup POST never overwrites the view model's store id — so
  a user holding `canmodifystoresettings` on store A could make the page re-render with store B's
  `type=flint;store-id=…;key=…` bearer spend credential embedded in it (the attacker reaches the
  partial on every validation-failure re-render, and there are six). Both partials now resolve the
  store the request was authorised for (`HttpContext.GetStoreDataOrNull()`, populated by core's
  authorisation on GET and POST alike) and render nothing when there is none or it disagrees; the
  connection-string lookup takes only the authorised id and documents that as a caller contract.
  Regression tests render the actual compiled partials for all three cases (and the tests were
  shown to fail when the guard is bypassed). Verified end to end against a live BTCPay v2.4.2 host:
  the forged re-render shows nothing, the owner's own view is unchanged. Core-side companion:
  [btcpayserver#7544](https://github.com/btcpayserver/btcpayserver/issues/7544).
- **A Spark SDK connect that throws rather than hangs no longer leaves the store's wallet locked
  out of its own storage.** `StartInstanceAsync` handed the `FileShare.None` storage lock over only
  on its timeout and success paths; a throw (bad Breez API key, corrupt SQLite store, unsupported
  target, listener setup failing) escaped both and every later reconfigure of that store failed with
  the misleading "Another process is already using this store's storage" refusal until a server
  restart. The region now releases the lock — and any SDK client it connected but never adopted —
  on every exit; a factory test proves the second save for the same store is not refused. The
  SDK's abandoned event channel is completed along the same path.
- **The funded-regtest CI artifacts can no longer publish the one thing the suite exists to catch.**
  `forwarded.log` was written before any gate and `preimage-audit.md` printed every 64-hex run
  verbatim into a 14-day downloadable artifact even when `sdk.log` was withheld for carrying the
  seed, a payment preimage or the provider session token. The same withholding gate now covers the
  scrubbed stream, preimages print one-way fingerprints everywhere (including the assertion the
  suite makes into the public job log), occurrence counts and classifications survive, and a
  `<file>.WITHHELD.txt` marker names what was withheld and why.
- **The dependency graph is pinned and hash-verified at restore.** Committed `packages.lock.json`
  for the plugin and test projects (covering the ~200 MB native payload by content hash), an exact
  `[0.23.0]` pin for `Breez.Sdk.Spark` on the comment that always claimed it, a root
  `NuGet.config` restricted to api.nuget.org with `packageSourceMapping` pinning every package id
  to that source, locked-mode restores that fail the build on any graph drift, and the release
  packaging no longer restores from a prefix-matched NuGet cache. The Breez update automation
  (check script and bump workflow) survives the bracketed pin and preserves it when bumping.
- **SDK exception text shown to merchants is scrubbed at the single choke point.** `SparkErrors.
  Describe` relays Breez error payloads (with their `@v1=` prefix stripped) into banners, Greenfield
  4xx bodies, sweep errors and claim outcomes; its output now passes through the log scrubber, so a
  secret-shaped payload in an SDK exception cannot reach a merchant's screen, the API or the
  database verbatim. The fail-closed replacement text is merchant-phrased rather than a redaction
  marker.
- **Rejected imports no longer land in any cache.** `SparkController` sets `ResponseCache(NoStore)`
  at class level: a rejected seed-import re-render carries the submitted recovery phrase back to the
  merchant, and nothing set cache headers before.

### Changed

- **The shipped native payload set drops win-x86.** The Breez SDK's 32-bit Windows library (~15 MB,
  15% of the pre-prune payload) is deleted at packaging and the packaged RID set is asserted to be
  exactly `linux-x64`, `linux-arm64`, `osx-arm64`, `osx-x64` and `win-x64` — no BTCPay host loads
  32-bit plugins. This is a support change: instances pinned to a 32-bit runtime were already
  unable to run any of the plugin's IL, and the 64-bit payloads were and are unchanged.
- **The cross-store configuration sweep runs at startup plus every half hour, and classifies each
  store from the store rows it already loaded.** The backstop rode the reconciliation's one-minute
  cadence and, per store, re-fetched the store by id to then parse the same two JSONB columns it had
  just materialised and thrown away — 1+N round trips and 2N parses per pass over every store on
  the server, to find a condition save-time validation refuses on every HTTP path. It now runs from
  one loaded store table, on a 30-minute schedule whose launcher-fired first pass is the startup
  sweep. Remediation semantics (clearing the cross-store config and rotating the victim's payment
  key) are unchanged.

### Performance

- **The settleable-invoice walk now seeks a partial covering index instead of walking a store's
  whole invoice history.** `ListForReconciliationAsync` ordered by creation, so a store's abandoned
  checkouts — unpaid and never pruned — sat permanently at the front of every pass: on a seeded
  500k-row history the planner walked and discarded 491k rows per pass (~62 ms, ~9.5k buffers). The
  walk now orders by expiry over `IX_InvoiceRecords_StoreId_ExpiresAt_Settleable`
  (`(StoreId, ExpiresAt)`, partial on non-paid invoices), turns the same query into an indexed range
  seek at both the 500-row and 500k-row shapes, and keeps keyset resume, per-pass caps and the
  settleability of recently-expired invoices exactly as they were. A Postgres contract test pins the
  planner's use of the index.
- **The payment-hash prompt recording stops writing for stores that do not use Flint, and its table
  retains rows for 14 days.** The indexer previously subscribed to every BTCPay Lightning prompt
  mint (the LNURL path writes one per payer request) and nothing ever deleted: its rows are only
  read within the credit walk's 7-day retry horizon plus 7-day reporting grace. Recording is now
  gated on any Flint store being provisioned (re-read live per event, so provisioning re-arms it
  without a restart), and an hourly task deletes rows past the walk's own listing floor via a single
  indexed `ExecuteDeleteAsync` — see `docs/limitations.md` for the window and its one stated edge.

### Fixed

- **A platform-refused storage claim no longer tells the merchant where the storage is.** The
  lock-refusal path for `UnauthorizedAccessException` echoed the exception message (an absolute
  path) to `canviewstoresettings` users; it now returns the same fixed merchant wording the
  permission failure already used, and the OS detail goes to the operator log as a structured field.
- **The cross-chain catalog client no longer follows redirects**, so a moved or hijacked catalog
  source surfaces as a fetch failure (treated as "no routes this round") rather than being silently
  trusted wherever it pointed.
- **The strip script's docker fallback now actually strips the Mach-O payloads it handles.** The
  fallback installed llvm but copied the file and verified the untouched copy, printing a false
  "already stripped" no-op; the `llvm-strip -x` step now sits between the copy and the signature
  verification, and the fallback was exercised end to end (page-hash verification and dlopen probe
  on arm64 macOS).
- **`plugin-register.sh` writes valid dev settings for any path.** The JSON was printf-interpolated
  into the file (broken by quotes/backslashes, silently truncated if jq was missing); it is now
  emitted with `jq -n --arg` behind an availability guard, and `docs/building.md` lists jq as a
  prerequisite.
- **The Postgres test harness truncates all four plugin tables**, including `InvoicePaymentHashes`
  (rows previously leaked across tests and could mask a credit-gateway fallback regression).

### Documentation

- `docs/trust-model.md` enforces the three-layer enumeration (save-time refusal, render-time
  authorised-store match, periodic sweep) and states the sweep's cadence.
- `docs/testing.md`'s artifact table states the withholding conditions for both artifact files and
  the fingerprinting behaviour.
- `docs/limitations.md` documents the 14-day payment-hash retention window and the one edge a
  payment minted near the window's floor can still meet.
- `docs/ci-and-releases.md` documents the shipped RID set and the packaging prune step.


## [1.0.3] — 2026-08-28

### Changed

- **The osx native payload is now stripped from CI builds too, and the strip runs in a pinned
  container.** The 1.0.2 strip covered the linux `.so` files; the osx dylibs shipped as-is from the
  linux runner because "GNU/LLVM strip cannot rewrite Mach-O" — the LLVM half of that claim was
  wrong. `llvm-strip` rewrites Mach-O fine, and for these payloads the rewrite preserves the code
  signature: the stripped arm64 dylib passes `codesign -v --strict` and `dlopen`s on a real arm64
  macOS, verified on both the runner's llvm-18 and a current brew llvm. Because that validity is an
  observed property rather than a contract, the script now reads each dylib's CodeDirectory before
  stripping (only unsigned and ad-hoc/linker-signed payloads are rewritten; a Developer ID-signed
  one is skipped, as the macOS path already did) and re-verifies every page hash after stripping,
  discarding the result if any hash fails — a fat artifact is a size regression, an invalid
  signature is a broken plugin. Net effect: the packed `.btcpay` drops from ~51.7 MB to ~31 MB.
  The strip step itself moved off the mutable runner image and into the digest-pinned
  `ubuntu:24.04` container (apt-verified llvm-18), so the toolchain that rewrites the attested
  bytes is pinned like everything else on the release path.
- **The strip script no longer risks leaving probe or backup files inside the packaged artifact.**
  The ELF probe (and the new Mach-O copies) are created outside the build output directory; a
  leaked 60 MB probe file previously could have been zipped into the `.btcpay` by a crash at the
  wrong moment.

### Fixed

- **`SSH.NET` is pinned to 2026.0.0**, clearing NU1903 (CVE-2026-48798, GHSA-q939-rpr3-3284, high:
  `ScpClient` recursive download could write outside the target directory on a malicious server).
  The pin forces the resolved dependency graph to the fixed version while `ExcludeAssets` keeps
  SSH.NET and its BouncyCastle dependency out of the plugin's build output — the plugin never
  calls SSH.NET, so nothing about the shipped artifact changes. Drop the pin once the pinned
  btcpayserver submodule carries a fixed SSH.NET on its own.
- **All GitHub Actions are pinned by commit SHA** (`checkout`, `setup-dotnet`, `cache`,
  `upload-artifact`, `attest-build-provenance`) with Dependabot continuing to move the pins.
  Mutable major tags on the workflow that mints the release attestation were the same class of
  supply-chain risk the docker fallback image was already pinned against.
- **The Postgres service image in CI is digest-pinned**, and **`SHA256SUMS` contents are verified
  against the packed artifacts before attestation** — the sums file is what release consumers
  trust, so CI now proves it lists exactly the shipped files with the hashes they carry.
- **The weekly update workflows' discovery jobs run with read-only permissions** — the write
  powers the bump jobs need no longer leak into the scheduled checks.
- **`global.json` pins the .NET SDK** (`10.0.400`, `rollForward: latestFeature`) so local and CI
  builds resolve the same compiler.

### Performance

- **`InvoiceRecords` gained a partial index for the credit sweep.** The every-minute cross-store
  query behind `ListStoreIdsAwaitingCreditAsync` (Paid, uncredited, settled in the window,
  `DISTINCT StoreId`) had no usable index and scanned the whole table each pass; the new partial
  index on `(StoreId, SettledAt)` filtered to exactly that predicate makes both it and the
  per-store `ListUncreditedAsync` index-only over roughly the unsettled tail of the table.


## [1.0.2] — 2026-08-27

### Changed

- **The packaged plugin is ~23% smaller: linux native libraries are stripped at packaging time.**
  Breez ships its Rust `.so` files with ~28 MB of DWARF debug info apiece — sections that are never
  mapped at run time, but that nonetheless travel inside every `.btcpay`. Packaging now runs
  `scripts/strip-native-payloads.sh` between the build and PluginPacker: 196 MB of runtimes become
  ~100 MB, and the artifact from 67.4 MB to 51.7 MB. The osx dylibs are additionally stripped when
  packaging happens on a macOS host (GNU strip cannot rewrite Mach-O; the step warns and ships them
  as-is on linux runners), and the Windows DLLs are untouched — they carry no strippable debug
  data. The costs are stated where the fix lives: symbolised native backtraces on linux are lost
  (Rust panic *messages* survive), and the shipped hashes no longer match Breez's upstream
  byte-for-byte — the script prints each pre-strip upstream sha256, MIT permits the modification,
  and the Sigstore attestation still binds the artifact to this repository and commit.
- **A store's reconciliation pass shares one Spark payment-history scan across an invoice page.**
  Each invoice with no recorded SDK payment id previously ran its own paged history scan — up to
  ten pages — every pass. A quiet store with five waiting invoices and no traffic paid five full
  scans per minute to find nothing. The pass now runs one scan per page anchored to its oldest
  unpaid invoice and settles records from a payment-hash index; a miss counts as unpaid only when
  the shared scan reached the end of its window, and anything else (a capped or failed scan, a
  record with a recorded id) keeps its own scan, bit-for-bit the path it took before. Settlement
  coverage is unchanged; the number of SDK calls per idle store per minute drops from one per
  unpaid invoice to one.
- **The Spark network status is read once per process, not once per store per request.** It is a
  process-global provider status; every configured store's status page and every Greenfield status
  call re-struck the identical third-party request. Successful reads are now reused for 45 seconds
  and failed reads back off for 10, with concurrent first callers folded into one round trip. The
  cached value is diagnostic only — no settlement, sweep or credit decision reads it.
- **SDK log lines below the operator's effective log level no longer pay for redaction.** The
  scrubber's five regex passes run only on lines that will actually be emitted. Lines that are
  emitted scrub exactly as before.


## [1.0.1] — 2026-08-26

### Changed

- **The setup page now tells a tenant whose store is not on their own server who holds their seed.**
  On a shared or public-registration instance, the store's Spark recovery phrase is stored encrypted
  on the server, and whoever operates the server can decrypt it and spend the store's Lightning
  funds. The setup page previously framed the stored copy as a recovery fact ("unreadable without
  this server's data-protection keys") — the wrong side of the truth about an operator — and now says
  plainly, before a seed is created or imported, that the server operator is a custodian, with a
  stronger warning for a store manager who is not a server admin. The same disclosure is on the
  Greenfield provisioning endpoint and in SECURITY.md, the README trust model and the trust-model
  document.


## [1.0.0] — 2026-08-26

### Security

- **The payment-hash → invoice association no longer depends on LUD-21.** BTCPay writes an LNURL
  prompt's payment hash into its payment-hash index only while LUD-21 is enabled, so a merchant who
  disables LUD-21 by hand removes the LNURL half of the association from core's tables, and a late
  payment to a superseded LNURL bolt11 after a restart reaches the Spark wallet with no BTCPay
  invoice credited. The plugin now keeps its own copy of the mint-time association from every
  prompt-mint event — which BTCPay publishes whether or not LUD-21 is on — and the credit gateway
  falls back to it whenever core's own index has no row. The one case left unattributable is a
  prompt minted while the plugin itself was not running.

### Changed

- **Breez.Sdk.Spark bumped to 0.23.0** (~200 MB of native libraries rebuilt against the new SDK).
  0.23.0's only breaking binding change is a new `receiverIdentityPublicKey` field on
  `ReceivePaymentMethod.Bolt11Invoice` (bolt11 invoices for external Spark recipients); the plugin
  passes a null receiver to preserve the pre-0.23.0 behaviour of crediting the connected wallet,
  which is what every LNURL receive here needs. The rest is additive — instant deposit claims and
  the new `DepositInfo` fields are default-off / ignored. Verified on mainnet against a live
  Lightspark service provider on the test servers; the SDK's own storage schema migrates to
  version 40 on startup.


## [0.1.5.5] — 2026-08-25

### Security

- **A superseded bolt11 paid after a restart now reaches its BTCPay invoice.** Recording the settlement
  was only half the job. BTCPay's Lightning listener matches a notification against a set built from each
  invoice's *current* payment prompt, so once bolt11 X has been replaced by Y and the server has
  restarted, only Y is watched — X stays payable on the service provider, and a payment to it settled in
  this plugin's records while the merchant's BTCPay invoice stayed unpaid, with the funds already in their
  Spark wallet. The plugin no longer relies on that listener: every settlement is also written directly
  onto the BTCPay invoice its bolt11 was minted for, found through BTCPay's own payment-hash index (which
  keeps the mint-time association permanently), and each settlement now records whether that credit
  landed so the reconciliation pass can retry it until it does. That retry also closes three smaller
  holes of the same shape — a payment that arrived while the server was down, a listening session
  saturated past its retry allowance, and a crash between recording the settlement and notifying BTCPay.
  The merchant cannot be credited twice: the credit is keyed on the payment hash, the same id BTCPay's own
  listener uses, so the two collide on BTCPay's payments primary key and exactly one of them records the
  money. Crediting an invoice that belongs to a different store than the wallet the money arrived in is
  refused outright and logged. The retry needs no Spark connection — crediting touches only BTCPay's own
  tables — so a store whose wallet is disconnected, its key rotated or its configuration removed still has
  money it already received routed onto the right invoice. LNURL proof-of-payment keeps working too: the
  preimage is written onto the invoice's payment prompt, which is where LUD-21 `verify` reads it from, and
  is deliberately not written when the prompt has since moved on to a replacement bolt11.
- **A settlement that can never be credited is now reported, once, instead of going quiet.** A payment
  whose hash BTCPay has no invoice for — a bolt11 minted through the plugin's own API, say — is retried
  for seven days. After that the next reconciliation pass logs it once at warning level with its amount
  and payment hash and marks it abandoned, which is recorded separately from "credited": the plugin's
  records keep saying that this money never reached a BTCPay invoice, so a merchant's wallet balance can
  be reconciled against them. The same applies to a BTCPay invoice with no payment prompt able to hold the
  payment. Nothing is reported twice and nothing is retried forever.

## [0.1.5.4] — 2026-08-24

### Security

- **A late payment of a superseded or cancelled invoice now credits the BTCPay invoice it was minted
  for.** The Spark SDK has no way to withdraw an invoice from the service provider, so a bolt11 that
  BTCPay replaced (a sequential request to a public TopUp LNURL callback, for example) remains
  payable. Previously the plugin marked the invoice cancelled locally and refused the later
  server-confirmed settlement, leaving the funds unattributed in the merchant's Spark balance while
  the BTCPay invoice stayed unpaid. Now the settlement is recorded and published like any other: the
  payment's hash names the invoice it pays, and that invoice is the one BTCPay credits. A cancelled
  invoice is also reported as unpaid rather than expired until it settles, so BTCPay's listener does
  not drop it before a late payment can arrive.
- **A store can no longer save another store's Lightning connection string, and existing
  cross-store configurations are swept.** The connection string is a bearer credential for the
  wallet it names, and the connection-string handler is never told which store is being configured,
  so a store could previously be pointed at another store's wallet. Saving one now fails validation
  on every HTTP path with a generic error, and a startup-plus-periodic sweep clears any cross-store
  configuration (with the victim's payment key rotated, so previously leaked copies of the victim's
  string stop resolving).

## [0.1.5.2] - 2026-08-22

### Changed

- **The Advanced page no longer displays the stored Breez API key.** The key is not a secret in Breez's
  model, but nobody else should be using a store's key either, so the page has no reason to print it into
  the DOM. The form now shows only whether an override is set; a new key replaces the current one, and an
  explicit "use the built-in key" button clears it — an empty field is what an untouched form looks like,
  so it no longer means "clear" and is refused instead.

## [0.1.5.1] — 2026-08-22

### Added

- **A store can use its own Breez API key.** The Advanced page gains a field for it, with a link to
  Breez's request form. The plugin's built-in key is shared by every install; on Breez's own suggestion,
  a merchant holding their own key loses nothing if the shared key is ever revoked or rate-limited.
  Saving restarts the store's wallet so the key takes effect immediately, and a key Spark refuses to
  start with is rolled back rather than left stored in front of a dead wallet. Leaving the field empty
  returns the store to the built-in key. (The setting itself predates this release; it was previously
  reachable only by editing the store's settings blob.)

## [0.1.5] — 2026-08-21

### Security

Findings from a third external review pass, each verified against the source before fixing:

- **Provider-supplied fees can no longer wrap negative past every fee ceiling.** Two fee sums cast the
  SDK's u64 components to `long` raw (Lightning) or clamped each component but added them unchecked
  (cooperative exits); a wrapped-negative fee would have passed every `<=` limit, including the 50% hard
  backstop. All conversions now saturate at `long.MaxValue`, and both fee approvers additionally refuse a
  negative fee outright as a backstop.
- **The setup page's sweeping opt-in works again.** A `[BindNever]` attribute intended for the
  `AlreadyConfigured` flag had drifted onto `EnableSweeping` when the property moved — attributes attach to
  the next declaration, comments notwithstanding — so a merchant who ticked "sweep automatically" at setup
  got a success message and no sweeping, while the flag it was written for was overpostable. A reflection
  test now pins the placement.
- **A token-funded write-off is gated on the token balance, exactly as a sats write-off is gated on sats.**
  Writing off a token row unblocks the same pass to plan a fresh keyless bridge send — nothing at the
  provider can dedupe a second one — so a held balance below what the row says was sent now keeps the row
  blocking, bounded by the same one-hour escalation as the sats gate.
- **The packaged `.btcpay` now carries NOTICE and LICENSE.** The artifact redistributes Breez's
  MIT-licensed binaries and their notices did not travel with it; the build output now includes both files
  and the packaging workflow refuses to ship an artifact without them.

### Fixed

- **Cross-chain `Sent` rows with an undecided conversion no longer age out of the recovery poll.** The
  conversion's outcome has no event and is learned only from that poll, so the 24-hour cutoff could strand
  a slow conversion silently, with a needed refund never requested. Undecided conversions are now exempt
  from the cutoff; terminal ones age out as before.
- **Invoice reconciliation resumes where the previous pass stopped** instead of re-examining the same
  oldest 1,000 invoices every pass, which starved everything behind them on a large backlog.
- **Switching the Stable Balance token is refused while the previous token still holds a balance**, which
  would otherwise become invisible to the plugin — only the configured token is converted, displayed and
  swept.
- **The bridge provider's order id is persisted.** It was set on the in-memory record but absent from the
  durable resolution, so restarting BTCPay lost the one handle a stuck-delivery investigation quotes at
  the provider.
- **An extreme requested invoice amount can no longer wrap into an amountless invoice.** The
  millisatoshi-to-satoshi ceiling used the `+999` idiom, which wraps negative near `long.MaxValue` and
  read downstream as "no amount".
- **The release tag guard rejects a three-part tag on a four-part build** (`v0.1.4` for a 0.1.4.1
  artifact), which would have published a release page disagreeing with its own artifact.
- **A dropped settlement push is retried instead of lost.** A listener that falls behind has its push
  held back and re-delivered on a short timer once it catches up — bounded by a per-subscription
  allowance and a delivery deadline, after which the log names the truth (the payment reaches BTCPay
  when it next reads the invoice, typically after a restart). This replaces a log line that credited a
  BTCPay one-minute invoice poll that does not exist, and a reconciliation task that only scans unpaid
  rows.
- **The funded regtest suite fails hard when the wallet seed appears in any log surface**, instead of
  quietly withholding the artifact; and its Postgres race test only counts a unique-key violation as
  losing the race, so an unrelated error can no longer masquerade as the loser.

### Documentation

- The connection-string example is `type=flint` (not the pre-rename `breezspark`), the storage path is
  `<DataDir>/Plugins/Flint/` throughout, the registry entry to search for is **Flint**, the built-against
  BTCPay version reads 2.4.2, the clone instructions `cd` into the right directory, and the
  connection-string handler's remarks reflect the 0.1.4.1 key rotation.

## [0.1.4.1] — 2026-08-21

### Security

Findings from the v0.1.4 external security review (a three-model quorum over the release tag), each verified
against the source before fixing:

- **A sweep write-off can no longer unblock a re-sweep on stale storage.** After the five-minute grace, a
  pending sweep the SDK had no payment for was written off as never sent — but that lookup ran before the
  pass's forced sync, and an SSP-accepted exit the SDK had not replayed locally yet looks exactly like
  "never sent". The write-off now forces an explicit wallet sync and repeats the lookup first, and for
  sats-funded rows it additionally refuses to close the row while the synced balance no longer holds the
  amount the sweep would have sent — the shape that suggests the exit actually happened. A row the gate
  refuses keeps blocking new sweeps, which is the safe direction.
- **A cross-chain send now checks the provider's echoed recipient against the requested destination.** The
  prepared payment's recipient is an echo from the provider, and every guard downstream is amount-shaped —
  none of them would have noticed the money going to the right chain at the wrong address. A mismatch
  refuses the send.
- **The cross-chain value guard refuses amounts too large to value, instead of skipping itself.** The
  base-unit-to-dollar conversion reported an overflowing value as zero, and the guard read zero as "already
  refused upstream" — so the most absurdly sized quotes were exactly the ones that bypassed the value check.
  Overflow is now an explicit refusal.
- **The payment key is rotated on every provision.** The connection string is a bearer spend credential;
  re-provisioning previously carried the old key over, so every previously issued copy of the string could
  drive the new wallet. Setting Spark up again now mints a fresh key and rewrites the store's Lightning
  configuration with it in the same operation — which also makes re-running setup the way to revoke a
  leaked string.
- **A wallet that has not reported its identity yet bypasses the deposit-address cache.** The cache was
  keyed on an empty identity like any other, so after a seed change a request racing the new wallet's first
  sync could be handed the previous wallet's deposit address. An unknown identity now always fetches a live
  address and caches nothing.
- **The plugin's directories are owner-only from the instant they exist.** Storage and log directories were
  created at the process umask (0755) and restricted to 0700 afterwards, leaving a window in which what
  landed there was world-readable; the mode is now passed to the creation itself, and the storage lock file
  is created owner-only too.

Two follow-ups from the quorum's review of the fixes themselves:

- **The write-off shortfall gate is bounded, not absolute.** The gate's observation — funds missing with no
  payment record — is also produced by a Lightning payout or a Stable Balance conversion landing near a
  sweep that genuinely never went out, and sweeps are close enough to the whole balance that any spend trips
  it. Unbounded, that coincidence would wedge the store's sweeping permanently with no operator escape; the
  gate now blocks for an hour of synced re-checks and then writes the row off with a reason stating exactly
  what was observed and what to verify.
- **A provisioning rollback now also covers a throwing Lightning-configuration write.** With the key
  rotating, settings carrying a new key beside a configuration still holding the old string is a store whose
  checkout fails; a throw out of the wiring write now restores the previous settings the same way a refused
  write always did. A process crash inside that window is still detectable and repairable from the status
  page, which inspects the wiring against the stored key.

### Changed

- **The Spark SDK is now 0.22.3**, up from 0.22.2, on the review's recommendation: the one upstream change
  ("stop cross-chain sends claiming their own outgoing transfer") is an accounting fix directly on the
  cross-chain rail this plugin uses.

## [0.1.4] — 2026-08-21

### Changed

- **The Spark SDK is now 0.22.2**, up from 0.22.0. A patch-range bump with, as with the last bump, no
  release notes published upstream. No API surface change reached this plugin: the build and every test
  suite — including the live and funded regtest suites against the real SSP — passed without a single
  call-site change, and the build ran on both test servers before this release was cut.
- **Sweep labels validate the provider's transaction id before writing it to the wallet.** The txid that
  labels a sweep comes from the Spark provider's payment data, so it is now checked as a well-formed Bitcoin
  transaction id (64 hex characters) — the same guard the plugin already applies to externally-supplied
  payment hashes — before it becomes a wallet object and a rendered `flint-sweep` label. A malformed id is
  skipped and logged, never thrown, keeping the labeler's best-effort contract total: a bad provider value
  cannot abort a reconciliation pass or attach a false "swept from this store's Spark wallet" provenance
  label to an unrelated transaction in the store's wallet. Cross-chain deliveries remain unlabelled and skip
  this check entirely.

## [0.1.3] — 2026-08-17

### Added

- **Sweeps are labelled in the store's Bitcoin wallet.** Once a sweep's transaction id is known — at send,
  or when crash reconciliation resolves it — the plugin writes a `flint-sweep` label onto the transaction in
  the store's BTCPay wallet, the same way core labels payouts and invoices. The wallet's transactions list
  then says where the money came from, with a tooltip, filterable like any other label. Cross-chain sweeps
  are not labelled, because their delivery never appears in a Bitcoin wallet.

### Changed

- **Deposits moved off the navigation, behind Advanced.** A Spark wallet is funded by customers paying
  invoices — no merchant needs to send their own funds in for the plugin to work — so the deposits page is
  now reached from the Advanced page instead of carrying a top-level entry. The status page still flags a
  stuck deposit loudly and links straight to the page that fixes it.

## [0.1.2] — 2026-08-17

A UI pass ahead of publicising the plugin: fewer words, and a page for the things most stores never touch.

### Changed

- **The navigation gained *Deposits* and *Advanced* entries, and now collapses.** The sub-entries render
  only while you are inside the Flint section, the way core's own store-settings menu behaves, instead of
  following you around the rest of the store. The status page's "Advanced" accordion is gone — it was not
  obviously expandable, and it was carrying real pages.
- **A new Advanced page** holds the recovery phrase's provenance, seed replacement, the Spark identity,
  the SDK storage path, wallet removal, and the two sweep settings almost nobody should change: the
  reserve ("Leave behind") and the fee policy ("Take the exit fee out of the swept amount"). All of it
  moved off the status and sweep pages; nothing changed in what is stored or in the Greenfield API.
- **The status page now leads with the balance** and the stuck-deposit alert links straight to the
  Deposits page. The recovery-phrase row, the wallet-details accordion and the removal button moved to
  Advanced, and the "indicative balance" footnote is gone.
- **The deposits page stopped printing its title twice** and is reachable from the navigation.
- **Form notes were cut back across the plugin.** The confirmation-speed field now shows roughly what
  each tier pays in sat/vB right now, read from the same mempool feed the deposits page uses, instead of
  a sentence about tiers.

## [0.1.1] — 2026-08-14

A dependency-only release. No plugin source changed between 0.1.0 and this; the packaged artifact
differs from 0.1.0 only in the Spark SDK's bundled native libraries. Upgrading is an ordinary plugin
update — the identifier, the settings key, the Postgres schema, the SDK storage directory and the
data-protection purpose are all untouched, so **no recovery phrase needs re-importing** and no
balance has to be swept out first. The 0.1.0 migration warning applies to arriving from the
predecessor plugin, not to this step.

### Changed

- **The Spark SDK is now 0.22.0**, up from the 0.19.2 that 0.1.0 shipped. Breez published no release
  notes across that range, so the bump was reviewed by reading the diff of the surface instead: it is
  additive — batch sends, CPFP, prepared unilateral exit, passkeys, none of which this plugin calls —
  with one breaking change that reached us. `SdkException.InsufficientFunds` stopped being a
  payload-free variant and now carries the identifier of whichever balance was short, which broke two
  test call sites and no production code. `SparkErrors.IsInsufficientFunds` classifies a token
  shortfall the same as a sat shortfall, because the plugin only ever spends sats and would otherwise
  report a token error as "state unknown" — the classification that makes a sweep unsafe to retry.
  The reason to ship a bump with no known fix in it is that the SDK is pre-1.0 and releases roughly
  monthly: staying near the tip keeps each upgrade a small step that can be read in an afternoon,
  rather than a large one taken later under pressure.
- **The plugin is built against BTCPay Server 2.4.2**, up from 2.4.1. This is the release it is
  compiled and tested against, not a new requirement: the declared support floor is unchanged at
  2.4.1, so every host that can run 0.1.0 can run this.

## [0.1.0] — 2026-08-08

The first release under the name **Flint**, and the first from this repository.

The code is not new. It was built and independently audited under a previous name and owner, and this
release is that work with the branding, licence and plugin identity changed. The sections under
*Earlier development* below record what happened before the rename; their version numbers were never
released and do not correspond to anything published here.

> **Migrating from the predecessor plugin?** The plugin identifier changed, so BTCPay treats this as a
> different plugin: uninstall the old one and install this. **You must re-import your store's recovery
> phrase.** Every constant that keys stored data moved with the rename — the settings key, the Postgres
> schema, the SDK storage directory, the Lightning connection-string type, and the data-protection
> purpose the phrase is encrypted under. Re-importing restores the wallet and its balance from the
> network; it does not restore local payment, payout or sweep history, which stays with the orphaned
> schema. Sweep everything out and settle any in-flight payment before you switch, because the
> idempotency records that stop a sweep being sent twice do not survive either.


### Added

- **Spark has sections in the store navigation** instead of a single entry. *Sweeps* and *Stable
  Balance* (mainnet only) are reachable directly, rather than by finding the right button on the status
  page. Deposits and removal deliberately stay off the nav: a Spark wallet is funded by customers paying
  invoices, so a merchant depositing by hand is the exception, and removal is destructive and belongs
  next to the state it destroys. Sub-entries appear only once the store has a wallet, because before
  that both destinations redirect to setup and would be three ways of reaching one page.
- **Setup can turn sweeping on.** Step 2 used to be a paragraph about sweeping and an assurance you
  could configure it afterwards, which put the safest configuration — not leaving a growing balance on
  a second layer — behind an extra trip to another page. It now asks the only two questions that decide
  whether sweeping happens at all: on or off, and the threshold. Destination, fee limits, minimum and
  confirmation speed keep their defaults on the sweep page. It is applied after provisioning and never
  before, and a failure there does not fail setup — the wallet is up and the usual cause is a store with
  no on-chain wallet to sweep into. But it is not silent: the reason rides along in the success message,
  so nobody reads "Spark is now set up" and believes a balance is being swept when none is.

### Changed

- **The plugin identifier is now `BTCPayServer.Plugins.Flint`.** ⚠️ **Operators must uninstall the
  old plugin, install this one, and re-import the store's recovery phrase** — BTCPay keys an install by
  the identifier, so it sees this as a different plugin, and every constant that keys stored data moved
  too. The migration warning at the top of this release is the authoritative list of what carries over
  (the wallet and its balance, from the network) and what does not (local history and idempotency
  records).
  The reason for the change is that a third party had already registered `BTCPayServer.Plugins.Spark` on
  the official plugin registry, and BTCPay joins an installed plugin to a registry entry by identifier
  alone: their repository was credited as this plugin's author, the card's "Sources" and "Details" links
  pointed at their code, and their build would have been offered as an update to this one as soon as
  their version passed ours. It also meant this plugin could never be listed under its own name.
- **The plugin's own page no longer prints its title twice.** `vc:title-header` renders a breadcrumb
  trail and then the title, and synthesises a trail from the title when a page sets none — so the status
  page showed "Flint" above "Flint". The status page is the plugin's root and has no parent to
  point at, so it renders the heading alone; the Sweeps, Stable Balance and removal pages now set a trail
  back to it, which is where a breadcrumb earns its place.
- **The plugin is called "Flint".** It is built and maintained by Seth For Privacy (see `LICENSE`), and
  a plugin calling itself plain "Spark" implies it comes from Spark or from Breez. The name appears in
  the manifest, the nav entry, the status page and the Lightning connection's label. Mentions of the
  Spark *network* — "Spark wallet", "Spark balance", "Spark sweeps" — are still correct and are
  unchanged: that is the network the plugin connects to, not what the plugin is called.
- **The nav entry carried the previous owner's logomark with a Spark asterisk**, instead of BTCPay's
  generic plugin symbol. (The mark has since been replaced by the Flint mark; the mechanics are
  unchanged.) It is an inline `<svg>` because `<vc:icon>` can only address symbols in core's own
  sprite, and it inherits `currentColor` so it survives dark themes and the nav's hover and active states.
- **The README and registry logo became a real mark**, in the same composition as the nav icon,
  replacing the placeholder drawn for the repository. It is a raster on backgrounds nobody controls
  rather than themed markup, so it carries its own colours instead of inheriting one. (That mark
  belonged to the previous owner and has since been replaced by the Flint mark.)
- **Cross-chain sweep destinations are read from the provider at runtime** instead of a hardcoded list of
  six chains and USDT. The provider carries thirteen EVM chains reachable from Spark and USDC on eleven
  of them, and says in as many words not to hardcode this. What the static list was missing was not a
  rounding error: `base`, `avalanche`, `monad`, `hyperevm` and `sei` were absent entirely, and USDC — the
  most widely carried asset in the table — was absent from every chain that was present. The catalogue is
  cached rather than fetched per render (six hours after a success, five minutes after a failure), no
  render ever waits on the network, and at most one fetch happens per interval however many requests
  arrive.
- **The chain and asset fields on the sweep page are pickers, not free text.** The two fields that decide
  which chain a store's money lands on were the two you could typo — the chain was free text with a
  datalist of suggestions, and the asset was free text with nothing at all. The asset list is derived
  from the selected chain. Nothing in the picker authorises a route: `CrossChainRouteResolver` re-reads
  the live table before every send.
- **The status page leads with the balance**, as two figures rather than a table row: sats always, and
  the stablecoin holding beside it when Stable Balance is on. Those are two different assets rather than
  two views of one, and setting them side by side is what makes that legible. Deliberately not a chart —
  the plugin stores no balance history, and the figure is read live from the SDK each time, so anything
  time-shaped would be invented rather than measured.
- **Spark identity, the SDK storage path and the on-chain deposit address moved into a collapsed
  Advanced section** on the status page, and the Spark network section is gone. The uncredited-deposit
  alert stays on the page unconditionally: that one is money the merchant sent that did not arrive.
  Merchant-facing copy also loses every "on regtest we measured" aside — those were notes to ourselves
  about how the numbers were obtained, and the substance survives where it changes a decision.
- **The plugin-list description is about 300 characters, down from 613.** It read like the README's opening
  section, which is the wrong job for a line sitting in a list beside a dozen other plugins, and it now
  describes Spark as a non-custodial layer two rather than naming the operator threshold. The operator
  count goes stale as operators are added, and a plugin-list entry is the worst place to carry a fact
  with a shelf life; the README, trust model and Stable Balance page still name the specifics, which is
  where someone goes to check them.

### Fixed

- **The seed-leak guards no longer fail at random.** Both tests that prove recovery-phrase material never
  leaves the server compared the phrase word by word against text that is ordinary English, so they
  collided with it. One matched `"word"` against raw JSON and could not tell a key from a value, so a
  generated phrase containing "history" hit `SparkSweepConfigurationData`'s own `history` property. The
  other matched `" word "` against the operator log, whose success line ends "…configured from a generate
  seed" — and "seed" is a BIP39 word, so roughly one provisioning in a hundred and seventy failed a test
  with no leak in it. Both now look for two *consecutive* words, which prose does not produce by accident
  and no real leak can avoid. This matters more than an ordinary flake: a security guard that fires at
  random teaches everyone to re-run it, so the first true failure gets dismissed as the flake.

## Earlier development

The two sections below predate the rename and were never released under any name. They are kept
because they are the honest provenance of this code, including the security audit and its fixes.

### Audit fixes (previously numbered 0.2.0)

Fixes from an independent security audit of `90bf6ee` (2026-08-07). Two of these move money, so this
supersedes 0.1.0 outright — do not ship 0.1.0.

### Fixed

- **A sweep whose SDK handle was disposed mid-send could be swept twice.** `IsProvablyNotSent` never
  mentioned `ObjectDisposedException`, but it tested `InvalidOperationException`, which disposal derives
  from — so a disposed handle was classified as "nothing was sent", the record resolved `Failed`, and the
  store was free to sweep the same balance again on the next pass while the first send may already have
  left the wallet. Disposal races a send on every reconfigure and shutdown. The generic catch's own
  comment always named a disposed handle as a genuinely unknown outcome; now it actually reaches it.
- **Two concurrent payments of one invoice both reported success.** `Pay` probes the SDK for an earlier
  send and then sends, with nothing serialising the two, so two callers — the automated payout processor
  ticking while someone confirms by hand, or two Greenfield pay calls — could both pass the probe and
  both send under the same idempotency key, marking two payouts `Completed` against one payment. The
  probe and send are now one critical section per invoice.
- **A blip before anything was sent got the payout cancelled.** Failures in the idempotency probe and in
  `PrepareSendPayment` both ran before `SendPayment` and provably spent nothing, but were reported as
  `Unknown`; BTCPay then marked the payout `InProgress` and cancelled it ten minutes later. They are now
  reported as a definite error, which returns the payout for an immediate, safe retry.
- **A hung SDK `Disconnect` wedged the whole plugin.** Teardown runs inside the process-wide instance
  lock, so an unbounded await there blocked every later setup save, every store deletion and host
  shutdown itself until the process was killed. It is now bounded like the connect already was, and the
  handle is disposed either way.
- **A permanently hung connect locked a store out of its own wallet until BTCPay restarted.** The
  abandoned connect held the store's storage lock while awaiting a task nothing can cancel, and the
  store's next attempt then failed with a message blaming another process for a hold this one was doing
  to itself. The lock is now released after a grace period.

### Changed

- **A settlement for less than the invoiced amount is now logged as a warning.** Nothing compared the
  arrived amount to the invoiced one, and the record settles once and never revises upward. This is a
  loud warning rather than a refusal on purpose: refusing on an amount the SDK reported slightly low
  would stop legitimate invoices settling, which is worse than the unproven Spark-rail case it would
  defend against.

### Documentation

- **The Lightning connection string is documented as a bearer spend credential**, in
  `docs/trust-model.md` and in `SparkConnectionStringHandler`'s own remarks. The previous claim that
  store binding "closes the cross-store wallet-hijack hole" was too strong: it closes the
  key-without-a-store-id case, but copying a whole string onto another store on the same server still
  drives the original's wallet — confirmed live by the audit. The key is also never rotated.
- **The status page no longer claims checkout will fail** when a store points at another store's Spark
  wallet. Checkout succeeds and the money goes to the other store, which is the thing worth saying.

### Initial implementation (previously numbered 0.1.0)

First release. Nothing shipped before it, so this is a description of what exists rather than a
diff, and it is deliberately as long on limitations as it is on features.

### What it does

- **Nodeless Lightning receive.** Registers a `breezspark` Lightning connection-string handler and a
  per-store `ILightningClient` backed by the [Breez Spark SDK](https://sdk-doc-spark.breez.technology/)
  running in-process. A store receives Lightning payments with no Lightning node, no channels and no
  inbound-liquidity management.
- **One-page setup, no connection string to copy.** Under *Plugins → Spark*, or from the store's
  *Connect to a Lightning node* screen. The plugin writes the store's `BTC-LN` and `BTC-LNURL`
  payment-method configuration itself, so LNURL and Lightning addresses work through BTCPay core as
  soon as setup finishes. Seed choice is: generate a new BIP39 phrase (default), reuse the store's
  hot-wallet phrase (offered only when BTCPay actually holds one, with the passphrase caveat spelled
  out on the page), or import an existing phrase. All three sit behind BTCPay's hot-wallet gate.
- **Seeds encrypted at rest and never rendered back.** The phrase is protected with `IDataProtector`
  before storage; there is no reveal-seed feature and no API that reads one out. A generated phrase
  is shown exactly once.
- **Settlement that does not trust the event stream.** The SDK's stream has been observed dropping a
  completion and firing a duplicate 57 ms apart on two threads, and BTCPay does not re-poll pending
  invoices. So the plugin runs its own reconciliation pass (at startup and once a minute) over every
  unpaid invoice, oldest first, and keeps looking for one hour past expiry because the service
  provider still accepts late payments. Settlement is a database compare-and-set, so a duplicated
  event, a `GetInvoice` lookup and a reconciliation pass racing each other produce exactly one credit.
- **Auto-sweep to Bitcoin, by cooperative exit only.** Threshold-triggered (checked every two
  minutes) or on demand via *Sweep now*, which goes through the identical engine with the identical
  server-side guards. Destinations: a fresh labelled address reserved from the store's own BTC
  derivation scheme and rotated per sweep, or one fixed address validated against this server's
  chain on save and again before every send.
- **A fee guard that cannot be switched off.** Percentage-based rather than flat, because a flat cap
  set today refuses every sweep the first time mainnet broadcast fees rise past it. Clearing it falls
  back to the default; whatever is configured, the plugin will not pay more than half of what a sweep
  delivers.
- **Crash-safe sweeps.** A `SweepRecord` with a fresh UUID is written *before* the SDK call, and the
  SDK adopts that UUID as the payment id, so the next pass can ask `GetPayment(key)` for a definitive
  answer. Nothing is retried blind, and no new sweep starts while an earlier one's fate is unknown.
- **Refusals are recorded, not just logged.** The history page answers "why has nothing swept?".
  Recurring automatic refusals fold onto one row keyed on the *kind* of refusal, with a count and a
  last-seen time, bounded to a day so a condition that stopped and came back reads as two episodes.
- **On-chain deposits that actually arrive.** The wallet's static deposit address, plus the three
  things that keep a top-up from silently stranding: a claim-fee ceiling expressed as the
  network-recommended rate plus a leeway rather than Spark's fixed 1 sat/vB default, a display of
  what is stuck and what fee it needs, and a one-click manual claim guarded by a per-store ceiling
  and by a backstop that refuses to spend more than half a deposit on claiming it. That last guard
  binds on the manual path only — see the maturity note below on automatic claiming.
- **Stable Balance (mainnet only).** Optionally hold the store's balance in USDB between sweeps.
  Off by default, gated behind an explicit acknowledgement of the freezability disclosure, and
  refused outright on non-mainnet rather than accepted and silently never converted.
- **Cross-chain sweeps (mainnet only).** A third destination: a stablecoin delivered to an address
  you control on an EVM chain, through a bridge provider, still as an ordinary cooperative Spark
  transfer at the point money leaves the wallet. EIP-55 checksums are verified where present, the
  asset is matched exactly (`USDT0` is not substituted for `USDT`), and the provider's quote is
  sanity-checked against the store's own exchange rates — a sweep losing more than 10% of its value
  is refused, as is one that cannot be checked at all.
- **A Greenfield API covering everything the pages do**, over ten endpoints under
  `/api/v1/stores/{storeId}/spark`, scoped to the usual store view/modify permissions and documented
  in the server's own `/docs` and `swagger.json`. It is a second surface over the same services, not
  a second implementation.
- **Operational hardening.** Per-store SDK state and the SDK log directory are created `0700` and
  re-hardened on every start; an exclusive claim on each store's storage directory refuses to start a
  second wallet on it rather than putting two writers on one SQLite file; the SDK's Rust log is
  scrubbed of credential-shaped values on its way into BTCPay's log, and a `trace` filter — at which
  the provider's session token is logged in full — is refused.
- **Its own Postgres schema and migrations**, applied from a startup task, with design-time tooling
  behind an opt-in MSBuild flag so no packaged plugin carries it.

### Maturity — read this before putting money through it

This release is **thinly proven**. It is feature-complete for what it sets out to do and it has been
run on mainnet, but "run on mainnet" means something narrower than it sounds:

- **One mainnet happy-path run per money path, and no more.** A real store delivered one full
  BTC → USDB → USDT → Arbitrum cross-chain sweep whose on-chain amount matched the plugin's recorded
  figure exactly, one cooperative-exit sweep to Bitcoin, and one on-chain deposit auto-claim at live
  fee rates. Single-digit dollar amounts, one operator, one happy path each. That establishes that
  the plumbing connects and that the figures the plugin reports are truthful. It establishes nothing
  about volume, concurrency, or any adverse condition.
- **Every recovery path is unexercised against the real provider.** A crashed cross-chain send, a
  stuck conversion, a refund, an expired fee quote mid-send — all of these are covered only by unit
  tests against a fake SDK deliberately built to model the real one's hazards. None has been made to
  happen for real.
- **Neither post-MVP feature can be tested off mainnet at all.** Cross-chain sending is hard-gated
  (the SDK throws at connect on any other network) and Stable Balance is accepted on regtest and then
  never converts, because USDB does not exist there. So neither has CI coverage against a live
  service, on any network.
- **The Orchestra bridge provider is a single point of failure for cross-chain sweeps.** It is the
  only provider that works today: every Boltz route currently fails when the send is prepared, so the
  plugin filters them out and reports a Boltz-only destination as having no route. If Orchestra stops
  routing, cross-chain sweeping stops, with no fallback.
- **USDB is issuer-freezable.** Holding the store's balance in USDB means holding a token whose
  metadata says its regulated issuer can freeze it. If they do, this plugin cannot move it, sweep it
  or convert it back. That is a named counterparty in a jurisdiction, *in addition to* the Spark
  operators, not instead of them.
- **Automatic deposit claiming is bounded by fee *rate*, not by a share of the deposit.** The
  "refuses to spend more than half a deposit on claiming it" backstop above binds on the *manual*
  claim path only. Automatic claiming is the SDK's own background worker: there is no per-deposit
  hook and no callback the plugin could refuse from, and the whole of the plugin's influence is the
  single `maxDepositClaimFee` handed over at connect — whose three available shapes (flat sat cap,
  flat rate, recommendation plus leeway) are all amount-blind. So a small deposit that matures during
  a fee spike can still lose a large share of itself to its own claim. Closing this properly means
  taking automatic claiming away from the SDK and claiming every matured deposit from the plugin
  through the guarded path, which is a real design but not one to ship without a mainnet run behind
  it — a mistake there strands every deposit rather than overpaying on one.
- **All cooperative-exit fee defaults are regtest measurements** from a chain at 1 sat/vB. Every real
  decision uses a live quote, but the shipped defaults will very likely refuse every sweep on
  mainnet until the threshold and minimum are raised by an order of magnitude. That is the intended
  failure direction — refuse rather than overpay — but it means the defaults are a starting point,
  not a recommendation.
- **Funds on Spark are not in your sole custody.** Spark is a 2-of-3 statechain operated by
  Lightspark, Breez and Flashnet, and this plugin performs cooperative exits only — it offers no
  unilateral-exit path anywhere in its UI or its code. If the operators became unavailable, recovery
  would mean taking the store's recovery phrase to another Spark wallet implementation. Sweeping is
  the only thing that reduces this exposure.

See **[Known limitations](docs/limitations.md)** for the full list, including the LUD-06
description-hash gap, unsupported platforms (`linux-musl`, `win-arm64`) and networks (testnet,
signet), the unrotated `sdk.log`, and the one SDK error classification with no automated coverage.

### Requirements

- BTCPay Server **2.4.1** or newer (declared support floor; the plugin is compiled against 2.4.1).
- A host on `linux-x64`, `linux-arm64`, `osx-x64`, `osx-arm64`, `win-x64` or `win-x86`. Standard
  Debian-based BTCPay Docker images are fine; Alpine is not, and neither is `win-arm64`.
- Mainnet or regtest. Testnet and signet are not supported by the SDK.
- Server-admin rights, or the *Non-admins can create Hot Wallets for their Store* policy, since
  Spark keeps keys on the server.

[0.1.0]: https://github.com/sethforprivacy/flint/releases/tag/v0.1.0
[1.0.0]: https://github.com/sethforprivacy/flint/releases/tag/v1.0.0
[1.0.1]: https://github.com/sethforprivacy/flint/releases/tag/v1.0.1
[1.0.2]: https://github.com/sethforprivacy/flint/releases/tag/v1.0.2
[1.0.3]: https://github.com/sethforprivacy/flint/releases/tag/v1.0.3
[1.0.4]: https://github.com/sethforprivacy/flint/releases/tag/v1.0.4
