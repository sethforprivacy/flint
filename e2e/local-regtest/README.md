# The local Spark stack

[callebtc/cashu-regtest](https://github.com/callebtc/cashu-regtest) with its `--spark` profile: Bitcoin
Core in regtest, three Spark operators signing 2-of-3, an
[`open-ssp`](https://github.com/benthecarman/open-ssp) service provider backed by an LDK node, Electrs serving
an Esplora API, and three LND plus three CLN nodes wired into a funded channel topology.

Those Lightning nodes are why this exists. The plugin's other regtest suite talks to Lightspark's
hosted SSP, where nothing on the far side of an invoice is under our control: no counterparty will
pay one, and no one can mine. Here `lnd-1` pays a Flint invoice and Flint pays `lnd-1`'s, block
production is a command, and every credential is a published fixture value. See
[docs/testing.md](../../docs/testing.md#against-a-local-spark-stack).

## Running it

```bash
e2e/local-regtest/up.sh                     # clone at the pin, pull images, init the stack
e2e/local-regtest/write-network.sh          # discover the live stack, write network.json

SPARK_LOCAL_REGTEST_NETWORK=$PWD/e2e/local-regtest/network.json \
  dotnet test --filter "Category=LocalRegtest"

e2e/local-regtest/down.sh                   # compose down --volumes, all profiles
```

`write-network.sh` prints the `export` line for the descriptor path when it finishes. Absent that
variable the suite skips itself, exactly as the Postgres and Lightspark suites do.

### What `up.sh` actually runs

Not the fixture's `./start.sh --spark`. That is the fixture's init chain *plus* its own acceptance
suites — seven nodes' channel and UTXO counts, an LNbits probe, `cashu-spark-e2e` (which rebuilds the
fixture's Rust Breez SDK test client with `--build` and settles four payments through it), and the `ldk/`
and `fees/` e2e scripts — all of which is the fixture proving itself, and which mines dozens of extra
blocks on the way. `up.sh` runs the init chain itself (stop, `up -d`, bitcoind funding, the seven-node
Lightning topology, the six LDK channels, then `cashu-spark-init`, the step that funds the SSP's Spark
liquidity) with one step left out: `cashu-fees-init`, which funds a fee-charging hub and waits for its
channel policies to gossip into three routing graphs. That wait is the fixture's flakiest step — its own
CI timed out in it in two of the three runs before this pin, and so did this suite's first run on
ubuntu-latest — and nothing here routes through the hub: `lnd-1` and the SSP's LDK node share a direct
channel. `up.sh` then asserts the three properties the suites actually depend on:

- `open-ssp` reports `ldk_mode: live` **and** non-zero `spark.available_sats`;
- the SSP's LDK node has all six channels ready;
- every Lightning node sits at `bitcoind`'s tip (the fixture's own `wait-for-ldk-height`).

Set `FLINT_REGTEST_FULL=1` to run the fixture's full `start.sh --spark` instead — worth doing when
you suspect the fixture rather than the plugin.

## What it costs

The fixture publishes no images: its compose file builds the Spark operators (Rust), `open-ssp` (Go),
Electrs (Rust) and `ldk-server` (Rust) **from source**, which is tens of minutes and several GB of
disk. So [`.github/workflows/local-regtest-images.yml`](../../.github/workflows/local-regtest-images.yml)
builds that set once per pinned fixture SHA and pushes it to `ghcr.io`, and `up.sh` pulls each image
and `docker tag`s it to the exact local name the pinned compose file expects — at which point compose
finds the image already present and skips the build. Both sides read the names and the service list out
of `docker compose --profile spark config` rather than hardcoding them, because a hardcoded list that
drifted would not fail: compose would quietly build again, and the only symptom would be a "fast" run
that takes forty minutes.

| variable | effect |
|---|---|
| `FLINT_REGTEST_IMAGE_PREFIX` | registry path to pull from. Default `ghcr.io/sethforprivacy/flint-regtest`. Set it to the empty string to never pull. |
| `FLINT_REGTEST_FULL=1` | run the fixture's own `./start.sh --spark` instead of the leaner path above. |

Every pull failure is a warning, never fatal — a fork whose packages were never published, or a
network blip, falls through to building from source, which still works. **The published images are
`linux/amd64` only** (an arm64 variant would mean cross-building several large Rust projects under
QEMU, which is hours rather than minutes), so on Apple silicon `up.sh` does not pull at all: it builds
native images once, and every run after that reuses those layers and comes up in a few minutes.

`up.sh` is idempotent about the *checkout*, not the *state*: the fixture's `start.sh` begins with
`docker compose down --volumes`, so every run resets the chain, the operator databases, the SSP
mnemonic and the generated TLS certificates. The descriptor is therefore only valid for the run
that produced it — re-run `write-network.sh` after every `up.sh`. `down.sh` deletes the stale
`network.json` for that reason.

Host dependencies are `docker`, `git`, `jq`, `curl` and `openssl`.

### On macOS

Docker Desktop for macOS cannot host lightningd's SQLite database on a bind mount: the three core Core
Lightning nodes die seconds after starting with `**BROKEN** lightningd: ... attempt to write a readonly
database`, `start.sh` times out in `wait-for-clightning-sync`, and the Spark init that funds the SSP never
runs. The fixture's own `clightning-4`, which sits on a named volume, starts fine on the same host — so on
Darwin `up.sh` writes a `docker-compose.override.yml` into the checkout that gives `clightning-1..3` (and
the REST sidecar that shares `clightning-2`'s directory) named volumes too. Compose merges `volumes` by
container target path, so the override replaces the bind mounts without touching the pinned compose file,
and Linux keeps the fixture's own layout. The override is only written if the checkout has none.

One consequence: with named volumes the host-side `data/clightning-*` directories stay empty, so the
fixture's CLN REST credentials are not on the host. No LocalRegtest test uses them — `lnd-1` is the
counterparty on both sides — and `write-network.sh` treats `clightning-1` as optional for that reason.

Do not mine or run the suite while `up.sh` is still running. The fixture's init waits for all seven
Lightning nodes to sit at one exact block height, and a block mined from outside during that wait makes
the loop time out; the stack then looks up but the SSP is unfunded.

## What is pinned, and how to bump it

`up.sh` holds `CASHU_REGTEST_SHA`, and verifies the checkout matches it on every run — including a
checkout you point at with `CASHU_REGTEST_DIR`. The pin covers more than the compose topology: the
operators' identity public keys, the loopback ports `8535`/`8536`/`8537`, the Esplora port `30000`,
the SSP's port `5000`, the `cashu` Compose project name that gives every container its name, and
the `regtest-spark-admin-token`. `write-network.sh` reads what it can from the pinned checkout's own
`spark/operators.json` rather than duplicating it, and asserts what it cannot — that each
certificate carries a `DNS:localhost` SAN, that `/identity` returned a compressed pubkey.

To bump: update `CASHU_REGTEST_SHA` here **and** the `ref:` in
[`.github/workflows/local-regtest.yml`](../../.github/workflows/local-regtest.yml), delete the local
checkout, and run the flow above. Read the fixture's diff for moved ports or changed operator keys
while you are at it; the assertions above will catch some of that, but not a port that moved.

`up.sh` is the *only* copy of the pin that anything reads programmatically: the images workflow greps
`CASHU_REGTEST_SHA` out of this file rather than carrying its own, and tags the images it publishes with
it. So a merged bump republishes the image set automatically (that workflow triggers on pushes to `main`
touching `up.sh`), and until it finishes, CI's pull misses and falls back to building from source —
slow, not broken.

## Credentials

Every secret in this stack is a public regtest fixture value, committed in the upstream repository:
the `cashu`/`cashu` Bitcoin Core RPC credentials, the `regtest-spark-admin-token`, the operator
signing keyshares. The wallet the suite connects is generated from random entropy per run and holds
regtest coins. **Never point any of this at real funds.**
