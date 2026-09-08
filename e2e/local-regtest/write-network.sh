#!/usr/bin/env bash
# Write the network descriptor the LocalRegtest suite and the plugin's custom-network loader read.
#
# Everything in the descriptor is discovered from the running stack rather than hardcoded here,
# because the two pieces that matter most cannot be known ahead of time: the SSP's identity public
# key and the operators' TLS certificates are generated fresh on every `start.sh`, which begins by
# destroying the volumes that held the previous ones. The parts that ARE fixed by the pin — operator
# identity keys, published host ports, the admin token — are read out of the pinned checkout's own
# files where possible, so a bump that changes them shows up here instead of silently disagreeing.
#
# Usage:  write-network.sh [--docker] [output-path]
#
#   --docker   emit IN-NETWORK addresses instead of published loopback ports, for a client that runs
#              inside the fixture's own docker network (`cashu_default`) rather than on the host. That
#              is the BTCPay Server container the BtcpayE2E suite drives: from inside the network,
#              `127.0.0.1:5000` is the BTCPay container itself and `localhost:8535` is nothing at all,
#              so the descriptor has to name the compose services — `spark-operator-N:8535`,
#              `spark-ssp:5000`, `spark-electrs:3002`. The default output path changes to
#              network.docker.json so a --docker run cannot quietly replace the host descriptor the
#              LocalRegtest suite is pointed at; the two are not interchangeable, and a wallet
#              connected through the wrong one fails as a TLS or timeout error naming neither.
#
# Environment:
#   CASHU_REGTEST_DIR      the fixture checkout (default: the one up.sh manages, alongside this script)
#   COMPOSE_PROJECT_NAME   defaults to `cashu`, which is what the fixture's docker-scripts.sh exports
#                          and therefore what named its containers and volumes
set -euo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
checkout="${CASHU_REGTEST_DIR:-$script_dir/cashu-regtest}"

in_docker=0
output=""
while [ $# -gt 0 ]; do
  case "$1" in
    --docker) in_docker=1 ;;
    -h|--help) sed -n '2,30p' "${BASH_SOURCE[0]}"; exit 0 ;;
    -*) echo "error: unknown option $1" >&2; exit 1 ;;
    *) output="$1" ;;
  esac
  shift
done

if [ -z "$output" ]; then
  if [ "$in_docker" = 1 ]; then output="$script_dir/network.docker.json"; else output="$script_dir/network.json"; fi
fi

# The two address sets. Everything else in the descriptor is identical between them: the operators'
# certificates, their identity keys, the SSP's identity and the whole fixture block are properties of
# the stack, not of where the client sits.
if [ "$in_docker" = 1 ]; then
  # `spark-operator-<i>` is both the compose service name and the SAN the certificate carries, which is
  # what makes this address verifiable at all; asserted per operator below rather than assumed.
  operator_host_for() { printf 'spark-operator-%s' "$1"; }
  operator_port_for() { printf '8535'; }
  operator_san_for()  { printf 'DNS:spark-operator-%s' "$1"; }
  ssp_base_url="http://spark-ssp:5000"
  esplora_url="http://spark-electrs:3002"
else
  operator_host_for() { printf 'localhost'; }
  # Every operator listens on 8535 inside the network; the compose file publishes them as 8535/8536/8537.
  operator_port_for() { printf '%s' "$((8535 + $1))"; }
  operator_san_for()  { printf 'DNS:localhost'; }
  ssp_base_url="http://127.0.0.1:5000"
  esplora_url="http://127.0.0.1:30000"
fi

# The fixture pins its compose project name in docker-scripts.sh (`export COMPOSE_PROJECT_NAME=cashu`)
# rather than letting Compose derive it from the directory name. That is what makes the containers
# `cashu-bitcoind-1`, `cashu-lnd-1-1`, `cashu-spark-ssp-1` and so on, and the volumes `cashu_*` —
# and it means resolving anything with `docker compose` from a differently-named directory finds
# nothing unless the same value is exported here.
export COMPOSE_PROJECT_NAME="${COMPOSE_PROJECT_NAME:-cashu}"
# The Spark services are all behind the `spark` profile; `docker compose ps -q` ignores services
# whose profile is not active, and would return empty for every one of them without this.
export COMPOSE_PROFILES="${COMPOSE_PROFILES:-spark}"

for tool in docker jq curl openssl; do
  command -v "$tool" >/dev/null || { echo "error: $tool is required" >&2; exit 1; }
