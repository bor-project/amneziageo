# AmneziaGeo privacy

Version 1, 24 October 2026

AmneziaGeo has no accounts, no telemetry and no analytics. The project runs no servers of its own: nothing about you or your traffic reaches its authors. The code carries no telemetry or crash-reporting library.

## Who the program exchanges data with

- The servers you added yourself. While the tunnel is up, your traffic goes through them. The program opens a subscription link on the schedule of the subscription refresh.
- GitHub (api.github.com, github.com). Checks for new versions several times a day and on the "Check for updates" button. Downloads when you start one.
- Sources of the geo databases. By default these are github.com (the Loyalsoldier, runetfreedom and jameszeroX sets) and raw.githubusercontent.com/bor-project/amneziageo-geo. The program checks and downloads the databases on a schedule, once every 24 hours. The sources can be changed.
- Region detection (www.cloudflare.com/cdn-cgi/trace, api.country.is). The window asks for the country code when it picks routing presets and the tunnel is not up. The answer is the country code alone.
- Speed probe (speed.cloudflare.com). Only when you run a probe yourself.
- DNS over HTTPS (cloudflare-dns.com). Only when it is turned on in the settings.
- Finding the cause of a drop. When the tunnel stops carrying traffic, the service checks, outside the tunnel, the gateway of the network, your server, GitHub and the sources of the geo databases to name the cause.

Everything else that goes to the network is your own traffic.

## What stays on the device

Configurations, keys, routing rules, logs and the record of accepted terms are not sent anywhere:

- Windows: C:\ProgramData\AmneziaGeo (service) and %LOCALAPPDATA%\AmneziaGeo (window);
- Linux: /var/lib/amneziageo (service) and the user profile (window);
- Android: the private storage of the application.

Logs are a local SQLite database. The diagnostics archive is built only on your command, saved where you say and uploaded nowhere.

## Uninstalling

Windows: "Apps & features". Linux: the package manager. Android: like any application. Uninstalling stops the service and removes the program files.
