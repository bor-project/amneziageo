# Android XHTTP runtime

AWG and Xray run in the same `libamneziawg-go.so` and share one Go runtime. The Android `VpnService` protects the file descriptors created by Xray before connect. `agXHttpStart` accepts a profile and the process-lifetime protection callback; a different callback address is rejected. `agXHttpStop(handle, waitMs)` returns `0` after cleanup and `1` while cleanup continues. A pending handle remains owned until cleanup completes.

The supported profile has one loopback UDP tunnel inbound and one VLESS outbound through TLS HTTP/2 XHTTP `packet-up`. Uplink requests use GET with data in `X-Data`, chunks up to 2048 bytes, fixed 100-byte padding, and no request body. The outbound address is a literal resolved on the physical Android network. The public host and TLS server name remain in the profile. TLS checks the certificate chain and name, or the explicitly configured SHA256 certificate pin.

The wrapper builds `core.Config` from this validated profile. It does not import the general JSON loader or register unrelated protocols. Xray logs are disabled. Errors crossing the native boundary contain stable fault codes.

## Versions and sources

- Go toolchain: `1.27.3`.
- Xray release: `26.10.31`.
- Xray commit: [`a4a3dff5b20c2faae567b742c172c7b95e75c083`](https://github.com/XTLS/Xray-core/tree/a4a3dff5b20c2faae567b742c172c7b95e75c083).
- Go module: `github.com/xtls/xray-core v1.260327.1-0.20261031132019-a4a3dff5b20c`.
- Go module sum: `h1:XijTasBJ2fImHZo1GoAgSfHuDqloQGYHEADLy9CgrJ8=`.
- Go module file sum: `h1:/Umx7vpU+A78gOOuKl6ESU6eQJ5EBRhwr5cl7+9/drw=`.
- Android NDK tested: `26.3.11579264`, API 24, 16KB load alignment.
- AWG submodule tested: `b5928efb6ca19f0153958460c3d141f04abc5c2e`; its source and HEAD are unchanged.

Xray uses [Mozilla Public License 2.0](third_party/Xray.LICENSE). Its full source is available at the pinned commit above. The Xray changes used in this build are supplied as source in `internal/xrayoverlay/main.go` and `internal/xrayoverlay/native_pool.go.txt`; the generated pool file uses MPL-2.0. The Android package includes this source notice and the license. Distribution must provide the corresponding modified source, including these overlay inputs.

## Build

Run `tools/build-engine-android.ps1 -GoExecutable <Go1.27.3/bin/go.exe>` on Windows or `tools/build-engine-android.sh` with Go 1.27.3 in PATH on Linux/macOS. Both build the combined library and retain the existing AWG exports. No separate Xray executable is used.

Each ABI contains `native-source.sha256`, covering wrapper and AWG Go/C/assembly/header inputs, module files, overlay source and native build scripts. The Android MSBuild guard checks this fingerprint before using the library. A normal C# build does not invoke Go; stale native output requires rerunning the native script with the explicit toolchain. The four existing ABIs are supported, including optional `x86`.

The generator copies the pinned Xray module into `.local/xray-overlay/xray`, creates a separate module file, and applies a Go build overlay there. The module cache and AWG submodule are not modified. The generator rejects other module versions and verifies SHA256 of the two patched upstream files:

| File | SHA256 |
| --- | --- |
| `transport/internet/splithttp/dialer.go` | `ed4ec0d02b7f4bed517f1326c534186e7648af81fe0c974f601a1e933666d0cd` |
| `transport/internet/splithttp/client.go` | `b528c116a72e77cb51e4fb1978a07d0e92d6b0587e0bafb3011d4891a63499dc` |

For host tests, prepare the overlay with `go run ./internal/xrayoverlay -output <directory>`, then pass its `native.go.mod` and `overlay.json` through `go test -modfile <file> -overlay <file> ./xhttpnative`. Unix socket tests in other packages require a Unix host.

## Instance lifetime

`core.NewWithContext` propagates an owner key into the supported outbound path. The owner participates in the XHTTP pool key, so simultaneous VPN and switch-probe instances do not share pools. Request contexts retain the owner lifetime while preserving Xray trace values. Stop cancels that lifetime, closes response bodies, waits for requests, and closes current and retired HTTP transports. The overlay also closes idle HTTP/2 connections when upstream XMUX retires a client.

Android stores the selected descriptor with the private VPN request, like the WireGuard keys. It passes the descriptor through the connect intent and switch request. An explicit selection never enables the old direct-UDP fallback. A physical-network change recreates the carrier and resolves its address again.

## UDP queue patch

The shared pinned patch in `../../tools/xray-build/udp-worker-queue.patch` raises the inbound UDP pipe budget from16KiB to256KiB. The overlay verifies the upstream and patched worker checksums from the shared manifest. The package includes the manifest, patch and modified-source notice. Both Android source fingerprint implementations include the shared build inputs; previous SO/stamps must be rebuilt. No diagnostic observers are included.
