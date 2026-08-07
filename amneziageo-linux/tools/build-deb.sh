#!/usr/bin/env bash
# Builds the AmneziaGeo Debian packages: the agent with its console client, and the desktop interface.
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$HERE/../.." && pwd)"
PACKAGING="$ROOT/amneziageo-linux/packaging"
ENGINE_DIR="$ROOT/amneziageo-android/amneziawg-go"
ICONS="$ROOT/amneziageo-android/AmneziaGeo.Android.Ui/Resources"

ARCHES="amd64"
VERSION=""
# ICU packages the .NET runtime can load: the first one present satisfies the dependency.
ICU="libicu78 | libicu77 | libicu76 | libicu74 | libicu72 | libicu71 | libicu70"
OUT="$ROOT/dist"
CONFIGURATION="Release"
WITH_GUI="yes"
# Release manifest the packaged agent checks itself against; empty leaves the update section hidden.
UPDATE_URL="https://github.com/bor-project/amneziageo/releases/latest/download/update.json"
PRERELEASE=""

usage() {
  cat <<EOF
usage: build-deb.sh [options]

  --arch <list>       target architectures, comma separated: amd64, arm64 (default: $ARCHES)
  --version N.N.N.N   package version (default: 0.0.1.<commit count>)
  --out <dir>         where the packages land (default: $OUT)
  --debug             build the Debug configuration
  --no-gui            skip the amneziageo-gui package
  --update-url <url>  update manifest the agent checks against (empty turns the check off)
  --prerelease        keep the build on the prerelease channel
  --help              this help

One build gives two packages per architecture: amneziageo (agent, engine, console client and the
systemd unit, no desktop libraries) and amneziageo-gui (the desktop interface, which depends on
the same version of amneziageo). Building needs the .NET SDK and the Go toolchain; the packages
are self-contained, so the target machine needs neither.

  sudo apt install ./amneziageo_<version>_amd64.deb ./amneziageo-gui_<version>_amd64.deb
EOF
}

while [ $# -gt 0 ]; do
  case "$1" in
    --arch) ARCHES="$(printf '%s' "$2" | tr ',' ' ')"; shift 2 ;;
    --version) VERSION="$2"; shift 2 ;;
    --out) OUT="$2"; shift 2 ;;
    --debug) CONFIGURATION="Debug"; shift ;;
    --no-gui) WITH_GUI="no"; shift ;;
    --update-url) UPDATE_URL="$2"; shift 2 ;;
    --prerelease) PRERELEASE="1"; shift ;;
    --help|-h) usage; exit 0 ;;
    *) echo "unknown argument '$1'" >&2; usage >&2; exit 2 ;;
  esac
done

if [ -z "$VERSION" ]; then
  VERSION="0.0.1.$(git -C "$ROOT" rev-list --count HEAD 2>/dev/null || echo 0)"
fi

if ! printf '%s' "$VERSION" | grep -Eq '^[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+$'; then
  echo "invalid version '$VERSION' (expected N.N.N.N)" >&2
  exit 2
fi

for tool in dotnet dpkg-deb; do
  if ! command -v "$tool" >/dev/null 2>&1; then
    echo "$tool is not in PATH" >&2
    exit 1
  fi
done

# Runtime identifier of a Debian architecture.
rid_of() {
  case "$1" in
    amd64) echo "linux-x64" ;;
    arm64) echo "linux-arm64" ;;
    *) echo "unsupported architecture '$1' (expected amd64 or arm64)" >&2; return 1 ;;
  esac
}

# Emits the AmneziaWG engine for the target architecture.
build_engine() {
  local goarch="$1" out="$2"
  local go_bin
  go_bin="$(command -v go || true)"
  if [ -z "$go_bin" ] && [ -n "${GOROOT:-}" ] && [ -x "$GOROOT/bin/go" ]; then
    go_bin="$GOROOT/bin/go"
  fi

  if [ -n "$go_bin" ]; then
    ( cd "$ENGINE_DIR" && GOOS=linux GOARCH="$goarch" CGO_ENABLED=0 "$go_bin" build -o "$out" )
    return 0
  fi

  if [ "$goarch" = "$(dpkg --print-architecture)" ] && [ -x "$ENGINE_DIR/amneziawg-go" ]; then
    echo "Go is not in PATH; packing the prebuilt $ENGINE_DIR/amneziawg-go"
    cp "$ENGINE_DIR/amneziawg-go" "$out"
    return 0
  fi

  cat >&2 <<EOF
Go toolchain not found in PATH, so the AmneziaWG engine cannot be built for $goarch.
amneziawg-go needs the Go version pinned in $ENGINE_DIR/go.mod (pure Go - no cgo, no C toolchain).
Install it, then re-run this script:
  sudo apt install golang-go          # or: sudo snap install go --classic
EOF
  exit 1
}

