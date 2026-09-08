#!/usr/bin/env bash
# Bring up a BTCPay Server running the Flint plugin against the already-running local Spark stack,
# provision a store through Greenfield, fund its wallet, and write e2e/btcpay/btcpay.json.
#
# This is stage 2 of the local-regtest work. Stage 1 (BTCPayServer.Plugins.Flint.Tests/LocalRegtest)
# drives the SDK directly and proves the plugin's own collaborators against local Spark operators.
# What it cannot see is everything that only exists inside a host: BTCPay's invoice lifecycle, the
# Lightning payment method wiring, the Greenfield surface, the reconciler running as a hosted
# service, and the sweep engine's guards reading real store settings. That is what this brings up.
#
# Prerequisites, all asserted below rather than assumed:
#   * the fixture stack is up (e2e/local-regtest/up.sh) — this joins its docker network and uses its
#     bitcoind, Spark operators, SSP and Esplora. Nothing here starts a chain.
#   * docker, jq, curl, and a dotnet SDK on PATH (or DOTNET at its path).
#
# What it does NOT do: mine while the fixture is initialising, or restart anything of the fixture's.
# It mines only to mature its own funding deposit, once the stack has already reported healthy.
#
# Environment:
#   FLINT_BTCPAY_E2E_PORT   host port BTCPay is published on (default 14142, loopback only)
#   FLINT_REGTEST_NETWORK   the fixture's docker network (default cashu_default)
#   SPARK_NETWORK_DESCRIPTOR
#                           an existing --docker descriptor to use instead of generating one
#   DOTNET                  the dotnet binary (default: `dotnet` from PATH, else ~/.dotnet/dotnet)
#   FLINT_BTCPAY_SKIP_BUILD if 1, reuse the plugin's existing Release build output
set -euo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd -- "$script_dir/../.." && pwd)"
data_dir="$script_dir/data"
plugin_dir="$data_dir/plugins/BTCPayServer.Plugins.Flint"
descriptor="$data_dir/spark-network.json"
output="$script_dir/btcpay.json"

port="${FLINT_BTCPAY_E2E_PORT:-14142}"
network="${FLINT_REGTEST_NETWORK:-cashu_default}"
base_url="http://127.0.0.1:$port"

# A throwaway administrator on a throwaway regtest server. Fixed rather than generated so a run that
# died halfway can be re-run against the same containers: creating the first admin twice answers 403
# (registrations are disabled once one exists), and the script treats that as "already done" below.
admin_email="flint-e2e@example.com"
admin_password="Flint-e2e-regtest-1!"

# How long BTCPay may take to answer /api/v1/health. Generous because the first start applies both
# BTCPay's and the plugin's migrations against an empty database.
health_timeout=300
# The funding path: bitcoind -> deposit address -> 3 confirmations -> the SSP claims it. Stage 1
# measured this at anywhere between 7 s and 305 s on the same stack, because it is the SDK's own
# deposit worker deciding when to look. The ceiling is so a broken stack explains itself instead of
# hanging until a CI job's timeout kills the run with nothing to read.
funding_timeout=900

# Sized for the whole suite exactly as LocalRegtestStack.FundingSats is, and for the same two
# reasons. Below: a ~2,000-sat receive, a ~3,000-sat send, and a cooperative exit whose fee on this
# chain (~100 sat/vB) runs to ~20,000 sats. Above: the fixture seeds open-ssp with a single
# 500,000-sat leaf, and every Spark payment out of this wallet needs the SSP to split leaves it can
# back — funding beyond a third of the SSP's balance was observed to succeed and then fail the first
# Lightning send with "Tree service error: insufficient funds".
funding_sats=150000

# Ceiling for a manual deposit claim, in satoshi. The SDK's automatic claim is priced off the
# chain's *recommended* fee plus the plugin's 2 sat/vB leeway, and on this fixture that is far below
# what the SSP asks: every funding transaction the fixture sends specifies fee_rate=100, so a claim
# is quoted near 100 sat/vB (~10,000 sats) and the automatic attempt is refused. The manual claim
# endpoint exists for exactly that, and the plugin's own guards still apply — 25,000 sats is the
# store's default manual ceiling and no configuration can authorise more than half a deposit.
claim_fee_ceiling_sats=20000

