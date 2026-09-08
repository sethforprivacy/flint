[← Docs index](README.md)

# Tests

```bash
dotnet test
```

The default run covers the plugin's own logic — invoice-record state transitions, payment-hash mapping,
connection-string handling, listener safety, settlement fan-out, the sweep engine's economics, guards and
crash recovery, and the whole `ILightningClient` surface driven through a fake SDK. It needs no Docker, no
database and no network, and finishes in a couple of seconds. It does **not** cover the EF store or the SDK
itself; those need the opt-in suites below.

The fake SDK deliberately models the real one's hazards rather than an idealised SDK: a cooperative-exit
quote that does not check the balance, quotes that expire, the script-type dust floor, a send that returns
still-pending and never completes, an idempotency-key replay that returns the original payment without
spending again, and a `Payment` whose amount is already net of the fee under `FeesIncluded` (as the real one
is — a fake that echoed the request back instead is what let the sweep message double-subtract the fee on
mainnet). A cooperative fake would let through precisely the bugs these tests exist to catch.

The suite runs on **Microsoft.Testing.Platform**, not VSTest: xunit.v3 4.0.0 dropped the VSTest bridge on the
.NET 10 SDK. Two things turn that on and both are needed — `UseMicrosoftTestingPlatformRunner` in the test
`.csproj`, and the repository-root `global.json`, which is what makes plain `dotnet test` speak MTP. The
`--filter "Category=…"` syntax below is unaffected; MTP's xunit runner accepts VSTest filters verbatim. What
does change is the console-verbosity flag: `--logger "console;verbosity=detailed"` (and its `-l` short form)
is VSTest-only, and **MTP does not reject it** — it reads it as a filter, matches no tests, and exits 5 with
"Zero tests ran". Use `--output Detailed` instead.

It also covers what the compiler cannot: `ViewComponentCompatibilityTests` resolves every `<vc:…>` tag,
partial, layout and UI-extension target the plugin names as a string against the components and views that
actually exist in the pinned submodule. Those are resolved by name at render time, so a green build against
an older BTCPay says nothing about them: a `<vc:…>` tag compiles to a *string* looked up in a dictionary the
host builds at startup, and against a host that lacks the component Razor emits the tag as literal HTML rather
than failing. That is how the declared floor in `Constants.MinBTCPayServerVersion` was once lowered onto a
BTCPay that 500'd on every plugin page. **Raising the floor is a support decision; lowering it is an
engineering claim**, and needs a real host of that version with every view actually rendered — not a green
build.

**Against a real Postgres** — required before trusting any change to `Data/`, because the store's
compare-and-set behaviour and its interaction with BTCPay's `EnableRetryOnFailure` cannot be reproduced
in memory:

```bash
docker run -d --rm --name spark-test-pg -e POSTGRES_PASSWORD=sparktest \
  -e POSTGRES_DB=sparktests -p 54329:5432 postgres:17-alpine

SPARK_POSTGRES_TESTS="Host=127.0.0.1;Port=54329;Database=sparktests;Username=postgres;Password=sparktest" \
  dotnet test --filter "Category=Postgres"
```

**Against the Lightspark-hosted regtest** — loads the SDK's native library and talks to the real service
provider. No API key is needed; regtest accepts a null one:

```bash
SPARK_INTEGRATION_TESTS=1 dotnet test --filter "Category=Integration"
```

It connects a throwaway wallet and mints a real `lnbcrt…` invoice. It cannot cover settlement, which
needs a funded wallet — which is what the next section is for.

## A funded regtest wallet for CI

The regtest smoke test above stops at the point where anything settles. Everything past that point — a
Lightning receive that actually completes, a cooperative exit that reaches `Confirmed`, a sweep interrupted
between the record insert and the send and then recovered — has never run against the real SDK. Neither have
**the log lines a completed payment emits**, which is where a preimage would appear and is the one stated gap
in the plugin's log audit and in `SparkLogScrubber`'s remarks.

A funded wallet closes that. It is one wallet, holding regtest coins that are worth nothing, shared by CI:

```bash
SPARK_REGTEST_SEED="<twelve words>" dotnet test --filter "Category=FundedRegtest"
```

Absent the variable the suite skips, exactly as the Postgres and integration suites do. The suite needs
**100,000 sats** to start and burns roughly **4,000 sats per run** in cooperative-exit fees — the principal is
not burned, because sweeps are directed at the wallet's own static deposit address and come back on-chain.

### Standing the wallet up

**1. Generate a seed — on your own machine, never in CI.**

```bash
SPARK_REGTEST_WALLET_GENERATE=1 dotnet test \
  --filter "FullyQualifiedName~Generate_a_wallet_mnemonic" --output Detailed
```

