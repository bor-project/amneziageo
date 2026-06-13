# amneziageo-android

Android implementation of [AmneziaGeo](../README.md). **Tractable mobile target.**

## Design

- **UI + service:** C# (.NET for Android) hosts both the UI and a `VpnService` subclass (`Android.Net.VpnService` is fully bound in .NET — no Kotlin required for the service itself).
- **Engine:** [`amneziawg-go`](https://github.com/amnezia-vpn/amneziawg-go) via a gomobile `.aar`. The tun fd from `VpnService.Builder.establish()` is handed to the Go engine (`WG_TUN_FD` pattern); `VpnService.protect()` keeps the engine's outbound socket off the tunnel.
- **Geo split-tunnel:** the DNS proxy can run **in-process** (C#). Dynamic routing uses the **in-engine `AllowedIPs` trie via UAPI** — note that changing OS routes baked into the tun via `Builder.addRoute()` requires a disruptive `establish()` re-run, so prefer reprogramming `AllowedIPs` over OS routes.
- **Leak prevention:** DNS forced via `VpnService.Builder.addDnsServer` pointed at the proxy.

## Constraints

- Android 14+: declare a `foregroundServiceType` for the VPN service.

## Status

📋 Planned. C# owns everything except the prebuilt Go engine `.aar`.