say() { printf '==> %s\n' "$*"; }
die() { printf 'error: %s\n' "$*" >&2; exit 1; }

for tool in docker jq curl; do
  command -v "$tool" >/dev/null || die "$tool is required"
done

dotnet_bin="${DOTNET:-}"
if [ -z "$dotnet_bin" ]; then
  if command -v dotnet >/dev/null; then dotnet_bin="dotnet"
  elif [ -x "$HOME/.dotnet/dotnet" ]; then dotnet_bin="$HOME/.dotnet/dotnet"
  else die "no dotnet on PATH and none at ~/.dotnet/dotnet; set DOTNET"; fi
fi

# ---------------------------------------------------------------------------------------------
# 1. The fixture has to be up first, and it has to be *usable*, not merely present.
# ---------------------------------------------------------------------------------------------
docker network inspect "$network" >/dev/null 2>&1 \
  || die "no docker network '$network'. Bring the Spark stack up first: e2e/local-regtest/up.sh"

# The in-network Spark descriptor, generated first because it is also the stack's own health check:
# write-network.sh asserts every operator is running, reads their live TLS certificates out of the
# compose volume, verifies the SAN the in-network address needs, and reads the SSP's identity. A
# half-started stack fails here, naming the service, rather than fifteen minutes later as a wallet
# that will not connect.
mkdir -p "$data_dir"
if [ -n "${SPARK_NETWORK_DESCRIPTOR:-}" ]; then
  say "using the descriptor at $SPARK_NETWORK_DESCRIPTOR"
  cp "$SPARK_NETWORK_DESCRIPTOR" "$descriptor"
else
  say "writing an in-network Spark descriptor"
  # --docker, not the host descriptor: from inside cashu_default, `127.0.0.1:5000` is the BTCPay
  # container itself and `localhost:8535` is nothing. See write-network.sh's header.
  "$repo_root/e2e/local-regtest/write-network.sh" --docker "$descriptor" >/dev/null
fi
jq -e '.operators[0].address | startswith("https://spark-operator-")' "$descriptor" >/dev/null \
  || die "$descriptor does not carry in-network operator addresses. Regenerate it with
       e2e/local-regtest/write-network.sh --docker."

# The SSP's Spark liquidity is the real budget: this wallet is funded out of the SSP's own leaves,
# and a claim against an SSP holding less than the funding amount fails as "insufficient funds"
# naming this wallet rather than the SSP. Checked before anything is built, because the fix (top the
# SSP up the way the fixture's cashu-spark-fund-ssp does) is a different job from anything below.
#
# /status is admin-authenticated — the token is the fixture's fixed one, recorded in the descriptor
# — while /identity is not; write-network.sh already read /identity, so this call is only about the
# balance.
say "checking the fixture's Spark SSP"
ssp_status="$(curl --fail --silent --max-time 15 \
  -H "Authorization: Bearer $(jq -er '.fixture.sspAdminToken' "$descriptor")" \
  http://127.0.0.1:5000/status || true)"
[ -n "$ssp_status" ] || die "the fixture's SSP did not answer http://127.0.0.1:5000/status.
       The stack is not ready; read e2e/local-regtest/up.log."

