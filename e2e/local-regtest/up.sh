#!/usr/bin/env bash
# Bring up the local Spark stack the LocalRegtest suite runs against.
#
# The stack is callebtc/cashu-regtest with its `--spark` profile: Bitcoin Core in regtest, three
# Spark operators (2-of-3 FROST), an `open-ssp` service provider backed by an LDK node, Electrs
# serving an Esplora API, and three LND plus three CLN nodes wired into a real channel topology.
# Those Lightning nodes are the point: they are external counterparties the plugin's SSP has to
# actually route to and from, which is the one thing Lightspark's hosted regtest cannot give us
# (see docs/testing.md).
#
# Idempotent by design: run it twice and the second run reuses the existing checkout. It is NOT
# incremental past that — `start.sh` itself begins with `docker compose down --volumes`, so every
# run resets the chain, the operators' databases and every node identity. That is the fixture's
# model, not ours; treat the stack as disposable.
#
# Environment:
#   CASHU_REGTEST_DIR  where the fixture checkout lives. Defaults to a clone this script manages.
#                      CI sets it to the path actions/checkout already wrote, so the workflow does
#                      not clone twice.
#   FLINT_REGTEST_IMAGE_PREFIX
#                      registry path the prebuilt fixture images live under, published by
#                      .github/workflows/local-regtest-images.yml. Defaults to this repository's
#                      GHCR namespace. Set it to the empty string to never pull and always build.
#   FLINT_REGTEST_FULL if 1, run the fixture's own ./start.sh --spark instead of the leaner path
#                      below — that additionally runs the fixture's acceptance suites, which rebuild
#                      its Rust Breez test client and mine dozens of extra blocks. Useful when you
#                      suspect the fixture itself; not what CI needs.
set -euo pipefail

# Pinned so the descriptor write-network.sh emits, the operator keys it reads out of
# spark/operators.json, and the published host ports all stay in agreement. The fixture is a
# regtest playground and reorganises its compose file freely; an unpinned clone would move the
# ports or the keys out from under the tests with no signal. To bump it, see
# e2e/local-regtest/README.md — the bump is a reviewed change, not a `git pull`.
CASHU_REGTEST_SHA="23d5c160e0b5fade7e4a245b7385f9116bf2fc5c"
CASHU_REGTEST_REPO="https://github.com/callebtc/cashu-regtest.git"

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
checkout="${CASHU_REGTEST_DIR:-$script_dir/cashu-regtest}"
log="$script_dir/up.log"

if [ ! -d "$checkout" ]; then
  echo "==> cloning $CASHU_REGTEST_REPO into $checkout"
  # A full clone, deliberately: the pinned SHA is not a tag and not necessarily reachable from the
  # default branch's tip in a shallow fetch, and the repository is small.
  git clone --quiet "$CASHU_REGTEST_REPO" "$checkout"
  git -C "$checkout" checkout --quiet "$CASHU_REGTEST_SHA"
fi

# Verify rather than trust, on every run and not just after a fresh clone: the common failure is a
# checkout somebody updated by hand, or a CASHU_REGTEST_DIR pointing at their own working copy — and
# CI sets that variable to a path actions/checkout wrote from its own `ref`, so this is also what
# catches that ref drifting from the constant above. A stack built from a different revision fails
# later, deep inside a test, with an error that says nothing about the cause. Anything that does not
# resolve to a 40-hex commit is reported as "not a git checkout" rather than passed through: a plain
# directory or an extracted tarball makes `rev-parse` fail, and an empty repository makes it
# succeed-ish by echoing back "HEAD".
head_sha="$(git -C "$checkout" rev-parse HEAD 2>/dev/null || true)"
if ! printf '%s' "$head_sha" | grep -Eq '^[0-9a-f]{40}$'; then
  head_sha="not a git checkout"
fi
if [ "$head_sha" != "$CASHU_REGTEST_SHA" ]; then
  echo "error: $checkout is at $head_sha, expected the pinned $CASHU_REGTEST_SHA" >&2
  echo "       Check it out at the pin, or delete the directory and let this script clone it." >&2
  exit 1
fi

