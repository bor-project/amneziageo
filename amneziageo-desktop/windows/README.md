# amneziageo-windows

Windows implementation of [AmneziaGeo](../README.md). **First implementation target.**

## Design

- **UI / control plane:** C# (Avalonia planned).
- **Engine:** [`amneziawg-windows`](https://github.com/amnezia-vpn/amneziawg-windows) added as a **git submodule**, built via its `build.cmd` into `tunnel.dll` (CGO `-buildmode c-shared`, Wintun userspace path). It exports `WireGuardTunnelService(conf, name)` and `WireGuardGenerateKeypair`, consumed from C# via `[LibraryImport]` P/Invoke. The DLL is loaded **in-process** by the privileged host.
- **Privilege model:** creating the Wintun adapter and programming routes / `AllowedIPs` requires SYSTEM. The tunnel runs inside a Windows service (`SERVICE_SID_TYPE_UNRESTRICTED`); the unprivileged UI drives it over a local named-pipe IPC. This mirrors the ProtonVPN / WireGuard embeddable-dll-service split.
- **Geo split-tunnel:** a local DNS proxy on `127.0.0.1:53` in the privileged host. On each matched `geosite` answer it adds the learned `/32`/`/128` to the peer `AllowedIPs` (UAPI `set`) **and** the OS route (`iphlpapi`), **before relaying the DNS reply** (race-free ordering). `geoip` CIDRs are preinstalled at connect.
- **Leak prevention:** public DoH/DoT resolvers are blocked via WFP so apps fall back to the system resolver the proxy can see.

## Build (planned)

1. `git submodule update --init amneziageo-desktop/windows/amneziawg-windows`
2. Build `tunnel.dll` via the submodule's `build.cmd` (first run downloads pinned Go + llvm-mingw + wintun into `.deps`).
3. Build the C# host/UI; ship `tunnel.dll` + `wintun.dll` alongside the service binary.

## Reference

The prior C++ proof-of-concept (in the abandoned Amnezia client fork) validated the exact runtime flow: `dnsrouteproxy` on `127.0.0.1:53` → `updatePeerAllowedIPs` (UAPI-only `set`) → `updateRoutePrefix` on the Wintun LUID, with route-before-relay ordering and WFP DoH blocking. None of that logic needs C++; it is plain socket I/O + route syscalls in C#.

## Status

🚧 Scaffolding in progress.
