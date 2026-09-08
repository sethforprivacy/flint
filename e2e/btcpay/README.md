# Flint on a real BTCPay Server, against the local Spark stack

This directory stands up **BTCPay Server 2.4.4 + NBXplorer + Postgres**, joined to the Spark fixture's own
docker network, with the Flint plugin installed the way an install installs it. It exists so the plugin can
be observed from *outside*: the [`BtcpayE2E`](../../BTCPayServer.Plugins.Flint.Tests/BtcpayE2E) suite drives
it entirely over Greenfield HTTP, and every assertion is checked against the fixture's own bitcoind or LND
rather than against anything the plugin says about itself.

It is stage 2. Stage 1 — [`../local-regtest`](../local-regtest) plus the `LocalRegtest` suite — builds the
plugin's collaborators by hand and connects an SDK instance of its own. That proves the money paths against
real Spark operators, and proves nothing at all about the host: BTCPay's invoice lifecycle, the Lightning
payment method the provisioner writes, the Greenfield surface's authorisation, the reconciliation and sweep
tasks running on BTCPay's schedule against Postgres, or the plugin *loading* — out of the official image,
as a directory under the data dir, with Breez's native library resolved by BTCPay's plugin load context.

## Use

The Spark stack has to be up first; nothing here starts a chain.

```bash
../local-regtest/up.sh          # tens of minutes cold, minutes warm
./up.sh                         # ~20 s
FLINT_BTCPAY_E2E=$PWD/btcpay.json \
  ~/.dotnet/dotnet test ../../BTCPayServer.Plugins.Flint.Tests/BTCPayServer.Plugins.Flint.Tests.csproj \
  -c Release --filter "Category=BtcpayE2E" --output Detailed
./down.sh                       # this side only
../local-regtest/down.sh
```

`up.sh` is idempotent against live containers: it reuses the store and leaves a running wallet alone. A
clean slate is `down.sh` then `up.sh`, which gets a fresh Postgres and therefore a fresh store.

BTCPay's UI is on <http://127.0.0.1:14142> (loopback only), and `up.sh` prints the throwaway
administrator's credentials. `FLINT_BTCPAY_E2E_PORT` moves the port.

## What `up.sh` does, and why in that order

1. **Asserts the fixture is up and usable.** The in-network Spark descriptor is generated first, because
   `../local-regtest/write-network.sh --docker` is also the stack's health check: it asserts every operator
   is running, reads their live TLS certificates out of the compose volume, verifies the SAN the in-network
   address needs, and reads the SSP's identity. Then the SSP's Spark liquidity is checked — see
   [Liquidity](#liquidity-is-the-consumable).
2. **Builds the plugin and stages it.** The official image is a Release build and `DEBUG_PLUGINS` is behind
   `#if DEBUG` in `PluginManager`, so the side-loading route in
   [`docs/development.md`](../../docs/development.md) does not exist here. What does exist is the directory
   scan: `PluginManager` walks `<plugindir>/*/` and loads `<identifier>/<identifier>.dll`. An unpacked copy
   of the Release build output under that name is byte-for-byte what `PluginPacker` would have zipped into a
   `.btcpay`, minus the zip. Both `linux-x64` and `linux-arm64` copies of
   `libbreez_sdk_spark_bindings.so` are asserted present — the images are multi-arch, so the same staged
   directory serves an amd64 runner and an arm64 Mac.
3. **Starts the containers** and waits for `/api/v1/health`, then reads
   `Running plugin BTCPayServer.Plugins.Flint` off the log. Healthy and loaded are different questions: a
   plugin that threw during `Execute` is disabled, BTCPay restarts *without* it, and every Flint route then
   404s.
4. **Provisions over Greenfield**, exactly as [`docs/greenfield-api.md`](../../docs/greenfield-api.md)
   describes: the first administrator (`POST /api/v1/users`, which needs no authentication while no admin
   exists), an API key, a store, and `POST /api/v1/stores/{id}/spark` with `seedSource: generate`.
5. **Funds the wallet through the plugin's own endpoints** — `GET .../spark/deposit` for the address, then
   bitcoind, then mining, then the SDK's own claim (with `POST .../spark/deposit/claim` as the fallback the
   plugin provides for a claim the automatic worker priced too low). Deliberately not the SSP's admin API: a
   deposit credited out of band would prove nothing about how the plugin configures the SDK.
6. **Configures sweeping** with a static destination and a fee ceiling this chain's exit fee can actually
   clear, and writes `btcpay.json`.

The generated recovery phrase is never captured. It is regtest money out of a fixture `down.sh` destroys,
and writing a seed into a file beside a checkout is not a habit worth forming.

