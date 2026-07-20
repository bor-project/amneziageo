# amneziageo-ui

Shared .NET libraries and Avalonia UI consumed by the Windows and Android applications.

## Projects

- `AmneziaGeo.Decl` - domain models and shared contracts.
- `AmneziaGeo.Dal` - SQLite-backed persistence.
- `AmneziaGeo.Geo` - geo database parsing, matching, and WireGuard config editing.
- `AmneziaGeo.Ipc` - messages shared by UI and platform hosts.
- `AmneziaGeo.Localization` - localized strings and culture management.
- `AmneziaGeo.Ui` - shared Avalonia theme, responsive views, view models, and platform connection contract.

Platform projects consume these libraries through `ProjectReference`. They are part of this repository and are not Git submodules.

## Target framework

The shared projects target `net10.0`. The Android `net10.0-android` application references them directly and hosts the same `MainView` as Windows.
