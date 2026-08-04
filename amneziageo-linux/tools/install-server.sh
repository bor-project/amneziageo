#!/usr/bin/env bash
# Installs the AmneziaGeo agent and its console client as a systemd service on a server without a desktop.
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$HERE/../.." && pwd)"

PREFIX="/opt/amneziageo"
DATA="/var/lib/amneziageo"
IFACE="awg0"
RUNTIME="linux-x64"
SELF_CONTAINED="true"
INSTALL_SERVICE="yes"

usage() {
  cat <<EOF
usage: install-server.sh [options]

  --prefix <dir>     install directory (default: $PREFIX)
  --data <dir>       library holding state.db, logs and geo bases (default: $DATA)
  --iface <name>     tunnel interface the agent creates (default: $IFACE)
  --runtime <rid>    publish runtime identifier (default: $RUNTIME)
  --framework        publish framework-dependent; the server then needs the .NET runtime
  --no-service       install the files only, leave systemd alone
  --help             this help

Run it on the machine that will host the tunnel. Building needs the .NET SDK and the Go
toolchain (the AmneziaWG engine is built from the submodule); running needs neither.
EOF
}

while [ $# -gt 0 ]; do
  case "$1" in
    --prefix) PREFIX="$2"; shift 2 ;;
    --data) DATA="$2"; shift 2 ;;
    --iface) IFACE="$2"; shift 2 ;;
    --runtime) RUNTIME="$2"; shift 2 ;;
    --framework) SELF_CONTAINED="false"; shift ;;
    --no-service) INSTALL_SERVICE="no"; shift ;;
    --help|-h) usage; exit 0 ;;
    *) echo "unknown argument '$1'" >&2; usage >&2; exit 2 ;;
  esac
done

SUDO=""
if [ "$(id -u)" != "0" ]; then
  SUDO="sudo"
fi

ENGINE="$ROOT/amneziageo-android/amneziawg-go/amneziawg-go"
if [ ! -x "$ENGINE" ]; then
  echo "Building the AmneziaWG engine"
  bash "$ROOT/amneziageo-linux/tools/build-engine-linux.sh"
fi

STAGE="$(mktemp -d)"
trap 'rm -rf "$STAGE"' EXIT

echo "Publishing to $STAGE ($RUNTIME, self-contained=$SELF_CONTAINED)"
for project in AmneziaGeo.Linux.App AmneziaGeo.Linux.Cli; do
  dotnet publish "$ROOT/amneziageo-linux/$project/$project.csproj" \
    -c Release \
    -r "$RUNTIME" \
    --self-contained "$SELF_CONTAINED" \
    -o "$STAGE" \
    -v minimal \
    --nologo
done

echo "Installing into $PREFIX"
$SUDO mkdir -p "$PREFIX" "$DATA"
$SUDO cp -a "$STAGE/." "$PREFIX/"
$SUDO chmod 700 "$DATA"
$SUDO ln -sf "$PREFIX/amneziageo" /usr/local/bin/amneziageo

if [ "$INSTALL_SERVICE" = "yes" ]; then
  $SUDO "$PREFIX/amneziageo" daemon install --data "$DATA" --iface "$IFACE" --agent "$PREFIX/AmneziaGeo.Linux.App"
else
  echo "Skipping the service; the unit text is available with:"
  echo "  $PREFIX/amneziageo daemon install --print --data $DATA --iface $IFACE"
fi

cat <<EOF

Installed.
  binaries : $PREFIX
  library  : $DATA
  client   : amneziageo

Next steps:
  sudo amneziageo geo download                       # seed and download the geo bases
  sudo amneziageo config import work --file work.conf
  sudo amneziageo profile add work work
  sudo amneziageo up work
  sudo amneziageo settings set survive-reboot on
  sudo amneziageo settings set periodic-reconnect-enabled on
  sudo amneziageo tui                                # full-screen console
EOF
