#!/usr/bin/env bash
# Builds AmneziaGeo.Android.Engine/native/<abi>/libamneziawg-go.so with the Android NDK toolchain.
#
# The Linux and macOS counterpart of build-engine-android.ps1, used by CI and by the Linux stand.
# The amneziawg-go submodule is NOT modified: the c-shared entry points live in the separate
# libamneziawg-go module next to it, which pulls the submodule in through a replace directive.
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ANDROID_DIR="$(cd "$HERE/.." && pwd)"
MODULE="$ANDROID_DIR/libamneziawg-go"
SUBMODULE="$ANDROID_DIR/amneziawg-go"
OUT_ROOT="$ANDROID_DIR/AmneziaGeo.Android.Engine/native"

ABIS="arm64-v8a armeabi-v7a x86_64"
API=24
NDK="${ANDROID_NDK_HOME:-${ANDROID_NDK_ROOT:-${ANDROID_NDK_LATEST_HOME:-}}}"

usage() {
  cat <<EOF
usage: build-engine-android.sh [options]

  --abi <list>    ABIs, comma separated: arm64-v8a, armeabi-v7a, x86_64, x86 (default: $ABIS)
  --api <level>   API level of the NDK toolchain (default: $API)
  --ndk <dir>     NDK root; otherwise ANDROID_NDK_HOME, ANDROID_NDK_ROOT or the newest one in the SDK
  --help          this help

The output is the gitignored native/<abi>/libamneziawg-go.so that AmneziaGeo.Android.Engine.csproj
consumes - the same arrangement as x64/tunnel.dll on Windows.
EOF
}

while [ $# -gt 0 ]; do
  case "$1" in
    --abi) ABIS="$(printf '%s' "$2" | tr ',' ' ')"; shift 2 ;;
    --api) API="$2"; shift 2 ;;
    --ndk) NDK="$2"; shift 2 ;;
    --help|-h) usage; exit 0 ;;
    *) echo "unknown argument '$1'" >&2; usage >&2; exit 2 ;;
  esac
done

if [ ! -f "$SUBMODULE/go.mod" ]; then
  echo "amneziawg-go submodule not found at $SUBMODULE (run: git submodule update --init --recursive)" >&2
  exit 1
fi

# NDK from the SDK when the environment names none.
if [ -z "$NDK" ]; then
  for root in "${ANDROID_SDK_ROOT:-}/ndk" "${ANDROID_HOME:-}/ndk" "$HOME/Android/Sdk/ndk" "$HOME/Library/Android/sdk/ndk"; do
    [ -d "$root" ] || continue
    newest="$(ls -1 "$root" 2>/dev/null | sort -V | tail -1)"
    if [ -n "$newest" ]; then
      NDK="$root/$newest"
      break
    fi
  done
fi

if [ -z "$NDK" ] || [ ! -d "$NDK" ]; then
  cat >&2 <<EOF
Android NDK not found. Set ANDROID_NDK_HOME or pass --ndk <path>:
  sudo apt install google-android-ndk-installer      # or install it from the SDK manager
EOF
  exit 1
fi

case "$(uname -s)" in
  Linux)  HOST_TAG="linux-x86_64" ;;
  Darwin) HOST_TAG="darwin-x86_64" ;;
  *) echo "unsupported host '$(uname -s)'; on Windows run build-engine-android.ps1" >&2; exit 1 ;;
esac

NDK_BIN="$NDK/toolchains/llvm/prebuilt/$HOST_TAG/bin"
if [ ! -d "$NDK_BIN" ]; then
  echo "no $HOST_TAG toolchain inside $NDK" >&2
  exit 1
fi

# Compiler triple of an ABI.
triple_of() {
  case "$1" in
    arm64-v8a)   echo "aarch64-linux-android" ;;
    x86_64)      echo "x86_64-linux-android" ;;
    armeabi-v7a) echo "armv7a-linux-androideabi" ;;
    x86)         echo "i686-linux-android" ;;
    *) echo "unsupported ABI '$1'" >&2; return 1 ;;
  esac
}