This needs no network, so it works from a machine whose IP the SSP blocks. The generator **asserts that
`GITHUB_ACTIONS` is unset** and fails if it is: it prints a private key to stdout, and a job log is a
published document readable by everyone with repository access.

> **Why the seed is not generated in CI.** The obvious design — CI mints a mnemonic and returns it in an
> encrypted artifact — has no safe return channel. An encrypted artifact needs a passphrase, and the only
> channel `workflow_dispatch` offers is its inputs, which GitHub records against the run *unmasked*; that puts
> the key beside the lock. Artifacts are downloadable by every user with read access and retained for months,
> so a passphrase that leaks once compromises the wallet permanently. Generating locally means the seed
> crosses a network exactly once — when you paste it into GitHub's secret form over TLS. (Having CI write the
> secret itself via the API was also rejected: `GITHUB_TOKEN` cannot write secrets, so it would need a PAT
> with `secrets: write`, which is a far larger grant than the thing it protects.)

**2. Store it as the repository secret `SPARK_REGTEST_SEED`.**
Settings → Secrets and variables → Actions → New repository secret. Paste the twelve words with **no quotes
and no trailing newline**; the suite validates the mnemonic up front and says so if you do.

**3. Get the deposit address.** Actions → **Spark regtest wallet** → Run workflow. The run summary prints the
wallet's static Bitcoin deposit address, its identity pubkey and its balance. The mnemonic appears nowhere —
the wallet is identified by a SHA-256 prefix, and the tool asserts the seed is absent from its own output.

**4. Fund it** at <https://app.lightspark.com/regtest-faucet>. The faucet is reCAPTCHA-gated, so this step is
unavoidably manual. Send at least 150,000 sats; the faucet caps each grant, so this may take a few rounds.
The address is **static** — keep it, and you never need step 3 again.

### Topping it up

The `funded-regtest-test` job fails with a message that begins **"The CI regtest wallet is out of money"** and
names the balance and the shortfall. That is an operations event, not a code defect, and the message says so.
Send more coins to the same static address from step 4. Nothing else needs to change.

If the wallet empties **mid-run**, the tests that had already started fail on their own balance checks with
the same message rather than on a confusing assertion about a sweep. No state needs cleaning up: the suite
holds nothing but a temp SQLite file it deletes, and any cooperative exit already in flight completes on-chain
into the same wallet.

To rotate the wallet — the seed leaked, or you want a fresh one — repeat steps 1 to 4. The old wallet's coins
are regtest and not worth recovering.

### The captured log, and the question it settles

Every run uploads a **`funded-regtest-log-audit`** artifact, on success *and* on failure. It holds:

| file | what it is |
|---|---|
| `forwarded.log` | what an operator's BTCPay log would have shown — everything through `SparkLogScrubber`. **Withheld if secret material appears in it** — the wallet's mnemonic, a recorded payment preimage, the service provider's session token. This is the post-scrub log, so secret material in it *is* a scrubber hole, and even then a leak must not also be a publication. A withheld file leaves a `forwarded.log.WITHHELD.txt` marker naming the reason |
| `sdk.log` | the raw file the Rust subscriber wrote, which no C# scrubbing reaches. **Withheld if the wallet's mnemonic, a recorded payment preimage, or the session token appears in it** — these artifacts are downloadable by anyone with read access, and a leak must not also be a publication. A withheld file leaves an `sdk.log.WITHHELD.txt` marker naming the reason |
| `preimage-audit.md` | the answer: every distinct 64-hex run the SDK emitted, classified against the preimage, payment hash and txid the run recorded, with the surrounding words. Preimage-kind values print as one-way SHA-256 fingerprints unconditionally — the fingerprint plus the occurrence counts proves the same thing without publishing the secret — and public identifiers (payment hashes, txids, idempotency keys) stay verbatim. For a withheld source, its hex rows' values print as fingerprints, their contexts are redacted, and its count columns print *withheld*; classifications and the raw-side occurrence counts stay |

Read `preimage-audit.md` first, and **check its banner before anything else**: a run that aborted on a drained
wallet still produces hundreds of lines of connect chatter and an empty, clean-looking table, and the banner
is what stops that being misread as "measured, nothing there". Only a run that says *"This run completed a
payment"* is evidence.

Given that, the preimage row must show `0` occurrences in the forwarded column — the suite asserts it, so a
non-zero value is already a red run. Then look at the `sdk.log` rows classified `PREIMAGE`. None means the SDK
does not write a preimage to disk at `debug`, and §8.6's gap closes as measured. Some means reading their
context: a preimage next to a name the scrubber knows is handled; one with no name beside it is the case the
scrubber's remarks left open, and since it lives in `sdk.log` the fix is a log-level or file-permissions
question rather than a regex. Either way, **record the answer in `Sdk/SparkLogScrubber.cs` and delete the
stated gap.** That is what the artifact is for; it only needs reading once.

