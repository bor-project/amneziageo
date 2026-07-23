<#
  Builds AmneziaGeo.Android.Engine\native\<abi>\libamneziawg-go.so with the Android NDK toolchain.

  The amneziawg-go submodule is NOT modified: the c-shared entry points live in the separate
  libamneziawg-go module next to it, which pulls the submodule in through a replace directive.
  The output is the gitignored native\<abi>\libamneziawg-go.so that AmneziaGeo.Android.Engine.csproj
  already consumes - the same arrangement as x64\tunnel.dll on Windows.

  Usage (on the build machine):
    powershell -NoProfile -ExecutionPolicy Bypass -File build-engine-android.ps1
    powershell -NoProfile -ExecutionPolicy Bypass -File build-engine-android.ps1 -Abi arm64-v8a,x86_64,armeabi-v7a,x86
#>
param(
    [ValidateSet('arm64-v8a', 'x86_64', 'armeabi-v7a', 'x86')]
    [string[]]$Abi = @('arm64-v8a', 'x86_64'),
    [int]$ApiLevel = 24,
    [string]$NdkRoot
)

$ErrorActionPreference = 'Stop'

$toolsDir  = $PSScriptRoot
$android   = Split-Path $toolsDir -Parent
$module    = Join-Path $android 'libamneziawg-go'
$submodule = Join-Path $android 'amneziawg-go'
$outRoot   = Join-Path $android 'AmneziaGeo.Android.Engine\native'

if (-not (Test-Path (Join-Path $submodule 'go.mod'))) {
    throw "amneziawg-go submodule not found at $submodule (run: git submodule update --init --recursive)"
}

$targets = @{
    'arm64-v8a'   = @{ GoArch = 'arm64'; GoArm = '';  Triple = 'aarch64-linux-android' }
    'x86_64'      = @{ GoArch = 'amd64'; GoArm = '';  Triple = 'x86_64-linux-android' }
    'armeabi-v7a' = @{ GoArch = 'arm';   GoArm = '7'; Triple = 'armv7a-linux-androideabi' }
    'x86'         = @{ GoArch = '386';   GoArm = '';  Triple = 'i686-linux-android' }
}

# ---- 1. locate the NDK ----
function Find-Ndk {
    if ($NdkRoot) { return $NdkRoot }
    foreach ($v in @($env:ANDROID_NDK_HOME, $env:ANDROID_NDK_ROOT)) {
        if ($v -and (Test-Path $v)) { return $v }
    }
    $roots = @(
        (Join-Path $env:LOCALAPPDATA 'Android\Sdk\ndk'),
        $(if ($env:ANDROID_HOME) { Join-Path $env:ANDROID_HOME 'ndk' }),
        $(if ($env:ANDROID_SDK_ROOT) { Join-Path $env:ANDROID_SDK_ROOT 'ndk' }),
        'C:\Program Files (x86)\Android\android-sdk\ndk',
        'C:\Microsoft\AndroidNDK\android-ndk',
        'C:\Microsoft\AndroidNDK'
    ) | Where-Object { $_ -and (Test-Path $_) }

    foreach ($r in $roots) {
        $newest = Get-ChildItem -Directory $r -ErrorAction SilentlyContinue |
                  Where-Object { Test-Path (Join-Path $_.FullName 'toolchains\llvm\prebuilt') } |
                  Sort-Object { try { [version]($_.Name -replace '^android-ndk-r?', '') } catch { [version]'0.0' } } |
                  Select-Object -Last 1
        if ($newest) { return $newest.FullName }
    }
    return $null
}

$ndk = Find-Ndk
if (-not $ndk) {
    throw "Android NDK not found. Set ANDROID_NDK_HOME or pass -NdkRoot <path>."
}

$ndkBin = Join-Path $ndk 'toolchains\llvm\prebuilt\windows-x86_64\bin'
if (-not (Test-Path $ndkBin)) { throw "no windows-x86_64 toolchain inside $ndk" }

foreach ($a in $Abi) {
    $cc = Join-Path $ndkBin "$($targets[$a].Triple)$ApiLevel-clang.cmd"
    if (-not (Test-Path $cc)) { throw "no compiler for $a at API $ApiLevel`: $cc" }
}

$goExe = (Get-Command go.exe -ErrorAction SilentlyContinue).Source
if (-not $goExe) { throw 'go.exe not found in PATH' }