# What the fixture's own CI does before `start.sh` (.github/workflows/ci.yml, spark-regtest job),
# and it is load-bearing rather than cargo cult: the compose file bind-mounts ./data and ./spark
# into containers that run as assorted non-root uids — lightningd, lnd, the operators — and those
# containers write into the mounts. Without world-writable modes the node data directories fail to
# initialise and the stack dies during startup with permission errors that look like node bugs.
# It is safe here because everything under the checkout is disposable regtest fixture data.
# Docker Desktop for macOS cannot host lightningd's SQLite database on a bind mount: every Core Lightning
# node dies seconds after start with "attempt to write a readonly database", start.sh times out in
# wait-for-clightning-sync, and the Spark init that funds the SSP never runs. The fixture's own
# clightning-4 — on a named volume — starts fine on the same host, so the fix is to give the three core
# CLN nodes named volumes too. Compose merges `volumes` by container target path, so an override file
# replaces the bind mounts without editing the pinned compose file. Linux keeps the fixture's own
# layout; the override is written only where it is needed, and only if the checkout has none of its own.
if [ "$(uname -s)" = "Darwin" ] && [ ! -f "$checkout/docker-compose.override.yml" ]; then
  echo "==> macOS: writing docker-compose.override.yml (named volumes for the CLN nodes' SQLite)"
  cat > "$checkout/docker-compose.override.yml" <<'OVERRIDE'
# Written by Flint's e2e/local-regtest/up.sh on macOS. See that script for why.
services:
  clightning-1:
    volumes:
      - cln1-data:/root/.lightning/
  clightning-2:
    volumes:
      - cln2-data:/root/.lightning/
  clightning-2-rest:
    volumes:
      - cln2-data:/root/.lightning/
  clightning-3:
    volumes:
      - cln3-data:/root/.lightning/
volumes:
  cln1-data:
  cln2-data:
  cln3-data:
OVERRIDE
fi

echo "==> chmod -R 777 $checkout (the fixture's containers write into its bind mounts as several uids)"
chmod -R 777 "$checkout"

# The Spark services are all behind a compose profile and the Spark half of the fixture's init is
# gated on CASHU_SPARK_REGTEST. start.sh sets both from its --spark flag; the leaner path below calls
# the fixture's functions directly, so it has to set them itself. Exported before anything reads the
# compose file so `docker compose config`, `pull`, `ps` and `logs` all see the same service set.
export COMPOSE_PROFILES="spark"
export CASHU_SPARK_REGTEST="true"
export CASHU_BARK_REGTEST="false"

# --------------------------------------------------------------------------------------------------
# Prebuilt images.
#
# The fixture publishes none, so compose would build the Spark operators (Rust), open-ssp (Go),
# Electrs (Rust) and ldk-server (Rust) from source — tens of minutes locally and about forty on a
# cache-less CI runner, which is the single reason the local-regtest job was too expensive to gate a
# release on. .github/workflows/local-regtest-images.yml builds that set once per pinned fixture SHA
# and pushes it to GHCR; this pulls it and retags each image to the exact local name the pinned
# compose file names, at which point compose finds the image already present and skips the build.
#
# Both sides read the names out of `docker compose config` rather than hardcoding them, because a
# hardcoded list that drifted from the compose file would not fail — compose would just quietly build
# again, and the only symptom would be a "fast" path that takes forty minutes.
#
# Every failure here is a warning, never fatal: a fork whose packages were never published, a network
# blip, or an arm64 host all fall through to building from source, which still works.
image_prefix="${FLINT_REGTEST_IMAGE_PREFIX-ghcr.io/sethforprivacy/flint-regtest}"
# Only amd64. The published images are linux/amd64 only (see that workflow's header for why: an arm64
# variant means cross-building several large Rust projects under QEMU). Pulling them onto an Apple
# silicon host would succeed and then run the whole stack emulated, which is far worse than building
# native images once and keeping the layers — so on any other architecture, don't pull at all.
docker_arch="$(docker version --format '{{.Server.Arch}}' 2>/dev/null || true)"
if [ -z "$image_prefix" ]; then
  echo "==> FLINT_REGTEST_IMAGE_PREFIX is empty: not pulling; compose will build anything missing."
elif [ "$docker_arch" != "amd64" ]; then
  echo "==> docker server arch is '${docker_arch:-unknown}', not amd64: not pulling."
  echo "    The published fixture images are linux/amd64 only; compose will build native ones."
