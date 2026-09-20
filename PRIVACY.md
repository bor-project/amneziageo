# Privacy policy

AmneziaGeo has no accounts, no telemetry and no analytics. The project runs no server of its own, nothing
about you or your traffic reaches its authors, and the codebase carries no telemetry or crash-reporting
library.

## What the application connects to by itself

| Destination | When | Why |
|---|---|---|
| `github.com`, `api.github.com` | on an update check and on the download you start | read `update.json`, fetch the setup |
| `github.com/Loyalsoldier/v2ray-rules-dat` | when the geo databases are refreshed | download `geoip.dat` and `geosite.dat` |
| `speed.cloudflare.com` | only while you run the built-in speed probe | measure the channel |
| the server from your own configuration | while the tunnel is up | carry your traffic |

Nothing else is contacted on the application's own initiative. Every other address is one your own traffic
asked for.

## What stays on the machine

Configurations, private keys, routing rules and logs never leave the device:

- Windows: `C:\ProgramData\AmneziaGeo` (agent) and `%LOCALAPPDATA%\AmneziaGeo` (UI);
- Linux: `/var/lib/amneziageo` (agent) and the user profile (UI);
- Android: the application's private storage.

Logs are a local SQLite database. The diagnostics archive is built only when you ask for it, written where
you choose, and never uploaded anywhere.

## Uninstalling

The Windows installer registers an uninstaller in "Apps & features", the deb packages uninstall through the
package manager, the Android application uninstalls like any other. Uninstalling stops the service and
removes the program files.
