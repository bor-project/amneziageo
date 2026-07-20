# amneziageo-ui

Shared .NET libraries consumed by the Windows and Android applications. This directory is also the home for platform-neutral Avalonia UI code as it is extracted from the platform heads.

## Projects

- `AmneziaGeo.Decl` - domain models and shared contracts.
- `AmneziaGeo.Dal` - SQLite-backed persistence.
- `AmneziaGeo.Geo` - geo database parsing, matching, and WireGuard config editing.
- `AmneziaGeo.Ipc` - messages shared by UI and platform hosts.
- `AmneziaGeo.Localization` - localized strings and culture management.

Platform projects consume these libraries through `ProjectReference`. They are part of this repository and are not Git submodules.

## Target framework

The shared projects target `net10.0`. The Android `net10.0-android` application can reference them directly.
