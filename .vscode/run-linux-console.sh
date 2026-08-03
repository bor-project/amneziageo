#!/usr/bin/env bash
# Runs the built console client paused until a debugger attaches, so "Attach Linux (console)" has a target.
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$HERE/.." && pwd)"
SETTINGS="$HERE/debug.linux.jsonc"

read_setting() {
  [ -f "$SETTINGS" ] || return 0
  sed -E 's#//.*$##' "$SETTINGS" \
    | grep -oE "\"$1\"[[:space:]]*:[[:space:]]*\"[^\"]*\"" \
    | head -n1 | sed -E 's/.*:[[:space:]]*"([^"]*)".*/\1/'
}

CONFIGURATION="${CONFIGURATION:-$(read_setting configuration)}"
: "${CONFIGURATION:=Debug}"

CLI="$ROOT/amneziageo-linux/AmneziaGeo.Linux.Cli/bin/$CONFIGURATION/net10.0/amneziageo"
if [ ! -x "$CLI" ]; then
  cat >&2 <<EOF
The console client is not built: $CLI
Build it first, then re-run this script:
  .vscode/build-linux.sh
EOF
  exit 1
fi

DOTNET="$(command -v dotnet || true)"
if [ -z "$DOTNET" ]; then
  DOTNET="$(getent passwd "$(id -un)" | cut -d: -f6)/.dotnet/dotnet"
fi
if [ ! -x "$DOTNET" ]; then
  echo "dotnet not found." >&2
  exit 1
fi

export DOTNET_ROOT="$(dirname "$(readlink -f "$DOTNET")")"
export AMNEZIAGEO_WAIT_DEBUGGER=1

echo "Attach to pid printed below with \"Attach Linux (console)\"."

# SUDO=1 reproduces the server flow, where the client runs elevated.
if [ "${SUDO:-}" = "1" ] && [ "$(id -u)" -ne 0 ]; then
  exec sudo env "DOTNET_ROOT=$DOTNET_ROOT" AMNEZIAGEO_WAIT_DEBUGGER=1 "$CLI" "$@"
fi

exec "$CLI" "$@"
