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

echo "==> ./start.sh --spark  (first run builds the Rust and Go images from source: tens of minutes)"
echo "    full output is also being written to $log"
# `start.sh` sources docker-scripts.sh with relative paths, so it only works from its own directory.
# `set -e` is suspended around the pipeline so the tail-of-log diagnostic below actually runs; the
# exit status comes from PIPESTATUS[0] rather than the pipeline, so `tee` cannot mask a failure.
set +e
( cd "$checkout" && ./start.sh --spark ) 2>&1 | tee "$log"
start_status="${PIPESTATUS[0]}"
set -e

if [ "$start_status" -ne 0 ]; then
  echo "" >&2
  echo "error: the fixture's ./start.sh --spark failed (exit $start_status)." >&2
  echo "       Last 80 lines; start.sh's own EXIT trap has already dumped container logs above." >&2
  echo "---------------------------------------------------------------------------" >&2
  tail -n 80 "$log" >&2
  echo "---------------------------------------------------------------------------" >&2
  exit "$start_status"
fi

echo ""
echo "==> stack is up and the fixture's own acceptance tests passed."
echo "    Next: $script_dir/write-network.sh"