## Against a local Spark stack

Both regtest suites above run against Lightspark's hosted service, where everything on the far side of an
invoice is somebody else's: **no counterparty will pay an invoice the plugin mints**, no one can mine a
block on demand, and the funded suite needs a faucet-filled wallet behind a repository secret — which is
why it goes red when the wallet drains, and why a fork cannot run it at all.

A local stack removes all three constraints. [`e2e/local-regtest/`](../e2e/local-regtest/) stands up
[callebtc/cashu-regtest](https://github.com/callebtc/cashu-regtest)'s `--spark` profile: Bitcoin Core in
regtest, three Spark operators signing 2-of-3, an `open-ssp` service provider backed by an LDK node,
Electrs serving an Esplora API, and three LND plus three CLN nodes in a funded channel topology. Those
Lightning nodes are the point — `lnd-1` pays a Flint invoice and Flint pays `lnd-1`'s, so a **settlement**
is observable from both sides, with the payment hash checked against what the payer recorded. Deposits
confirm because a test mines them, a cooperative exit reaches `Confirmed` in seconds rather than whenever
a block arrives, and there is no secret and no balance to keep topped up.

```bash
e2e/local-regtest/up.sh                     # clone at the pinned SHA, then ./start.sh --spark
e2e/local-regtest/write-network.sh          # discover the live stack, write network.json

SPARK_LOCAL_REGTEST_NETWORK=$PWD/e2e/local-regtest/network.json \
  dotnet test --filter "Category=LocalRegtest"

e2e/local-regtest/down.sh
```

`SPARK_LOCAL_REGTEST_NETWORK` points at a **network descriptor**: a JSON file naming the three operators
with their addresses, identity keys and TLS certificates, the SSP's base URL and identity key, and the
Esplora URL. The plugin's `SparkCustomNetwork` loader reads it and rewrites the SDK's regtest config from
it; the test fixture additionally reads the container names it needs to drive `bitcoin-cli` and `lncli`.
It has to be generated rather than committed, because the SSP's identity key and the operators'
certificates are created fresh by every `start.sh` — so **re-run `write-network.sh` after every
`up.sh`**. Absent the variable the suite skips itself, as the Postgres and integration suites do.

The cost is time: the fixture publishes no images, so a cold run builds six services from Rust and Go
source and takes **tens of minutes** (a few minutes once the layers are cached). On Docker Desktop for macOS
the fixture's Core Lightning nodes cannot write their SQLite database on a bind mount, so `up.sh` gives them
named volumes through a compose override there; see the
[fixture README](../e2e/local-regtest/README.md#on-macos). Do not mine or start the suite while `up.sh` is
still running — the fixture's init waits for every node to reach one exact height, and outside mining
makes that wait time out with the SSP unfunded.

Everything the stack holds — the `cashu`/`cashu` RPC credentials, the `regtest-spark-admin-token`, the
operator keyshares — is a public fixture value, and the suite's wallet is random per run. Details, and how
to bump the pin, are in [`e2e/local-regtest/README.md`](../e2e/local-regtest/README.md).

### Through a real BTCPay Server

The suite above drives the plugin's own collaborators and connects an SDK instance of its own. That is
enough to prove the money paths, but it means the plugin has never been observed **inside a host**: no
BTCPay invoice ever reached `Settled`, the Lightning payment method the provisioner writes was never
resolved by BTCPay, the reconciliation and sweep tasks never ran on BTCPay's schedule against real store
settings in Postgres, and the plugin was never once *loaded* the way an install loads it — as a directory
under the data dir, out of the official image, with Breez's native library resolved by BTCPay's plugin load
context. [`e2e/btcpay/`](../e2e/btcpay/) closes that gap.

It brings up BTCPay Server, NBXplorer and Postgres **joined to the Spark stack's own docker network**, so
NBXplorer uses the fixture's `bitcoind` and the plugin inside BTCPay reaches the fixture's operators, SSP
and Esplora. One chain, one fee market, and a cooperative exit the tests can mine to confirmation.

```bash
e2e/local-regtest/up.sh                     # the Spark stack first; this needs it
e2e/btcpay/up.sh                            # BTCPay + NBXplorer + Postgres, then provision and fund

FLINT_BTCPAY_E2E=$PWD/e2e/btcpay/btcpay.json \
  dotnet test --filter "Category=BtcpayE2E" --output Detailed

e2e/btcpay/down.sh                          # BTCPay side only; then e2e/local-regtest/down.sh
```

`up.sh` takes about **20 seconds** on a warm machine — it starts three containers, waits for BTCPay's
`/api/v1/health`, and then does everything a merchant would do, over Greenfield: create the first
administrator, mint an API key, create a store, `POST /api/v1/stores/{id}/spark` with
`seedSource: generate`, read the deposit address back, pay it from the fixture's `bitcoind`, mine, wait for
the claim, and configure sweeping. It writes `e2e/btcpay/btcpay.json` — base URL, API key, store id and the
same `fixture` block `network.json` carries — which is the only thing the tests read. The suite itself takes
about **two minutes**, nearly all of it the cooperative exit.

Two things about that provisioning are worth knowing before scripting anything similar.

- **The API key needs `btcpay.server.canmodifyserversettings`**, even when it belongs to an administrator.
  `seedSource: generate` creates a hot wallet, and BTCPay's own `CanUseHotWallet` check answers yes only
  when the server policy *allow non-admins to create hot wallets* is on, or when **the caller** holds the
  server-settings policy — and an API key is judged on its own permissions, not on its owner's role. Without
  it the provisioning `POST` answers `403 hot-wallet-not-allowed`. See
  [`greenfield-api.md`](greenfield-api.md).
- **The image is pinned by digest** to `btcpayserver/btcpayserver:2.4.4`, matching the `btcpayserver`
  submodule tag and therefore `Constants.BuiltAgainstBTCPayServerVersion`. The official image is a Release
  build, so `DEBUG_PLUGINS` — the side-loading route [`development.md`](development.md) uses — does not
  exist in it; `up.sh` instead stages the Release build output as
  `<plugindir>/BTCPayServer.Plugins.Flint/`, which is the layout `PluginManager` scans and byte-for-byte
  what `PluginPacker` would have zipped. `BTCPAY_PLUGINDIR` is set explicitly because BTCPay resolves the
  plugin directory independently of `BTCPAY_DATADIR`.

The three tests each close one host-facing gap, and each is checked against the counterparty rather than
against the plugin:

1. an invoice created through Greenfield, its BOLT11 read off the payment-methods endpoint, paid by `lnd-1`,
   reaching `Settled` — with the payment hash BTCPay recorded compared against the hash LND says it paid;
2. `POST /api/v1/stores/{id}/lightning/BTC/invoices/pay` paying an invoice `lnd-1` minted, with the verdict
   read from LND's own copy of that invoice — which also proves BTCPay resolved a plugin-supplied
   `ILightningClient` from the connection string the provisioner wrote;
3. `POST /api/v1/stores/{id}/spark/sweep` reaching outcome `Swept`, then `Confirmed` after mining, with the
   record's txid looked up in the fixture's `bitcoind` and its confirmations counted.

**The fixture's SSP liquidity is the consumable, and the numbers are tight.** The wallet is funded with
150,000 sats out of the SSP's own Spark leaves, and the deposit is only the first demand: every Spark payment
out of the wallet needs the SSP to *split* a leaf it can back. Against a leaf set that is too small or too
coarse, a wallet takes its deposit and then refuses both the first Lightning send and the exit quote with
`Tree service error: insufficient funds` — a message that names the wallet and means the SSP. This is
sequence-dependent: the fixture seeds one 500,000-sat leaf, the `LocalRegtest` suite that runs first in CI
borrows ~140,000 of it and returns most through its cooperative exit, and this suite's deposit then left
~360,000 — at which point a 3,000-sat send failed, while the same run against two leaves (857,000) passed.
So `up.sh` **tops the SSP up itself** until it holds at least five times the funding amount, adding one
500,000-sat leaf per round exactly as the fixture's `cashu-spark-fund-ssp` does (admin deposit address →
`bitcoind` → three confirmations → admin claim). Regtest coins are free; the top-up costs about ten seconds.

`down.sh` is what keeps that from being necessary often. It pays the store's remaining balance back to
`lnd-1` over Lightning before removing the containers, which returns the leaves to the SSP: a full
`up.sh` → suite → `down.sh` cycle was measured to cost the SSP **500 sats** net. The return leg is a
Lightning payment and not a sweep on purpose — an exit on this chain costs around 20,000 sats, so the
plugin's fee guard correctly refuses any remainder much smaller than that, which is exactly the amount left
after the sweep test. Without it the leftover is stranded in a wallet whose storage `down.sh` destroys, and
one measured pair of runs took the SSP from 500,000 to 382,000.

Observed figures from a green run, for calibration: 150,000 sats deposited, **140,100 credited** (a
9,900-sat claim fee at ~100 sat/vB, auto-claimed by the SDK's own worker), and a sweep of 125,100 delivering
105,400 for a **19,700-sat exit fee** — 18.7% of what the destination received, inside the store's 40%
ceiling and well inside the engine's 50% hard backstop. Every one of those numbers is set by the fixture's
fee market, which drifts upward as the fixture sends more `fee_rate=100` transactions, so the sweep test
quotes first and sizes itself off the quote rather than fixing an amount.