else
  echo "==> checking for prebuilt fixture images under $image_prefix"
  # Same derivation as the images workflow: services carrying a `build:` section, deduplicated by
  # image, since ldk/ldk-fee share one image and the three operators share another.
  wanted="$(cd "$checkout" && docker compose config --format json \
    | jq -r '[ .services | to_entries[] | select(.value.build != null) ]
             | group_by(.value.image) | map(.[0].value.image) | .[]')"
  if [ -z "$wanted" ]; then
    echo "    warning: found no buildable services in the pinned compose file; skipping the pull." >&2
  fi
  while IFS= read -r local_image; do
    [ -n "$local_image" ] || continue
    # A locally built image wins. On a repeat run this is the common case and skipping the pull saves
    # a registry round trip; on an arm64 host it is also what protects a native image from being
    # replaced by an emulated one if this block is ever reached with the arch check relaxed.
    if docker image inspect "$local_image" >/dev/null 2>&1; then
      echo "    $local_image is already present locally"
      continue
    fi
    remote="$image_prefix/${local_image%%:*}:$CASHU_REGTEST_SHA"
    echo "    pulling $remote"
    if docker pull --quiet --platform linux/amd64 "$remote" >/dev/null 2>&1 \
      && docker tag "$remote" "$local_image"; then
      echo "      -> tagged as $local_image"
    else
      echo "      warning: could not pull $remote; compose will build $local_image from source." >&2
      echo "               (Expected in a fork whose packages are private or unpublished.)" >&2
    fi
  done <<< "$wanted"
fi
# --------------------------------------------------------------------------------------------------

# The fixture's own start.sh is `cashu-regtest-start` plus its acceptance suites: seven Lightning
# nodes' channel and UTXO counts, an LNbits probe, `cashu-spark-e2e` (which rebuilds the fixture's
# Rust Breez SDK test client with --build — another long compile on a cold runner — and settles four
# payments through it), and the ldk/ and fees/ e2e scripts. All of that is the *fixture* proving
# itself, and it mines dozens of extra blocks doing so. We want the stack, not the audit, so the
# default path runs the fixture's init chain itself — stop, `compose up -d`, then every init step
# except the fee-hub one (see flint_start_stack for why), including `cashu-spark-init`, the step that
# funds the SSP's Spark liquidity — and then asserts the three properties the LocalRegtest suite actually depends on. Set FLINT_REGTEST_FULL=1 to run the
# fixture's full path instead when you suspect the fixture rather than the plugin.
flint_start_stack() {
  # No `set -e` in here, on purpose: start.sh does not run under it either, and the fixture's
  # functions are written to return non-zero rather than to be safe under errexit — several of their
  # steps (a `docker run` that deletes node data, a `psql` poll) fail benignly. Every call below is
  # therefore checked explicitly.
  set +e
  # docker-scripts.sh sources ./bark/scripts.sh, ./ldk/scripts.sh and ./fees/scripts.sh by relative
  # path, and is also what exports COMPOSE_PROJECT_NAME=cashu — the name every container and volume
  # in the stack is created under. Sourcing it from anywhere but the checkout root silently does
  # nothing useful.
  cd "$checkout" || return 1
  # shellcheck disable=SC1091  # a file in the pinned foreign checkout, not in this repository
  . ./docker-scripts.sh

  if [ "${FLINT_REGTEST_FULL:-0}" = "1" ]; then
    echo "==> FLINT_REGTEST_FULL=1: running the fixture's own ./start.sh --spark"
    ./start.sh --spark || return 1
    return 0
  fi

  # The fixture's `cashu-regtest-start` is stop + `compose up -d` + `cashu-regtest-init`, and that init
  # chain is reproduced here step by step so that ONE of its steps can be left out: `cashu-fees-init`,
  # which funds a fee-charging hub and three leaf nodes and then waits for their channel *policies* to
  # appear in three separate routing graphs. That gossip wait is the flakiest thing in the fixture — its
  # own CI timed out in it ("Timed out: fee-policies-ready") in two of the three runs preceding this
  # pin, and so did this workflow's first run on ubuntu-latest — and nothing this plugin tests routes
  # through the hub: lnd-1 and the SSP's LDK node share a direct channel. The fee containers still
  # start (they are in the default profile), they are simply never waited on or funded. Everything the
  # suites do depend on — bitcoind funds, the seven-node topology, the six LDK channels, and
  # `cashu-spark-init` funding the SSP — runs exactly as the fixture's own init runs it.
  # Two attempts, each from a clean stop. The fixture's init is a chain of bounded waits on things that
  # are asynchronous by nature — LDK's BDK wallet noticing its own funding spend, seven nodes reaching one
  # exact height, channel gossip — and any one of them can time out on a slow or busy host with nothing
  # actually wrong ("LDK wallet funding sync timed out" was seen once in five local starts). A second
  # attempt from `compose down --volumes` costs a few minutes with cached images; a red release gate on a
  # fixture wait costs a human an afternoon. Two rather than more, so a genuinely broken stack still
  # fails inside the job's timeout with the fixture's own message intact.
  local attempt
  for attempt in 1 2; do
    if flint_init_chain; then
      echo "==> asserting the stack is ready for the LocalRegtest suite"
      flint_assert_ready || return 1
      return 0
    fi
    if [ "$attempt" = 1 ]; then
      echo "==> the fixture's init chain failed; retrying once from a clean stop (its waits are known to flake)" >&2
    fi
  done
  return 1
}

