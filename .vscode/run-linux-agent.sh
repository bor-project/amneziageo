#!/usr/bin/env bash
# Runs the built Linux agent elevated, so it can create the tunnel interface and install routes.
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
IFACE="${IFACE:-$(read_setting interface)}"
: "${CONFIGURATION:=Debug}"
: "${IFACE:=awg0}"

AGENT="$ROOT/amneziageo-linux/AmneziaGeo.Linux.App/bin/$CONFIGURATION/net10.0/AmneziaGeo.Linux.App.dll"
if [ ! -f "$AGENT" ]; then
  cat >&2 <<EOF
The agent is not built: $AGENT
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

if [ "$(id -u)" -eq 0 ]; then
  exec "$DOTNET" "$AGENT" --iface "$IFACE"
fi

echo "Elevating: the agent creates $IFACE and installs routes."
if [ -n "${AMNEZIAGEO_DATA:-}" ]; then
  exec sudo env AMNEZIAGEO_DATA="$AMNEZIAGEO_DATA" "$DOTNET" "$AGENT" --iface "$IFACE"
fi

exec sudo "$DOTNET" "$AGENT" --iface "$IFACE"
