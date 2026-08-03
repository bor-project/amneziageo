#!/usr/bin/env bash
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$HERE/.." && pwd)"
SETTINGS="$HERE/debug.android.jsonc"

if [ ! -f "$SETTINGS" ]; then
  echo "Creating default $SETTINGS"
  cat > "$SETTINGS" <<'EOF'
{
  // Deploy targets. Takes the first entry with isCurrent = true.
  "targets": [
    {
      "isCurrent": false,
      "name": "Device",

      // Deploy target.
      // Available: emulator | device
      "target": "device",

      // Device serial.
      // List: `adb devices`.   Empty = the single connected device.
      "device": "",

      // Native ABIs to build, semicolon-separated.
      // Available: android-arm64 | android-x64 | android-arm | android-x86.   Empty = as declared in the csproj.
      "runtimeIdentifiers": "android-arm64"
    },
    {
      "isCurrent": false,
      "name": "Phone emulator",
      "target": "emulator",

      // AVD name.
      // List: `avdmanager list avd`  or  Android Studio > Device Manager.
      "emulator": "Pixel_8_Pro",
      "runtimeIdentifiers": "android-x64"
    },
    {
      "isCurrent": true,
      "name": "Android TV emulator",
      "target": "emulator",
      "emulator": "Television_1080p_x64",
      "runtimeIdentifiers": "android-x64"
    }
  ],

  // Build configuration.
  // Available: Debug | Release
  "configuration": "Debug"
}
EOF
fi

strip_comments() {
  sed -E 's#//.*$##' "$SETTINGS"
}

read_setting() {
  strip_comments \
    | grep -oE "\"$1\"[[:space:]]*:[[:space:]]*\"[^\"]*\"" \
    | head -n1 | sed -E 's/.*:[[:space:]]*"([^"]*)".*/\1/' || true
}

current_target() {
  strip_comments \
    | tr -d '\n' \
    | sed -E 's#^.*"targets"[[:space:]]*:[[:space:]]*\[##; s#\].*$##' \
    | tr '{' '\n' \
    | grep -E '"isCurrent"[[:space:]]*:[[:space:]]*true' \
    | head -n1 || true
}

target_setting() {
  printf '%s' "$CURRENT" \
    | grep -oE "\"$1\"[[:space:]]*:[[:space:]]*\"[^\"]*\"" \
    | head -n1 | sed -E 's/.*:[[:space:]]*"([^"]*)".*/\1/' || true
}

CURRENT="$(current_target)"
[ -n "$CURRENT" ] || { echo "No target with \"isCurrent\": true in $SETTINGS" >&2; exit 1; }

NAME="$(target_setting name)"
TARGET="${TARGET:-$(target_setting target)}"
EMULATOR_AVD="${EMULATOR_AVD:-$(target_setting emulator)}"
DEVICE="${DEVICE:-$(target_setting device)}"
RUNTIME_IDENTIFIERS="${RUNTIME_IDENTIFIERS:-$(target_setting runtimeIdentifiers)}"
CONFIGURATION="${CONFIGURATION:-$(read_setting configuration)}"
: "${CONFIGURATION:=Debug}"
: "${NAME:=$TARGET}"

case "$TARGET" in
  emulator|device) ;;
  *) echo "Target '$TARGET' is not supported. Available: emulator | device." >&2; exit 1 ;;
esac

if [ "$TARGET" = "emulator" ]; then
  WHERE="$EMULATOR_AVD"
elif [ -n "$DEVICE" ]; then
  WHERE="$DEVICE"
else
  WHERE="the only connected device"
fi

if [ -n "$RUNTIME_IDENTIFIERS" ]; then
  ABIS="$RUNTIME_IDENTIFIERS"
else
  ABIS="as in csproj"
fi

echo "Target: $NAME -> $TARGET $WHERE ($CONFIGURATION, $ABIS)"

SDK="${ANDROID_SDK_ROOT:-${ANDROID_HOME:-}}"
if [ -z "$SDK" ]; then
  for c in "$HOME/Library/Android/sdk" "$HOME/Android/Sdk" "$HOME/android-sdk"; do
    [ -d "$c" ] && SDK="$c" && break
  done
fi
[ -n "$SDK" ] || { echo "Android SDK not found. Set ANDROID_SDK_ROOT or ANDROID_HOME." >&2; exit 1; }

ADB_TARGET="-d"
if [ "$TARGET" = "emulator" ]; then
  [ -n "$EMULATOR_AVD" ] || { echo "Target '$NAME' has no emulator AVD name." >&2; exit 1; }
  bash "$HERE/start-android-emulator.sh" --sdk "$SDK" --avd "$EMULATOR_AVD" --no-snapshot-load
  ADB_TARGET="-e"
elif [ -n "$DEVICE" ]; then
  ADB_TARGET="-s $DEVICE"
fi

PROJECT="$ROOT/amneziageo-android/AmneziaGeo.Android.Ui/AmneziaGeo.Android.Ui.csproj"

BUILD_ARGS=(
  "-p:Configuration=$CONFIGURATION"
  "-p:AdbTarget=$ADB_TARGET"
  "-p:AndroidAttachDebugger=true"
  "-p:AndroidSdbHostPort=10000"
  "-p:AndroidSdbTargetPort=10000"
  "-p:AndroidSdkDirectory=$SDK"
)

if [ -n "$RUNTIME_IDENTIFIERS" ]; then
  BUILD_ARGS+=("-p:AndroidRuntimeIdentifiers=$RUNTIME_IDENTIFIERS")
fi

echo "Deploying Android: Name=$NAME Target=$TARGET Configuration=$CONFIGURATION AdbTarget=$ADB_TARGET Abis=$ABIS"
dotnet build "$PROJECT" -f net10.0-android -t:Run "${BUILD_ARGS[@]}" -v minimal -nologo