## The two descriptors are not interchangeable

`network.json` names `https://localhost:8535` and `http://127.0.0.1:5000`, which is right for a client on
the host. From inside the fixture's network `127.0.0.1` is *the BTCPay container* and `localhost:8535` is
nothing at all, so this side needs `write-network.sh --docker`, which emits `spark-operator-N:8535`,
`spark-ssp:5000` and `spark-electrs:3002`. Those hostnames are verifiable because `spark-cert-init` issues
each operator certificate with `DNS:spark-operator-<i>` alongside `DNS:localhost`; the script asserts
whichever SAN it is about to depend on rather than assuming both survive a fixture bump.

The descriptor is mounted read-only at `/datadir/spark-network.json` and named by
`SPARK_LOCAL_REGTEST_NETWORK`. That variable is the plugin's **one** production read of the environment, in
`SparkService.ResolveCustomNetwork`, and it is not read at all off regtest — the comment there gives three
independent reasons why that is safe.

## Liquidity is the consumable

The wallet is funded with 150,000 sats out of the SSP's own Spark leaves, and the deposit is only the first
demand: every Spark payment out of the wallet needs the SSP to *split* a leaf it can back. Against a leaf set
that is too small or too coarse the wallet takes its deposit and then refuses both the first Lightning send
and the exit quote with `Tree service error: insufficient funds` — a message that names this wallet and means
the SSP. Measured: with the fixture's single 500,000-sat leaf, running this suite right after `LocalRegtest`
left ~360,000 and a 3,000-sat send failed; the same run against two leaves (857,000) passed. So `up.sh`
**tops the SSP up itself** until it holds at least five times the funding amount, adding one 500,000-sat leaf
per round the way the fixture's `cashu-spark-fund-ssp` does — admin deposit address, `bitcoind`, three
confirmations, admin claim — about ten seconds per leaf. To do it by hand:

```bash
cd ../local-regtest/cashu-regtest
export COMPOSE_PROJECT_NAME=cashu COMPOSE_PROFILES=spark
. ./docker-scripts.sh && cashu-spark-fund-ssp
```

`down.sh` is what keeps that rare. Before removing the containers it pays the store's remaining balance
back to `lnd-1` over Lightning, which returns the leaves to the SSP; a full `up.sh` → suite → `down.sh`
cycle was measured to cost the SSP **500 sats** net. The return leg is a Lightning payment and not a sweep
on purpose: an exit on this chain costs around 20,000 sats, so the plugin's fee guard correctly refuses any
remainder much smaller than that — which is exactly the amount the sweep test leaves. Without the return,
one measured pair of runs took the SSP from 500,000 to 382,000.

`down.sh --volumes` is not optional either. BTCPay's data directory holds the plugin's per-store SDK
storage, and a store's wallet is keyed by a seed that only ever existed there.

## Pins

| Image | Pin | Why that one |
|---|---|---|
| `btcpayserver/btcpayserver` | `2.4.4`, by index digest | The `btcpayserver` submodule tag, and therefore `Constants.BuiltAgainstBTCPayServerVersion`. Multi-arch index digest so one line works on amd64 and arm64. |
| `nicolasdorier/nbxplorer` | `2.6.10`, by index digest | What BTCPay's own `BTCPayServer.Tests/docker-compose.yml` pairs with this tag. |
| `postgres` | `17-alpine`, by digest | The same image and digest `ci.yml`'s store-test service uses, so the plugin's migrations run on a version CI already covers. |

## Notes

- `BTCPAY_PLUGINDIR` is set explicitly. `DataDirectories.Configure` reads `plugindir` independently of
  `BTCPAY_DATADIR` and otherwise falls back to a path under `$HOME`, so without it the bind mount would not
  be the directory `PluginManager` scans.
- `ASPNETCORE_URLS` is set rather than BTCPay's `--bind`/`--port`: those are only honoured when an HTTPS
  certificate is configured (see `Startup.cs`), and the image sets no `ASPNETCORE_URLS` of its own, so
  Kestrel would listen on `127.0.0.1:5000` *inside* the container and the published port would answer
  nothing.
- Service names are prefixed `flint-e2e-` because they share a network namespace with about forty fixture
  services, and a hostname collision on a shared network resolves silently to the wrong container.
- BTCPay logs a warning that NBXplorer's cookie file is missing. Expected: NBXplorer runs with
  `NBXPLORER_NOAUTH=1`, and BTCPay connects anyway — the log line to look for is
  `Connected to WebSocket of NBXplorer (BTC)`.