done
[ -d "$checkout" ] || { echo "error: no fixture checkout at $checkout; run up.sh first" >&2; exit 1; }
cd "$checkout"

# Resolve a service's container name through Compose rather than assuming the `cashu-<svc>-1`
# pattern. The pattern is what the pin produces today, but the descriptor is consumed by
# `docker exec` calls in the test fixture, and a wrong name there fails as an unhelpful "No such
# container" in the middle of a payment test.
#
# `ps -aq`, not `ps -q`: a name is a name whether or not the container is currently running, and
# resolving it is separate from asserting it is up (which the second argument does). Getting an
# exited container's name into the descriptor is useful — `docker logs` on it is the first thing
# anyone reads.
#
#   container_name <service> required   — absent or exited is a hard failure
#   container_name <service> optional   — absent or exited warns and yields the empty string
container_name() {
  local service="$1" requirement="$2" id state
  id="$(docker compose ps -aq "$service" || true)"
  if [ -n "$id" ]; then
    state="$(docker inspect -f '{{.State.Status}}' "$id")"
    if [ "$state" = "running" ]; then
      docker inspect -f '{{.Name}}' "$id" | sed 's|^/||'
      return 0
    fi
  fi

  local detail="is not running (state: ${state:-no such container})"
  if [ "$requirement" = optional ]; then
    echo "warning: service '$service' $detail; recording it as empty in the descriptor." >&2
    if [ -n "$id" ]; then docker inspect -f '{{.Name}}' "$id" | sed 's|^/||'; fi
    return 0
  fi
  echo "error: service '$service' $detail (COMPOSE_PROJECT_NAME=$COMPOSE_PROJECT_NAME," >&2
  echo "       COMPOSE_PROFILES=$COMPOSE_PROFILES). Run up.sh first, and read its output." >&2
  return 1
}

echo "==> resolving containers"
bitcoind_container="$(container_name bitcoind required)"
lnd_container="$(container_name lnd-1 required)"
ssp_container="$(container_name spark-ssp required)"
for id in 0 1 2; do
  # Not recorded in the descriptor — the operators are reached over their published ports, not
  # `docker exec` — but asserted, because an operator that fell over during keyshare generation is
  # the single most common way this stack comes up half-working, and the resulting SDK failure says
  # only that a signing round timed out.
  container_name "spark-operator-$id" required >/dev/null
done
# Optional, and deliberately so: no LocalRegtest test drives Core Lightning — `lnd-1` is the
# counterparty on both sides of the Lightning tests — so a CLN node that is down should cost a warning,
# not the descriptor. (On macOS the CLN nodes only run at all because up.sh moves their SQLite onto named
# volumes; see the README.)
cln_container="$(container_name clightning-1 optional)"

echo "==> reading the SSP identity from http://127.0.0.1:5000/identity"
# Generated per run from a mnemonic the SSP writes into its own volume, so there is nothing to pin.
# The fixture's wait-for-spark-ssp already blocked until this endpoint agrees with the SSP's
# internal /status view, so by the time up.sh returned this is the settled value.
ssp_identity="$(curl --fail --silent --max-time 15 http://127.0.0.1:5000/identity | jq -er '.identityPublicKey')"
if ! printf '%s' "$ssp_identity" | grep -Eq '^0[23][0-9a-f]{64}$'; then
  echo "error: /identity returned '$ssp_identity', which is not a 33-byte compressed pubkey" >&2
  exit 1
fi
echo "    SSP identity $ssp_identity"

echo "==> reading operator certificates out of the ${COMPOSE_PROJECT_NAME}_spark-tls-certs volume"
# spark-cert-init generates these on first start into a named volume that `start.sh --spark` wipes
# every run, so they must be read live. Preferred route is `docker exec` into the operator that
# actually mounts the volume: it needs no extra image and it proves the operator can see the same
# bytes the client will pin. The `docker run` fallback covers an operator that has exited (a
# post-mortem descriptor is still useful) by mounting the volume directly.
read_operator_cert() {
  local id="$1" cid
  cid="$(docker compose ps -q "spark-operator-$id" || true)"
  if [ -n "$cid" ] && docker exec "$cid" cat "/opt/spark/tls/server_$id.crt" 2>/dev/null; then
    return 0
  fi
  docker run --rm -v "${COMPOSE_PROJECT_NAME}_spark-tls-certs:/tls:ro" alpine:3.20 \
    cat "/tls/server_$id.crt"
}

