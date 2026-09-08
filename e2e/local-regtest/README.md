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
e2e/local-regtest/up.sh                     # clone at the pin, then ./start.sh --spark
e2e/local-regtest/write-network.sh          # discover the live stack, write network.json

SPARK_LOCAL_REGTEST_NETWORK=$PWD/e2e/local-regtest/network.json \
  dotnet test --filter "Category=LocalRegtest"

e2e/local-regtest/down.sh                   # compose down --volumes, all profiles
```

`write-network.sh` prints the `export` line for the descriptor path when it finishes. Absent that
variable the suite skips itself, exactly as the Postgres and Lightspark suites do.

## What it costs

The fixture builds the Spark operators (Rust and Go), Electrs, `open-ssp` and `ldk-server` **from
source** — there are no published images. A cold first run is **tens of minutes** (the fixture's own
CI allows 90, and its Spark job takes around 40) and wants several GB of disk for the image layers
and build caches. Subsequent runs reuse those layers and come up in a few minutes.

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

## Credentials

Every secret in this stack is a public regtest fixture value, committed in the upstream repository:
the `cashu`/`cashu` Bitcoin Core RPC credentials, the `regtest-spark-admin-token`, the operator
signing keyshares. The wallet the suite connects is generated from random entropy per run and holds
regtest coins. **Never point any of this at real funds.**
