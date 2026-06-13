# amneziageo-linux

Linux implementation of [AmneziaGeo](../README.md).

## Design

- **UI / control plane:** C# (Avalonia), unprivileged.
- **Engine:** [`amneziawg-go`](https://github.com/amnezia-vpn/amneziawg-go) userspace binary — opens `/dev/net/tun` (needs `root` or `CAP_NET_ADMIN`) and exposes the WireGuard **UAPI** at `/run/wireguard/<dev>.sock`.
- **Privilege model:** a small privileged helper (systemd unit or `setcap` binary) spawns/supervises the engine, writes UAPI (`set` / `replace_allowed_ips`), and programs addresses/routes via `ip(8)` / netlink (`RTM_NEWROUTE`). The GUI stays unprivileged and talks to the helper over a unix socket.
- **Geo split-tunnel:** DNS proxy + dynamic `AllowedIPs`/routes live in the helper. `geoip` CIDRs preinstalled at connect; `geosite` resolved live (`replace_allowed_ips` append + `ip route add` before relaying the answer).
- **Leak prevention:** force queries through the proxy via `resolv.conf` / `systemd-resolved`, block public DoH/DoT with `nftables`.

## Status

📋 Planned. Mostly out-of-process orchestration — no P/Invoke required.
