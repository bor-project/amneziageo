#!/usr/bin/env bash
# Builds the amneziawg-go daemon consumed by the Linux agent.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
root="$(cd "$here/../.." && pwd)"
engine="$root/amneziageo-android/amneziawg-go"

if [ ! -f "$engine/go.mod" ]; then
  cat >&2 <<EOF
amneziawg-go submodule is missing at $engine
The Linux agent uses the same checkout as the Android head - there is no separate submodule under amneziageo-linux.
Initialize it, then re-run this script:
  git submodule update --init --recursive
  amneziageo-linux/tools/build-engine-linux.sh
EOF
  exit 1
fi

# Go from PATH, otherwise from GOROOT.
go_bin="$(command -v go || true)"
if [ -z "$go_bin" ] && [ -n "${GOROOT:-}" ] && [ -x "$GOROOT/bin/go" ]; then
  go_bin="$GOROOT/bin/go"
fi

required="$(sed -n 's/^go \([0-9][0-9.]*\).*/\1/p' "$engine/go.mod" | head -1)"

if [ -z "$go_bin" ]; then
  cat >&2 <<EOF
Go toolchain not found in PATH.
amneziawg-go needs Go ${required:-1.25} or newer (pure Go - no cgo, no C toolchain). Install it, then re-run this script:
  sudo snap install go --classic        # or: sudo apt install golang-go
  amneziageo-linux/tools/build-engine-linux.sh
For a Go unpacked outside PATH: export PATH="\$GOROOT/bin:\$PATH"
EOF
  exit 1
fi

have="$("$go_bin" env GOVERSION | sed 's/^go//')"
if [ -n "$required" ] && [ "$(printf '%s\n%s\n' "$required" "$have" | sort -V | head -1)" != "$required" ]; then
  cat >&2 <<EOF
Go $have at $go_bin is older than the $required required by $engine/go.mod
Upgrade Go, then re-run this script:
  sudo snap install go --classic
EOF
  exit 1
fi

echo "== toolchain: $("$go_bin" version) =="
PATH="$(dirname "$go_bin"):$PATH" make -C "$engine"

bin="$engine/amneziawg-go"
echo
echo "== result =="
printf '   %s  %s bytes\n' "$bin" "$(stat -c %s "$bin")"
if command -v file >/dev/null 2>&1; then
  printf '   %s\n' "$(file -b "$bin")"
fi
echo
echo 'Now build the agent - AmneziaGeo.Linux.App.csproj stages this binary automatically.'
