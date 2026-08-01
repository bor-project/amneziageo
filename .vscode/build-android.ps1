param(
  [ValidateSet("emulator", "device")]
  [string]$Target,
  [string]$Emulator,
  [string]$Device,
  [ValidateSet("Debug", "Release")]
  [string]$Configuration,
  [string]$AndroidSdk = "C:\Users\admin\AppData\Local\Android\Sdk"
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$settingsPath = Join-Path $PSScriptRoot "debug.android.jsonc"

$defaultSettings = @'
{
  // Deploy target.
  // Available: emulator | device
  "target": "emulator",

  // AVD name when target = emulator.
  // List: `avdmanager list avd`  or  Android Studio > Device Manager.
  "emulator": "Pixel_8_Pro",

  // Device serial when target = device.
  // List: `adb devices`.   Empty = the single connected device.
  "device": "",

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
if (-not $Target) { $Target = $settings.target }
if (-not $Emulator) { $Emulator = $settings.emulator }
if (-not $Device) { $Device = $settings.device }
if (-not $Configuration) { $Configuration = $settings.configuration }

$adbTarget = "-d"
if ($Target -eq "emulator") {
  & (Join-Path $PSScriptRoot "start-android-emulator.ps1") -AndroidSdk $AndroidSdk -AvdName $Emulator -NoSnapshotLoad
  $adbTarget = "-e"
}
elseif ($Device) {
  $adbTarget = "-s $Device"
}

$project = Join-Path $root "amneziageo-android\AmneziaGeo.Android.Ui\AmneziaGeo.Android.Ui.csproj"

Write-Host "Deploying Android: Target=$Target Configuration=$Configuration AdbTarget=$adbTarget"
& dotnet build $project -f net10.0-android -t:Run `
  "-p:Configuration=$Configuration" `
  "-p:AdbTarget=$adbTarget" `
  -p:AndroidAttachDebugger=true `
  -p:AndroidSdbHostPort=10000 `
  -p:AndroidSdbTargetPort=10000 `
  "-p:AndroidSdkDirectory=$AndroidSdk" `
  -v minimal -nologo
exit $LASTEXITCODE
