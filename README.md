# AmneziaGeo

**English** | [Русский](README.ru.md)

![platform](https://img.shields.io/badge/platform-Windows-0078D6)
![engine](https://img.shields.io/badge/engine-AmneziaWG-2E7D32)
![license](https://img.shields.io/badge/license-GPL--3.0-blue)

AmneziaGeo runs on the AmneziaWG engine, a WireGuard fork that is harder to block. Only what you pick goes through the tunnel; everything else goes direct.

## What it is for

Plain WireGuard decides by IP address and knows nothing about domains. Taking a category of sites and expanding it once into a list of addresses does not help much: large CDNs (Cloudflare, for example) rotate their addresses constantly, and every subdomain gets its own. The app reaches an address that was never on the list, and the traffic leaks past the tunnel.

AmneziaGeo watches the real DNS answers. The moment the system learns the address of a domain you chose, the client sees that address in the reply and adds it to the tunnel right away, before the app even opens the connection. Whole countries are given as address ranges and added to the tunnel at connect time, because country ranges do not change.

## What it does

Two modes:

- **Full tunnel.** Everything through the VPN, with the local network bypassed. There is a kill-switch: if the tunnel drops, traffic will not leak direct.
- **Split tunnel.** By default everything goes direct. Only what matches your rules takes the tunnel, and domains add themselves as soon as a DNS answer arrives for them.

Rules are set by domain, site category, country, address range, or application. They are grouped into lists you can attach to different configs.

**A single app into the tunnel.** Add a program (a browser, a game, a messenger) and only its traffic takes the tunnel, wherever it connects. The client follows the process and its child processes.

**All UDP into the tunnel.** Calls and games usually learn their server addresses without DNS, so a domain rule never catches them. Turn on the toggle and every outgoing UDP datagram goes into the tunnel, except the local network and the VPN server itself.

**When UDP is blocked.** AmneziaWG runs over UDP, and on some networks (corporate and guest Wi-Fi, some mobile carriers) it does not get through. The whole tunnel can then run over a WebSocket on TCP. From the outside it looks like ordinary HTTPS traffic and passes where UDP is closed. Any port, set in the settings. If the server requires authentication, provide a login and password or a token.

Domain and country edits apply on the fly, without reconnecting.

## Installation (Windows)

You need Windows 7, 10, or 11 (x64 or arm64) and administrator rights: the installer sets up a system service.

1. Download an installer from the [Releases](../../releases) page. There are x64 and arm64 builds. The regular build carries everything it needs. There is a smaller variant, but it needs the [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) already installed.
2. Run it. The service and the app are installed.
3. Open AmneziaGeo and import a config: a `.conf` file, a QR code, or a shared settings file.
4. Pick a rule list (or the full tunnel) and connect.

After that the app updates itself.

## Example: Discord where UDP is blocked

1. Create a list and name it `discord`.
2. In the application rules, pick the running Discord and add it.
3. Turn on "All UDP into the tunnel": Discord's voice servers arrive without DNS.
4. Since the network blocks UDP, enable WebSocket in the transport settings and set your server's address, port, and authentication (if the server requires it).
5. Select the `discord` list and connect.

Discord text and voice now go through the tunnel; everything else goes direct.

## Building from source

Each platform builds on its own. Windows is supported today.

### Windows

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0), [WiX](https://wixtoolset.org) (for the installer), and git. The tunnel engine builds from a submodule, so start with it.

#### 1. Fetch the engine submodule

The Windows AmneziaWG engine (`amneziawg-windows`) is a git submodule and is not part of the main checkout. Without it the engine build fails right away with a clear hint. Fetch it:

```powershell
git submodule update --init --recursive
```

#### 2. Build the tunnel engine

So the tunnel also works on Windows 7, the engine is built with a Windows 7-capable Go:

```powershell
# tunnel engine -> tunnel.dll
amneziageo-windows\tools\build-engine-win7.ps1
```

The first run downloads everything it needs — Go, llvm-mingw, and wintun via the submodule's `build.cmd`, plus a separate Go for Windows 7 — verifies the downloads by checksum, and drops the finished `tunnel.dll` where the app picks it up. Options:

- `-Arch x64|x86|arm64` — target architecture (default `x64`).
- `-Upstream` — build with stock Go, without Windows 7 support (for comparison).
- `-Force` — re-download and rebuild the toolchain.

#### 3. Build the app and installer

```powershell
# app and service
dotnet build amneziageo-windows\AmneziaGeo.Windows.Ui\AmneziaGeo.Windows.Ui.csproj -c Release

# installer -> dist\AmneziaGeo-<version>-win-<arch>-<payload>.exe
amneziageo-windows\installer\AmneziaGeo.Windows.Installer.Bundle\build-installer.ps1
```

By default one variant is built: `x64`, framework-dependent (the target needs the .NET 10 Desktop Runtime installed). Builder options (each has a short alias; `-h` lists them all):

- `-v, -Version N.N.N.N` — bundle and binary version (otherwise `0.0.1.<commit count>`).
- `-a, -Arch x64,arm64` — architectures, as a list or `all`.
- `-p, -Payload fdd,scd` — payload kind: `fdd` needs the runtime installed (lighter), `scd` bundles the runtime (installs anywhere, large).
- `-c, -Configuration Debug|Release`.
- `-pre, -Prerelease` — bake in the beta update channel.
- `-r, -Rebuild` — clean before building.
- `-l, -ListOnly` — print the build matrix and exit.

arm64 builds through the whole pipeline, but the native parts (`tunnel.dll`, `wintun.dll`, `wstunnel.exe`) are x64-only for now, so the working target today is x64.

#### Running in development

For development there is a launcher: with one command it brings up the backend agent and the UI in a single process. Run it from an elevated console (it installs the service and WFP rules and brings up the tunnel); it needs the `tunnel.dll` from step 2.

```powershell
# backend + UI in one process
dotnet run --project amneziageo-windows\tools\AmneziaGeo.Windows.Launcher
```

With no flags both parts start. Flags: `--service` — agent only; `--ui` — UI only; `--target <name>` — drive a profile or config right away; `--config <path.conf>` — register a wg-quick config and launch on it.

## Ubuntu Server (headless)

The Linux head runs without a desktop: the agent is a systemd service and everything the GUI
configures is reachable from the console client `amneziageo`.

### Install

```bash
git submodule update --init --recursive
amneziageo-linux/tools/install-server.sh
```

Building needs the .NET SDK and the Go toolchain; the published output is self-contained, so the
server needs neither. The script publishes into `/opt/amneziageo`, keeps the library in
`/var/lib/amneziageo`, links `amneziageo` into `/usr/local/bin` and installs
`amneziageo-agent.service`.

### First run

```bash
sudo amneziageo geo download
sudo amneziageo config import work --file work.conf     # or --link 'vpn://…' or --stdin
sudo amneziageo profile add work work
sudo amneziageo up work
sudo amneziageo settings set survive-reboot on
sudo amneziageo settings set periodic-reconnect-enabled on
```

`survive-reboot` dials at agent start, `periodic-reconnect-enabled` redials when the tunnel dies.
Without them a reboot or a crashed engine leaves the server without a tunnel.

### Day to day

```bash
amneziageo status                  # what runs, and what the next connect would use
amneziageo doctor                  # the checks a headless install usually fails
amneziageo --json profile list     # script-friendly output
amneziageo log tail --level info
amneziageo tui                     # full-screen console over SSH
```

`amneziageo help` lists every command. Exit codes: 0 done, 1 the agent refused, 2 wrong usage,
3 agent unreachable, 5 not implemented by the Linux agent.

### Notes

- The agent runs as root: it creates the tunnel device and rewrites routes, and `CAP_NET_ADMIN`
  alone does not satisfy its preflight.
- The control socket is `/tmp/CoreFxPipe_AmneziaGeo.Agent`, so the unit must not set `PrivateTmp`.
  Every local account that can reach the socket can drive the agent, including reading a
  configuration's keys.
- The Linux tunnel applies the configuration's own addresses, MTU and `AllowedIPs`. Routing lists,
  per-config DNS, exclusions and the WebSocket transport are stored and reported, but not yet
  enforced by the Linux data plane.

## License

GPL-3.0 or later, see [LICENSE](LICENSE). Uses the AmneziaWG engine under its authors' license.

## Support

If AmneziaGeo is useful to you and you would like to support its development, you can donate in one of the crypto networks:

- TRON network (TRC20): `TNHcrYqUv2pUfW7BEzYJyXfVk9wEJrs4FR`

Thank you for your support!

## Credits

Built on the [Amnezia VPN](https://github.com/amnezia-vpn) ecosystem: the AmneziaWG protocol and engines. AmneziaGeo adds the choice of what goes through the tunnel on top.