# `.spark.available_sats` is where open-ssp reports its Spark leaves; the two fallbacks cover older
# and flatter shapes of the same document rather than failing on a field move.
available_sats="$(jq -r '.spark.available_sats // .available_sats // 0' <<<"$ssp_status")"
# Topped up rather than refused. The SSP is funded out of *leaves*, and the demand is not the deposit
# alone: every Spark payment out of this wallet needs the SSP to split a leaf it can back, and a leaf set
# too small or too coarse answers the first Lightning send and the exit quote with "Tree service error:
# insufficient funds" — a failure that names this wallet and means the SSP. It is sequence-dependent,
# which is what makes it worth automating: the fixture seeds one 500,000-sat leaf, the LocalRegtest suite
# that runs before this in CI takes ~140,000 of it for its own deposit and gives most back through its
# cooperative exit, and this script's 150,000 deposit then left ~360,000 — at which point a 3,000-sat
# send failed, while the same run against 857,000 (two leaves) passed. Five times the funding amount is
# the floor here, and each top-up adds one 500,000-sat leaf exactly the way the fixture's own
# cashu-spark-fund-ssp does: admin deposit address → bitcoind → three confirmations → admin claim.
ssp_admin_token="$(jq -er '.fixture.sspAdminToken' "$descriptor")"
bitcoind_container="$(jq -er '.fixture.bitcoindContainer' "$descriptor")"
bitcoin_cli() {
  docker exec "$bitcoind_container" bitcoin-cli -regtest \
    -rpcuser="$(jq -er '.fixture.bitcoindRpcUser' "$descriptor")" \
    -rpcpassword="$(jq -er '.fixture.bitcoindRpcPassword' "$descriptor")" \
    -rpcwallet=cashu "$@"
}
mine() { bitcoin_cli generatetoaddress "$1" "$(bitcoin_cli getnewaddress)" >/dev/null; }
ssp_available_sats() {
  curl --fail --silent --max-time 15 -H "Authorization: Bearer $ssp_admin_token" \
    http://127.0.0.1:5000/status | jq -r '.spark.available_sats // .available_sats // 0'
}
fund_ssp_leaf() {
  local leaf_sats=500000 address txid vout tx_hex
  address="$(curl --fail --silent --max-time 30 -X POST -H "Authorization: Bearer $ssp_admin_token" \
    http://127.0.0.1:5000/admin/spark/deposit-address | jq -er '.address')" || return 1
  txid="$(bitcoin_cli -named sendtoaddress "address=$address" amount=0.005 fee_rate=100 | tr -d '\r\n')" || return 1
  mine 3
  sleep 4
  vout="$(bitcoin_cli getrawtransaction "$txid" true | jq -er --arg a "$address" \
    '.vout[] | select(.scriptPubKey.address == $a) | .n')" || return 1
  tx_hex="$(bitcoin_cli getrawtransaction "$txid" false | tr -d '\r\n')" || return 1
  curl --fail --silent --max-time 60 -X POST -H "Authorization: Bearer $ssp_admin_token" \
    -H "Content-Type: application/json" \
    -d "$(jq -nc --arg h "$tx_hex" --argjson v "$vout" '{transaction_hex: $h, vout: $v}')" \
    http://127.0.0.1:5000/admin/spark/claim-deposit >/dev/null || return 1
  say "added a $leaf_sats-sat leaf to the SSP"
}
ssp_floor=$((funding_sats * 5))
for _ in 1 2 3; do
  [ "$available_sats" -ge "$ssp_floor" ] && break
  say "SSP has $available_sats sats of Spark liquidity, below the $ssp_floor floor: topping it up"
  fund_ssp_leaf || die "could not add a leaf to the SSP; read docker logs of the spark-ssp container"
  available_sats="$(ssp_available_sats)"
done
[ "$available_sats" -ge "$ssp_floor" ] \
  || die "the SSP still reports only $available_sats sats of Spark liquidity after three top-ups."
say "SSP has $available_sats sats of Spark liquidity"

# ---------------------------------------------------------------------------------------------
# 2. Build the plugin and stage it the way a real install is laid out.
# ---------------------------------------------------------------------------------------------
# The official btcpayserver image is a Release build, and DEBUG_PLUGINS is behind `#if DEBUG` in
# PluginManager — so the side-loading route docs/development.md describes does not exist here. What
# does exist is the directory scan: PluginManager walks <plugindir>/*/ and loads
# <identifier>/<identifier>.dll. An unpacked copy of the Release build output under that name is
# byte-for-byte what PluginPacker would have zipped into a .btcpay, minus the zip.
if [ "${FLINT_BTCPAY_SKIP_BUILD:-0}" != "1" ]; then
  say "building the plugin (Release)"
  # -m:1 for the reason ci.yml gives: the Razor and BTCPayServer builds race on shared intermediate
  # output under parallel MSBuild.
  "$dotnet_bin" build "$repo_root/BTCPayServer.Plugins.Flint/BTCPayServer.Plugins.Flint.csproj" \
    -c Release -m:1
