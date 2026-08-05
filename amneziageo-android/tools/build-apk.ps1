<#
  Builds the installable AmneziaGeo Android APK into dist\, mirroring the Windows installer layout
  (build-installer.ps1 -> dist\AmneziaGeo-<version>-<target>.<ext>).

  Output : dist\AmneziaGeo-<version>-android.apk
  Version: -Version N.N.N.N override, else 0.0.1.<git-commit-count> (same scheme as the Windows bundle);
           the last field also becomes the Android versionCode.

  The APK consumes the native Go engine (native\<abi>\libamneziawg-go.so); if it is missing the Engine
  build stops with the submodule/build-engine instructions.

  Usage (on the build machine):
    pwsh -File build-apk.ps1 [-c Release|Debug] [-v N.N.N.N]
  Both configurations are signed with the SDK debug key and install as-is; Release is AOT-compiled and starts
  about twice as fast on a weak TV, so it is the default. Release is not debuggable: run-as cannot reach the
  application's private files, so set the device up through the interface and read results from logcat.
  Set AndroidRuntimeIdentifiers to one ABI (e.g. android-arm) to cut the Release build to about a minute.
#>
param(
  [Alias('c')][ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
  [Alias('v')][string]$Version
)

$ErrorActionPreference = 'Stop'

$tools = $PSScriptRoot
$root  = Split-Path $tools -Parent
$ui    = Join-Path $root 'AmneziaGeo.Android.Ui\AmneziaGeo.Android.Ui.csproj'
$dist  = Join-Path $tools 'dist'

if ($Version) {
  if ($Version -notmatch '^\d+\.\d+\.\d+\.\d+$') { throw "Invalid -Version '$Version' (expected N.N.N.N)." }
  $version = $Version
}
else {
  $count = (& git -C $tools rev-list --count HEAD 2>$null | Out-String).Trim()
  if (-not $count) { $count = '0' }
  $version = "0.0.1.$count"
}
$code = [int]($version.Split('.')[-1])
if ($code -lt 1) { $code = 1 }
Write-Host "== apk version $version (versionCode $code), configuration $Configuration =="

New-Item -ItemType Directory -Force -Path $dist | Out-Null

Write-Host '== build + sign APK =='
& dotnet build $ui -c $Configuration -f net10.0-android -t:SignAndroidPackage `
  -p:AndroidPackageFormat=apk `
  -p:EmbedAssembliesIntoApk=true `
  "-p:ApplicationDisplayVersion=$version" `
  "-p:ApplicationVersion=$code" `
  -v minimal -nologo
if ($LASTEXITCODE -ne 0) { throw "APK build failed ($LASTEXITCODE)" }

$binDir = Join-Path $root "AmneziaGeo.Android.Ui\bin\$Configuration\net10.0-android"
$apk = Get-ChildItem -Recurse $binDir -Filter *-Signed.apk -ErrorAction SilentlyContinue |
       Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $apk) {
  $apk = Get-ChildItem -Recurse $binDir -Filter *.apk -ErrorAction SilentlyContinue |
         Sort-Object LastWriteTime -Descending | Select-Object -First 1
}
if (-not $apk) { throw "APK not found under $binDir after build." }

$distName = "AmneziaGeo-$version-android.apk"
Copy-Item -Force $apk.FullName (Join-Path $dist $distName)
Write-Host "   -> dist\$distName  ($([math]::Round($apk.Length / 1MB, 1)) MB)"

Write-Host ''
Write-Host '== result (dist) =='
Get-ChildItem $dist -Filter *.apk | Select-Object Name, @{N = 'MB'; E = { [math]::Round($_.Length / 1MB, 1) } }
