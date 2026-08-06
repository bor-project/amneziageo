#!/usr/bin/env bash
# Builds the installable AmneziaGeo Android APK into dist/, mirroring build-apk.ps1 and the Windows
# installer layout (dist/AmneziaGeo-<version>-<target>.<ext>).
#
# The APK consumes the native Go engine (AmneziaGeo.Android.Engine/native/<abi>/libamneziawg-go.so);
# build it first with build-engine-android.sh.
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ANDROID_DIR="$(cd "$HERE/.." && pwd)"
UI="$ANDROID_DIR/AmneziaGeo.Android.Ui/AmneziaGeo.Android.Ui.csproj"
DIST="$HERE/dist"

CONFIGURATION="Release"
VERSION=""
ABIS=""

usage() {
  cat <<EOF
usage: build-apk.sh [options]

  --config <name>     Release or Debug (default: $CONFIGURATION)
  --version N.N.N.N   package version (default: 0.0.1.<commit count>); the last field is the versionCode
  --abi <list>        runtime identifiers, comma separated: android-arm, android-arm64, android-x64
                      (default: every ABI the project declares)
  --help              this help

Both configurations install as-is; Release is AOT-compiled and starts about twice as fast on a weak TV,
so it is the default. Signing takes the SDK debug key unless ANDROID_KEYSTORE names a keystore, in which
case ANDROID_KEY_ALIAS, ANDROID_STORE_PASS and ANDROID_KEY_PASS have to be set as well.
EOF
}

while [ $# -gt 0 ]; do
  case "$1" in
    --config|-c) CONFIGURATION="$2"; shift 2 ;;
    --version|-v) VERSION="$2"; shift 2 ;;
    --abi) ABIS="$(printf '%s' "$2" | tr ',' ';')"; shift 2 ;;
    --help|-h) usage; exit 0 ;;
    *) echo "unknown argument '$1'" >&2; usage >&2; exit 2 ;;
  esac
done

case "$CONFIGURATION" in
  Release|Debug) ;;
  *) echo "invalid configuration '$CONFIGURATION' (expected Release or Debug)" >&2; exit 2 ;;
esac

if [ -z "$VERSION" ]; then
  VERSION="0.0.1.$(git -C "$ANDROID_DIR" rev-list --count HEAD 2>/dev/null || echo 0)"
fi

if ! printf '%s' "$VERSION" | grep -Eq '^[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+$'; then
  echo "invalid version '$VERSION' (expected N.N.N.N)" >&2
  exit 2
fi

CODE="${VERSION##*.}"
if [ "$CODE" -lt 1 ]; then
  CODE=1
fi

echo "== apk version $VERSION (versionCode $CODE), configuration $CONFIGURATION =="
mkdir -p "$DIST"

args=(
  "$UI"
  -c "$CONFIGURATION"
  -f net10.0-android
  -t:SignAndroidPackage
  -p:AndroidPackageFormat=apk
  -p:EmbedAssembliesIntoApk=true
  "-p:ApplicationDisplayVersion=$VERSION"
  "-p:ApplicationVersion=$CODE"
)

if [ -n "$ABIS" ]; then
  args+=("-p:AndroidRuntimeIdentifiers=$ABIS")
fi

# Release keystore from the environment; the passwords stay in variables, off the command line.
if [ -n "${ANDROID_KEYSTORE:-}" ]; then
  echo "   signing with $ANDROID_KEYSTORE"
  args+=(
    -p:AndroidKeyStore=true
    "-p:AndroidSigningKeyStore=$ANDROID_KEYSTORE"
    "-p:AndroidSigningKeyAlias=${ANDROID_KEY_ALIAS:?ANDROID_KEY_ALIAS is not set}"
    -p:AndroidSigningStorePass=env:ANDROID_STORE_PASS
    -p:AndroidSigningKeyPass=env:ANDROID_KEY_PASS
  )
else
  echo '   signing with the SDK debug key - a build from another machine will not upgrade over it'
fi

echo '== build + sign APK =='
dotnet build "${args[@]}" -v minimal --nologo

BIN="$ANDROID_DIR/AmneziaGeo.Android.Ui/bin/$CONFIGURATION/net10.0-android"
apk="$(find "$BIN" -name '*-Signed.apk' -printf '%T@ %p\n' 2>/dev/null | sort -nr | head -1 | cut -d' ' -f2-)"
if [ -z "$apk" ]; then
  apk="$(find "$BIN" -name '*.apk' -printf '%T@ %p\n' 2>/dev/null | sort -nr | head -1 | cut -d' ' -f2-)"
fi
if [ -z "$apk" ]; then
  echo "APK not found under $BIN after build." >&2
  exit 1
fi

name="AmneziaGeo-$VERSION-android.apk"
cp -f "$apk" "$DIST/$name"

echo
echo "== result (dist) =="
size="$(stat -c %s "$DIST/$name" 2>/dev/null || stat -f %z "$DIST/$name")"
printf '   %s  %s MB\n' "$DIST/$name" "$((size / 1048576))"
