#!/usr/bin/env bash
#
# Strip debug information from the Breez Spark native payload in a build output
# directory, in place. Run against a plugin TargetDir *before* PluginPacker zips
# it; package.yml runs it there, and the same command is what a local pre-release
# check uses.
#
# Why: Breez.Sdk.Spark ships its Rust libraries unstripped. Each linux .so carries
# ~28 MB of .debug_* DWARF (~48% of the file) plus the .symtab that goes with it;
# the osx dylibs carry ~9-11 MB of symbol/linkedit. None of it is loaded at run
# time — stripping removes only never-mapped sections, .text/.rodata/.data/
# .eh_frame/.dynsym are untouched — yet it is what makes the packaged runtimes
# ~200 MB instead of ~100 MB. Measured on Breez.Sdk.Spark 0.25.0:
#
#   linux-x64   60 MB -> 21 MB     linux-arm64  59 MB -> 18 MB
#   osx-x64     26 MB -> 16 MB     osx-arm64    25 MB -> 14 MB
#
# The osx payload is stripped on every host now: llvm-strip rewrites Mach-O
# (the earlier "LLVM strip cannot rewrite Mach-O" note was wrong — only GNU
# strip cannot). For these files that rewrite keeps the code signature valid,
# verified on llvm 18.1.3 (the version inside the pinned container) and on a
# current brew llvm: `codesign -v --strict` passes, the stripped arm64 dylib
# dlopens on a real arm64 macOS, and every CodeDirectory page hash recomputes.
# Because signature validity is an observed property of llvm-strip rather than
# a contract, the Mach-O path never ships on trust:
#   - Before stripping, the CodeDirectory flags are read (the script's own
#     Python parser): files with no signature and ad-hoc/linker-signed files
#     are stripped; anything else — a Developer ID or other real identity —
#     is skipped with a warning, exactly as the macOS path has always done.
#   - After stripping, every CodeDirectory page hash is recomputed against the
#     file (the same check dyld performs at load). A stripped copy that fails
#     is discarded and the original bytes kept: a fat artifact is a size
#     regression, an invalid signature is a broken plugin.
# On a macOS host the Apple toolchain is used instead, where `strip -x`
# re-establishes the ad-hoc signature and `codesign -v` proves it.
#
# What stripping costs, stated plainly:
#   - Native crash triage: Rust panic *messages* (with file:line) survive, they
#     live in .rodata. Symbolised RUST_BACKTRACE/gdb/coredumps on linux do not:
#     frames show bare addresses instead of function names.
#   - Shipped hashes no longer match Breez's upstream artifacts. Provenance is
#     this plugin's build attestation (package.yml), and the sha256 before each
#     strip is printed so an upstream mapping always exists. MIT permits the
#     modification; NOTICE still travels (enforced in package.yml).
#
# What it deliberately does not touch:
#   - Windows DLLs. Checked on 0.25.0: the PE debug directory is a CodeView
#     pointer of tens of bytes on win-x64; the payload is all real code.
#   - A Mach-O carrying a real (non-ad-hoc, non-linker-signed) signature: strip
#     would invalidate the signing identity. Today the dylibs are unsigned
#     (x86_64) and ad-hoc linker-signed (arm64); if Breez ever ships Developer
#     ID-signed binaries, skipping them is the correct behaviour.
#   - Fat/universal or 32-bit Mach-O: not among Breez's payloads, and not
#     worth a second code path — they are detected and skipped.
#
# Toolchain resolution per ELF file (first that can handle the file's arch):
# llvm-strip (any ELF arch) -> <arch>-linux-gnu-strip -> host strip (probed) ->
# docker (ubuntu:24.04 + cross binutils). Per Mach-O file on a non-macOS host:
# llvm-strip (host, or versioned llvm-strip-NN) -> docker (pinned container:
# llvm-18 + python3 strip and verify together). Idempotent: an already-stripped
# file is a no-op, so re-running after an incremental build is always safe.
#
# Usage: scripts/strip-native-payloads.sh <target-dir>   (expects <target-dir>/runtimes/)

set -euo pipefail

