# Flint plugin documentation

Everything about the plugin beyond "what is it and how do I install it". Start at the
[project README](../README.md) if you have not installed it yet.

## Running a store on it

- **[Setting a store up](store-setup.md)** — where the seed comes from, what the status page tells you,
  what removing Spark from a store does, and how the plugin guarantees an invoice is settled.
- **[Sweeping the balance out](sweeping.md)** — cooperative exits to Bitcoin, what they cost and why the
  fee guard exists; and the cross-chain destination, delivering a stablecoin to an address you control on
  an EVM chain.
- **[Funding the wallet on-chain](deposits.md)** — the static deposit address, and the fee-ceiling failure
  that leaves a deposit unclaimed forever if nothing intervenes.
- **[Holding the balance in dollars: Stable Balance](stable-balance.md)** — converting to USDB between
  sweeps, and the freeze risk that comes with it.
- **[Known limitations](limitations.md)** — the full list, stated rather than implied. Read it before you
  put money through this.
- **[Trust model](trust-model.md)** — every party the plugin depends on, what each can do, and what
  recourse exists.

## Automating it

- **[Greenfield API](greenfield-api.md)** — the endpoints that do everything the pages do, their
  API-key permissions, and a worked `curl` script for provisioning a store headlessly.

## Deploying it

- **[Railway](railway.md)** — persistent volume requirements, environment variables, log rotation
  options, and a post-deploy verification script for Railway deployments.

## Working on it

- **[Building](building.md)** — prerequisites, the submodule pin, packaging a `.btcpay`, and why the
  built-against version and the support floor are separate numbers.
- **[Tests](testing.md)** — the default run, the opt-in Postgres and regtest suites, and the runbook for
  the funded regtest wallet CI uses.
- **[Local development](development.md)** — side-loading the plugin into a local BTCPay, and authoring EF
  migrations.
- **[CI, releases & upstream updates](ci-and-releases.md)** — what each workflow does, which jobs gate a
  merge, artifact signing, and how the btcpayserver submodule gets bumped.

## Elsewhere in the repository

- **[CHANGELOG.md](../CHANGELOG.md)** — what is in this release, and what its maturity actually is.
- **[NOTICE](../NOTICE)** — third-party notices and attribution.