# Agent, engine, console client, unit and the default settings.
stage_core() {
  local arch="$1" rid="$2" tree="$3"
  local libdir="$tree/usr/lib/amneziageo"

  mkdir -p "$libdir" "$tree/usr/bin" "$tree/usr/lib/systemd/system" "$tree/etc/default" \
           "$tree/var/lib/amneziageo" "$tree/usr/share/doc/amneziageo"

  for project in AmneziaGeo.Linux.App AmneziaGeo.Linux.Cli; do
    dotnet publish "$ROOT/amneziageo-linux/$project/$project.csproj" \
      -c "$CONFIGURATION" \
      -r "$rid" \
      --self-contained true \
      -p:Version="$VERSION" \
      -p:UpdateUrl="$UPDATE_URL" \
      -p:AllowPrerelease="$PRERELEASE" \
      -o "$libdir" \
      -v minimal \
      --nologo
  done

  build_engine "$arch" "$libdir/amneziawg-go"
  ln -sf ../lib/amneziageo/amneziageo "$tree/usr/bin/amneziageo"

  install -m 0644 "$PACKAGING/systemd/amneziageo-agent.service" "$tree/usr/lib/systemd/system/amneziageo-agent.service"
  install -m 0644 "$PACKAGING/systemd/amneziageo.default" "$tree/etc/default/amneziageo"
  install -m 0644 "$PACKAGING/deb/copyright" "$tree/usr/share/doc/amneziageo/copyright"
}

# Desktop interface, its menu entry and its icons.
stage_gui() {
  local arch="$1" rid="$2" tree="$3"
  local libdir="$tree/usr/lib/amneziageo-gui"

  mkdir -p "$libdir" "$tree/usr/bin" "$tree/usr/share/applications" "$tree/usr/share/doc/amneziageo-gui"

  dotnet publish "$ROOT/amneziageo-linux/AmneziaGeo.Linux.Ui/AmneziaGeo.Linux.Ui.csproj" \
    -c "$CONFIGURATION" \
    -r "$rid" \
    --self-contained true \
    -p:Version="$VERSION" \
    -o "$libdir" \
    -v minimal \
    --nologo

  ln -sf ../lib/amneziageo-gui/AmneziaGeo.Linux.Ui "$tree/usr/bin/amneziageo-gui"
  install -m 0644 "$PACKAGING/desktop/amneziageo.desktop" "$tree/usr/share/applications/amneziageo.desktop"

  for pair in 48x48:mdpi 72x72:hdpi 96x96:xhdpi 192x192:xxxhdpi; do
    local size="${pair%%:*}" density="${pair##*:}"
    mkdir -p "$tree/usr/share/icons/hicolor/$size/apps"
    install -m 0644 "$ICONS/mipmap-$density/appicon.png" "$tree/usr/share/icons/hicolor/$size/apps/amneziageo.png"
  done

  install -m 0644 "$PACKAGING/deb/copyright" "$tree/usr/share/doc/amneziageo-gui/copyright"
}

# Distribution modes, not the umask of the build host.
normalize() {
  local tree="$1"

  find "$tree" -type d -exec chmod 0755 {} +
  find "$tree" -type f -exec chmod 0644 {} +

  for executable in usr/lib/amneziageo/AmneziaGeo.Linux.App usr/lib/amneziageo/amneziageo \
                    usr/lib/amneziageo/amneziawg-go usr/lib/amneziageo/createdump \
                    usr/lib/amneziageo-gui/AmneziaGeo.Linux.Ui usr/lib/amneziageo-gui/createdump; do
    if [ -f "$tree/$executable" ]; then
      chmod 0755 "$tree/$executable"
    fi
  done

  if [ -d "$tree/var/lib/amneziageo" ]; then
    chmod 0700 "$tree/var/lib/amneziageo"
  fi
}

# Control files, checksums and the package itself.
pack() {
  local package="$1" arch="$2" tree="$3"
  local source="$PACKAGING/deb/$package"
  local size

  normalize "$tree"
  size="$(du -ks "$tree" | cut -f1)"
  mkdir -p "$tree/DEBIAN"
  sed -e "s/@VERSION@/$VERSION/g" -e "s/@ARCH@/$arch/g" -e "s/@SIZE@/$size/g" -e "s/@ICU@/$ICU/g" "$source/control" > "$tree/DEBIAN/control"

  for name in conffiles; do
    if [ -f "$source/$name" ]; then
      install -m 0644 "$source/$name" "$tree/DEBIAN/$name"
    fi
  done

  for name in postinst prerm postrm; do
    if [ -f "$source/$name" ]; then
      install -m 0755 "$source/$name" "$tree/DEBIAN/$name"
    fi
  done

  ( cd "$tree" && find . -type f ! -path './DEBIAN/*' -printf '%P\0' | sort -z | xargs -0 -r md5sum > DEBIAN/md5sums )
  chmod 0644 "$tree/DEBIAN/md5sums"

  dpkg-deb --root-owner-group --build "$tree" "$OUT/${package}_${VERSION}_${arch}.deb" >/dev/null
  echo "   $OUT/${package}_${VERSION}_${arch}.deb"
}

mkdir -p "$OUT"
STAGE="$(mktemp -d)"
trap 'rm -rf "$STAGE"' EXIT

for arch in $ARCHES; do
  rid="$(rid_of "$arch")"
  echo "== amneziageo $VERSION, $arch ($rid, $CONFIGURATION) =="

  core="$STAGE/$arch/amneziageo"
  stage_core "$arch" "$rid" "$core"
  pack amneziageo "$arch" "$core"

  if [ "$WITH_GUI" = "yes" ]; then
    gui="$STAGE/$arch/amneziageo-gui"
    stage_gui "$arch" "$rid" "$gui"
    pack amneziageo-gui "$arch" "$gui"
  fi
done

cat <<EOF

Install them with apt, which pulls in the shared libraries the packages name:
  sudo apt install $OUT/amneziageo_${VERSION}_<arch>.deb $OUT/amneziageo-gui_${VERSION}_<arch>.deb

The agent starts and is enabled at boot right away; the library lives in /var/lib/amneziageo.
EOF