fi

build_output="$repo_root/BTCPayServer.Plugins.Flint/bin/Release/net10.0"
[ -f "$build_output/BTCPayServer.Plugins.Flint.dll" ] \
  || die "no plugin assembly at $build_output. Run without FLINT_BTCPAY_SKIP_BUILD=1."

say "staging the plugin into $plugin_dir"
rm -rf "$plugin_dir"
mkdir -p "$plugin_dir"
cp -R "$build_output/." "$plugin_dir/"

# Breez's SDK is a Rust library behind a C ABI, and the SDK's very first call P/Invokes it. The
# plugin's load context resolves it out of runtimes/<rid>/native (AssemblyDependencyResolver over the
# plugin's own deps.json), so the RID that matches the container has to be present. Both linux RIDs
# are asserted rather than just the host's: the images are multi-arch, so the same staged directory
# is used by an amd64 CI runner and an arm64 developer machine.
for rid in linux-x64 linux-arm64; do
  [ -f "$plugin_dir/runtimes/$rid/native/libbreez_sdk_spark_bindings.so" ] \
    || die "the staged plugin has no $rid native library. The Breez.Sdk.Spark package ships both
       linux RIDs; a build output missing one will fail inside the container as a
       DllNotFoundException on the first SDK call."
done

# ---------------------------------------------------------------------------------------------
# 3. Start the containers.
# ---------------------------------------------------------------------------------------------
compose() {
  FLINT_BTCPAY_E2E_PORT="$port" FLINT_REGTEST_NETWORK="$network" \
    docker compose -f "$script_dir/docker-compose.yml" "$@"
}

say "starting Postgres, NBXplorer and BTCPay Server on the $network network"
compose up -d

say "waiting for BTCPay Server on $base_url (up to ${health_timeout}s)"
# /api/v1/health is unauthenticated and reports the database, which is the slow part of a first
# start. Polled rather than waited on with a compose healthcheck so the failure message can name the
# container whose log to read.
deadline=$((SECONDS + health_timeout))
until curl --fail --silent --max-time 5 "$base_url/api/v1/health" >/dev/null 2>&1; do
  if [ "$SECONDS" -ge "$deadline" ]; then
    compose logs --tail 80 flint-e2e-btcpay >&2 || true
    die "BTCPay Server did not become healthy in ${health_timeout}s (log above)."
  fi
  sleep 3
done

# Loading the plugin is a separate question from being healthy: a plugin that threw during Execute is
# disabled and BTCPay restarts *without* it, and every Flint route then 404s. Read it off the log
# rather than inferring it from a later failure.
#
# The log is captured to a variable before being searched, not piped into `grep -q`. Under
# `set -o pipefail` that pipeline fails even on a match: grep exits at the first hit, docker takes
# SIGPIPE, and the pipeline's status becomes the failure — which read as "the plugin did not load"
# against a server that had loaded it perfectly.
btcpay_log="$(compose logs flint-e2e-btcpay 2>/dev/null || true)"
if ! printf '%s' "$btcpay_log" | grep -F "Running plugin BTCPayServer.Plugins.Flint" >/dev/null; then
  compose logs --tail 120 flint-e2e-btcpay >&2 || true
  die "BTCPay Server started but never logged 'Running plugin BTCPayServer.Plugins.Flint'.
       The plugin did not load; the log above says why (look for 'Error when executing plugin')."
fi
say "plugin loaded: $(printf '%s' "$btcpay_log" | grep -F 'Running plugin BTCPayServer.Plugins.Flint' \
  | sed 's/.*Running plugin //' | tail -1)"

