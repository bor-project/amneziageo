#!/usr/bin/env bash
# Runs the C# debugger backend, elevated when the current debug.linux.jsonc target is.
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SETTINGS="$HERE/debug.linux.jsonc"

VSDBG="$(ls -d "$HOME"/.vscode/extensions/ms-dotnettools.csharp-*/.debugger/vsdbg 2>/dev/null | sort -V | tail -n1)"
if [ -z "$VSDBG" ]; then
  echo "vsdbg not found. Install the C# extension: code --install-extension ms-dotnettools.csharp" >&2
  exit 1
fi

current_target() {
  [ -f "$SETTINGS" ] || return 0
  sed -E 's#//.*$##' "$SETTINGS" \
    | tr -d '\n' \
    | sed -E 's#^.*"targets"[[:space:]]*:[[:space:]]*\[##; s#\].*$##' \
    | tr '{' '\n' \
    | grep -E '"isCurrent"[[:space:]]*:[[:space:]]*true' \
    | head -n1 || true
}

ELEVATE="$(current_target | grep -oE '"sudo"[[:space:]]*:[[:space:]]*(true|false)' | head -n1 | sed -E 's/.*:[[:space:]]*//' || true)"

# sudo drops the private dotnet from PATH, and vsdbg needs it to launch the agent.
DOTNET_DIR="$(getent passwd "$(id -un)" | cut -d: -f6)/.dotnet"

# VS Code appends either the debugger command or a process-listing command; only the first is rewritten.
if [ "${1:-}" = "vsdbg" ]; then
  shift
  if [ "$ELEVATE" = "true" ]; then
    exec sudo -n env "PATH=$DOTNET_DIR:$PATH" "DOTNET_ROOT=$DOTNET_DIR" "$VSDBG" "$@"
  fi

  exec env "PATH=$DOTNET_DIR:$PATH" "DOTNET_ROOT=$DOTNET_DIR" "$VSDBG" "$@"
fi

if [ "$ELEVATE" = "true" ]; then
  exec sudo -n "$@"
fi

exec "$@"