# GOARCH of an ABI.
goarch_of() {
  case "$1" in
    arm64-v8a)   echo "arm64" ;;
    x86_64)      echo "amd64" ;;
    armeabi-v7a) echo "arm" ;;
    x86)         echo "386" ;;
    *) echo "unsupported ABI '$1'" >&2; return 1 ;;
  esac
}

for abi in $ABIS; do
  cc="$NDK_BIN/$(triple_of "$abi")$API-clang"
  if [ ! -x "$cc" ]; then
    echo "no compiler for $abi at API $API: $cc" >&2
    exit 1
  fi
done

# Go from PATH, otherwise from GOROOT.
go_bin="$(command -v go || true)"
if [ -z "$go_bin" ] && [ -n "${GOROOT:-}" ] && [ -x "$GOROOT/bin/go" ]; then
  go_bin="$GOROOT/bin/go"
fi

required="$(sed -n 's/^go \([0-9][0-9.]*\).*/\1/p' "$MODULE/go.mod" | head -1)"

if [ -z "$go_bin" ]; then
  cat >&2 <<EOF
Go toolchain not found in PATH.
libamneziawg-go needs Go ${required:-1.25} or newer and the NDK clang as its cgo compiler. Install it, then re-run:
  sudo snap install go --classic        # or: sudo apt install golang-go
EOF
  exit 1
fi

have="$("$go_bin" env GOVERSION | sed 's/^go//')"
if [ -n "$required" ] && [ "$(printf '%s\n%s\n' "$required" "$have" | sort -V | head -1)" != "$required" ]; then
  echo "Go $have at $go_bin is older than the $required required by $MODULE/go.mod" >&2
  exit 1
fi

echo
echo "== toolchain: $("$go_bin" version) =="
echo "   NDK $NDK (API $API)"

export GOOS="android"
# without this a go.mod toolchain bump would silently download another Go
export GOTOOLCHAIN="local"
export GOFLAGS="-mod=readonly"
export CGO_ENABLED="1"
# set here rather than in a #cgo directive: cgo rejects -Wl,-z from source, trusts the environment
export CGO_LDFLAGS="-Wl,-z,max-page-size=16384"

cd "$MODULE"
for abi in $ABIS; do
  triple="$(triple_of "$abi")"
  out_dir="$OUT_ROOT/$abi"
  mkdir -p "$out_dir"

  echo "== building $abi/libamneziawg-go.so =="
  GOARCH="$(goarch_of "$abi")" \
  GOARM="$([ "$abi" = "armeabi-v7a" ] && echo 7 || echo '')" \
  CC="$NDK_BIN/$triple$API-clang" \
  CXX="$NDK_BIN/$triple$API-clang++" \
    "$go_bin" build -buildmode c-shared -ldflags '-w -s' -trimpath -o "$out_dir/libamneziawg-go.so"

  rm -f "$out_dir/libamneziawg-go.h"
done

echo
echo "== result =="
for abi in $ABIS; do
  so="$OUT_ROOT/$abi/libamneziawg-go.so"
  printf '   %s  %s bytes\n' "$so" "$(stat -c %s "$so" 2>/dev/null || stat -f %z "$so")"

  # Android 15 devices require 16 KB pages: report the largest PT_LOAD alignment.
  if command -v readelf >/dev/null 2>&1; then
    align=0
    while read -r a; do
      a=$((a))
      if [ "$a" -gt "$align" ]; then
        align=$a
      fi
    done < <(readelf -lW "$so" | awk '$1 == "LOAD" { print $NF }')
    warn=""
    if [ "$align" -lt 16384 ]; then
      warn=" - NOT 16 KB page safe"
    fi
    printf '   max PT_LOAD align : %s bytes%s\n' "$align" "$warn"
  fi

  missing=""
  for symbol in wgTurnOn wgTurnOff wgGetSocketV4 wgGetConfig; do
    grep -qa "$symbol" "$so" || missing="$missing $symbol"
  done
  if [ -n "$missing" ]; then
    printf '   exports           : MISSING%s\n' "$missing"
  else
    printf '   exports           : all present\n'
  fi
done

echo
echo 'Now build the app - AmneziaGeo.Android.Engine.csproj picks these .so up automatically.'
