#!/usr/bin/env bash
# Installs AmneziaGeo from its GitHub releases: picks the packages for this architecture and hands them to apt.
set -euo pipefail

REPO="bor-project/amneziageo"
TAG=""
PRERELEASE="no"
WITH_GUI="yes"
KEEP="no"
WORK=""

usage() {
  cat <<EOF
usage: install.sh [options]

  --no-gui            install only the agent and the console client (headless server)
  --tag v1.2.3.4      install this release instead of the newest one
  --prerelease        consider prereleases, not just the latest stable release
  --repo owner/name   pull from another repository (default: $REPO)
  --keep              keep the downloaded packages in the working directory
  --help              this help

Downloads the amneziageo and amneziageo-gui packages built for this machine, checks them against the
SHA-256 published in update.json and installs them with apt. Needs root:

  curl -fsSL https://raw.githubusercontent.com/$REPO/master/amneziageo-linux/tools/install.sh | sudo bash

The agent starts and is enabled at boot right away; it checks for later versions by itself.
EOF
}

while [ $# -gt 0 ]; do
  case "$1" in
    --no-gui) WITH_GUI="no"; shift ;;
    --gui) WITH_GUI="yes"; shift ;;
    --tag) TAG="$2"; shift 2 ;;
    --prerelease) PRERELEASE="yes"; shift ;;
    --repo) REPO="$2"; shift 2 ;;
    --keep) KEEP="yes"; shift ;;
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

# Release to install: the named tag, the newest tag including prereleases, or the latest stable one.
if [ -n "$TAG" ]; then
  RELEASE="$(api "releases/tags/$TAG")"
elif [ "$PRERELEASE" = "yes" ]; then
  fetch "$(api 'releases?per_page=20')" "$WORK/releases.json"
  TAG="$(grep -o '"tag_name": *"[^"]*"' "$WORK/releases.json" | cut -d'"' -f4 | sort -V | tail -1)"
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

cat <<EOF

Installed: ${FILES[*]}
The agent runs as the amneziageo-agent service and is enabled at boot; it checks for later versions itself.
  amneziageo status              # console client
  amneziageo-gui                 # desktop interface
EOF

if [ "$KEEP" = "yes" ]; then
  echo "Packages kept in $WORK"
fi
