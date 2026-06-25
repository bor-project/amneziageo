<#
  Builds a SEPARATE, PRECONFIGURED AmneziaGeo MSI (AmneziaGeo-preconfigured-*.msi):
    1. publishes the LAUNCHER (tools\AmneziaGeo.Windows.Launcher -> AmneziaGeo.exe) as ONE self-contained
       win-x64 folder (stage). The launcher hosts the agent in-process + tray UI and SEEDS itself from
       appsettings.json on first launch.
    2. overwrites the staged appsettings.json with a seed (the bundled config + routing list + profile +
       WebSocket transport with its auth token), and drops the bundled .conf into stage\seed\.
    3. builds the per-machine MSI from that stage (AmneziaGeo.Windows.Installer.Package.Preconfigured).

  The result installs a client that is ready to connect: on first run it registers the config, applies the
  routing/profile, and turns on the WebSocket (wstunnel) transport with the preset token. The first-run
  seed is guarded by a ProgramData marker (LauncherOptions.SeedOnce) so the user's later changes survive.

  This is a TEST/DISTRIBUTION artifact, separate from build-installer.ps1 (the normal service build). It
  shares the product UpgradeCode + ProgramData state with the normal build, so the two are mutually
  exclusive (installing one replaces the other).

  Usage (on the build machine):
    powershell -File build-installer-preconfigured.ps1 [-Configuration Release]
        [-ConfigPath <path\to.conf>] [-WsHost <host>] [-WsPort <port>] [-WsToken <token>] [-WsOff]

  -WsToken is the wstunnel path-prefix auth token. It is intentionally NOT defaulted here (a secret must
  not live in source); pass it for a token-gated server, e.g. -WsToken ag-xxxxxxxx. Omit it for a server
  with no token (the WebSocket address is then the bare host with no secret path).
#>
param(
    [string]$Configuration = 'Release',
    [string]$ConfigPath,
    [string]$WsHost = 'vpn.example.com',
    [int]$WsPort = 443,
    [string]$WsToken = '',
    [switch]$WsOff
)

$ErrorActionPreference = 'Stop'
$rid = 'win-x64'

$bundleDir = $PSScriptRoot                                   # ...\AmneziaGeo.Windows.Installer.Bundle
$win       = Split-Path $bundleDir -Parent                   # ...\amneziageo-windows
$repo      = Split-Path $win -Parent                         # ...\amneziageo

if (-not $ConfigPath) { $ConfigPath = Join-Path $repo '.claude\bor123.conf' }
if (-not (Test-Path $ConfigPath)) { throw "config not found: $ConfigPath" }

$launcherProj = Join-Path $win 'tools\AmneziaGeo.Windows.Launcher\AmneziaGeo.Windows.Launcher.csproj'
$msiProj      = Join-Path $win 'AmneziaGeo.Windows.Installer.Package.Preconfigured\AmneziaGeo.Windows.Installer.Package.Preconfigured.wixproj'

$stage    = Join-Path $bundleDir 'stage-preconfigured'
$confFile = Split-Path $ConfigPath -Leaf                     # bor123.conf
$confName = [System.IO.Path]::GetFileNameWithoutExtension($confFile)  # bor123

# ---- installer config (installer.config.json): icon embedded into AmneziaGeo.exe + the ARP/shortcuts;
# updateUrl baked into the app; signingCert signs the MSI. Each optional - null/empty leaves it off. ----
$cfgPath = Join-Path $bundleDir 'installer.config.json'
$cfg = if (Test-Path $cfgPath) { Get-Content $cfgPath -Raw | ConvertFrom-Json } else { $null }

$updateUrl = if ($cfg -and $cfg.updateUrl) { [string]$cfg.updateUrl } else { '' }
$updateProps = if ($updateUrl) { @("-p:UpdateUrl=$updateUrl") } else { @() }

$iconAbs = ''
if ($cfg -and $cfg.iconPath) {
    $p = [string]$cfg.iconPath
    $iconAbs = if ([System.IO.Path]::IsPathRooted($p)) { $p } else { Join-Path $bundleDir $p }
    $iconAbs = [System.IO.Path]::GetFullPath($iconAbs)
    if (-not (Test-Path $iconAbs)) { throw "iconPath '$p' set in installer.config.json but not found at $iconAbs" }
}
$hasIcon = if ($iconAbs) { 'true' } else { 'false' }
$iconProps = if ($hasIcon -eq 'true') { @('-p:HasIcon=true', "-p:IconFile=$iconAbs") } else { @('-p:HasIcon=false') }
Write-Host "== config: icon=$(if ($hasIcon -eq 'true') { $iconAbs } else { '(none)' }); updateUrl=$(if ($updateUrl) { $updateUrl } else { '(none)' }); signing=$(if ($cfg -and $cfg.signingCert) { 'on' } else { 'off' }) =="