TARGET_DIR="${1:?usage: $0 <plugin TargetDir containing runtimes/>}"
RUNTIMES="$TARGET_DIR/runtimes"
[ -d "$RUNTIMES" ] || { echo "error: $RUNTIMES does not exist; pass the plugin build output directory" >&2; exit 1; }

# Pinned by manifest digest, not the mutable tag: the binary this container
# produces rewrites libraries that ship inside the attested artifact, so a
# repointed ubuntu:24.04 would put unverified bytes on the release path while
# the Sigstore attestation still vouched for the result. The apt-installed
# binutils/llvm are themselves GPG-verified against the Ubuntu archive key by
# apt. This digest is what `ubuntu:24.04` resolved to when the first stripped,
# server-verified artifacts were produced; refresh deliberately (and re-verify
# the output) when the base image is next needed for anything new.
DOCKER_IMAGE="ubuntu:24.04@sha256:33ceb71981b602c1a7443a53469e4dba065f7503eab3078a2d7a57a2ab987517"

sha() { sha256sum "$1" 2>/dev/null | cut -d' ' -f1 || shasum -a 256 "$1" | cut -d' ' -f1; }
fsize() { stat -c%s "$1" 2>/dev/null || stat -f%z "$1"; }
mb() { awk -v n="$1" 'BEGIN{printf "%.0f", n/1048576}'; }

# ELF e_machine (bytes 18-19, little-endian) decides which cross-tool to reach for.
elf_arch() {
  case "$(od -An -j18 -N2 -t x1 "$1" | tr -d ' \n')" in
    3e00) echo x86_64 ;;
    b700) echo aarch64 ;;
    *) echo unknown ;;
  esac
}

docker_strip() { # $1 = directory, $2 = file name — strips in place via bind mount
  local dir; dir="$(cd "$1" && pwd)"   # docker -v needs an absolute host path
  docker run --rm -v "$dir:/work" "$DOCKER_IMAGE" bash -c '
    set -e
    apt-get update -qq >/dev/null && apt-get install -y -qq binutils-aarch64-linux-gnu binutils-x86-64-linux-gnu >/dev/null
    aarch64-linux-gnu-strip -s "/work/$1" || x86_64-linux-gnu-strip -s "/work/$1"
  ' strip "$2"
}

# ---------------------------------------------------------------------------
# Mach-O signature inspection, shared by the guard and the verification.
#
# A thin little-endian 64-bit Mach-O only (Breez ships one dylib per RID).
# Modes:
#   flags  -> prints one of: unsigned (no LC_CODE_SIGNATURE), adhoc (ad-hoc or
#             linker-signed CodeDirectory), signed (a real signing identity);
#             dies on fat/32-bit/unrecognised files.
#   verify -> exit 0 iff an ad-hoc CodeDirectory exists and every page hash
#             recomputes against the file bytes — the check dyld performs at
#             load time on arm64.
# Code Signature blobs are BIG-endian (Apple's embedded signature
# specification); only the Mach-O load commands themselves are LE.
# ---------------------------------------------------------------------------
MACHO_PY="$(mktemp "${TMPDIR:-/tmp}/flint-macho-sig.XXXXXX")"
trap 'rm -f "$MACHO_PY"' EXIT
cat > "$MACHO_PY" <<'PY'
import hashlib, struct, sys

def die(msg):
    sys.stderr.write("error: " + msg + "\n")
    sys.exit(2)

with open(sys.argv[2], "rb") as fh:
    data = fh.read()
if len(data) < 32:
    die("file too small to be a Mach-O")
magic = struct.unpack_from("<I", data, 0)[0]
if magic != 0xFEEDFACF:  # MH_MAGIC_64, little-endian
    die("not a thin little-endian 64-bit Mach-O (fat/32-bit are skipped, not stripped)")

ncmds = struct.unpack_from("<I", data, 16)[0]
off = 32
sig_off = None
for _ in range(ncmds):
    cmd, cmdsize = struct.unpack_from("<II", data, off)
    if cmd == 0x1D:  # LC_CODE_SIGNATURE
        sig_off = struct.unpack_from("<I", data, off + 8)[0]
    off += cmdsize

