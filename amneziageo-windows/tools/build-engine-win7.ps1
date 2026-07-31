<#
  Builds <arch>\tunnel.dll with a Windows 7 compatible Go toolchain (thongtech/go-legacy-win7).

  The amneziawg-windows submodule is NOT modified: the toolchain is unpacked into its gitignored
  .deps\go-win7 (the stock .deps\go stays where build.cmd put it) and the output is the same
  gitignored <arch>\tunnel.dll that App.csproj already consumes. Build flags mirror build.cmd.

  Every run checks the .deps artifacts (stock Go, llvm-mingw, wintun) and re-bootstraps them through
  the submodule's own build.cmd when anything is missing; the win7 toolchain and the Go caches are
  carried across that wipe. build.cmd also produces a stock tunnel.dll, which this script overwrites.

  Usage (on the build machine):
    powershell -NoProfile -ExecutionPolicy Bypass -File build-engine-win7.ps1
    powershell -NoProfile -ExecutionPolicy Bypass -File build-engine-win7.ps1 -Upstream   # stock toolchain, for A/B
#>
param(
    [string]$GoVersion = '1.25.12-1',
    [string]$Sha256 = '01dde86e7b8e9d2e9617d68afb90af377e82da22509889402788f23971466fb0',
    [string[]]$Arch = @('x64'),
    [switch]$Upstream,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'

$toolsDir  = $PSScriptRoot
$win       = Split-Path $toolsDir -Parent
$submodule = Join-Path $win 'amneziawg-windows'
$deps      = Join-Path $submodule '.deps'

if (-not (Test-Path (Join-Path $submodule 'build.cmd'))) {
    throw "amneziawg-windows submodule not found at $submodule (run: git submodule update --init --recursive)"
}

$targets = @{
    'x64'   = @{ GoArch = 'amd64'; Cc = 'x86_64-w64-mingw32-gcc';  Wintun = 'amd64' }
    'x86'   = @{ GoArch = '386';   Cc = 'i686-w64-mingw32-gcc';    Wintun = 'x86' }
    'arm64' = @{ GoArch = 'arm64'; Cc = 'aarch64-w64-mingw32-gcc'; Wintun = 'arm64' }
}
foreach ($a in $Arch) {
    if (-not $targets.ContainsKey($a)) { throw "Invalid -Arch '$a' (expected x64, x86 or arm64)." }
}

# ---- 1. bootstrap .deps (Go, llvm-mingw, wintun) through the submodule's own build.cmd ----

# what a completed build.cmd bootstrap leaves behind for the requested arches
function Get-MissingDeps {
    $expected = @(Join-Path $deps 'go\bin\go.exe')
    foreach ($a in $Arch) {
        $expected += Join-Path $deps "llvm-mingw\bin\$($targets[$a].Cc).exe"
        $expected += Join-Path $deps "wintun\bin\$($targets[$a].Wintun)\wintun.dll"
    }
    # the .deps\prepared marker is deliberately not checked: it only tells build.cmd whether to redownload
    return @($expected | Where-Object { -not (Test-Path $_) })
}

$missing = Get-MissingDeps
if ($missing) {
    Write-Host '== .deps incomplete - wiping and re-bootstrapping via the submodule build.cmd =='
    foreach ($m in $missing) {
        Write-Host "   missing: $m"
    }

    # build.cmd wipes .deps whole, so park what it never downloads outside of it
    $keepDir = Join-Path $submodule '.deps-keep'
    $keep = @('go-win7', 'gocache-win7', 'gocache-upstream', 'gopath')
    $keep += @(Get-ChildItem -Path $deps -Filter 'go-legacy-win7-*.zip' -ErrorAction SilentlyContinue | ForEach-Object Name)

    if (Test-Path $keepDir) { Remove-Item -Recurse -Force $keepDir }
    New-Item -ItemType Directory -Force -Path $keepDir | Out-Null
    foreach ($k in $keep) {
        $src = Join-Path $deps $k
        if (Test-Path $src) { Move-Item -Force $src (Join-Path $keepDir $k) }
    }

    try {
        # drop the marker so build.cmd takes its :installdeps path
        $marker = Join-Path $deps 'prepared'
        if (Test-Path $marker) { Remove-Item -Force $marker }

        Push-Location $submodule
        try {
            & cmd.exe /c build.cmd
            if ($LASTEXITCODE -ne 0) { throw "build.cmd bootstrap failed ($LASTEXITCODE)" }
        } finally { Pop-Location }
    } finally {
        New-Item -ItemType Directory -Force -Path $deps | Out-Null
        foreach ($k in $keep) {
            $src = Join-Path $keepDir $k
            if (Test-Path $src) { Move-Item -Force $src (Join-Path $deps $k) }
        }
        if (Test-Path $keepDir) { Remove-Item -Recurse -Force $keepDir }
    }

    $missing = Get-MissingDeps
    if ($missing) {
        throw "bootstrap finished but these are still missing:`n  $($missing -join "`n  ")`nA download was rejected - check that go.dev, download.wireguard.com and wintun.net are reachable from here (a captive portal or filter answers with an HTML page, whose SHA256 never matches), or drop the file in by hand."
    }
}

# ---- 2. fetch and verify the Windows 7 toolchain ----
$goRoot = if ($Upstream) { Join-Path $deps 'go' } else { Join-Path $deps 'go-win7' }

if (-not $Upstream) {
    if ($Force -and (Test-Path $goRoot)) { Remove-Item -Recurse -Force $goRoot }

    if (-not (Test-Path (Join-Path $goRoot 'bin\go.exe'))) {
        $name = "go-legacy-win7-$GoVersion.windows_amd64.zip"
        $url  = "https://github.com/thongtech/go-legacy-win7/releases/download/v$GoVersion/$name"
        $zip  = Join-Path $deps $name

        if (-not (Test-Path $zip)) {
            Write-Host "== downloading $name =="
            if (Get-Command curl.exe -ErrorAction SilentlyContinue) {
                & curl.exe -#fLo $zip $url
                if ($LASTEXITCODE -ne 0) { throw "download failed ($LASTEXITCODE): $url" }
            } else {
                $prev = $ProgressPreference; $ProgressPreference = 'SilentlyContinue'
                try { Invoke-WebRequest -Uri $url -OutFile $zip -UseBasicParsing } finally { $ProgressPreference = $prev }
            }
        }

        Write-Host '== verifying SHA256 =='
        $actual = (Get-FileHash -Algorithm SHA256 $zip).Hash.ToLower()
        if ($actual -ne $Sha256.ToLower()) {
            Remove-Item -Force $zip
            throw "SHA256 mismatch for $name`n  expected $($Sha256.ToLower())`n  actual   $actual`n(the corrupt/tampered download was deleted)"
        }

        Write-Host '== extracting toolchain =='
        $tmp = Join-Path $deps '.go-win7-tmp'
        if (Test-Path $tmp) { Remove-Item -Recurse -Force $tmp }
        # PowerShell 7 carries the type already and rejects the name; Windows PowerShell needs the load.
        try { Add-Type -AssemblyName System.IO.Compression.FileSystem } catch { }
        [System.IO.Compression.ZipFile]::ExtractToDirectory($zip, $tmp)

        # the archive's root folder name tracks the fork's version, so locate it by its bin\go.exe
        $found = Get-ChildItem -Recurse -File -Filter go.exe $tmp |
                 Where-Object { $_.Directory.Name -eq 'bin' } | Select-Object -First 1
        if (-not $found) { throw "no bin\go.exe inside $name" }
        Move-Item (Split-Path $found.Directory.FullName -Parent) $goRoot
        Remove-Item -Recurse -Force $tmp
    }
}

if (-not (Test-Path (Join-Path $goRoot 'bin\go.exe'))) { throw "no Go toolchain at $goRoot" }
$goExe = Join-Path $goRoot 'bin\go.exe'

# ---- 3. build, mirroring build.cmd's environment ----
$saved = @{}
foreach ($v in @('GOOS', 'GOARCH', 'GOARM', 'GOROOT', 'GOPATH', 'GOCACHE', 'GOTOOLCHAIN', 'GOFLAGS', 'CGO_ENABLED', 'CGO_CFLAGS', 'CGO_LDFLAGS', 'CC', 'PATH', 'PATHEXT')) {
    $saved[$v] = [Environment]::GetEnvironmentVariable($v)
}

try {
    $env:GOOS        = 'windows'
    $env:GOARM       = '7'
    $env:GOROOT      = $goRoot
    $env:GOPATH      = Join-Path $deps 'gopath'
    # separate caches per toolchain so an A/B run never reuses the other one's objects
    $env:GOCACHE     = Join-Path $deps $(if ($Upstream) { 'gocache-upstream' } else { 'gocache-win7' })
    # without this a go.mod/toolchain bump would silently download a stock Go and undo the whole point
    $env:GOTOOLCHAIN = 'local'
    $env:GOFLAGS     = '-mod=readonly'
    $env:CGO_ENABLED = '1'
    $env:CGO_CFLAGS  = '-O3 -Wall -Wno-unused-function -Wno-switch -std=gnu11 -DWINVER=0x0601'
    $env:CGO_LDFLAGS = '-Wl,--dynamicbase -Wl,--nxcompat -Wl,--export-all-symbols -Wl,--high-entropy-va'
    $env:PATH        = (Join-Path $deps 'llvm-mingw\bin') + ';' + (Join-Path $goRoot 'bin') + ';' + $env:PATH
    $env:PATHEXT     = '.exe'

    Write-Host ''
    Write-Host "== toolchain: $(& $goExe version) =="
    Write-Host "   GOROOT $goRoot"

    Push-Location $submodule
    try {
        foreach ($a in $Arch) {
            $env:GOARCH = $targets[$a].GoArch
            $env:CC     = $targets[$a].Cc
            New-Item -ItemType Directory -Force -Path (Join-Path $submodule $a) | Out-Null

            Write-Host "== building $a\tunnel.dll =="
            & $goExe build -buildmode c-shared -ldflags '-w -s' -trimpath -o "$a\tunnel.dll"
            if ($LASTEXITCODE -ne 0) { throw "go build failed for $a ($LASTEXITCODE)" }

            $header = Join-Path $submodule "$a\tunnel.h"
            if (Test-Path $header) { Remove-Item -Force $header }
        }
    } finally { Pop-Location }
} finally {
    foreach ($v in $saved.Keys) { [Environment]::SetEnvironmentVariable($v, $saved[$v]) }
}

# ---- 4. report what came out ----

# DLLs named in the import directory, i.e. the ones the loader must resolve before the DLL can start
function Get-ImportedDlls($b) {
    $peOff = [BitConverter]::ToInt32($b, 0x3C)
    $numSec = [BitConverter]::ToUInt16($b, $peOff + 6)
    $optSize = [BitConverter]::ToUInt16($b, $peOff + 20)
    $optStart = $peOff + 24
    $ddOff = $(if ([BitConverter]::ToUInt16($b, $optStart) -eq 0x20B) { 112 } else { 96 })
    $impRva = [BitConverter]::ToUInt32($b, $optStart + $ddOff + 8)
    if ($impRva -eq 0) { return @() }

    $secs = @()
    $p = $optStart + $optSize
    for ($i = 0; $i -lt $numSec; $i++) {
        $secs += @{
            VAddr = [BitConverter]::ToUInt32($b, $p + 12)
            VSize = [BitConverter]::ToUInt32($b, $p + 8)
            RawPtr = [BitConverter]::ToUInt32($b, $p + 20)
            RawSize = [BitConverter]::ToUInt32($b, $p + 16)
        }
        $p += 40
    }
    $rva2off = {
        param($rva)
        foreach ($s in $secs) {
            $span = $(if ($s.VSize -gt $s.RawSize) { $s.VSize } else { $s.RawSize })
            if ($rva -ge $s.VAddr -and $rva -lt ($s.VAddr + $span)) { return [int]($s.RawPtr + ($rva - $s.VAddr)) }
        }
        return -1
    }

    $out = @()
    $o = & $rva2off $impRva
    while ($true) {
        $nameRva = [BitConverter]::ToUInt32($b, $o + 12)
        if ($nameRva -eq 0) { break }
        $s = & $rva2off $nameRva
        $e = $s
        while ($b[$e] -ne 0) { $e++ }
        $out += [Text.Encoding]::ASCII.GetString($b, $s, $e - $s)
        $o += 20
    }
    return $out
}

Write-Host ''
Write-Host '== result =='
$unsafe = @()
foreach ($a in $Arch) {
    $dll = Join-Path $submodule "$a\tunnel.dll"
    $b = [IO.File]::ReadAllBytes($dll)
    $text = [Text.Encoding]::GetEncoding(28591).GetString($b)
    $bcrypt = $text.Contains('bcryptprimitives.dll')
    if ($bcrypt) { $unsafe += $a }

    Write-Host ("   {0}  {1:N0} bytes" -f $dll, $b.Length)
    Write-Host ("   references bcryptprimitives.dll : {0}" -f $(if ($bcrypt) { 'YES - not Windows 7 safe' } else { 'no' }))
    Write-Host ("   static imports: {0}" -f ((Get-ImportedDlls $b) -join ', '))
}

# A stock-Go build ships silently otherwise, and Windows 7 only fails at load time on the user's machine.
if ($unsafe -and -not $Upstream) {
    throw "not Windows 7 safe: $($unsafe -join ', ') import bcryptprimitives.dll, which Windows 7 does not have. The go-legacy-win7 toolchain was expected to be used - rerun with -Force to rebuild it."
}

Write-Host ''
Write-Host 'Now build the installer with build-installer.ps1 - App.csproj picks this tunnel.dll up automatically.'
