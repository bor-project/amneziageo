<#
  Builds the AmneziaGeo setup bundle (AmneziaGeoSetup.exe):
    1. publishes the single-process host (AmneziaGeo.exe = agent in-process + tray UI) as ONE
       self-contained win-x64 folder (stage),
    2. builds the per-machine MSI from that stage (AmneziaGeo.Windows.Installer.Package),
    3. publishes the WPF bootstrapper application self-contained (AmneziaGeo.Windows.Installer),
    4. generates a PayloadGroup over the BA publish folder (Burn does not auto-harvest it),
    5. builds the Burn bundle that hosts the BA and chains the MSI.

  The bundle Version is 1.0.1.<git-commit-count> so every build is strictly newer to Burn; combined
  with the MSI MajorUpgrade/@AllowDowngrades this makes reinstall-same-code, update and downgrade all
  work.

  Usage (on the build machine):  pwsh -File build-installer.ps1 [-Configuration Release]
#>
param([string]$Configuration = 'Release')

$ErrorActionPreference = 'Stop'
$rid = 'win-x64'

$bundleDir = $PSScriptRoot
$win       = Split-Path $bundleDir -Parent                       # ...\amneziageo-windows

$hostProj   = Join-Path $win 'tools\AmneziaGeo.Windows.Launcher\AmneziaGeo.Windows.Launcher.csproj'
$baProj     = Join-Path $win 'AmneziaGeo.Windows.Installer\AmneziaGeo.Windows.Installer.csproj'
$msiProj    = Join-Path $win 'AmneziaGeo.Windows.Installer.Package\AmneziaGeo.Windows.Installer.Package.wixproj'
$bundleProj = Join-Path $bundleDir 'AmneziaGeo.Windows.Installer.Bundle.wixproj'

$stage     = Join-Path $bundleDir 'stage'
$baPublish = Join-Path $bundleDir 'ba-publish'
$genWxs    = Join-Path $bundleDir 'BaPayloads.generated.wxs'
$baExeName = 'AmneziaGeo.Windows.Installer.exe'

# ---- version: 1.0.1.<commit count> (4th field always increasing) ----
$build = (& git -C $win rev-list --count HEAD).Trim()
if (-not $build) { $build = '0' }
$version = "1.0.1.$build"
Write-Host "== bundle version $version =="

# ---- 1. stage the self-contained app (App first, UI second, same folder) ----
if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
New-Item -ItemType Directory -Force -Path $stage | Out-Null

Write-Host '== publish single-process host (AmneziaGeo.exe, self-contained) =='
dotnet publish $hostProj -c $Configuration -r $rid --self-contained true -p:PublishTrimmed=false -p:PublishSingleFile=false -p:Version=$version -o $stage
if ($LASTEXITCODE -ne 0) { throw "Host publish failed ($LASTEXITCODE)" }

# Ship a clean Launcher config: the dev appsettings seeds test profiles/lists; blank it so the shipped
# product only hosts the agent + UI (LauncherOptions defaults: RunService + RunUi, no seeding).
Set-Content -Path (Join-Path $stage 'appsettings.json') -Value '{}' -Encoding UTF8

# ---- 2. build the MSI from the stage ----
Write-Host '== build MSI =='
dotnet build $msiProj -c $Configuration -p:StageDir=$stage
if ($LASTEXITCODE -ne 0) { throw "MSI build failed ($LASTEXITCODE)" }

$msiBin = Join-Path (Split-Path $msiProj -Parent) 'bin'
$msi = Get-ChildItem -Recurse $msiBin -Filter *.msi -ErrorAction SilentlyContinue |
       Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $msi) { throw 'MSI not found under bin after build.' }
Write-Host "   MSI: $($msi.FullName)"

# ---- 3. publish the bootstrapper application (self-contained, so no .NET needed to show the UI) ----
if (Test-Path $baPublish) { Remove-Item -Recurse -Force $baPublish }
Write-Host '== publish bootstrapper application (self-contained) =='
dotnet publish $baProj -c $Configuration -r $rid --self-contained true -p:PublishTrimmed=false -p:PublishSingleFile=false -o $baPublish
if ($LASTEXITCODE -ne 0) { throw "BA publish failed ($LASTEXITCODE)" }

$baExe = Join-Path $baPublish $baExeName
if (-not (Test-Path $baExe)) { throw "BA exe not found: $baExe" }

# ---- 4. generate the PayloadGroup over the BA publish folder (every file except the BA exe) ----
Write-Host '== generate BA payload group =='
$prefix = $baPublish.TrimEnd('\') + '\'
$lines = [System.Collections.Generic.List[string]]::new()
$lines.Add('<?xml version="1.0" encoding="utf-8"?>')
$lines.Add('<Wix xmlns="http://wixtoolset.org/schemas/v4/wxs">')
$lines.Add('  <Fragment>')
$lines.Add('    <PayloadGroup Id="BaPayloads">')
foreach ($f in Get-ChildItem -Recurse -File $baPublish) {
    $rel = $f.FullName.Substring($prefix.Length)
    if ($rel -ieq $baExeName) { continue }
    $name = [System.Security.SecurityElement]::Escape($rel)
    $src  = [System.Security.SecurityElement]::Escape($f.FullName)
    $lines.Add("      <Payload Name=`"$name`" SourceFile=`"$src`" />")
}
$lines.Add('    </PayloadGroup>')
$lines.Add('  </Fragment>')
$lines.Add('</Wix>')
Set-Content -Path $genWxs -Value $lines -Encoding UTF8

# ---- 5. build the bundle ----
Write-Host '== build bundle =='
dotnet build $bundleProj -c $Configuration -p:Platform=x64 -p:BundleVersion=$version -p:BaExe=$baExe -p:MsiPath=$($msi.FullName)
if ($LASTEXITCODE -ne 0) { throw "Bundle build failed ($LASTEXITCODE)" }

Write-Host '== result =='
Get-ChildItem -Recurse $bundleDir -Filter AmneziaGeoSetup.exe |
    Select-Object FullName, @{N='MB';E={[math]::Round($_.Length/1MB,1)}}
