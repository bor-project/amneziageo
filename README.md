# AmneziaGeo

Lightweight, cross-platform **AmneziaWG** client with **geosite / geoip split tunneling** — route only the destinations you choose (by domain category or by country) through an obfuscated AmneziaWG tunnel, and send everything else direct.

## The problem this solves

WireGuard (and its DPI-resistant fork AmneziaWG) routes purely by **IP** — *cryptokey routing* via a peer's `AllowedIPs`. The protocol has **no concept of a domain**. Naively expanding a `geosite:` category into a frozen snapshot of IPs breaks in practice: CDNs (Cloudflare et al.) rotate A-records and use different IPs per subdomain, so the app hits an IP that was never routed and the traffic leaks direct.

AmneziaGeo fixes this with **live DNS interception**: a local DNS proxy observes the *real* answers the system receives and programs the tunnel's routed set (`AllowedIPs` + OS routes) **dynamically, before the app opens the connection**. This is the sing-box/v2ray "route from real DNS" idea, kept on top of WireGuard's cryptokey routing.

## Architecture (three tiers)

1. **Control plane — C# (desktop & Android) / Swift (Apple).** Parses `geosite.dat` / `geoip.dat`, resolves tokens (`geosite:x` → domain suffixes, `geoip:cc` → CIDRs), generates config, and runs the DNS proxy + route programmer.
2. **Data plane — Go.** The AmneziaWG obfuscation engine (`amneziawg-go`), shipped as an **opaque, per-OS native artifact** (`tunnel.dll` / `.so` / `libwg-go.a` / `.aar`). Never reimplemented; driven over the WireGuard **UAPI** (`set` / `replace_allowed_ips`).
3. **Privileged / native execution.** A privileged host (service/helper) on desktop; a native system VPN extension on mobile/Apple.

**Key rule:** `geoip` (CIDR) is **preinstalled statically** into `AllowedIPs` at connect time (IP ranges don't rotate). The **live DNS-interception path is reserved for `geosite` (domains)** only — that halves runtime complexity and UAPI churn.

> Honest scope note: "one C# codebase for everything" does not survive contact with platform VPN APIs. **Apple (macOS + iOS) is a single native Swift project** (NetworkExtension is native-only). C# owns Windows, Linux, Android, and all desktop UI/orchestration. The two language worlds share concepts (config, geo logic), not code — unless the geo loop is folded into the Go engine layer (see `amneziageo-apple`).

## Repository layout

| Directory | Platform | Engine packaging | Status |
|---|---|---|---|
| [`amneziageo-core`](amneziageo-core/) | shared (C#) | .NET class library — geo parsing, config, DNS proxy, UAPI client | 📋 planned |
| [`amneziageo-windows`](amneziageo-windows/) | Windows | C# host + `tunnel.dll` (c-shared) via P/Invoke | 🚧 first target |
| [`amneziageo-linux`](amneziageo-linux/) | Linux | C# + `amneziawg-go` userspace daemon (UAPI) | 📋 planned |
| [`amneziageo-android`](amneziageo-android/) | Android | C# (.NET) `VpnService` + gomobile `.aar` | 📋 planned |
| [`amneziageo-apple`](amneziageo-apple/) | macOS + iOS | native Swift — one Xcode project, shared `AmneziaGeoKit`, over `libwg-go.a` | 📋 planned |

Shared code is per-language: [`amneziageo-core`](amneziageo-core/) — a .NET class library referenced by the Windows / Linux / Android heads via a `<ProjectReference>` (**not** a submodule); each head keeps its own `.sln`, so there is no mega-solution — and `AmneziaGeoKit` (Swift) shared across the two Apple platforms. Git submodules are reserved for the external upstream engines (`amneziawg-windows`, `amneziawg-apple`).

## Status

Early scaffolding. **Windows is the first implementation target.**

## License

TBD.
