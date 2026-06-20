<#
  Builds the AmneziaGeo MSI:
    1. publishes the backend (App) and GUI (Ui) as ONE self-contained win-x64 folder (shared .NET
       runtime + tunnel.dll/wintun.dll + Avalonia natives), so the target machine needs no .NET;
    2. builds the WiX project, harvesting that folder into a per-machine MSI.

  Usage (on the build machine):  pwsh -File build-installer.ps1 [-Configuration Release]
#>
param([string]$Configuration = 'Release')

$ErrorActionPreference = 'Stop'
$rid       = 'win-x64'
$installer = $PSScriptRoot
$root      = Split-Path $installer -Parent          # ...\amneziageo-windows
$stage     = Join-Path $installer 'stage'
$appProj   = Join-Path $root 'AmneziaGeo.Windows.App\AmneziaGeo.Windows.App.csproj'
$uiProj    = Join-Path $root 'AmneziaGeo.Windows.Ui\AmneziaGeo.Windows.Ui.csproj'
$wixProj   = Join-Path $installer 'AmneziaGeo.Windows.Installer.wixproj'

if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
New-Item -ItemType Directory -Force -Path $stage | Out-Null

# App first, UI second, into the SAME folder: the shared framework DLLs are identical (one TFM/RID),
# so the second publish just overwrites them and adds the UI-only assets. No trimming (the UI uses
# reflection-based Avalonia bindings, which trimming would strip).
Write-Host '== publish backend (AmneziaGeo.Windows.App, self-contained) =='
dotnet publish $appProj -c $Configuration -r $rid --self-contained true -p:PublishTrimmed=false -p:PublishSingleFile=false -o $stage
if ($LASTEXITCODE -ne 0) { throw "App publish failed ($LASTEXITCODE)" }

Write-Host '== publish GUI (AmneziaGeo.Windows.Ui, self-contained) =='
dotnet publish $uiProj -c $Configuration -r $rid --self-contained true -p:PublishTrimmed=false -p:PublishSingleFile=false -o $stage
if ($LASTEXITCODE -ne 0) { throw "UI publish failed ($LASTEXITCODE)" }

Write-Host '== build MSI =='
dotnet build $wixProj -c $Configuration -p:StageDir=$stage
if ($LASTEXITCODE -ne 0) { throw "WiX build failed ($LASTEXITCODE)" }

Write-Host '== result =='
Get-ChildItem -Recurse $installer -Filter *.msi | Select-Object FullName, @{N='MB';E={[math]::Round($_.Length/1MB,1)}}
