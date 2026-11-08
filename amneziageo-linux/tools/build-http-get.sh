#!/usr/bin/env bash
# Builds the HTTP GET runtime.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
tool="$(cd "$here/../../tools/http-get-build" && pwd)"
go_bin="${GO_EXECUTABLE:-go}"
case "${1:-amd64}" in
  amd64|x64|linux-x64) arch=amd64 ;;
  arm64|linux-arm64) arch=arm64 ;;
  *) echo "unsupported HTTP GET architecture: $1" >&2; exit 2 ;;
esac
host_go() {
  GOROOT='' GOOS='' GOARCH='' GOARM='' GOAMD64='' GOARM64='' GOFIPS140=off GOFLAGS='' GOEXPERIMENT='' GOWORK=off GOENV=off GOTOOLCHAIN=local "$go_bin" "$@"
}
if [ "$(host_go env GOVERSION)" != 'go1.27.3' ]; then
  echo 'HTTP GET preparation requires Go1.27.3; set GO_EXECUTABLE to that SDK.' >&2
  exit 1
fi
host_go -C "$tool" run . -cache "$here/../.deps/http-get" -os linux -arch "$arch" -go "$go_bin"
