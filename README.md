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

With no flags both parts start. Flags: `--service` — agent only; `--ui` — UI only; `--target <name>` — select a configuration right away; `--config <path.conf>` — register a wg-quick config and launch on it.

## Linux

The Linux head ships as two Debian packages built from one source tree:

- `amneziageo` - the agent that runs as a systemd service, the AmneziaWG engine it drives and the
  console client `amneziageo` with its full-screen console interface. It pulls in no desktop
  libraries, so it fits a headless server.
- `amneziageo-gui` - the desktop interface. It runs as the desktop user and drives the same agent
  over its control socket, and it needs the matching version of `amneziageo`.

A server takes the first package alone, a desktop takes both. Everything the desktop interface
configures is also reachable from the console client.

### Install

The installer takes the packages this machine needs straight from the newest release:

```bash
curl -fsSL https://raw.githubusercontent.com/bor-project/amneziageo/master/amneziageo-linux/tools/install.sh | sudo bash
```

It reads the architecture from dpkg, checks every package against the SHA-256 published in
`update.json` and hands them to apt. Options: `--no-gui` (agent alone), `--tag v1.2.3.4` (a named
release), `--prerelease`, `--repo owner/name`.

Or take the packages for your architecture from the [Releases](../../releases) page - there are
amd64 and arm64 builds - and let apt pull in the shared libraries they name:

```bash
# server
sudo apt install ./amneziageo_<version>_amd64.deb

# desktop
sudo apt install ./amneziageo_<version>_amd64.deb ./amneziageo-gui_<version>_amd64.deb
```

The agent starts and is enabled at boot right away. The binaries live in `/usr/lib/amneziageo`, the
library in `/var/lib/amneziageo`, the client is `/usr/bin/amneziageo`, and the interface the agent
creates comes from `/etc/default/amneziageo`. `apt remove` keeps the library, `apt purge` deletes
it.

### Update

A packaged build carries the release manifest it checks against, so the app updates itself: the
window offers the new version, the agent downloads exactly the packages this machine has installed
for its own architecture, verifies them against the published SHA-256 and lets apt install them from
a transient unit that outlives the agent restart. `amneziageo update check` reports the same from the
console. Restart the window afterwards to run the new interface too.

### Build the packages

```bash
git submodule update --init --recursive
amneziageo-linux/tools/build-deb.sh --arch amd64,arm64
```

Building needs the .NET SDK and the Go toolchain; the packages are self-contained, so the target
machine needs neither. They land in `dist/`. Options: `--version N.N.N.N` (otherwise
`0.0.1.<commit count>`), `--arch amd64,arm64`, `--out <dir>`, `--no-gui`, `--debug`. The `libicu`
alternatives in the script name the ICU packages a target distribution may carry, so a new
distribution release adds one entry there.

For a machine that is not Debian-based, `amneziageo-linux/tools/install-server.sh` publishes the
agent and the console client from the sources straight into `/opt/amneziageo`.

### First run

```bash
sudo amneziageo geo download
sudo amneziageo config import work --file work.conf     # or --link 'vpn://…' or --stdin
sudo amneziageo up work
sudo amneziageo settings set survive-reboot on
sudo amneziageo settings set periodic-reconnect-enabled on
```

`up <config>` selects the configuration and connects on it; `select <config>` only remembers it for
the next connect. The routing list is one setting for the whole machine, not a per-configuration
pairing: `routing use <name>` picks it, `routing use none` leaves the configuration's own
`AllowedIPs` to decide what the tunnel carries.

`survive-reboot` dials at agent start, `periodic-reconnect-enabled` redials when the tunnel dies.
Without them a reboot or a crashed engine leaves the server without a tunnel.

### Day to day

```bash
amneziageo status                  # what runs, and what the next connect would use
amneziageo doctor                  # the checks a headless install usually fails
amneziageo --json config list      # script-friendly output
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
- The Linux tunnel applies the configuration's own addresses, MTU and `AllowedIPs`, the selected
  routing list, and the resolvers stored for the configuration. A split routing list starts
  with nothing but the resolver routed, and every destination earns a host route on first contact;
  `route-ttl-seconds` decides how long one outlives its traffic. Exclusions, rules by application and
  the WebSocket transport are stored and reported, but not yet enforced by the Linux data plane.
- Destinations are decided by the names the machine looks up, so an application that resolves on its
  own, over DoH, is decided by address alone. While the tunnel carries no IPv6, an address over it is
  withheld from the names the rules send through the tunnel, which would otherwise leave by the
  physical path.

## Console

One command set on all three platforms: the shared `AmneziaGeo.Cli` assembly with a thin host per
system. Everything goes through the agent over the `IpcContract` protocol; the console never touches
the database, so the agent and the UI see its edits at once.

| platform | how to run it |
|---|---|
| Linux | `amneziageo <command>` |
| Windows | `amneziageo.exe <command>` from `%ProgramFiles%\AmneziaGeo` |
| Android | `adb shell am broadcast -a org.amneziageo.android.CLI -n org.amneziageo.android/.CliReceiver --es cmd "<command>"` |

On Android the answer comes back in `data=` of the same `adb` command, is mirrored whole to logcat
under the tag `AmneziaGeoCli`, and with `--es out <path>` is written to a file. Pass arguments that
start with a dash as an array: `--esa args ops,--probe`. The receiver is gated by
`android.permission.DUMP`, which the adb shell holds and an ordinary application cannot get. The UI
does not have to be up - the receiver starts the agent in its own process.

What debugging usually needs:

```bash
amneziageo status                   # state and the configuration table
amneziageo --json status            # the same for scripts
amneziageo doctor                   # the checks an install trips over
amneziageo runtime                  # the configuration the next connect would use
amneziageo cache --filter youtube   # what the agent resolved and where it routed it
amneziageo log tail --level info
amneziageo log say "run starts"     # mark the agent log from a test script
amneziageo ops                      # protocol operations and the commands that call them
amneziageo ops --probe              # which operations this platform's agent implements
amneziageo ipc <operation> [arg...] # call any operation directly
```

`ops --probe` sends every operation with no arguments: a refusal from its handler means the
operation is there, a refusal from the dispatcher means it is not. Operations that would do their
work for real with no arguments (connect, download, remove) are marked `-` and never sent.

Exit codes: 0 done, 1 the agent refused, 2 wrong usage, 3 agent unreachable, 5 not implemented by
this platform's agent.

## License

GPL-3.0 or later, see [LICENSE](LICENSE). Uses the AmneziaWG engine under its authors' license.

## Support

If AmneziaGeo is useful to you and you would like to support its development, you can donate in one of the crypto networks:

- TRON network (TRC20): `TNHcrYqUv2pUfW7BEzYJyXfVk9wEJrs4FR`

Thank you for your support!

## Credits

Built on the [Amnezia VPN](https://github.com/amnezia-vpn) ecosystem: the AmneziaWG protocol and engines. AmneziaGeo adds the choice of what goes through the tunnel on top.
