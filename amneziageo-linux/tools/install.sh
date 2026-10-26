#!/usr/bin/env bash
# Installs AmneziaGeo from its GitHub releases: picks the packages for this architecture and hands them to apt.
set -euo pipefail

REPO="bor-project/amneziageo"
TAG=""
PRERELEASE="no"
WITH_GUI="yes"
KEEP="no"
WORK=""
SHORTCUT=""
ASK_SHORTCUT="yes"
TOOL="/usr/bin/amneziageo"
LINK_DIR="/usr/local/bin"

usage() {
  cat <<EOF
usage: install.sh [options]

  --no-gui            install only the agent and the console client (headless server)
  --tag v1.2.3.4      install this release instead of the newest one
  --prerelease        consider the betas, not just the latest stable release
  --test              consider the test builds and the betas as well
  --repo owner/name   pull from another repository (default: $REPO)
  --keep              keep the downloaded packages in the working directory
  --shortcut <name>   put the short command <name> for amneziageo without asking
  --no-shortcut       put no short command and ask nothing
  --help              this help

Downloads the amneziageo and amneziageo-gui packages built for this machine, checks them against the
SHA-256 published in update.json and installs them with apt. Needs root:

  curl -fsSL https://raw.githubusercontent.com/$REPO/master/amneziageo-linux/tools/install.sh | sudo bash

The agent starts right away and checks for later versions by itself; it starts at boot while the
survive-reboot setting is on.

On a terminal it asks whether to put a short command for the console client, ag-c by default: a link
in /usr/local/bin to /usr/bin/amneziageo. A name another command holds is left alone, and removing
the package takes the link off.
EOF
}

while [ $# -gt 0 ]; do
  case "$1" in
    --no-gui) WITH_GUI="no"; shift ;;
    --gui) WITH_GUI="yes"; shift ;;
    --tag) TAG="$2"; shift 2 ;;
    --prerelease) PRERELEASE="beta"; shift ;;
    --test) PRERELEASE="test"; shift ;;
    --repo) REPO="$2"; shift 2 ;;
    --keep) KEEP="yes"; shift ;;
    --shortcut) SHORTCUT="$2"; shift 2 ;;
    --no-shortcut) ASK_SHORTCUT="no"; shift ;;
    --help|-h) usage; exit 0 ;;
    *) echo "unknown argument '$1'" >&2; usage >&2; exit 2 ;;
  esac
done

if [ "$(id -u)" != "0" ]; then
  echo "install.sh installs system packages and needs root: run it with sudo" >&2
  exit 1
fi

for tool in dpkg apt-get sha256sum; do
  if ! command -v "$tool" >/dev/null 2>&1; then
    echo "$tool is not in PATH; this installer serves Debian and Ubuntu" >&2
    exit 1
  fi
done

# One downloader, whichever the machine has.
if command -v curl >/dev/null 2>&1; then
  fetch() { curl -fsSL "$1" -o "$2"; }
elif command -v wget >/dev/null 2>&1; then
  fetch() { wget -qO "$2" "$1"; }
else
  echo "neither curl nor wget is in PATH" >&2
  exit 1
fi

ARCH="$(dpkg --print-architecture)"
case "$ARCH" in
  amd64|arm64) ;;
  *) echo "no AmneziaGeo package is published for $ARCH (amd64 and arm64 are)" >&2; exit 1 ;;
esac

WORK="$(mktemp -d)"
if [ "$KEEP" = "no" ]; then
  trap 'rm -rf "$WORK"' EXIT
fi

api() { echo "https://api.github.com/repos/$REPO/$1"; }

# Release to install: the named tag, the newest tag including the betas (and the test builds with --test), or the
# latest stable one.
if [ -n "$TAG" ]; then
  RELEASE="$(api "releases/tags/$TAG")"
