#!/usr/bin/env bash
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$HERE/.." && pwd)"
SETTINGS="$HERE/debug.linux.jsonc"

if [ ! -f "$SETTINGS" ]; then
  echo "Creating default $SETTINGS"
  cat > "$SETTINGS" <<'EOF'
{
  // Build configuration.
  // Available: Debug | Release   ("Debug Linux" always debugs the Debug binaries)
  "configuration": "Debug",

  // Tunnel interface the agent creates.
  "interface": "awg0"
}
EOF
fi

read_setting() {
  sed -E 's#//.*$##' "$SETTINGS" \
    | grep -oE "\"$1\"[[:space:]]*:[[:space:]]*\"[^\"]*\"" \
    | head -n1 | sed -E 's/.*:[[:space:]]*"([^"]*)".*/\1/'
}

CONFIGURATION="${CONFIGURATION:-$(read_setting configuration)}"
: "${CONFIGURATION:=Debug}"

ENGINE="$ROOT/amneziageo-android/amneziawg-go/amneziawg-go"
if [ ! -x "$ENGINE" ]; then
  echo "Building the AmneziaWG engine first"
  bash "$ROOT/amneziageo-linux/tools/build-engine-linux.sh"
fi

echo "Building Linux: Configuration=$CONFIGURATION"
dotnet build "$ROOT/AmneziaGeo.Linux.slnx" -c "$CONFIGURATION" -v minimal -nologo

echo
echo "Agent : $ROOT/amneziageo-linux/AmneziaGeo.Linux.App/bin/$CONFIGURATION/net10.0/AmneziaGeo.Linux.App.dll"
echo "UI    : $ROOT/amneziageo-linux/AmneziaGeo.Linux.Ui/bin/$CONFIGURATION/net10.0/AmneziaGeo.Linux.Ui.dll"
echo "Client: $ROOT/amneziageo-linux/AmneziaGeo.Linux.Cli/bin/$CONFIGURATION/net10.0/amneziageo"
echo "Bringing a tunnel up needs root - \"Debug Linux (agent)\" and the \"Run Linux agent (sudo)\" task both elevate."
echo "The console client runs unelevated - \"Debug Linux (console)\" asks for the command line, \"Run Linux console (attachable)\" waits for \"Attach Linux (console)\"."
