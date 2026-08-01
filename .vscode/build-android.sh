#!/usr/bin/env bash
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$HERE/.." && pwd)"
SETTINGS="$HERE/debug.android.jsonc"

if [ ! -f "$SETTINGS" ]; then
  echo "Creating default $SETTINGS"
  cat > "$SETTINGS" <<'EOF'
{
  // Deploy target.
  // Available: emulator | device
  "target": "emulator",

  // AVD name when target = emulator.
  // List: `avdmanager list avd`  or  Android Studio > Device Manager.
  "emulator": "Pixel_8_Pro",

  // Device serial when target = device.
  // List: `adb devices`.   Empty = the single connected device.
  "device": "",

  // Build configuration.
  // Available: Debug | Release
  "configuration": "Debug"
}
EOF
fi

read_setting() {
  sed -E 's#//.*$##' "$SETTINGS" \
    | grep -oE "\"$1\"[[:space:]]*:[[:space:]]*\"[^\"]*\"" \
    | head -n1 | sed -E 's/.*:[[:space:]]*"([^"]*)".*/\1/'
}

TARGET="${TARGET:-$(read_setting target)}"
EMULATOR_AVD="${EMULATOR_AVD:-$(read_setting emulator)}"
DEVICE="${DEVICE:-$(read_setting device)}"
CONFIGURATION="${CONFIGURATION:-$(read_setting configuration)}"
: "${TARGET:=emulator}"
: "${CONFIGURATION:=Debug}"

SDK="${ANDROID_SDK_ROOT:-${ANDROID_HOME:-}}"
if [ -z "$SDK" ]; then
  for c in "$HOME/Library/Android/sdk" "$HOME/Android/Sdk" "$HOME/android-sdk"; do
    [ -d "$c" ] && SDK="$c" && break
  done
fi
[ -n "$SDK" ] || { echo "Android SDK not found. Set ANDROID_SDK_ROOT or ANDROID_HOME." >&2; exit 1; }

ADB_TARGET="-d"
if [ "$TARGET" = "emulator" ]; then
  bash "$HERE/start-android-emulator.sh" --sdk "$SDK" --avd "$EMULATOR_AVD" --no-snapshot-load
  ADB_TARGET="-e"
elif [ -n "$DEVICE" ]; then
  ADB_TARGET="-s $DEVICE"
fi

PROJECT="$ROOT/amneziageo-android/AmneziaGeo.Android.Ui/AmneziaGeo.Android.Ui.csproj"

echo "Deploying Android: Target=$TARGET Configuration=$CONFIGURATION AdbTarget=$ADB_TARGET"
dotnet build "$PROJECT" -f net10.0-android -t:Run \
  "-p:Configuration=$CONFIGURATION" \
  "-p:AdbTarget=$ADB_TARGET" \
  -p:AndroidAttachDebugger=true \
  -p:AndroidSdbHostPort=10000 \
  -p:AndroidSdbTargetPort=10000 \
  "-p:AndroidSdkDirectory=$SDK" \
  -v minimal -nologo
