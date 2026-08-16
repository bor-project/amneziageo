#!/usr/bin/env bash
# Builds the Linux tree and starts the current debug.linux.jsonc target, held until "Debug Linux" attaches.
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$HERE/.." && pwd)"
SETTINGS="$HERE/debug.linux.jsonc"

# "Debug Linux" attaches by this name, whatever component the target names.
DEBUG_NAME="amneziageo-dbg"

# Also creates $SETTINGS when it is missing.
bash "$HERE/build-linux.sh"

strip_comments() {
  sed -E 's#//.*$##' "$SETTINGS"
}

value_of() {
  grep -oE "\"$1\"[[:space:]]*:[[:space:]]*(\"[^\"]*\"|true|false)" \
    | head -n1 \
    | sed -E "s/^\"$1\"[[:space:]]*:[[:space:]]*//; s/^\"//; s/\"$//" || true
}

current_target() {
  strip_comments \
    | tr -d '\n' \
    | sed -E 's#^.*"targets"[[:space:]]*:[[:space:]]*\[##; s#\].*$##' \
    | tr '{' '\n' \
    | grep -E '"isCurrent"[[:space:]]*:[[:space:]]*true' \
    | head -n1 || true
}

CURRENT="$(current_target)"
[ -n "$CURRENT" ] || { echo "No target with \"isCurrent\": true in $SETTINGS" >&2; exit 1; }

target_setting() {
  printf '%s' "$CURRENT" | value_of "$1"
}

NAME="$(target_setting name)"
COMPONENT="${COMPONENT:-$(target_setting component)}"
ARGS="${ARGS:-$(target_setting args)}"
ELEVATE="${SUDO:-$(target_setting sudo)}"
CONFIGURATION="${CONFIGURATION:-$(strip_comments | value_of configuration)}"
: "${CONFIGURATION:=Debug}"
: "${COMPONENT:=agent}"
: "${NAME:=$COMPONENT}"

case "$COMPONENT" in
  agent)
    DIR="$ROOT/amneziageo-linux/AmneziaGeo.Linux.App/bin/$CONFIGURATION/net10.0"
    EXE="AmneziaGeo.Linux.App"
    ;;
  ui)
    DIR="$ROOT/amneziageo-linux/AmneziaGeo.Linux.Ui/bin/$CONFIGURATION/net10.0"
    EXE="AmneziaGeo.Linux.Ui"
    ;;
  console)
    DIR="$ROOT/amneziageo-linux/AmneziaGeo.Linux.Cli/bin/$CONFIGURATION/net10.0"
    EXE="amneziageo"
    ;;
  *)
    echo "Component '$COMPONENT' is not supported. Available: agent | ui | console." >&2
    exit 1
    ;;
esac

if [ ! -x "$DIR/$EXE" ]; then
  echo "Not built: $DIR/$EXE" >&2
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

DOTNET_ROOT="$(dirname "$(readlink -f "$DOTNET")")"
export DOTNET_ROOT

# One launcher name for every component, so a single attach configuration finds it.
rm -f "$DIR/$DEBUG_NAME"
cp "$DIR/$EXE" "$DIR/$DEBUG_NAME"

if [ "$ELEVATE" = "true" ]; then
  WHO="elevated"
else
  WHO="as $(id -un)"
fi

echo "Target: $NAME -> $COMPONENT $ARGS ($CONFIGURATION, $WHO)"

RUN_ENV=("DOTNET_ROOT=$DOTNET_ROOT" "AMNEZIAGEO_WAIT_DEBUGGER=${WAIT_DEBUGGER:-1}")
if [ -n "${AMNEZIAGEO_DATA:-}" ]; then
  RUN_ENV+=("AMNEZIAGEO_DATA=$AMNEZIAGEO_DATA")
fi

# shellcheck disable=SC2086
if [ "$ELEVATE" = "true" ] && [ "$(id -u)" -ne 0 ]; then
  exec sudo env "${RUN_ENV[@]}" "$DIR/$DEBUG_NAME" $ARGS
fi

# shellcheck disable=SC2086
exec env "${RUN_ENV[@]}" "$DIR/$DEBUG_NAME" $ARGS
