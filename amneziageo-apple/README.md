# amneziageo-apple

Apple (**macOS + iOS**) implementation of [AmneziaGeo](../README.md) — a **single Xcode project** covering both platforms, written in **Swift**.

## Why one project for macOS + iOS

This mirrors how [`amneziawg-apple`](https://github.com/amnezia-vpn/amneziawg-apple) and upstream `wireguard-apple` are organized: one project, both platforms, a shared kit. A VPN client shares the vast majority of its code across macOS and iOS — the `NEPacketTunnelProvider`, the Go-engine adapter, config parsing, the DNS-intercept/route loop, Keychain + App Group handling. Only packaging (sysext vs appex), entitlements, UI chrome, and memory tuning differ. Two separate projects would duplicate all of that.

## Structure (planned)

```
amneziageo-apple/
├─ AmneziaGeo.xcworkspace
├─ AmneziaGeoKit/         ← shared Swift package (the "core")
│     config model · AmneziaWG engine adapter · geosite/geoip parsing
│     NEPacketTunnelProvider base + DNS-intercept/route loop
├─ App-macOS/             ← macOS app target (UI)
├─ App-iOS/               ← iOS app target (UI)
├─ Provider-macOS/        ← NE provider, packaged as a System Extension (sysext)
├─ Provider-iOS/          ← NE provider, packaged as an app extension (appex)
└─ amneziawg-apple/       ← submodule; builds libwg-go.a (the Go engine)
```

Both providers share one `NEPacketTunnelProvider` subclass from `AmneziaGeoKit`, differing only by `#if os(...)` and build settings.

## Engine & geo loop

- Go engine via the [`amneziawg-apple`](https://github.com/amnezia-vpn/amneziawg-apple) submodule → `libwg-go.a`, linked into the providers.
- The DNS-intercept + dynamic-`AllowedIPs` loop lives **inside the provider** (on iOS there is no live IPC from the app into a running tunnel). The app passes the `geosite` domain list + `geoip` CIDRs via the App Group container at activation. `geoip` is preinstalled; `geosite` is resolved live.
- **Design note:** the heaviest shared logic (DNS proxy + `AllowedIPs` programming) can alternatively be folded into the Go engine wrapper, so it is written once and reused by both the Swift providers here and the C# desktop/Android hosts — avoiding a Swift⇄C# reimplementation. TBD when the Windows target stabilizes.

## Platform specifics

- **macOS:** provider packaged as a Developer-ID-signed, **notarized System Extension** (per Apple TN3134) for non-App-Store distribution (or an appex for the App Store).
- **iOS:** provider is an appex (App Store only); **~50 MiB jetsam memory cap** — validate with a worst-case `Jc`/`Jmax` config, tune Go GC.
- Both: Apple Developer account + **Network Extension entitlement**.

## Status

📋 Planned. One Swift codebase for both Apple platforms.
