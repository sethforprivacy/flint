#!/usr/bin/env bash
# Provisions one or more BTCPay stores with Flint and configures sweep.
# Usage: ./setup-stores.sh <btcpay-url> <store-id> [<store-id>...]
#
# Environment variables (required):
#   BTCPAY_API_KEY   - API key with btcpay.store.canmodifystoresettings on every listed store
#
# Environment variables (optional):
#   SWEEP_ENABLED              - "true" (default) or "false"
#   SWEEP_THRESHOLD_SATS       - balance threshold (default: 200000)
#   SWEEP_MIN_SATS             - minimum sweep (default: 100000)
#   SWEEP_MAX_FEE_PERCENT      - fee ceiling, percent (default: 3.0)
#   SWEEP_DESTINATION_MODE     - StoreWallet (default) | StaticAddress
#   SWEEP_STATIC_ADDRESS       - required when SWEEP_DESTINATION_MODE=StaticAddress
#   SWEEP_DRAIN                - "true" (default) or "false"
#   SWEEP_SPEED                - Slow | Medium (default) | Fast
#   SEED_GPG_RECIPIENT         - if set, seeds are encrypted to this GPG key; plain-text otherwise
#   SEED_OUTPUT_DIR            - directory for seed files (default: ./seeds)
#
# The generated recovery phrase is printed exactly once by the API and never again.
# Keep the output files safe.

set -euo pipefail

# ── argument validation ───────────────────────────────────────────────────────