elif [ "$PRERELEASE" != "no" ]; then
  fetch "$(api 'releases?per_page=20')" "$WORK/releases.json"
  TAG="$(grep -o '"tag_name": *"[^"]*"' "$WORK/releases.json" | cut -d'"' -f4 \
          | { if [ "$PRERELEASE" = "test" ]; then cat; else grep -v -- '-test' || true; fi; } | sort -V | tail -1)"
  if [ -z "$TAG" ]; then
    echo "$REPO has published no release" >&2
    exit 1
  fi
  RELEASE="$(api "releases/tags/$TAG")"
else
  RELEASE="$(api 'releases/latest')"
fi

fetch "$RELEASE" "$WORK/release.json"
grep -o '"browser_download_url": *"[^"]*"' "$WORK/release.json" | cut -d'"' -f4 > "$WORK/assets"
if [ ! -s "$WORK/assets" ]; then
  echo "the release carries no assets: $RELEASE" >&2
  exit 1
fi

# The URL of an asset whose name matches the pattern.
url_of() {
  grep -E "/$1\$" "$WORK/assets" | head -1
}

CORE_URL="$(url_of "amneziageo_[^/]*_${ARCH}\.deb" || true)"
if [ -z "$CORE_URL" ]; then
  echo "the release carries no amneziageo package for $ARCH" >&2
  exit 1
fi

FILES=("$(basename "$CORE_URL")")
URLS=("$CORE_URL")
if [ "$WITH_GUI" = "yes" ]; then
  GUI_URL="$(url_of "amneziageo-gui_[^/]*_${ARCH}\.deb" || true)"
  if [ -z "$GUI_URL" ]; then
    echo "the release carries no amneziageo-gui package for $ARCH; installing the agent alone" >&2
  else
    FILES+=("$(basename "$GUI_URL")")
    URLS+=("$GUI_URL")
  fi
fi

# The manifest carries the SHA-256 of every published file; without it the packages install unverified.
MANIFEST_URL="$(url_of 'update\.json' || true)"
if [ -n "$MANIFEST_URL" ]; then
  fetch "$MANIFEST_URL" "$WORK/update.json"
  tr -d ' \n' < "$WORK/update.json" > "$WORK/update.compact"
fi

sha_of() {
  if [ ! -s "$WORK/update.compact" ]; then
    return 0
  fi

  grep -o "\"name\":\"$1\"[^}]*" "$WORK/update.compact" | grep -o '"sha256":"[^"]*"' | cut -d'"' -f4 | head -1
}

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

# Tells whether a word can name the short command: no command holds it, or it leads to amneziageo already.
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

# Prints the short commands that lead to amneziageo.
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

echo "== AmneziaGeo for $ARCH from $REPO =="
for i in "${!URLS[@]}"; do
  name="${FILES[$i]}"
  echo "   downloading $name"
  fetch "${URLS[$i]}" "$WORK/$name"
  expected="$(sha_of "$name")"
  if [ -n "$expected" ]; then
    actual="$(sha256sum "$WORK/$name" | cut -d' ' -f1)"
    if [ "$actual" != "$expected" ]; then
      echo "$name does not match the published checksum; nothing was installed" >&2
      exit 1
    fi
  else
    echo "   no published checksum for $name; installing it unverified" >&2
  fi
done

PATHS=()
for name in "${FILES[@]}"; do
  PATHS+=("$WORK/$name")
done

echo "== installing =="
DEBIAN_FRONTEND=noninteractive apt-get install -y --allow-downgrades --reinstall "${PATHS[@]}"

# Packages older than this script leave the agent stopped when they are reinstalled after a remove.
if [ -d /run/systemd/system ]; then
  systemctl start amneziageo-agent.service || true
fi

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
  && ! ln -s "$TOOL" "$LINK_DIR/$SHORTCUT"; then
  SHORTCUT=""
fi

cat <<EOF

Installed: ${FILES[*]}
The agent runs as the amneziageo-agent service and checks for later versions itself.
  amneziageo status                               # console client
  amneziageo tui                                  # full-screen console
  sudo amneziageo start                           # start the agent
  sudo amneziageo stop                            # stop the agent and drop the tunnel
  sudo amneziageo settings set survive-reboot on  # start at boot and connect again
EOF
if [ -n "${GUI_URL:-}" ]; then
  echo "  amneziageo-gui                                  # desktop interface"
fi
if [ -n "$SHORTCUT" ]; then
  printf '  %-48s# %s\n' "$SHORTCUT status" "the console client by its short command"
fi

if [ "$KEEP" = "yes" ]; then
  echo "Packages kept in $WORK"
fi