# ---- 2. build ----
$saved = @{}
foreach ($v in @('GOOS', 'GOARCH', 'GOARM', 'GOCACHE', 'GOTOOLCHAIN', 'GOFLAGS', 'CGO_ENABLED', 'CGO_LDFLAGS', 'CC', 'CXX')) {
    $saved[$v] = [Environment]::GetEnvironmentVariable($v)
}

try {
    $env:GOOS        = 'android'
    # without this a go.mod toolchain bump would silently download another Go
    $env:GOTOOLCHAIN = 'local'
    $env:GOFLAGS     = '-mod=readonly'
    $env:CGO_ENABLED = '1'
    # set here rather than in a #cgo directive: cgo rejects -Wl,-z from source, trusts the environment
    $env:CGO_LDFLAGS = '-Wl,-z,max-page-size=16384'

    Write-Host ''
    Write-Host "== toolchain: $(& $goExe version) =="
    Write-Host "   NDK $ndk (API $ApiLevel)"

    Push-Location $module
    try {
        foreach ($a in $Abi) {
            $env:GOARCH = $targets[$a].GoArch
            $env:GOARM  = $targets[$a].GoArm
            $env:CC     = Join-Path $ndkBin "$($targets[$a].Triple)$ApiLevel-clang.cmd"
            $env:CXX    = Join-Path $ndkBin "$($targets[$a].Triple)$ApiLevel-clang++.cmd"

            $outDir = Join-Path $outRoot $a
            New-Item -ItemType Directory -Force -Path $outDir | Out-Null
            $out = Join-Path $outDir 'libamneziawg-go.so'

            Write-Host "== building $a\libamneziawg-go.so =="
            & $goExe build -buildmode c-shared -ldflags '-w -s' -trimpath -o $out
            if ($LASTEXITCODE -ne 0) { throw "go build failed for $a ($LASTEXITCODE)" }

            $header = Join-Path $outDir 'libamneziawg-go.h'
            if (Test-Path $header) { Remove-Item -Force $header }
        }
    } finally { Pop-Location }
} finally {
    foreach ($v in $saved.Keys) { [Environment]::SetEnvironmentVariable($v, $saved[$v]) }
}

# ---- 3. report what came out ----

$machines = @{ 0x03 = 'i386'; 0x28 = 'arm'; 0x3E = 'x86-64'; 0xB7 = 'aarch64' }

# ELF machine plus the largest PT_LOAD alignment - Android 15 devices require 16 KB pages
function Get-ElfInfo($b) {
    $is64 = $b[4] -eq 2
    $machine = [BitConverter]::ToUInt16($b, 18)
    $phOff = $(if ($is64) { [BitConverter]::ToInt64($b, 0x20) } else { [BitConverter]::ToUInt32($b, 0x1C) })
    $phEntSize = [BitConverter]::ToUInt16($b, $(if ($is64) { 0x36 } else { 0x2A }))
    $phNum = [BitConverter]::ToUInt16($b, $(if ($is64) { 0x38 } else { 0x2C }))

    $align = 0
    for ($i = 0; $i -lt $phNum; $i++) {
        $p = [int]$phOff + $i * $phEntSize
        if ([BitConverter]::ToUInt32($b, $p) -ne 1) { continue }
        $a = $(if ($is64) { [BitConverter]::ToInt64($b, $p + 48) } else { [BitConverter]::ToUInt32($b, $p + 28) })
        if ($a -gt $align) { $align = $a }
    }
    return @{ Machine = $machines[[int]$machine]; Align = $align }
}

Write-Host ''
Write-Host '== result =='
$exports = @('wgTurnOn', 'wgTurnOff', 'wgGetSocketV4', 'wgGetConfig')
foreach ($a in $Abi) {
    $so = Join-Path $outRoot "$a\libamneziawg-go.so"
    $b = [IO.File]::ReadAllBytes($so)
    $text = [Text.Encoding]::GetEncoding(28591).GetString($b)
    $elf = Get-ElfInfo $b
    $missing = $exports | Where-Object { -not $text.Contains($_) }

    Write-Host ("   {0}  {1:N0} bytes  {2}" -f $so, $b.Length, $elf.Machine)
    Write-Host ("   max PT_LOAD align : {0} bytes{1}" -f $elf.Align, $(if ($elf.Align -lt 16384) { ' - NOT 16 KB page safe' } else { '' }))
    Write-Host ("   exports           : {0}" -f $(if ($missing) { "MISSING $($missing -join ', ')" } else { 'all present' }))
}
Write-Host ''
Write-Host 'Now build the app - AmneziaGeo.Android.Engine.csproj picks these .so up automatically.'
