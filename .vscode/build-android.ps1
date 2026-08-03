param(
  [ValidateSet("emulator", "device")]
  [string]$Target,
  [string]$Emulator,
  [string]$Device,
  [string]$RuntimeIdentifiers,
  [ValidateSet("Debug", "Release")]
  [string]$Configuration,
  [string]$AndroidSdk = "C:\Users\admin\AppData\Local\Android\Sdk"
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$settingsPath = Join-Path $PSScriptRoot "debug.android.jsonc"

$defaultSettings = @'
{
  // Deploy targets. Takes the first entry with isCurrent = true.
  "targets": [
    {
      "isCurrent": false,
      "name": "Device",

      // Deploy target.
      // Available: emulator | device
      "target": "device",

      // Device serial.
      // List: `adb devices`.   Empty = the single connected device.
      "device": "",

      // Native ABIs to build, semicolon-separated.
      // Available: android-arm64 | android-x64 | android-arm | android-x86.   Empty = as declared in the csproj.
      "runtimeIdentifiers": "android-arm64"
    },
    {
      "isCurrent": false,
      "name": "Phone emulator",
      "target": "emulator",

      // AVD name.
      // List: `avdmanager list avd`  or  Android Studio > Device Manager.
      "emulator": "Pixel_8_Pro",
      "runtimeIdentifiers": "android-x64"
    },
    {
      "isCurrent": true,
      "name": "Android TV emulator",
      "target": "emulator",
      "emulator": "Television_1080p_x64",
      "runtimeIdentifiers": "android-x64"
    }
  ],

  // Build configuration.
  // Available: Debug | Release
  "configuration": "Debug"
}
'@

if (-not (Test-Path -LiteralPath $settingsPath)) {
  Write-Host "Creating default $settingsPath"
  [System.IO.File]::WriteAllText($settingsPath, $defaultSettings)
}

function Read-Jsonc([string]$path) {
  $raw = Get-Content -LiteralPath $path -Raw
  $noBlock = [regex]::Replace($raw, '/\*[\s\S]*?\*/', '')
  $noLine = [regex]::Replace($noBlock, '(?m)^\s*//.*$', '')
  $noTrail = [regex]::Replace($noLine, '(?m)\s//[^"\r\n]*$', '')
  return $noTrail | ConvertFrom-Json
}

$settings = Read-Jsonc $settingsPath

$current = @($settings.targets) | Where-Object { $_.isCurrent } | Select-Object -First 1
if (-not $current) {
  throw "No target with isCurrent = true in '$settingsPath'."
}

if (-not $Target) { $Target = $current.target }
if (-not $Emulator) { $Emulator = $current.emulator }
if (-not $Device) { $Device = $current.device }
if (-not $RuntimeIdentifiers) { $RuntimeIdentifiers = $current.runtimeIdentifiers }
if (-not $Configuration) { $Configuration = $settings.configuration }

if ($Target -ne "emulator" -and $Target -ne "device") {
  throw "Target '$Target' is not supported. Available: emulator | device."
}

$name = if ($current.name) { $current.name } else { $Target }
$where = if ($Target -eq "emulator") { $Emulator } elseif ($Device) { $Device } else { "the only connected device" }
$abis = if ($RuntimeIdentifiers) { $RuntimeIdentifiers } else { "as in csproj" }

Write-Host "Target: $name -> $Target $where ($Configuration, $abis)"

$adbTarget = "-d"
if ($Target -eq "emulator") {
  if (-not $Emulator) {
    throw "Target '$name' has no emulator AVD name."
  }

  & (Join-Path $PSScriptRoot "start-android-emulator.ps1") -AndroidSdk $AndroidSdk -AvdName $Emulator -NoSnapshotLoad
  $adbTarget = "-e"
}
elseif ($Device) {
  $adbTarget = "-s $Device"
  if ($Device -match '^\d{1,3}(\.\d{1,3}){3}:\d+$') {
    $adb = Join-Path $AndroidSdk "platform-tools\adb.exe"
    Write-Host "Connecting Wi-Fi device $Device"
    & $adb connect $Device | Write-Host
  }
}

$project = Join-Path $root "amneziageo-android\AmneziaGeo.Android.Ui\AmneziaGeo.Android.Ui.csproj"

$buildArgs = @(
  "-p:Configuration=$Configuration",
  "-p:AdbTarget=$adbTarget",
  "-p:AndroidAttachDebugger=true",
  "-p:AndroidSdbHostPort=10000",
  "-p:AndroidSdbTargetPort=10000",
  "-p:AndroidSdkDirectory=$AndroidSdk"
)

if ($RuntimeIdentifiers) {
  $buildArgs += "-p:AndroidRuntimeIdentifiers=$RuntimeIdentifiers"
}

Write-Host "Deploying Android: Name=$name Target=$Target Configuration=$Configuration AdbTarget=$adbTarget Abis=$abis"
& dotnet build $project -f net10.0-android -t:Run @buildArgs -v minimal -nologo
exit $LASTEXITCODE
