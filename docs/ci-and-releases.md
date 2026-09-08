[← Docs index](README.md)

# CI, releases & upstream updates

- **`.github/workflows/ci.yml`** runs on every push to `main`, on all PRs, and on a daily schedule so
  SSP/SDK drift shows up on days with no commits. Four jobs, three of which gate a pull request:

  | job | gates a merge? | why |
  |---|---|---|
  | `build-and-test` | **yes** | no external dependency; a failure is always our bug |
  | `store-test` | **yes** | real Postgres in a service container, no third-party network |
  | `integration-test` | no — `continue-on-error` | depends on Lightspark's hosted regtest; an outage there would block merges |
  | `funded-regtest-test` | **on PRs** — advisory on schedule/push | a human is present on a PR to judge a failure; scheduled and push runs stay advisory because the suite also depends on a faucet-funded balance, and the CI wallet draining is an operations event, not a defect in main |

  The advisory runs still report their real pass/fail in the run summary. **Someone has to look**: an SDK
  error-string re-wording, or a preimage reaching the log, shows as a failed job inside a green
  scheduled run. A drained wallet failing a PR is answered by funding the wallet and re-running, not by
  reading it as a code failure.
- **`.github/workflows/local-regtest.yml`** runs the `LocalRegtest` suite against a Spark stack it
  stands up itself — [callebtc/cashu-regtest](https://github.com/callebtc/cashu-regtest)'s `--spark`
  profile, pinned by SHA in both the workflow and `e2e/local-regtest/up.sh`. It is the only job that
  covers settlement end to end with **no third-party service and no secret**: real LND nodes on both
  sides of an invoice, and blocks mined on demand. Daily, on PRs, and on manual dispatch.

  | job | gates a merge? | why |
  |---|---|---|
  | `local-regtest` | no — `continue-on-error` | no measured flake rate yet, and the fixture is six services built from source with keyshare generation and channel gossip to wait on; a flaky fixture blocking merges would cost more than the gap it closes. Unlike `integration-test` the dependency is not a third-party service, so red here is likelier to be our bug — which is the argument for eventually gating it on PRs, once the daily schedule has accumulated a pass/fail record, exactly as `funded-regtest-test` earned its gate |

  It is also the slowest job in the repository by a wide margin: the fixture publishes no images, so
  every run builds the Spark operators, Electrs, `open-ssp` and `ldk-server` from Rust and Go source
  on a cache-less runner — around **40 minutes**, against a 90-minute timeout. Publishing those images
  to `ghcr.io` once per pinned SHA is the noted follow-up, and is what would make the job cheap enough
  to gate. On failure it uploads a `local-regtest-stack-logs` artifact with `docker compose ps -a` and
  the tail of each service's log, because a failure here is usually "which of six services fell over"
  rather than anything the .NET output shows. See
  ["Against a local Spark stack"](testing.md#against-a-local-spark-stack).
- **`.github/workflows/spark-regtest-wallet.yml`** is manual-only. It prints the CI regtest wallet's
  static deposit address and balance so a maintainer can fund it — see
  ["A funded regtest wallet for CI"](testing.md#a-funded-regtest-wallet-for-ci). It never prints the seed, and
  the seed generator refuses to run on a runner at all.
- **`.github/workflows/package.yml`** builds a `.btcpay` release artifact via BTCPay's
  `PluginPacker` on `v*` tags and on manual dispatch, uploads it (plus its `.btcpay.json` manifest
  and `SHA256SUMS`) as a workflow artifact, and attaches it to the corresponding GitHub Release when
  triggered by a tag. On a tag it also refuses to build if the tag disagrees with the version in the
  csproj, so a `v0.2.0` tag on a 0.1.0 tree fails rather than producing a mislabelled release.
  - **All four ELF/Mach-O native payloads are stripped at packaging time** by
    [`scripts/strip-native-payloads.sh`](../scripts/strip-native-payloads.sh): Breez ships its Rust
    libraries with DWARF debug info and fat symbol tables — never-mapped data that nonetheless
    travels inside every `.btcpay` — and stripping takes the packaged runtimes from ~196 MB to
    ~100 MB uncompressed. The step runs inside the digest-pinned `ubuntu:24.04` container with
    apt-verified `llvm-18`, so the toolchain that rewrites the attested bytes is itself pinned (the
    same policy as the script's own docker fallback for local runs). llvm-strip rewrites Mach-O, so
    the osx dylibs strip on every host now, not just macOS ones; because signature validity after
    that rewrite is an observed property rather than a contract, the script reads each dylib's
    CodeDirectory before touching it (unsigned and ad-hoc/linker-signed only — a Developer
    ID-signed payload is skipped) and re-verifies every page hash afterwards, discarding the
    stripped copy if any hash fails. The stripped arm64 dylib was further proven with
    `codesign -v --strict` and a real `dlopen` on arm64 macOS. The trade (symbolised native
    backtraces, and hashes that no longer match Breez's upstream byte-for-byte — hence the upstream
    sha256 each strip prints) is argued in the script's header, which is also the local pre-release
    recipe: run `dotnet build -c Release`, run the script against the output directory, then
    `PluginPacker` by hand. The step is idempotent and fails the package loudly if an ELF cannot be
    stripped; the Windows DLLs are not touched at all, because they carry no strippable debug
    data. The 32-bit `win-x86` payload is instead pruned at packaging by the step that follows,
    whose RID-set assertion pins the shipped set to `linux-x64`, `linux-arm64`, `osx-arm64`,
    `osx-x64` and `win-x64` only.
  - **Artifacts are signed with keyless Sigstore build provenance**
    (`actions/attest-build-provenance`), not a maintainer GPG key: there is no long-lived key for
    anyone to generate, store, lose or leak, and the attestation binds the artifact's digest to this
    repository, this workflow and the commit that produced it rather than merely asserting who built
    it. Verify a download with
    `gh attestation verify BTCPayServer.Plugins.Flint.btcpay --repo sethforprivacy/flint`,
    or offline against the `attestation.jsonl` bundle attached to the release. The reasoning, and
    what this deliberately does *not* protect against, is in the header comment of
    [`package.yml`](../.github/workflows/package.yml).
- **Test / GitHub Actions version bumps** are handled by
  [Dependabot](../.github/dependabot.yml) (`nuget` and `github-actions` ecosystems); CI on the PRs
  it opens is the gate.
- **Breez.Sdk.Spark bumps** are *not* Dependabot's (it is ignored there): Breez pushes tag-only
  patch versions to NuGet with no release entry, and a PR per push is churn.
  **`.github/workflows/breez-sdk-update.yml`** runs weekly (and on manual dispatch, with a dry-run
  option) and opens a bump PR only for a version upstream has published a GitHub Release for that
  also exists on NuGet — see `scripts/check-breez-sdk-update.sh`. A tag-only fix worth shipping
  early is bumped by hand, exactly as 0.22.2 and 0.22.3 were.
- **`btcpayserver` submodule bumps** are *not* handled by Dependabot: its `gitsubmodule` ecosystem
  tracks the latest commit on a branch, not release tags, which is the wrong model for a submodule
  pinned to stable releases. Instead, **`.github/workflows/btcpayserver-update.yml`** runs weekly
  (and on manual dispatch) to look for the newest stable `vX.Y.Z` btcpayserver tag past the one
  currently pinned, and if it finds one, opens a PR that bumps the submodule and updates
  `Constants.BuiltAgainstBTCPayServerVersion` (and the mention of the pin in [Building](building.md)) to match, leaving the
  declared support floor alone — see the
  discovery logic in `scripts/check-btcpayserver-update.sh`.
- **Branch protection**: not configured by these workflows. For `main`, enable "Require status
  checks to pass before merging" with `ci.yml`'s `build-and-test` **and `store-test`** jobs required
  (`integration-test` intentionally *not* required, since it is `continue-on-error` by design), plus
  "Require branches to be up to date before merging". `funded-regtest-test` blocks PRs since it is
  no longer `continue-on-error` there; requiring it in branch protection as well is a choice —
  doing so means a drained CI wallet holds every merge until someone funds it. `local-regtest` is
  `continue-on-error` on every trigger and should not be required either, for now.
