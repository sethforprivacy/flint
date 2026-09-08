#!/usr/bin/env bash
# Tear the local Spark stack down and reclaim its disk.
#
# This runs the fixture's own `cashu-regtest-stop`, which is `docker compose --profile "*" down
# --volumes` followed by deleting and recreating the bind-mounted Lightning node data directories.
# The profile wildcard matters: the Spark services are all behind a profile, and a plain
# `docker compose down` leaves every one of them running. The `--volumes` matters too — the
# operators' Postgres databases, the SSP's mnemonic and the generated TLS certificates all live in
# named volumes, and a half-reset stack (fresh chain, stale keyshares) fails in ways that look like
# Spark bugs. Recreating the data directories afterwards is what keeps the next start from failing
# on permissions.
#
# It does not remove the checkout, and it does not remove the built images: those are the tens of
# minutes of Rust and Go compilation, and keeping them is what makes a second run fast. Delete
# e2e/local-regtest/cashu-regtest by hand if you want the source gone as well.
#
# Environment:
#   CASHU_REGTEST_DIR  the fixture checkout to stop (default: the one alongside this script)
set -euo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
checkout="${CASHU_REGTEST_DIR:-$script_dir/cashu-regtest}"

if [ ! -d "$checkout" ]; then
  echo "No fixture checkout at $checkout; nothing to stop."
  exit 0
fi

# docker-scripts.sh only resolves its own relative sources (bark/, ldk/, fees/ scripts) from the
# checkout root, and it is also what exports COMPOSE_PROJECT_NAME=cashu — the project name the
# stack was created under. Sourcing it from anywhere else stops nothing.
cd "$checkout"
# shellcheck disable=SC1091  # a file in the pinned foreign checkout, not in this repository
. ./docker-scripts.sh

echo "==> cashu-regtest-stop (compose down --volumes across all profiles)"
cashu-regtest-stop

# The descriptor names container IDs and holds certificates that no longer exist; leaving it in
# place would let a later `dotnet test` run point the SDK at a dead stack and fail on a connection
# error rather than saying the stack is down.
if [ -f "$script_dir/network.json" ]; then
  echo "==> removing the stale $script_dir/network.json"
  rm -f "$script_dir/network.json"
fi

echo "==> stack is down. Built images are kept; delete $checkout to remove the source too."
