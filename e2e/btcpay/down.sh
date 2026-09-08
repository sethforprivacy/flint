#!/usr/bin/env bash
# Remove the BTCPay Server, NBXplorer and Postgres containers this directory's compose file created,
# and their volumes.
#
# It touches nothing of the Spark stack's. That is the point of it being a separate script: the
# fixture takes tens of minutes to build and the BTCPay side takes seconds, so tearing the second
# down and leaving the first up is the normal development loop — and in CI, this runs *before*
# e2e/local-regtest/down.sh so BTCPay releases its Postgres and its plugin storage while the network
# it is attached to still exists. (A compose `down` against a network that is already gone leaves
# the containers behind.)
#
# `--volumes` is not optional. BTCPay's data directory holds the plugin's per-store SDK storage, and
# a store's Spark wallet is keyed by a seed that only ever existed in that directory: a second `up.sh`
# over a surviving volume would find settings for a store that no longer exists in a fresh Postgres,
# and the plugin would refuse to start the wallet on a storage claim it cannot explain.
#
# Environment:
#   FLINT_BTCPAY_E2E_PORT  only so compose can interpolate the same port mapping; unused otherwise
#   FLINT_REGTEST_NETWORK  the fixture network the compose file declares as external
set -euo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"

export FLINT_BTCPAY_E2E_PORT="${FLINT_BTCPAY_E2E_PORT:-14142}"
export FLINT_REGTEST_NETWORK="${FLINT_REGTEST_NETWORK:-cashu_default}"

# ---------------------------------------------------------------------------------------------
# Give the SSP's liquidity back first, best effort.
# ---------------------------------------------------------------------------------------------
# Every up.sh funds a wallet with 150,000 sats out of the SSP's own Spark leaves, and a wallet that
# is simply abandoned keeps whatever is left in it: the fixture's seed falls by the leftover per run
# and never recovers. Measured directly — a run whose sweep was refused took the fixture's SSP from
# 382,000 to 239,900 in one go, and up.sh's own liquidity floor then refused to start the next one.
#
# **The return leg is a Lightning payment, not a cooperative exit.** Both give the leaves back to the
# SSP, but an exit on this chain costs around 20,000 sats and the plugin's fee guard therefore
# refuses any remainder under roughly three times that — correctly, and the remainder after the sweep
# test is deliberately much smaller than that. Paying an invoice on the fixture's own LND costs a few
# sats of routing fee and moves the whole balance, so it works on exactly the amounts an exit cannot.
#
# Best effort throughout, and silent about failure beyond a line: this runs after the tests have
# their verdicts, and in CI on the failure path too. A teardown that failed the job while tidying up
# would be worse than a stack that needs a top-up (cashu-spark-fund-ssp, in the fixture's
# docker-scripts.sh).
handoff="$script_dir/btcpay.json"
if [ -f "$handoff" ] && command -v jq >/dev/null && command -v curl >/dev/null \
   && command -v docker >/dev/null; then
  base_url="$(jq -r '.baseUrl // empty' "$handoff")"
  api_key="$(jq -r '.apiKey // empty' "$handoff")"
  store_id="$(jq -r '.storeId // empty' "$handoff")"
  lnd="$(jq -r '.fixture.lndContainer // empty' "$handoff")"

  if [ -n "$base_url" ] && [ -n "$api_key" ] && [ -n "$store_id" ] && [ -n "$lnd" ]; then
    balance="$(curl --silent --max-time 60 "$base_url/api/v1/stores/$store_id/spark" \
      -H "Authorization: token $api_key" 2>/dev/null \
      | jq -r '.balanceSats // 0' 2>/dev/null || echo 0)"

    # A floor rather than "> 0": below a couple of thousand sats there is nothing worth a round trip,
    # and balanceSats is documented as indicative anyway (it lags settlement by ~20 s), so a small
    # number here is not something to act precisely on.
    if [ "${balance:-0}" -gt 2000 ]; then
      # 500 sats of headroom for the routing fee, which the payer pays on top of the invoice amount.
      amount=$((balance - 500))
      echo "==> returning $amount of the store's $balance sats to the SSP over Lightning (best effort)"

      # --rpcserver is required and cannot be localhost: the fixture starts lnd with
      # `--rpclisten=<hostname>:10009`, so it is not listening on loopback at all. The hostname is
      # read off the container rather than guessed from its name — the container is `cashu-lnd-1-1`
      # and the hostname is `lnd-1`, and nothing derives one from the other reliably. Same
      # invocation RegtestControl uses, for the same reason.
      lnd_host="$(docker inspect -f '{{.Config.Hostname}}' "$lnd" 2>/dev/null || true)"
      bolt11=""
      if [ -n "$lnd_host" ]; then
        bolt11="$(docker exec "$lnd" lncli --network regtest --rpcserver="$lnd_host:10009" \
          addinvoice --amt "$amount" --memo flint-btcpay-e2e-return --expiry 600 2>/dev/null \
          | jq -r '.payment_request // empty' 2>/dev/null || true)"
      fi

      if [ -n "$bolt11" ]; then
        outcome="$(curl --silent --max-time 180 -X POST \
          "$base_url/api/v1/stores/$store_id/lightning/BTC/invoices/pay" \
          -H "Authorization: token $api_key" -H 'Content-Type: application/json' \
          -d "$(jq -nc --arg b "$bolt11" '{BOLT11: $b, maxFeePercent: "5.0", sendTimeout: 120}')" \
          2>/dev/null || true)"
        echo "    $(printf '%s' "${outcome:-no response}" | head -c 200)"
      else
        echo "    could not mint an LND invoice to pay; leaving the balance where it is."
      fi
    fi
  fi
fi

# The declared external network may already be gone (someone ran the fixture's down.sh first).
# Compose refuses to resolve the file at all in that case, so fall back to removing the project's
# containers and volumes by label — which is what `down` would have done.
if docker network inspect "$FLINT_REGTEST_NETWORK" >/dev/null 2>&1; then
  echo "==> docker compose down --volumes"
  docker compose -f "$script_dir/docker-compose.yml" down --volumes --remove-orphans
else
  echo "==> the $FLINT_REGTEST_NETWORK network is gone; removing the project by label instead"
  containers="$(docker ps -aq --filter label=com.docker.compose.project=flint-btcpay-e2e)"
  if [ -n "$containers" ]; then
    # shellcheck disable=SC2086  # deliberate word splitting of the id list
    docker rm -f $containers >/dev/null
  fi
  for volume in flint-btcpay-e2e_flint-e2e-postgres \
                flint-btcpay-e2e_flint-e2e-nbxplorer \
                flint-btcpay-e2e_flint-e2e-btcpay; do
    docker volume rm "$volume" >/dev/null 2>&1 || true
  done
fi

# The handoff file names an API key for a server that no longer exists, and the staged plugin plus
# the in-network descriptor are both per-run copies. Left behind, btcpay.json is the more dangerous
# of the three: a later `dotnet test` would find FLINT_BTCPAY_E2E pointing at a dead host and fail on
# a connection error rather than skipping.
rm -f "$script_dir/btcpay.json"
rm -rf "$script_dir/data"

echo "==> BTCPay e2e stack removed. The Spark stack is untouched; e2e/local-regtest/down.sh stops that."
