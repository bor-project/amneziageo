<#
  Builds the installable AmneziaGeo Android APK into dist\, mirroring the Windows installer layout
  (build-installer.ps1 -> dist\AmneziaGeo-<version>-<target>.<ext>).

  Output : dist\AmneziaGeo-<version>-android.apk
  Version: -Version N.N.N.N override, else 0.0.1.<git-commit-count> (same scheme as the Windows bundle);
           all four fields are packed into the Android versionCode.

  The APK consumes the native Go engine (native\<abi>\libamneziawg-go.so); if it is missing the Engine
  build stops with the submodule/build-engine instructions.

  Usage (on the build machine):
    pwsh -File build-apk.ps1 [-c Release|Debug] [-v N.N.N.N] [-UpdateUrl <url>] [-Prerelease]
  The update URL is baked into the package: the application reads it to offer its own update.
  Signing takes the SDK debug key unless ANDROID_KEYSTORE names a keystore, in which case ANDROID_KEY_ALIAS,
  ANDROID_STORE_PASS and ANDROID_KEY_PASS have to be set as well; a debug-signed build never updates over a
  release. Both configurations install as-is; Release is AOT-compiled and starts
  about twice as fast on a weak TV, so it is the default. Release is not debuggable: run-as cannot reach the
  application's private files, so set the device up through the interface and read results from logcat.
  Set AndroidRuntimeIdentifiers to one ABI (e.g. android-arm) to cut the Release build to about a minute.
#>
param(
  [Alias('c')][ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
  [Alias('v')][string]$Version,
  [string]$UpdateUrl = 'https://github.com/bor-project/amneziageo/releases/latest/download/update.json',
  [switch]$Prerelease
)

$ErrorActionPreference = 'Stop'

$tools = $PSScriptRoot
$root  = Split-Path $tools -Parent
$ui    = Join-Path $root 'AmneziaGeo.Android.Ui\AmneziaGeo.Android.Ui.csproj'
$dist  = Join-Path $tools 'dist'

if ($Version) {
  $version = $Version
}
else {
  $count = (& git -C $tools rev-list --count HEAD 2>$null | Out-String).Trim()
  if (-not $count) { $count = '0' }
  $version = "0.0.1.$count"
}
if ($version -notmatch '^(\d|1\d|20)\.\d{1,2}\.\d{1,2}\.\d{1,4}$') {
  throw "Invalid version '$version' (expected N.N.N.N, up to 20.99.99.9999)."
}

# Packs all four fields into the versionCode, which Android requires to grow with every update: taking the
# last field alone made a release reset it, and the device turned the new package down as a downgrade.
$field = $version.Split('.') | ForEach-Object { [int]$_ }
$code = $field[0] * 100000000 + $field[1] * 1000000 + $field[2] * 10000 + $field[3]
if ($code -lt 1) { $code = 1 }
Write-Host "== apk version $version (versionCode $code), configuration $Configuration =="

New-Item -ItemType Directory -Force -Path $dist | Out-Null

# Release keystore from the environment; the passwords stay in variables, off the command line.
$sign = @()
if ($env:ANDROID_KEYSTORE) {
  if (-not $env:ANDROID_KEY_ALIAS) { throw 'ANDROID_KEY_ALIAS is not set.' }
  Write-Host "   signing with $env:ANDROID_KEYSTORE"
  $sign = @(
    '-p:AndroidKeyStore=true',
    "-p:AndroidSigningKeyStore=$env:ANDROID_KEYSTORE",
    "-p:AndroidSigningKeyAlias=$env:ANDROID_KEY_ALIAS",
    '-p:AndroidSigningStorePass=env:ANDROID_STORE_PASS',
    '-p:AndroidSigningKeyPass=env:ANDROID_KEY_PASS'
  )
}
else {
  Write-Host '   signing with the SDK debug key - a build from another machine will not upgrade over it'
}

Write-Host '== build + sign APK =='
& dotnet build $ui -c $Configuration -f net10.0-android -t:SignAndroidPackage `
  -p:AndroidPackageFormat=apk `
  -p:EmbedAssembliesIntoApk=true `
  "-p:ApplicationDisplayVersion=$version" `
  "-p:ApplicationVersion=$code" `
  "-p:UpdateUrl=$UpdateUrl" `
  "-p:AllowPrerelease=$(if ($Prerelease) { '1' } else { '' })" `
  @sign `
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