# ---------------------------------------------------------------------------------------------
# 4. Greenfield: an admin, an API key, a store, a Flint wallet.
# ---------------------------------------------------------------------------------------------
api() {
  # $1 method, $2 path, $3 optional JSON body. Auth is supplied by the caller through $AUTH_HEADER.
  local method="$1" path="$2" body="${3:-}"
  if [ -n "$body" ]; then
    curl --silent --show-error --max-time 120 -X "$method" "$base_url$path" \
      -H "$AUTH_HEADER" -H 'Content-Type: application/json' -d "$body"
  else
    curl --silent --show-error --max-time 120 -X "$method" "$base_url$path" -H "$AUTH_HEADER"
  fi
}

# POST /api/v1/users answers without authentication while the server has no administrator, and
# disables registrations as soon as the first one is created. So this is the one call with no auth,
# and a 403 on a re-run means the admin already exists — which is a success for our purposes.
say "creating the first administrator"
AUTH_HEADER="X-Flint-E2E: unauthenticated"
create_user="$(api POST /api/v1/users \
  "$(jq -nc --arg e "$admin_email" --arg p "$admin_password" \
      '{email: $e, password: $p, isAdministrator: true}')")"
if ! jq -e '.id' <<<"$create_user" >/dev/null 2>&1; then
  # Not fatal on its own: an existing admin is the expected answer on a second run against live
  # containers. Anything else is reported verbatim, because the body is BTCPay's own explanation.
  echo "    (user not created; BTCPay said: $(jq -c . <<<"$create_user" 2>/dev/null || echo "$create_user"))"
fi

# Basic auth is accepted wherever an API key is, throughout Greenfield — which is how the key itself
# is minted before any key exists.
AUTH_HEADER="Authorization: Basic $(printf '%s:%s' "$admin_email" "$admin_password" | base64 | tr -d '\n')"

say "creating an API key"
# The explicit permission set the tests actually use, rather than `unrestricted`: an over-scoped key
# would hide a missing permission on one of the plugin's own endpoints, and getting those right is
# part of what docs/greenfield-api.md promises.
#
# `btcpay.server.canmodifyserversettings` is on the list for one reason, and it is not the plugin's
# own endpoints. `seedSource: "generate"` creates a hot wallet, and SparkSeedResolver asks BTCPay's
# own CanUseHotWallet — which answers yes only when the server policy AllowHotWalletForAll is on, or
# when the *caller* holds the server-settings policy. An API key is judged on its own permissions and
# not on its owner's role, so a key belonging to an administrator that lacks this one is refused with
# `hot-wallet-not-allowed`. Found the hard way here; docs/greenfield-api.md now says so.
api_key="$(api POST /api/v1/api-keys '{
  "label": "flint-btcpay-e2e",
  "permissions": [
    "btcpay.store.canmodifystoresettings",
    "btcpay.store.canviewstoresettings",
    "btcpay.store.cancreateinvoice",
    "btcpay.store.canviewinvoices",
    "btcpay.store.cancreatelightninginvoice",
    "btcpay.store.canviewlightninginvoice",
    "btcpay.store.canuselightningnode",
    "btcpay.server.canmodifyserversettings"
  ]
}' | jq -er '.apiKey')" || die "could not create an API key; check the admin credentials."

AUTH_HEADER="Authorization: token $api_key"

# Reused if it already exists, so re-running this script against live containers does not leave a
# trail of stores each holding a wallet with money in it. A fresh `down.sh` + `up.sh` gets a fresh
# Postgres and therefore a fresh store either way.
store_id="$(api GET /api/v1/stores | jq -r '[.[] | select(.name == "Flint e2e")] | first | .id // empty')"
if [ -n "$store_id" ]; then
  say "reusing store $store_id"
else
  say "creating a store"
  store_id="$(api POST /api/v1/stores \
    '{"name": "Flint e2e", "defaultCurrency": "BTC"}' | jq -er '.id')" \
    || die "could not create a store."
  say "store $store_id"