cd = None
if sig_off is not None and sig_off + 12 <= len(data):
    sb_magic, _sb_len, count = struct.unpack_from(">III", data, sig_off)
    if sb_magic != 0xFADE0CC0:
        die("code signature superblob has an unexpected magic")
    for i in range(count):
        blob_type, blob_off = struct.unpack_from(">II", data, sig_off + 12 + 8 * i)
        if blob_type == 0:  # CSSLOT_CODEDIRECTORY
            cd = sig_off + blob_off
            # ld's linker-signed layout writes 0xfade0c02; codesign(1)-written
            # CodeDirectories use 0xfade0cde. Both parse identically thereafter.
            blob_magic = struct.unpack_from(">I", data, cd)[0]
            if blob_magic not in (0xFADE0CDE, 0xFADE0C02):
                die("CodeDirectory has an unexpected magic")
            break
    if cd is None:
        die("code signature carries no CodeDirectory")

mode = sys.argv[1]
if mode == "flags":
    if cd is None:
        print("unsigned")
    elif struct.unpack_from(">I", data, cd + 12)[0] & 0x2:  # CS_ADHOC
        print("adhoc")
    else:
        print("signed")
    sys.exit(0)

# verify mode
if cd is None:
    # An unsigned payload has nothing to invalidate: after a strip the only
    # requirement is that it is still a well-formed thin 64-bit Mach-O (we
    # just parsed it), which x86_64 macOS loads as-is.
    print("unsigned: no signature to invalidate, structure intact")
    sys.exit(0)
version = struct.unpack_from(">I", data, cd + 8)[0]
if version < 0x20200:
    die("CodeDirectory version too old to verify")
flags = struct.unpack_from(">I", data, cd + 12)[0]
if not flags & 0x2:
    die("signature is not ad-hoc; refusing to vouch for it")
(hash_offset, _ident, _n_special, n_code, code_limit,
 hash_size, _hash_type, _platform, page_pow) = struct.unpack_from(">IIIIIBBBB", data, cd + 16)
page = 1 << page_pow
sha256 = hashlib.sha256
for i in range(n_code):
    start = i * page
    end = min(start + page, code_limit)
    if end > len(data) or start >= end:
        die(f"code limit {code_limit} does not fit the file; signature cannot be valid")
    slot = cd + hash_offset + i * hash_size
    if slot + hash_size > len(data):
        die("CodeDirectory hash slots run past end of file")
    if sha256(data[start:end]).digest()[:hash_size] != data[slot:slot + hash_size]:
        die(f"page hash mismatch in slot {i} (file bytes {start}..{end}); stripped copy is not loadable")
print(f"verified: ad-hoc CodeDirectory, {n_code} page hashes match, page size {page}")
PY

macho_flags() { python3 "$MACHO_PY" flags "$1" || echo bad; }

strip_macho_darwin() { # $1 = path to .dylib — Apple toolchain path
  local f="$1" sig
  # Safe to strip when unsigned (x86_64 payloads ship that way; x86_64 macOS
  # does not require signatures at load) or ad-hoc/linker-signed (strip
  # re-establishes an equivalent ad-hoc signature). A real signing identity
  # must survive untouched: invalidating it is worse than the bytes saved.
  sig="$(codesign -dv "$f" 2>&1 || true)"
  if ! printf '%s' "$sig" | grep -q 'adhoc\|linker-signed\|not signed at all'; then
    echo "warn: $f carries a real code signature; not stripping (identity would be invalidated)" >&2
    return 2
  fi
  # || rc=$? in the caller suspends errexit here, so a failed strip must be explicit
  strip -x "$f" || { echo "error: strip -x failed on $f" >&2; return 1; }
  # before: the pre-strip size, set by the caller
  # There is no cheap "already stripped" test for Mach-O (unlike `file` on
  # ELF), but strip -x is idempotent: a second pass on a stripped dylib frees
  # nothing. No shrink therefore means no-op, not failure.
  if [ "$(fsize "$f")" = "$before" ]; then
    printf 'no-op  %-12s %3s MB — already stripped\n' "$(basename "$(dirname "$(dirname "$f")")")" "$(mb "$before")"
    return 3
  fi
  if printf '%s' "$sig" | grep -q 'adhoc\|linker-signed'; then codesign -v "$f"; fi   # verify the re-sign
  return 0
}

