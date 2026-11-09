# Transport runtime

The desktop carrier compiles `cmd/http-get-proxy` from the shared Go module with
Go1.27.3. It supports signed HTTP GET/POST v2 and QUIC WebTransport framed-stream
v1. The legacy GET profile remains readable. Canonical profiles explicitly carry
the method, certificate choice and destination; unsupported modes do not fall back.

QUIC uses [quic-go v0.61.0](https://github.com/quic-go/quic-go/releases/tag/v0.61.0)
and its HTTP/3 API with the server's WebTransport stream contract. No separate
WebTransport wrapper is linked. Dependencies and checksums are pinned in the
shared module's `go.mod` and `go.sum`. The `licenses` directory contains their
redistribution notices and is copied into the published application.

Prepare the executable with the platform `tools/build-http-get` script. The
receipt binds the source, notices, Go version, target and executable hash.
Windows checks the PE architecture before including the carrier in a build.

Private profiles travel over redirected standard input, rather than process
arguments or temporary files. Version capabilities are checked before startup.
Canonical startup waits for the transport session; cancellation and parent
termination close the owned process. Legacy callers retain their existing marker.

HTTP GET carries uplink data in headers and no body. POST carries data in its body
and binds POST in its MAC. Both verify signed replies. QUIC verifies its TLS
certificate or explicit pin and preserves UDP boundaries with a two-byte length.
