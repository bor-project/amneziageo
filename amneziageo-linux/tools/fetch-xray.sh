#!/usr/bin/env bash
# Fetches the pinned Xray runtime and its notices.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
version="26.10.31"
cache="$here/../.deps/xray/$version"
case "${1:-amd64}" in
  amd64|x64|linux-x64) arch=x64; asset=Xray-linux-64.zip; sha=e4bd9119f50ae7c41658b1abd0679ae06f98bd864a2907ff33f2f2cf7081b214; machine=62 ;;
  arm64|linux-arm64) arch=arm64; asset=Xray-linux-arm64-v8a.zip; sha=1811c8f1da0c3b6dc707ebaf1c8390e987ba39859a8f0a7aadd8526157f2ec8d; machine=183 ;;
  *) echo "unsupported Xray architecture: $1" >&2; exit 2 ;;
esac
mkdir -p "$cache"
archive="$cache/$asset"
if ! printf '%s  %s\n' "$sha" "$archive" | sha256sum --check --status 2>/dev/null; then
  download="$(mktemp "$cache/.xray-download.XXXXXXXX")"
  trap 'rm -f "$download"' EXIT
  curl --fail --location --retry 3 "https://github.com/XTLS/Xray-core/releases/download/v$version/$asset" --output "$download"
  printf '%s  %s\n' "$sha" "$download" | sha256sum --check --status
  mv "$download" "$archive"
  trap - EXIT
fi

python3 - "$archive" "$cache/$arch" "$machine" <<'PY'
import pathlib
import struct
import sys
import zipfile

destination = pathlib.Path(sys.argv[2])
destination.mkdir(parents=True, exist_ok=True)
with zipfile.ZipFile(sys.argv[1]) as archive:
    binary = archive.read('xray')
    if binary[:6] != b'\x7fELF\x02\x01' or struct.unpack_from('<H', binary, 18)[0] != int(sys.argv[3]):
        raise SystemExit('Xray ELF architecture mismatch')
    for name in ('xray', 'LICENSE', 'README.md'):
        target = destination / name
        target.write_bytes(binary if name == 'xray' else archive.read(name))
        target.chmod(0o755 if name == 'xray' else 0o644)
print('Xray 26.10.31 runtime and notices verified: ' + str(destination))
PY
