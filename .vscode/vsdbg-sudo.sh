#!/usr/bin/env bash
# Runs the C# debugger backend as root so it can launch and attach to the elevated agent.
set -euo pipefail

VSDBG="$(ls -d "$HOME"/.vscode/extensions/ms-dotnettools.csharp-*/.debugger/vsdbg 2>/dev/null | sort -V | tail -n1)"
if [ -z "$VSDBG" ]; then
  echo "vsdbg not found. Install the C# extension: code --install-extension ms-dotnettools.csharp" >&2
  exit 1
fi

# sudo drops the private dotnet from PATH, and vsdbg needs it to launch the agent.
DOTNET_DIR="$(getent passwd "$(id -un)" | cut -d: -f6)/.dotnet"

# VS Code appends either the debugger command or a process-listing command; only the first is rewritten.
if [ "${1:-}" = "vsdbg" ]; then
  shift
  exec sudo -n env "PATH=$DOTNET_DIR:$PATH" "DOTNET_ROOT=$DOTNET_DIR" "$VSDBG" "$@"
fi

exec sudo -n "$@"