strip_macho_llvm() { # $1 = path to .dylib — non-macOS host: llvm-strip + explicit verification
  local f="$1" mode tool out
  mode="$(macho_flags "$f")"
  case "$mode" in
    unsigned|adhoc) ;;
    *)
      echo "warn: $f not stripped (Mach-O signature state: $mode; only unsigned and ad-hoc payloads are safe to rewrite)" >&2
      return 2
      ;;
  esac

  # Host llvm-strip first (package.yml's pinned container carries llvm-18; brew
  # llvm exposes the unversioned name; Ubuntu only the versioned one). The
  # fallback probe must not kill the script under pipefail when nothing matches.
  tool="llvm-strip"
  command -v llvm-strip >/dev/null 2>&1 || { tool="$(ls /usr/bin/llvm-strip-* 2>/dev/null | sort -V | tail -n 1)" || tool=""; }
  if [ -n "$tool" ] && command -v python3 >/dev/null 2>&1; then
    # Strip a copy, verify the copy, only then replace the original: a strip
    # that invalidates the signature must degrade to "skipped", never ship.
    # Known wart: a $tool that resolves but fails at exec (a broken llvm package,
    # a 127) is blamed on signature verification by the warn below — the container
    # path does not share it, its apt install guarantees llvm-18 is present.
    out="$(mktemp "${TMPDIR:-/tmp}/flint-macho.XXXXXX")"
    if cp "$f" "$out" && "$tool" -x "$out" 2>/dev/null && python3 "$MACHO_PY" verify "$out"; then
      if [ "$(fsize "$out")" = "$before" ]; then
        rm -f "$out"
        printf 'no-op  %-12s %3s MB — already stripped\n' "$(basename "$(dirname "$(dirname "$f")")")" "$(mb "$before")"
        return 3
      fi
      chmod "$before_mode" "$out"
      mv "$out" "$f"
      return 0
    fi
    rm -f "$out"
    echo "warn: $f stripped copy failed Mach-O signature verification; original kept" >&2
    return 2
  fi

  command -v docker >/dev/null 2>&1 || {
    echo "error: no Mach-O stripper for $f (install llvm, or run with docker)" >&2
    return 1
  }
  local dir; dir="$(cd "$(dirname "$f")" && pwd)"
  local work; work="$(mktemp -d "${TMPDIR:-/tmp}/flint-macho-work.XXXXXX")"
  if docker run --rm -v "$dir:/src:ro" -v "$work:/out" -v "$MACHO_PY:/macho_sig.py:ro" "$DOCKER_IMAGE" bash -c '
      set -e
      apt-get update -qq >/dev/null && apt-get install -y -qq --no-install-recommends llvm-18 python3 >/dev/null
      mode="$(python3 /macho_sig.py flags "/src/$1")"
      case "$mode" in unsigned|adhoc) ;; *) echo "signature state: $mode" >&2; exit 3 ;; esac
      cp "/src/$1" /out/stripped.dylib
      # The actual strip. The llvm-18 package names its binary llvm-strip-18; probe the
      # unversioned name as a fallback, mirroring the host path above. Until this line
      # existed the container only copied and verified, so the docker fallback reported
      # every dylib as a no-op ("already stripped") and shipped it unstripped.
      "$(command -v llvm-strip-18 || command -v llvm-strip)" -x /out/stripped.dylib
      python3 /macho_sig.py verify /out/stripped.dylib
      chmod --reference "/src/$1" /out/stripped.dylib
    ' macho "$(basename "$f")"; then
    if [ "$(fsize "$work/stripped.dylib")" = "$before" ]; then
      rm -rf "$work"
      printf 'no-op  %-12s %3s MB — already stripped\n' "$(basename "$(dirname "$(dirname "$f")")")" "$(mb "$before")"
      return 3
    fi
    # The container wrote the file as root; it set the mode itself, but if the
    # volume mapping ever changes under us, re-apply what the original had.
    chmod "$before_mode" "$f" 2>/dev/null || true
    mv "$work/stripped.dylib" "$f"
    rm -rf "$work"
    return 0
  fi
  rm -rf "$work"
  echo "warn: $f not stripped (container Mach-O strip declined or failed verification)" >&2
  return 2
}

