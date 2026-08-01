#!/usr/bin/env bash
set -euo pipefail

SDK=""
AVD="Pixel_8_Pro"
TIMEOUT=300
NO_SNAPSHOT=0

while [ $# -gt 0 ]; do
  case "$1" in
    --sdk) SDK="$2"; shift 2 ;;
    --avd) AVD="$2"; shift 2 ;;
    --timeout) TIMEOUT="$2"; shift 2 ;;
    --no-snapshot-load) NO_SNAPSHOT=1; shift ;;
    *) echo "Unknown arg: $1" >&2; exit 2 ;;
  esac
done

[ -n "$SDK" ] || { echo "--sdk is required" >&2; exit 2; }
ADB="$SDK/platform-tools/adb"
EMULATOR="$SDK/emulator/emulator"
[ -x "$ADB" ] || { echo "adb not found at $ADB" >&2; exit 1; }
[ -x "$EMULATOR" ] || { echo "emulator not found at $EMULATOR" >&2; exit 1; }

avd_of() {
  "$ADB" -s "$1" emu avd name 2>/dev/null | tr -d '\r' | grep -v '^OK$' | head -n1
}

target_serial() {
  local line serial
  while IFS= read -r line; do
    serial="${line%%$'\t'*}"
    case "$serial" in
      emulator-*)
        [ "$(avd_of "$serial")" = "$AVD" ] && { echo "$serial"; return 0; }
        ;;
    esac
  done < <("$ADB" devices | tail -n +2)
  return 1
}

serial="$(target_serial || true)"
if [ -z "${serial:-}" ]; then
  echo "Starting Android emulator '$AVD'..."
  args=(-avd "$AVD")
  [ "$NO_SNAPSHOT" -eq 1 ] && args+=(-no-snapshot-load)
  "$EMULATOR" "${args[@]}" >/dev/null 2>&1 &
fi

deadline=$(( $(date +%s) + TIMEOUT ))
while [ -z "${serial:-}" ] && [ "$(date +%s)" -lt "$deadline" ]; do
  sleep 2
  serial="$(target_serial || true)"
done
[ -n "${serial:-}" ] || { echo "Timed out waiting for emulator '$AVD'." >&2; "$ADB" devices -l >&2; exit 1; }

echo "Waiting for boot: $serial"
"$ADB" -s "$serial" wait-for-device
while [ "$(date +%s)" -lt "$deadline" ]; do
  if [ "$("$ADB" -s "$serial" shell getprop sys.boot_completed 2>/dev/null | tr -d '\r')" = "1" ]; then
    echo "Emulator ready: $serial"
    exit 0
  fi
  sleep 2
done

echo "Timed out waiting for '$AVD' to finish booting." >&2
exit 1
