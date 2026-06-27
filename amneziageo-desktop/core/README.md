# amneziageo-core

Shared **.NET** core library for the C# platform heads (Windows, Linux, Android). Apple is Swift and does **not** consume this — its shared code is `AmneziaGeoKit` in [`amneziageo-apple`](../amneziageo-apple/).

## What lives here

- `geosite.dat` / `geoip.dat` parsing (Google.Protobuf) + token resolution (`geosite:x` → domain suffixes, `geoip:cc` → CIDRs)
- Config model + (de)serialization — AmneziaWG params + split-tunnel routing policy
- The DNS proxy (`127.0.0.1:53`) and the route / `AllowedIPs` programmer abstraction
- WireGuard **UAPI** client (`set` / `replace_allowed_ips`)
- IPC contracts between the unprivileged UI and the privileged host

## How it's consumed — project reference, NOT a submodule

`amneziageo-core` is a plain class library (`AmneziaGeo.Core.csproj`) in this monorepo. Each platform head references it directly:

```xml
<ProjectReference Include="..\amneziageo-core\AmneziaGeo.Core.csproj" />
```

There is **no mega-solution**: each platform keeps its own `.sln` (e.g. `amneziageo-windows/AmneziaGeo.Windows.sln`) that includes its own projects plus the core project. Git **submodules are reserved for external upstream engines** (`amneziawg-windows`, `amneziawg-apple`) — never for our own in-repo code. A project reference gives atomic cross-cutting commits and friction-free refactoring; a submodule would force a separate repo, pointer-bumping, and `--recurse-submodules` onboarding for no benefit.

> If core ever needs to be reused by *other* repositories, publish it as a **NuGet package** — still not a per-directory submodule.

## Target framework

Latest .NET (`net10.0`). The Android head consumes it via .NET for Android (`net10.0-android`).

## Status

📋 Planned — factored out as the Windows target takes shape.