operators_json="$(jq -n '[]')"
for id in 0 1 2; do
  # Command substitution strips trailing newlines; PEM readers want the last line terminated, and
  # the Rust reference client hands the SDK the file's bytes verbatim, so restore it.
  cert="$(read_operator_cert "$id")"$'\n'
  printf '%s' "$cert" | grep -q 'BEGIN CERTIFICATE' \
    || { echo "error: server_$id.crt does not look like a PEM certificate" >&2; exit 1; }

  # Whichever hostname the client will use has to be a name the certificate covers, or the SDK's TLS
  # handshake fails on hostname verification — and the certificate is the only thing pinned, since
  # there is no CA to fall back on. spark-cert-init issues each cert with
  # `subjectAltName = DNS:spark-operator-<i>,DNS:spark-operator-<i>.minikube.local,DNS:localhost`, so
  # both address sets are covered; this asserts the one actually being written rather than assuming,
  # because that SAN list is a line in the fixture's compose file and a bump could drop either entry.
  required_san="$(operator_san_for "$id")"
  if ! printf '%s' "$cert" | openssl x509 -noout -ext subjectAltName 2>/dev/null | grep -q "$required_san"; then
    echo "error: server_$id.crt has no $required_san SAN, so this client cannot verify it." >&2
    echo "       Check spark-cert-init's -addext subjectAltName in the fixture's docker-compose.yml." >&2
    exit 1
  fi

  # Operator identity keys are fixed by the pin — they are the public halves of the committed
  # keyshares in spark/keys — so they come from the pinned checkout's own operators.json rather
  # than from a constant duplicated here.
  identity_public_key="$(jq -er --argjson id "$id" '.[] | select(.id == $id) | .identity_public_key' spark/operators.json)"

  # Identifiers are the FROST participant identifiers: 64 hex digits of (id + 1). The addresses are
  # the loopback ports the compose file publishes — every operator listens on 8535 inside the
  # network, mapped to 8535/8536/8537 on the host.
  operators_json="$(jq \
    --argjson id "$id" \
    --arg identifier "$(printf '%064x' $((id + 1)))" \
    --arg address "https://$(operator_host_for "$id"):$(operator_port_for "$id")" \
    --arg identityPublicKey "$identity_public_key" \
    --arg caCertPem "$cert" \
    '. + [{id: $id, identifier: $identifier, address: $address,
           identityPublicKey: $identityPublicKey, caCertPem: $caCertPem}]' \
    <<<"$operators_json")"
done

echo "==> writing $output"
mkdir -p "$(dirname -- "$output")"
# jq builds the document so the PEM blocks are escaped correctly; hand-assembled JSON with embedded
# newlines is the classic way this file ends up unparseable.
jq -n \
  --argjson operators "$operators_json" \
  --arg sspBaseUrl "$ssp_base_url" \
  --arg esploraUrl "$esplora_url" \
  --arg sspIdentityPublicKey "$ssp_identity" \
  --arg bitcoindContainer "$bitcoind_container" \
  --arg lndContainer "$lnd_container" \
  --arg clnContainer "$cln_container" \
  --arg sspContainer "$ssp_container" \
  '{
    coordinatorIdentifier: "0000000000000000000000000000000000000000000000000000000000000001",
    threshold: 2,
    operators: $operators,
    ssp: {
      baseUrl: $sspBaseUrl,
      identityPublicKey: $sspIdentityPublicKey,
      schemaEndpoint: "graphql/spark/rc"
    },
    esploraUrl: $esploraUrl,
    fixture: {
      bitcoindContainer: $bitcoindContainer,
      bitcoindRpcUser: "cashu",
      bitcoindRpcPassword: "cashu",
      lndContainer: $lndContainer,
      clnContainer: $clnContainer,
      sspAdminToken: "regtest-spark-admin-token",
      sspContainer: $sspContainer
    }
  }' > "$output"

output_abs="$(cd -- "$(dirname -- "$output")" && pwd)/$(basename -- "$output")"
echo ""
if [ "$in_docker" = 1 ]; then
  echo "In-network descriptor written ($output_abs)."
  echo "It is only usable from inside the ${COMPOSE_PROJECT_NAME}_default docker network; e2e/btcpay/up.sh"
  echo "mounts it into the BTCPay container and sets SPARK_LOCAL_REGTEST_NETWORK to the mounted path."
else
  echo "Descriptor written. Point the suite at it with:"
  echo ""
  echo "  export SPARK_LOCAL_REGTEST_NETWORK=$output_abs"
fi
echo ""
