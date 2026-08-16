#!/usr/bin/env bash
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$HERE/.." && pwd)"
SETTINGS="$HERE/debug.linux.jsonc"

if [ ! -f "$SETTINGS" ]; then
  echo "Creating default $SETTINGS"
  cat > "$SETTINGS" <<'EOF'
{
  // Debug targets. Takes the first entry with isCurrent = true.
  "targets": [
    {
      "isCurrent": true,
      "name": "Agent",

      // Component to run.
      // Available: agent | ui | console
      "component": "agent",

      // Command line of the component.   Empty = none.
      // agent: --iface <name> --engine <path> --data <dir>.   console: status, tui, --json config list.
      "args": "--iface awg0",

      // Run elevated. The agent needs root: it creates the tunnel device and rewrites routes.
      // Available: true | false
      "sudo": true
    },
    {
      "isCurrent": false,
      "name": "UI",
      "component": "ui",
      "args": "",
      "sudo": false
    },
    {
      "isCurrent": false,
      "name": "Console",
      "component": "console",
      "args": "status",
      "sudo": false
    },
    {
      "isCurrent": false,
      "name": "Console TUI",
      "component": "console",
      "args": "tui",
      "sudo": false
    }
  ],

  // Build configuration.
  // Available: Debug | Release
  "configuration": "Debug"
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
echo "\"Debug Linux\" runs the current target of $SETTINGS, \"Attach Linux\" hooks a component already running."
