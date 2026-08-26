<#
.SYNOPSIS
  Builds gateway.exe, the userspace stack the shared access point's clients are carried through.

.DESCRIPTION
  The module lives in amneziageo-windows\gateway and stands on the sing-tun submodule next to it. The output
  goes to gateway\bin\<arch>\gateway.exe, where the App project picks it up (Gateway.targets).

  Go is taken from PATH, and failing that from the toolchain the engine build downloaded into
  amneziawg-windows\.deps. The gVisor stack is behind a build tag, so every build passes it.
#>
param(
    [ValidateSet('x64', 'arm64', 'both')]
    [string]$Arch = 'both'
)

$ErrorActionPreference = 'Stop'

$windows = Split-Path -Parent $PSScriptRoot
$module = Join-Path $windows 'gateway'
$singTun = Join-Path $windows 'sing-tun'

if (-not (Test-Path (Join-Path $module 'go.mod'))) { throw "no gateway module at $module" }
if (-not (Test-Path (Join-Path $singTun 'go.mod'))) {
    throw "the sing-tun submodule is missing at $singTun. Initialize it and run again:`n  git submodule update --init --recursive"
}

function Resolve-Go {
    $onPath = Get-Command go -ErrorAction SilentlyContinue
    if ($onPath) { return $onPath.Source }

    $deps = Join-Path $windows 'amneziawg-windows\.deps'
    foreach ($name in @('go', 'go-win7')) {
        $candidate = Join-Path $deps "$name\bin\go.exe"
        if (Test-Path $candidate) { return $candidate }
    }

    throw "no Go toolchain. Install Go, or build the engine once so it downloads one:`n  powershell -NoProfile -ExecutionPolicy Bypass -File amneziageo-windows\tools\build-engine-win7.ps1"
}

$go = Resolve-Go
Write-Host "== go: $go =="

$targets = if ($Arch -eq 'both') { @('x64', 'arm64') } else { @($Arch) }
foreach ($target in $targets) {
    $output = Join-Path $module "bin\$target\gateway.exe"
    Write-Host "== build $target =="
    $env:GOOS = 'windows'
    $env:GOARCH = if ($target -eq 'x64') { 'amd64' } else { 'arm64' }
    Push-Location $module
    try {
        & $go build -tags with_gvisor -trimpath -ldflags '-s -w' -o $output .
    }
    finally { Pop-Location }
    if ($LASTEXITCODE -ne 0) { throw "gateway build failed for $target" }
    Write-Host ("   -> {0} ({1:N1} MB)" -f $output, ((Get-Item $output).Length / 1MB))
}