strip_elf() { # $1 = path to .so
  local f="$1" arch tool=""
  arch="$(elf_arch "$f")"
  [ "$arch" != unknown ] || { echo "error: $f has an unrecognised ELF machine id" >&2; return 1; }

  if command -v llvm-strip >/dev/null 2>&1; then
    tool="llvm-strip"
  elif tool="$(ls /usr/bin/llvm-strip-* 2>/dev/null | sort -V | tail -n 1)" && [ -n "$tool" ]; then
    # llvm-strip handles every ELF arch; Ubuntu ships it versioned (llvm-strip-18)
    :
  elif command -v "${arch}-linux-gnu-strip" >/dev/null 2>&1; then
    tool="${arch}-linux-gnu-strip"
  elif command -v strip >/dev/null 2>&1; then
    # Host GNU strip is single-arch on Debian/Ubuntu images and macOS has no ELF
    # support at all; probe with a copy so a miss never touches the real file.
    # The probe lives outside the TargetDir: a leaked probe file inside the
    # build output would otherwise be zipped into the artifact by PluginPacker.
    local probe; probe="$(mktemp "${TMPDIR:-/tmp}/flint-elf-probe.XXXXXX")"
    if cp "$f" "$probe" && strip -s "$probe" 2>/dev/null; then
      tool="strip"
    fi
    rm -f "$probe"
  fi

  if [ -n "$tool" ]; then
    "$tool" -s "$f"
    return 0
  fi

  command -v docker >/dev/null 2>&1 || {
    echo "error: no ELF stripper for $f (install llvm or binutils-${arch}-linux-gnu, or run with docker)" >&2
    return 1
  }
  docker_strip "$(dirname "$f")" "$(basename "$f")"
}

total_before=0; total_after=0

while read -r f; do
  plat="$(basename "$(dirname "$(dirname "$f")")")"
  before="$(fsize "$f")"
  # The Mach-O paths replace the file via rename(2) from a mktemp copy (mode
  # 0600, root-owned when the copy was made inside the container); without
  # re-applying the original mode the stripped file is unreadable to whoever
  # zips the directory next.
  before_mode="$(stat -c '%a' "$f" 2>/dev/null || stat -f '%Lp' "$f")"
  total_before=$((total_before + before))

  case "$f" in
    *.dll)
      printf 'skip   %-12s %3s MB — PE payload has no strippable debug data\n' "$plat" "$(mb "$before")"
      total_after=$((total_after + before))
      continue
      ;;
    *.so)
      if ! file "$f" | grep -q 'not stripped\|with debug_info'; then
        printf 'no-op  %-12s %3s MB — already stripped\n' "$plat" "$(mb "$before")"
        total_after=$((total_after + before))
        continue
      fi
      hash_before="$(sha "$f")"
      strip_elf "$f"
      ;;
    *.dylib)
      hash_before="$(sha "$f")"
      if [ "$(uname -s)" = "Darwin" ]; then
        rc=0; strip_macho_darwin "$f" || rc=$?
      else
        rc=0; strip_macho_llvm "$f" || rc=$?
      fi
      if [ "$rc" = 2 ] || [ "$rc" = 3 ]; then   # 2 = skipped (warn printed), 3 = no-op (printed)
        total_after=$((total_after + before))
        continue
      fi
      [ "$rc" = 0 ] || exit 1
      ;;
    *)
      echo "error: unexpected native file $f" >&2; exit 1
      ;;
  esac

  after="$(fsize "$f")"
  total_after=$((total_after + after))
  printf 'strip  %-12s %3s MB -> %2s MB   upstream sha256 %s\n' \
    "$plat" "$(mb "$before")" "$(mb "$after")" "$hash_before"
  [ "$after" -lt "$before" ] || { echo "error: $f did not shrink after strip" >&2; exit 1; }
done < <(find "$RUNTIMES" -path '*/native/*' \( -name '*.so' -o -name '*.dylib' -o -name '*.dll' \) | sort)

printf 'runtimes total: %s MB -> %s MB\n' "$(mb "$total_before")" "$(mb "$total_after")"