flint_init_chain() {
  echo "==> compose down --volumes, up -d, then the fixture's init chain minus the fee-hub topology"
  cashu-regtest-stop || return 1
  docker compose up -d --remove-orphans || return 1
  cashu-bitcoin-init || return 1
  cashu-lightning-sync || return 1
  cashu-lightning-init || return 1
  cashu-ldk-init || return 1
  cashu-spark-init || return 1
}

# The three properties the suite depends on, each polled with a bounded wait. The init chain already
# asserts stronger versions of all three; re-checking here is cheap and turns "the stack came up but
# the SSP has no liquidity" into a failure of up.sh with a legible message, rather than a test
# failing on a Spark error deep inside the SDK.
flint_assert_ready() {
  local attempt status ldk_mode available channels

  # 1. open-ssp is live and actually holds Spark liquidity. `ldk_mode: live` means its LDK node is
  #    reachable, and available_sats > 0 means cashu-spark-fund-ssp's deposit claim landed — without
  #    it the SSP cannot pay or be paid and every Lightning test fails on an opaque service error.
  #    That liquidity is one 500,000-sat leaf and is the suite's whole budget.
  for attempt in $(seq 1 60); do
    status="$(curl --fail --silent --max-time 15 \
      -H "Authorization: Bearer $SPARK_ADMIN_TOKEN" http://127.0.0.1:5000/status 2>/dev/null)"
    ldk_mode="$(printf '%s' "$status" | jq -r '.ldk_mode // empty' 2>/dev/null)"
    available="$(printf '%s' "$status" | jq -r '.spark.available_sats // 0' 2>/dev/null)"
    case "$available" in ''|*[!0-9]*) available=0 ;; esac
    if [ "$ldk_mode" = "live" ] && [ "$available" -gt 0 ]; then
      echo "    open-ssp: ldk_mode=live, ${available} sats of Spark liquidity"
      break
    fi
    [ "$attempt" -eq 60 ] && {
      echo "error: open-ssp never reported a live LDK mode with liquidity" >&2
      echo "       (last ldk_mode='${ldk_mode:-none}', available_sats=$available)" >&2
      return 1
    }
    sleep 5
  done

  # 2. The SSP's LDK node has all six of its channels ready. Anything less and a payment to or from
  #    one of the fixture's LND/CLN nodes may have no route — which surfaces as a payment timeout,
  #    not as a topology error.
  for attempt in $(seq 1 60); do
    channels="$(ldk-cli-sim list-channels 2>/dev/null \
      | jq -r '[.channels[]? | select(.is_channel_ready == true)] | length' 2>/dev/null)"
    if [ "$channels" = "6" ]; then
      echo "    LDK: 6 ready channels"
      break
    fi
    [ "$attempt" -eq 60 ] && {
      echo "error: LDK reported ${channels:-no} ready channels, expected 6" >&2
      return 1
    }
    sleep 5
  done

  # 3. Every Lightning node sits at bitcoind's tip. wait-for-ldk-height is the fixture's own bounded
  #    check and covers the LDK node plus all three LND and all three CLN nodes at once, which is
  #    strictly stronger than the `lnd-1 synced_to_chain` the suite needs — and it is the property
  #    that breaks if anything mines a block while the stack is still initialising.
  wait-for-ldk-height || {
    echo "error: the Lightning nodes never converged on bitcoind's block height" >&2
    return 1
  }
  echo "    all seven Lightning nodes are at bitcoind's tip"
}

echo "==> starting the stack (a first run with no prebuilt images compiles from source: ~40 min)"
echo "    full output is also being written to $log"
# `set -e` is suspended around the pipeline so the tail-of-log diagnostic below actually runs; the
# exit status comes from PIPESTATUS[0] rather than the pipeline, so `tee` cannot mask a failure.
set +e
( flint_start_stack ) 2>&1 | tee "$log"
start_status="${PIPESTATUS[0]}"
set -e

if [ "$start_status" -ne 0 ]; then
  echo "" >&2
  echo "error: bringing the fixture's Spark stack up failed (exit $start_status)." >&2
  echo "       Last 80 lines follow; see also \`docker compose ps -a\` in $checkout." >&2
  echo "---------------------------------------------------------------------------" >&2
  tail -n 80 "$log" >&2
  echo "---------------------------------------------------------------------------" >&2
  exit "$start_status"
fi

echo ""
echo "==> stack is up and ready."
echo "    Next: $script_dir/write-network.sh"
