# Building from source

**English** | [Русский](build.ru.md)

Each platform builds on its own. The tunnel engine always comes first: the app picks up an already built binary.

## Windows

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0), [WiX](https://wixtoolset.org) for the installer, and git.

### 1. Fetch the submodules

Two things the build needs are git submodules and no part of the main checkout: the Windows AmneziaWG engine (`amneziawg-windows`), and `sing-tun`, the userspace network stack the access point gateway stands on:

```powershell
git submodule update --init --recursive
```

### 2. Build the tunnel engine

So the tunnel also works on Windows 7, the engine is built with a Windows 7-capable Go:

```powershell
amneziageo-windows\tools\build-engine-win7.ps1
```

The first run downloads everything it needs - Go, llvm-mingw and wintun via the `build.cmd` of the submodule, plus a separate Go for Windows 7 - verifies the downloads by checksum and drops the finished `tunnel.dll` where the app picks it up. Options:

- `-Arch x64|x86|arm64` - target architecture, `x64` by default;
- `-Upstream` - build with stock Go, without Windows 7 support;
- `-Force` - re-download and rebuild the toolchain.

### 3. Build the access point gateway

`gateway.exe` carries the clients of the shared access point through a userspace stack, so what they send leaves this machine the way its own traffic does. It is a Go module in `amneziageo-windows\gateway` standing on the `sing-tun` submodule next to it:

```powershell
amneziageo-windows\tools\build-gateway.ps1
```

Go is taken from `PATH`, and failing that from the toolchain step 2 downloaded into `amneziawg-windows\.deps`. The build lands in `gateway\bin\<arch>\gateway.exe`, where the app picks it up; without it the app build stops and names this step. Options:

- `-Arch x64|arm64|both` - target architecture, both by default.

### 4. Build the app and installer

```powershell
# app and service
dotnet build amneziageo-windows\AmneziaGeo.Windows.Ui\AmneziaGeo.Windows.Ui.csproj -c Release

# installer -> dist\AmneziaGeo-<version>-win-<arch>-<payload>.exe
amneziageo-windows\installer\AmneziaGeo.Windows.Installer.Bundle\build-installer.ps1
```

By default one variant is built: `x64`, framework-dependent - the target machine needs the .NET 10 Desktop Runtime installed. Builder options, each with a short alias, `-h` lists them all:

- `-v, -Version N.N.N.N` - bundle and binary version, otherwise `0.0.1.<commit count>`;
- `-a, -Arch x64,arm64` - architectures, as a list or `all`;
- `-p, -Payload fdd,scd` - payload kind: `fdd` needs the runtime installed and is lighter, `scd` bundles the runtime;
- `-c, -Configuration Debug|Release`;
- `-pre, -Prerelease` - bake in the beta update channel;
- `-r, -Rebuild` - clean before building;
- `-l, -ListOnly` - print the build matrix and exit.

### Running in development

The launcher brings up the backend agent and the UI in a single process with one command. Run it from an elevated console - it installs the service and the WFP rules and brings up the tunnel; it needs the `tunnel.dll` from step 2 and the `gateway.exe` from step 3.

```powershell
dotnet run --project amneziageo-windows\tools\AmneziaGeo.Windows.Launcher
```

With no flags both parts start. Flags: `--service` - agent only, `--ui` - UI only, `--target <name>` - select a configuration right away, `--config <path.conf>` - register a wg-quick config and launch on it.

## Linux

```bash
git submodule update --init --recursive
amneziageo-linux/tools/build-deb.sh --arch amd64,arm64
```

Building needs the .NET SDK and Go; the target machine needs neither, the packages are self-contained. They land in `dist/`. Options: `--version N.N.N.N` (otherwise `0.0.1.<commit count>`), `--arch amd64,arm64`, `--out <dir>`, `--no-gui`, `--debug`.

The `libicu` alternatives in the script name the ICU packages a target distribution may carry, so a new distribution release adds one entry there.

## Android

The native engine first, then the APK:

```bash
amneziageo-android/tools/build-engine-android.sh
amneziageo-android/tools/build-apk.sh --abi android-arm64
```

The engine is built with the Android NDK toolchain into `AmneziaGeo.Android.Engine/native/<abi>/libamneziawg-go.so`; the `amneziawg-go` submodule is not modified - the c-shared entry points live in a separate module next to it. Options of `build-apk.sh`:

- `--config Release|Debug` - `Release` by default, it is AOT-compiled and starts about twice as fast on a weak TV;
- `--version N.N.N.N` - package version, all four fields make the versionCode;
- `--abi android-arm,android-arm64,android-x64` - the ABI list, by default every ABI the project declares;
- `--update-url <url>` - the update manifest baked into the package;
- `--prerelease` - keep the build on the prerelease channel.

Signing takes the SDK debug key unless `ANDROID_KEYSTORE` names a keystore, in which case `ANDROID_KEY_ALIAS`, `ANDROID_STORE_PASS` and `ANDROID_KEY_PASS` have to be set as well.

The same scripts exist for PowerShell on Windows: `build-engine-android.ps1` and `build-apk.ps1`.
