# IPv6 + LAN-bypass cleanup plan

Goal: with the routing flag **off** wrap *all* traffic but keep the local network (RDP/SSH/printers)
working in parallel; with it **on** wrap only the selected geo routes. Do it dual-stack and without the
v4-only crutches, leaning on the clean logic already present in the submodule (`amneziawg-windows`) and
the reference (`amnezia-client`).

## Findings

- The reference's clean full-tunnel-with-LAN model = **default route on the tunnel** + **exclusion
  routes** for the local networks (routed out the physical gateway) + a **dual-stack firewall** that
  permits the tunnel, the LAN ranges, DHCPv6 and NDP while blocking everything else. It lives in
  `client/platforms/windows/daemon/`: `wireguardutilswindows.cpp` (`excludeLocalNetworks`),
  `windowsroutemonitor.cpp` (exclusion/capture routes, re-asserted on route change), `windowsfirewall.cpp`
  (`enableLanBypass`, v4+v6).
- The WG engine (`amneziawg-windows`) is fully dual-stack capable (`tunnel/addressconfig.go`) and has its
  own kill-switch (`tunnel/firewall/blocker.go`) — but that kill-switch has **no LAN/RFC1918 bypass**, so
  it cannot meet the goal alone.
- We already own the core primitive: `RouteManager.AddEndpointExclusion` is a one-prefix exclusion route
  (`FindPhysicalGateway` + `CreateIpForwardEntry2` + crash-safe persist). Extending it to the LAN ranges
  is small — we do **not** need to port the whole route monitor.
- The `/0 → /1+/1` split is **not** a crutch: it cleanly stops the engine arming its own (no-LAN-bypass)
  firewall without forking the submodule. Kept.
- Real crutches = the v4-only branches: v6 stripping, the blanket AAAA / type-65 deny, the `fec0::`
  resolver fix.

## Phases

### Phase 1 — LAN-bypass exclusion routes (v4) — *in progress*
Full tunnel (`!geoSplit`) + AllowLan: route `10/8, 172.16/12, 192.168/16` via the physical gateway so
routed local subnets (printers/RDP one hop away) stay reachable; the connected subnet was already direct
via its on-link route. Persisted + reverted on teardown/reconcile, mirroring endpoint exclusion. Files:
`RouteManager.cs`, `TunnelRunner.cs`, `TunnelPaths.cs`, `NetworkReconciler.cs`. Low risk; firewall already
permits these ranges. Build + stand-verify.

### Phase 2 — dual-stack IPv6
When the config has a v6 Interface Address: stop stripping v6 — hand `::/1`+`8000::/1` to the engine,
assign the v6 address, add v6 exclusion routes (`fc00::/7`, `fe80::/10`). Extend `WindowsFirewall` to
permit tunnel v6 + LAN v6 + DHCPv6 + NDP and block only off-tunnel v6 (port the filters from
`windowsfirewall.cpp`). `DnsProxy`: resolve AAAA for matched domains and inject `/128` tunnel routes; drop
the blanket AAAA deny. Needs a v6-capable test config; re-verify the kill-switch on the stand.

### Phase 3 — remove crutches + route-change re-assert
Remove the `fec0::` fix, the v6-strip branch, and the blanket AAAA/type-65 deny (keep the targeted QUIC
block). Optionally port `NotifyRouteChange2` re-assertion of exclusion routes for robustness across
mid-session network changes (Wi-Fi ↔ Ethernet).

## Out of scope
`windowssplittunnel.cpp` (the reference's WFP-callout IP/app split) — we do domain-based geo split via the
DNS proxy + dynamic routes, so it is not needed.