fi

# The provisioning POST is the only call that ever hands out the recovery phrase, and it hands it out
# once. It is deliberately NOT captured here: this wallet holds regtest sats out of a fixture that
# down.sh destroys, and a seed written into a file next to a checkout is a habit worth not forming.
# `| jq '.status'` also keeps the phrase out of the shell's own output.
# Provisioning again would *replace* the store's seed, abandoning whatever the previous wallet held
# — so a store that already has a running wallet is left alone.
existing="$(api GET "/api/v1/stores/$store_id/spark" | jq -c '.')"
if [ "$(jq -r '.walletRunning // false' <<<"$existing")" = "true" ]; then
  say "the store already has a running Flint wallet"
  provision_status="$existing"
else
  say "provisioning the store's Flint wallet"
  provision_status="$(api POST "/api/v1/stores/$store_id/spark" '{"seedSource":"generate"}' \
    | jq -c '.status // .')"
fi
jq -e '.walletRunning == true' <<<"$provision_status" >/dev/null \
  || die "the wallet did not start. Status: $provision_status
       Read \`docker compose -f e2e/btcpay/docker-compose.yml logs flint-e2e-btcpay\`: a wallet that
       will not start against a custom network is almost always the descriptor (a TLS SAN, or an
       operator that died during keyshare generation)."
say "wallet running, Lightning wiring $(jq -r '.lightningWiring' <<<"$provision_status")"

# ---------------------------------------------------------------------------------------------
# 5. Fund it, through the plugin's own endpoints.
# ---------------------------------------------------------------------------------------------
# Deliberately the plugin's route and not the SSP's admin API: a deposit credited out of band would
# prove nothing about how the plugin configures the SDK, and the claim-fee ceiling is one of the
# things this is here to exercise.
deposit_address="$(api GET "/api/v1/stores/$store_id/spark/deposit" | jq -er '.address')" \
  || die "the store has no deposit address."
say "funding $deposit_address with $funding_sats sats"

# -named with an explicit fee_rate, as the fixture's own helpers do: on a chain whose estimator has
# no data an unqualified sendtoaddress fails with "Fee estimation failed" rather than picking a floor.
amount_btc="$(jq -nr --argjson s "$funding_sats" '$s / 100000000 | tostring')"
txid="$(bitcoin_cli -named sendtoaddress "address=$deposit_address" "amount=$amount_btc" fee_rate=100 | tr -d '\r\n')"
say "deposit $txid; mining 3 blocks to mature it"
mine 3

say "waiting for the deposit to be credited (up to ${funding_timeout}s)"
deadline=$((SECONDS + funding_timeout))
claimed=0
balance=0
while [ "$SECONDS" -lt "$deadline" ]; do
  status="$(api GET "/api/v1/stores/$store_id/spark" || true)"
  balance="$(jq -r '.balanceSats // 0' <<<"$status" 2>/dev/null || echo 0)"
  if [ "${balance:-0}" -gt 0 ]; then break; fi

  if [ "$claimed" = 0 ]; then
    deposits="$(api GET "/api/v1/stores/$store_id/spark/deposit" || true)"
    entry="$(jq -c --arg t "$txid" '.deposits // [] | map(select(.txId == $t)) | first // empty' \
      <<<"$deposits" 2>/dev/null || true)"
    if [ -n "$entry" ] && [ "$(jq -r '.isMature' <<<"$entry")" = "true" ]; then
      vout="$(jq -r '.vout' <<<"$entry")"
      # Omit maxFeeSats when Spark has already said what the claim needs — that is the documented
      # ordinary case and the value the plugin will use. Otherwise name a ceiling, because on this
      # chain the automatic claim is priced below what the SSP asks and would never fire.
      if [ "$(jq -r '.requiredFeeSats // "null"' <<<"$entry")" != "null" ]; then
        claim_body="$(jq -nc --arg t "$txid" --argjson v "$vout" '{txId: $t, vout: $v}')"
      else
        claim_body="$(jq -nc --arg t "$txid" --argjson v "$vout" --argjson f "$claim_fee_ceiling_sats" \
          '{txId: $t, vout: $v, maxFeeSats: $f}')"
      fi
      claim="$(api POST "/api/v1/stores/$store_id/spark/deposit/claim" "$claim_body" || true)"
      if [ "$(jq -r '.status // ""' <<<"$claim")" = "Claimed" ]; then
        claimed=1
        say "claimed the deposit for $(jq -r '.feeSats // "?"' <<<"$claim") sats of fee"
      else
        echo "    claim not accepted yet: $(jq -rc '.status, .message' <<<"$claim" | paste -sd' ' -)"
      fi
    fi
  fi

  # A block per pass. Electrs and the SDK's chain service both follow the tip, and nothing else
  # advances an idle regtest chain.
  mine 1
  sleep 5
done

[ "${balance:-0}" -gt 0 ] || die "the store's wallet was never funded: $funding_sats sats went to
       $deposit_address in $txid and were mined, but the balance is still 0 after
       ${funding_timeout}s. Check that the SSP still reports Spark liquidity, and read the BTCPay
       log for the plugin's own claim refusals."
say "wallet funded: $balance sats"

# ---------------------------------------------------------------------------------------------
# 6. Sweep settings the third test can actually use.
# ---------------------------------------------------------------------------------------------
# A static destination rather than DestinationMode StoreWallet: the store has no on-chain BTCPay
# wallet, and giving it one would mean deriving a scheme and waiting for NBXplorer to track it —
# neither of which the sweep is about. StaticAddress exists for exactly this shape of store.
#
# The numbers are set by this chain's fee market, not by taste. A cooperative exit here quotes near
# 20,000 sats against a ~150,000-sat balance, which is ~13% — over the plugin's 3% default and under
# its 50% hard backstop. So: drain the whole balance, and set a ceiling that admits that fee
# deliberately. `enabled: false` because every sweep in the tests is a manual trigger, and an
# automatic pass firing mid-test would race the assertions.
sweep_destination="$(bitcoin_cli getnewaddress flint-btcpay-e2e-sweep bech32 | tr -d '\r\n')"
say "sweep destination $sweep_destination"
api PUT "/api/v1/stores/$store_id/spark/sweep" "$(jq -nc --arg a "$sweep_destination" '{
  enabled: false,
  balanceThresholdSats: 10000,
  minimumSweepSats: 10000,
  maxFeePercent: 40.0,
  drainWhenSweeping: true,
  confirmationSpeed: "Medium",
  destinationMode: "StaticAddress",
  staticAddress: $a
}')" | jq -e '.settings.destinationMode == "StaticAddress"' >/dev/null \
  || die "the sweep configuration was refused."

# ---------------------------------------------------------------------------------------------
# 7. The handoff file.
# ---------------------------------------------------------------------------------------------
# Shape the BtcpayE2E fixture reads: baseUrl / apiKey / storeId plus the same `fixture` block network.json
# carries, so the test suite can reuse RegtestControl unchanged to drive bitcoind and lnd-1.
jq -n \
  --arg baseUrl "$base_url" \
  --arg apiKey "$api_key" \
  --arg storeId "$store_id" \
  --argjson fixture "$(jq '.fixture' "$descriptor")" \
  '{baseUrl: $baseUrl, apiKey: $apiKey, storeId: $storeId, fixture: $fixture}' > "$output"
chmod 600 "$output"

echo ""
echo "BTCPay e2e stack is up. Run the suite with:"
echo ""
echo "  export FLINT_BTCPAY_E2E=$output"
echo "  $dotnet_bin test BTCPayServer.Plugins.Flint.Tests/BTCPayServer.Plugins.Flint.Tests.csproj \\"
echo "      -c Release --filter \"Category=BtcpayE2E\" --output Detailed"
echo ""
echo "BTCPay UI: $base_url  ($admin_email / $admin_password)"
echo "Tear it down with e2e/btcpay/down.sh (the Spark stack is left alone)."
echo ""