if [[ $# -lt 2 ]]; then
    echo "Usage: $0 <btcpay-url> <store-id> [<store-id>...]" >&2
    exit 1
fi

if [[ -z "${BTCPAY_API_KEY:-}" ]]; then
    echo "Error: BTCPAY_API_KEY is not set." >&2
    exit 1
fi

BTCPAY="${1%/}"
shift
STORES=("$@")

# ── defaults ─────────────────────────────────────────────────────────────────

SWEEP_ENABLED="${SWEEP_ENABLED:-true}"
SWEEP_THRESHOLD_SATS="${SWEEP_THRESHOLD_SATS:-200000}"
SWEEP_MIN_SATS="${SWEEP_MIN_SATS:-100000}"
SWEEP_MAX_FEE_PERCENT="${SWEEP_MAX_FEE_PERCENT:-3.0}"
SWEEP_DESTINATION_MODE="${SWEEP_DESTINATION_MODE:-StoreWallet}"
SWEEP_STATIC_ADDRESS="${SWEEP_STATIC_ADDRESS:-}"
SWEEP_DRAIN="${SWEEP_DRAIN:-true}"
SWEEP_SPEED="${SWEEP_SPEED:-Medium}"
SEED_GPG_RECIPIENT="${SEED_GPG_RECIPIENT:-}"
SEED_OUTPUT_DIR="${SEED_OUTPUT_DIR:-./seeds}"

AUTH="Authorization: token ${BTCPAY_API_KEY}"

mkdir -p "${SEED_OUTPUT_DIR}"
chmod 700 "${SEED_OUTPUT_DIR}"

# ── helpers ───────────────────────────────────────────────────────────────────

provision_store() {
    local store_id="$1"
    echo ""
    echo "=== Provisioning store: ${store_id} ==="

    # Provision with a freshly generated seed. The mnemonic in this response is the only
    # copy the server will ever hand out — capture it before doing anything else.
    local provision_json
    provision_json=$(curl -sS -f \
        -X POST "${BTCPAY}/api/v1/stores/${store_id}/spark" \
        -H "${AUTH}" -H 'Content-Type: application/json' \
        -d '{"seedSource":"generate"}') || {
        echo "  ERROR: provision request failed for store ${store_id}" >&2
        return 1
    }

    local mnemonic
    mnemonic=$(echo "${provision_json}" | jq -r '.mnemonic // empty')

    if [[ -z "${mnemonic}" ]]; then
        echo "  ERROR: no mnemonic in provision response. Store may already be configured." >&2
        echo "  Response: ${provision_json}" >&2
        return 1
    fi

    # Save the seed — this is the only chance.
    local seed_file="${SEED_OUTPUT_DIR}/spark-${store_id}.seed"
    if [[ -n "${SEED_GPG_RECIPIENT}" ]]; then
        echo "${mnemonic}" | gpg --batch --yes -e -r "${SEED_GPG_RECIPIENT}" -o "${seed_file}.gpg"
        chmod 600 "${seed_file}.gpg"
        echo "  Seed saved (GPG-encrypted): ${seed_file}.gpg"
    else
        printf '%s\n' "${mnemonic}" > "${seed_file}"
        chmod 600 "${seed_file}"
        echo "  Seed saved (plain text, keep safe): ${seed_file}"
    fi

    echo "  Wallet running: $(echo "${provision_json}" | jq -r '.status.walletRunning')"
    echo "  Lightning wiring: $(echo "${provision_json}" | jq -r '.status.lightningWiring')"
    echo "  Identity pubkey: $(echo "${provision_json}" | jq -r '.status.identityPubkey')"
}

configure_sweep() {
    local store_id="$1"
    echo "  Configuring sweep for store: ${store_id}"

    local body
    body=$(jq -n \
        --argjson enabled "${SWEEP_ENABLED}" \
        --argjson threshold "${SWEEP_THRESHOLD_SATS}" \
        --argjson min "${SWEEP_MIN_SATS}" \
        --argjson maxfee "${SWEEP_MAX_FEE_PERCENT}" \
        --argjson drain "${SWEEP_DRAIN}" \
        --arg speed "${SWEEP_SPEED}" \
        --arg mode "${SWEEP_DESTINATION_MODE}" \
        --arg addr "${SWEEP_STATIC_ADDRESS}" \
        '{
            enabled: $enabled,
            balanceThresholdSats: $threshold,
            minimumSweepSats: $min,
            maxFeePercent: $maxfee,
            drainWhenSweeping: $drain,
            confirmationSpeed: $speed,
            destinationMode: $mode
        } + (if $addr != "" then {staticAddress: $addr} else {} end)')

    local result
    result=$(curl -sS -f \
        -X PUT "${BTCPAY}/api/v1/stores/${store_id}/spark/sweep" \
        -H "${AUTH}" -H 'Content-Type: application/json' \
        -d "${body}") || {
        echo "  WARNING: sweep configuration failed for store ${store_id}" >&2
        return 1
    }

    echo "  Sweep enabled: $(echo "${result}" | jq -r '.settings.enabled')"
    echo "  Threshold: $(echo "${result}" | jq -r '.settings.balanceThresholdSats') sats"
    echo "  Destination: $(echo "${result}" | jq -r '.settings.destinationMode')"
    echo "  Warnings:"
    echo "${result}" | jq -r '.warnings[]? | "    - \(.)"' || true
}

read_status() {
    local store_id="$1"
    echo "  Verifying status for store: ${store_id}"

    local status
    status=$(curl -sS -f \
        "${BTCPAY}/api/v1/stores/${store_id}/spark" \
        -H "${AUTH}") || {
        echo "  WARNING: status read failed for store ${store_id}" >&2
        return 1
    }

    echo "  Configured: $(echo "${status}" | jq -r '.configured')"
    echo "  Wallet running: $(echo "${status}" | jq -r '.walletRunning')"
    echo "  Network operational: $(echo "${status}" | jq -r '.networkStatus.isOperational')"
}

# ── main loop ─────────────────────────────────────────────────────────────────

FAILED=()

for store_id in "${STORES[@]}"; do
    if provision_store "${store_id}"; then
        configure_sweep "${store_id}" || true
        read_status "${store_id}" || true
    else
        FAILED+=("${store_id}")
    fi
done

echo ""
if [[ ${#FAILED[@]} -gt 0 ]]; then
    echo "FAILED stores: ${FAILED[*]}" >&2
    exit 1
else
    echo "All stores provisioned successfully."
fi
