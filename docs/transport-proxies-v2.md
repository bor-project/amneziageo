# Transport proxy catalog v2

The shared client catalog stores local proxies and publications from authenticated
server hello responses. A configuration selects one `source/id`, or direct UDP.
Selection never triggers transport or proxy fallback.

## Published contract

`features.proxiesV2` contains `version: 2`, a stable source GUID, `items`, and the
existing source proof. Each item retains its stable `id`, name, public host,
port, path, update time, authorization and destination restrictions.

| Transport | Descriptor | Authorization | Certificate |
| --- | --- | --- | --- |
| `websocket` | No `http`, `quic` or `xhttp` descriptor | `bearer` | Explicit `tls.enabled` |
| `http` | `http.version: 2`, `method: GET`, `dataPlacement: header` | `hmac-sha256-v2` | Explicit `tls.enabled` |
| `http` | `http.version: 2`, `method: POST`, `dataPlacement: body` | `hmac-sha256-v2` | Explicit `tls.enabled` |
| `quic` | `quic.version: 1`, `protocol: webtransport`, `datagrams: framed-stream` | `bearer` | Required |

All v2 items must explicitly provide the boolean `tls.enabled`. TLS identity uses
`tls.serverName` and the optional SHA-256 certificate pin. Plain WS/HTTP must
have an empty pin. The port does not determine whether TLS is enabled.

HTTP credentials are nonempty UUIDs. The signed HTTP v2 protocol derives its key
from the canonical UUID and transmits a key identifier and request MAC, rather
than the credential. The actual HTTP method participates in the signature.
Challenge and response verification, epoch, nonce, payload hash, replay checks
and datagram framing remain part of the protocol.

GET sends uplink data in headers and has no request body. POST sends data in the
body. Neither mode substitutes the other method. The Yandex CDN profile uses
plain HTTP GET exclusively; it does not accept POST, Upgrade or CONNECT.

Destination restrictions are common to every transport. They apply to local
entries and publications, independently of availability. A denied or missing
destination does not cause a direct connection.

## Compatibility and migration

Clients prefer `proxiesV2`. If that feature is present but malformed or has an
unsupported version, the catalog reports an error and does not fall back to
`proxies`. A `proxiesV2` feature must contain catalog version 2; `proxies` must
contain version 1.

Old `proxies` hello catalogs and stored WS entries keep their existing TLS
behavior when the new field is absent. Legacy per-interface WS settings remain
supported. A version-1 experimental `xhttp` item becomes canonical HTTP GET only
when its `xhttp.version` is explicitly 2 or a signed v2 probe confirms the same
entry. Its name alone is not proof of the protocol. Unconfirmed entries remain
visible and stored; a failed confirmation never changes the selected transport.

Migration retains source IDs, item IDs, credentials, selections, server
publications and destination restrictions. Probe confirmation compares the
complete entry before storing its result, so an in-flight probe cannot upgrade
an edited entry. Repeated old hello responses retain confirmation for unchanged
connection parameters. Name-only edits also retain that confirmation.

Export/import carries HTTP method, certificate choice, QUIC descriptor and
restrictions. Removed entries retain their last public metadata for selections
that still reference them. Saved selection and actual running transport are
reported separately; editing a saved method does not relabel an active session.

## Local CLI and editor

`transport-proxy save [local-id] --file path.json` accepts the same item shape.
`transport-proxy list` lists public metadata. `showlocal source/id` includes the
local credential and must be treated as private output.

`config proxy config-name source/id` selects a specific item from any source;
`config proxy config-name none` selects direct UDP. Server entries are read-only
in the editor. New local creation offers WebSocket, HTTP and QUIC. HTTP exposes
GET/POST and certificate choice; QUIC requires its certificate option. Only
confirmed HTTP GET uses the application icon.

## Platform integration

The common model, hello, persistence, CLI, editor and signed availability probe
understand the modes above. Current GET runtimes receive a compatibility
projection of canonical HTTP GET, including the explicit certificate choice.
The common WebSocket carrier supports both plain and TLS connections.

Windows supports plain/TLS WebSocket, signed HTTP GET/POST and TLS QUIC through
the runtime integrated in AG-732. QUIC availability uses the same native runtime.
Linux and Android/TV integration remains in AG-733 and AG-734. On those platforms,
VPN startup with POST or QUIC returns `proxy-platform-unsupported`; it does not
send GET or start direct UDP. A successful HTTP POST availability probe alone
does not imply that the platform VPN runtime supports POST.

HTTP TLS stabilization and live CDN HTTPS diagnostics belong to AG-737. Apple
clients are outside this iteration.
