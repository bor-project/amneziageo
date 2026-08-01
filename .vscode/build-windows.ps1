param(
  [ValidateSet("Debug", "Release")]
  [string]$Configuration,
  [ValidateSet("x64", "arm64")]
  [string]$Platform
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$settingsPath = Join-Path $PSScriptRoot "debug.windows.jsonc"

$defaultSettings = @'
{
  // CPU platform passed to the build (PlatformTarget).
  // Available: x64 | arm64   (shipped native engine deps are x64; arm64 is experimental)
  "platform": "x64",

  // Build configuration for the standalone "Build Windows" task.
  // Available: Debug | Release   ("Debug Windows" always debugs the Debug binary)
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
if (-not $Platform) { $Platform = $settings.platform }
if (-not $Configuration) { $Configuration = $settings.configuration }

$project = Join-Path $root "amneziageo-windows\tools\AmneziaGeo.Windows.Launcher\AmneziaGeo.Windows.Launcher.csproj"

Write-Host "Building Windows: Configuration=$Configuration PlatformTarget=$Platform"
& dotnet build $project -c $Configuration "-p:PlatformTarget=$Platform" -v minimal -nologo
exit $LASTEXITCODE
