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
SHORTCUT=""
ASK_SHORTCUT="yes"
LINK_DIR="/usr/local/bin"

usage() {
  cat <<EOF
usage: install-server.sh [options]

  --prefix <dir>     install directory (default: $PREFIX)
  --data <dir>       library holding state.db, logs and geo bases (default: $DATA)
  --iface <name>     tunnel interface the agent creates (default: $IFACE)
  --runtime <rid>    publish runtime identifier (default: $RUNTIME)
  --framework        publish framework-dependent; the server then needs the .NET runtime
  --no-service       install the files only, leave systemd alone
  --shortcut <name>  put the short command <name> for amneziageo without asking
  --no-shortcut      put no short command and ask nothing
  --help             this help

Run it on the machine that will host the tunnel. Building needs the .NET SDK and the Go
toolchain (the AmneziaWG engine is built from the submodule); running needs neither. On a terminal it
asks whether to put a short command for the console client, ag-c by default, a link in /usr/local/bin.
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
    --shortcut) SHORTCUT="$2"; shift 2 ;;
    --no-shortcut) ASK_SHORTCUT="no"; shift ;;
    --help|-h) usage; exit 0 ;;
    *) echo "unknown argument '$1'" >&2; usage >&2; exit 2 ;;
  esac
done

SUDO=""
if [ "$(id -u)" != "0" ]; then
  SUDO="sudo"
fi

TOOL="$PREFIX/amneziageo"

# Tells whether a terminal is there to ask on.
on_terminal() {
  { : < /dev/tty; } 2> /dev/null
}

# Asks a line on the terminal and prints it, the default for an empty one; fails where the terminal ends.
ask() {
  local answer=""
  read -r -p "$1 " answer < /dev/tty || return 1
  printf '%s' "${answer:-$2}"
}

# Tells whether a word can name the short command: no command holds it, or it leads to the client already.
shortcut_ok() {
  local link="$LINK_DIR/$1" taken=""
  if ! [[ $1 =~ ^[A-Za-z0-9][A-Za-z0-9._-]{0,31}$ ]]; then
    echo "'$1' cannot name a command: letters, digits, dots, dashes and underscores" >&2
    return 1
  fi

  if [ "$1" = "amneziageo" ]; then
    taken="$TOOL"
  elif [ "$(readlink "$link" || true)" = "$TOOL" ]; then
    return 0
  elif [ -e "$link" ] || [ -L "$link" ]; then
    taken="$link"
  elif compgen -b -k -- "$1" | grep -qxF -- "$1"; then
    taken="a command of the shell"
  else
    taken="$(type -P "$1" || true)"
  fi

  if [ -n "$taken" ]; then
    echo "$1 is taken: $taken" >&2
    return 1
  fi
}

# Prints the first name of the short command no command holds: the name given, else it with a number after it.
free_shortcut() {
  local name="$1" number=1
  while ! shortcut_ok "$name" 2> /dev/null; do
    name="$1$number"
    number=$((number + 1))
  done

  printf '%s' "$name"
}

# Prints the short commands that lead to the client.
shortcuts() {
  local link
  for link in "$LINK_DIR"/*; do
    if [ -L "$link" ] && [ "${link##*/}" != "amneziageo" ] && [ "$(readlink "$link")" = "$TOOL" ]; then
      printf '%s\n' "${link##*/}"
    fi
  done
}

# Asks on the terminal for the short command and prints its name, nothing for none.
ask_shortcut() {
  local answer="" offered name
  if ! on_terminal; then
    return 0
  fi

  while [ "$answer" != "yes" ]; do
    answer="$(ask "Put a short command for amneziageo? [Y/n]" y)" || return 0
    case "${answer,,}" in
      y|yes) answer="yes" ;;
      n|no) return 0 ;;
      *) echo "answer y or n" >&2 ;;
    esac
  done

  offered="$(free_shortcut ag-c)"
  while name="$(ask "Name of the short command [$offered]:" "$offered")"; do
    if shortcut_ok "$name"; then
      printf '%s' "$name"
      return 0
    fi
  done

  return 0
}

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

# The short command: the one named, one already put on, or the one asked for on the terminal.
HELD="$(shortcuts | head -1)"
if [ -n "$SHORTCUT" ]; then
  shortcut_ok "$SHORTCUT" || SHORTCUT=""
elif [ -n "$HELD" ]; then
  SHORTCUT="$HELD"
elif [ "$ASK_SHORTCUT" = "yes" ]; then
  SHORTCUT="$(ask_shortcut)"
fi

if [ -n "$SHORTCUT" ] && [ "$(readlink "$LINK_DIR/$SHORTCUT" || true)" != "$TOOL" ] \
  && ! $SUDO ln -s "$TOOL" "$LINK_DIR/$SHORTCUT"; then
  SHORTCUT=""
fi

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
  client   : amneziageo${SHORTCUT:+, $SHORTCUT}

Next steps:
  sudo amneziageo geo download                       # seed and download the geo bases
  sudo amneziageo config import work --file work.conf
  sudo amneziageo up work
  sudo amneziageo settings set survive-reboot on     # start at boot and connect again
  sudo amneziageo settings set periodic-reconnect-enabled on
  sudo amneziageo tui                                # full-screen console
  sudo amneziageo stop                               # stop the agent; 'start' brings it back
EOF