function Invoke-Sign([string]$file) {
    if (-not ($cfg -and $cfg.signingCert)) { return }
    $sc = $cfg.signingCert
    $a = @('sign', '/fd', 'SHA256')
    if ($sc.timestampUrl) { $a += @('/tr', [string]$sc.timestampUrl, '/td', 'SHA256') }
    if ($sc.thumbprint) { $a += @('/sha1', [string]$sc.thumbprint) }
    elseif ($sc.pfxPath) { $a += @('/f', [string]$sc.pfxPath); if ($sc.password) { $a += @('/p', [string]$sc.password) } }
    else { Write-Host '   signingCert set but neither thumbprint nor pfxPath given - skipping signing'; return }
    $a += $file
    Write-Host "== sign $([System.IO.Path]::GetFileName($file)) =="
    & signtool.exe @a
    if ($LASTEXITCODE -ne 0) { throw "signtool failed on $file ($LASTEXITCODE)" }
}

# ---- version: 1.0.1.<commit count> (4th field always increasing) ----
$build = (& git -C $win rev-list --count HEAD).Trim()
if (-not $build) { $build = '0' }
$version = "1.0.1.$build"
Write-Host "== preconfigured bundle version $version =="

# ---- 1. stage the self-contained launcher publish ----
if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
New-Item -ItemType Directory -Force -Path $stage | Out-Null

Write-Host '== publish launcher (AmneziaGeo.exe, self-contained) =='
dotnet publish $launcherProj -c $Configuration -r $rid --self-contained true -p:PublishTrimmed=false -p:PublishSingleFile=false -p:Version=$version $updateProps $iconProps -o $stage
if ($LASTEXITCODE -ne 0) { throw "launcher publish failed ($LASTEXITCODE)" }

# ---- 2. stage the seed (appsettings.json + seed\<conf>) ----
# Drop any dev override that the publish carried (it points at a build-machine path and is dev-only).
$devOverride = Join-Path $stage 'appsettings.Development.json'
if (Test-Path $devOverride) { Remove-Item -Force $devOverride }

$wsEnabled = if ($WsOff) { 'false' } else { 'true' }
$wsHostValue = if ([string]::IsNullOrWhiteSpace($WsToken)) { $WsHost } else { "wss://${WsHost}:${WsPort}/${WsToken}" }

$seedJson = @"
{
  "Launcher": {
    "Target": "main",
    "RunService": true,
    "RunUi": true,
    "SeedOnce": true,
    "ConfigPaths": [ "seed\\$confFile" ],
    "RoutingLists": [
      { "Name": "openai-route", "Rules": [ "geosite:openai" ] }
    ],
    "Profiles": [
      { "Name": "main", "Mode": "priority", "RecheckSeconds": 60, "Members": [ "$confName" ], "RoutingList": "openai-route", "UseRouting": true }
    ],
    "WebSockets": [
      { "Config": "$confName", "Enabled": $wsEnabled, "Port": $WsPort, "Host": "$wsHostValue" }
    ]
  }
}
"@
Set-Content -Path (Join-Path $stage 'appsettings.json') -Value $seedJson -Encoding UTF8

$seedDir = Join-Path $stage 'seed'
New-Item -ItemType Directory -Force -Path $seedDir | Out-Null
Copy-Item -Force $ConfigPath (Join-Path $seedDir $confFile)
Write-Host "== seeded: config '$confName', WebSocket enabled=$wsEnabled, host=$wsHostValue =="

# ---- 3. build the MSI from the stage ----
Write-Host '== build preconfigured MSI =='
dotnet build $msiProj -c $Configuration -p:StageDir=$stage "-p:HasIcon=$hasIcon"
if ($LASTEXITCODE -ne 0) { throw "MSI build failed ($LASTEXITCODE)" }

$msiBin = Join-Path (Split-Path $msiProj -Parent) 'bin'
$msi = Get-ChildItem -Recurse $msiBin -Filter *.msi -ErrorAction SilentlyContinue |
       Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $msi) { throw 'MSI not found under bin after build.' }
Invoke-Sign $msi.FullName

Write-Host '== result =='
$msi | Select-Object FullName, @{N='MB';E={[math]::Round($_.Length/1MB,1)}}
