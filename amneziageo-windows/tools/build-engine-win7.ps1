<#
  Builds <arch>\tunnel.dll with a Windows 7 compatible Go toolchain (thongtech/go-legacy-win7).

  The amneziawg-windows submodule is NOT modified: the toolchain is unpacked into its gitignored
  .deps\go-win7 (the stock .deps\go stays where build.cmd put it) and the output is the same
  gitignored <arch>\tunnel.dll that App.csproj already consumes. Build flags mirror build.cmd.

  A first run with no .deps present bootstraps them by invoking the submodule's own build.cmd, which
  downloads Go + llvm-mingw + wintun and produces a stock tunnel.dll; this script then overwrites it.

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
    'x64'   = @{ GoArch = 'amd64'; Cc = 'x86_64-w64-mingw32-gcc' }
    'x86'   = @{ GoArch = '386';   Cc = 'i686-w64-mingw32-gcc' }
    'arm64' = @{ GoArch = 'arm64'; Cc = 'aarch64-w64-mingw32-gcc' }
}
foreach ($a in $Arch) {
    if (-not $targets.ContainsKey($a)) { throw "Invalid -Arch '$a' (expected x64, x86 or arm64)." }
}

# ---- 1. bootstrap .deps (Go, llvm-mingw, wintun) through the submodule's own build.cmd ----
$cc = Join-Path $deps 'llvm-mingw\bin\x86_64-w64-mingw32-gcc.exe'
if (-not (Test-Path $cc)) {
    Write-Host '== bootstrapping .deps via the submodule build.cmd (downloads Go, llvm-mingw, wintun) =='
    Push-Location $submodule
    try {
        & cmd.exe /c build.cmd
        if ($LASTEXITCODE -ne 0) { throw "build.cmd bootstrap failed ($LASTEXITCODE)" }
    } finally { Pop-Location }
    if (-not (Test-Path $cc)) { throw "llvm-mingw still missing after bootstrap: $cc" }
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
        Add-Type -AssemblyName System.IO.Compression.FileSystem
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
foreach ($a in $Arch) {
    $dll = Join-Path $submodule "$a\tunnel.dll"
    $b = [IO.File]::ReadAllBytes($dll)
    $text = [Text.Encoding]::GetEncoding(28591).GetString($b)

    Write-Host ("   {0}  {1:N0} bytes" -f $dll, $b.Length)
    Write-Host ("   references bcryptprimitives.dll : {0}" -f $(if ($text.Contains('bcryptprimitives.dll')) { 'YES - not Windows 7 safe' } else { 'no' }))
    Write-Host ("   static imports: {0}" -f ((Get-ImportedDlls $b) -join ', '))
}
Write-Host ''
Write-Host 'Now build the installer with build-installer.ps1 - App.csproj picks this tunnel.dll up automatically.'
