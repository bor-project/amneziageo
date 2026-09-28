<#
.SYNOPSIS
  Builds wstunnel.exe, the websocket carrier of a configuration with WebSocket, with the C runtime linked in.

.DESCRIPTION
  The erebe/wstunnel release binaries import vcruntime140.dll, which a clean Windows does not have, so the
  carrier dies at start there. This builds the pinned tag from source with +crt-static and fails if the result
  still imports the VC++ runtime. The x64 build replaces AmneziaGeo.Windows.App\tools\wstunnel.exe, which the
  App project ships; the arm64 build goes to tools\wstunnel-arm64.exe.

  The sources are cloned into amneziageo-windows\.deps. Cargo is taken from PATH, and failing that from
  %USERPROFILE%\.cargo\bin; the MSVC linker comes from Visual Studio.
#>
param(
    [ValidateSet('x64', 'arm64', 'both')]
    [string]$Arch = 'x64',
    [string]$Version = '10.5.5'
)

$ErrorActionPreference = 'Stop'

$windows = Split-Path -Parent $PSScriptRoot
$tools = Join-Path $windows 'AmneziaGeo.Windows.App\tools'
$source = Join-Path $windows ".deps\wstunnel-$Version"

function Resolve-Cargo {
    $onPath = Get-Command cargo -ErrorAction SilentlyContinue
    if ($onPath) { return $onPath.Source }

    $candidate = Join-Path $env:USERPROFILE '.cargo\bin\cargo.exe'
    if (Test-Path $candidate) { return $candidate }

    throw "no Rust toolchain. Install it from https://rustup.rs and run again"
}

$cargo = Resolve-Cargo
$rustup = Join-Path (Split-Path -Parent $cargo) 'rustup.exe'
Write-Host "== cargo: $cargo =="

if (-not (Test-Path (Join-Path $source 'Cargo.toml'))) {
    Write-Host "== clone wstunnel v$Version =="
    git clone --quiet --depth 1 --branch "v$Version" https://github.com/erebe/wstunnel.git $source
    if ($LASTEXITCODE -ne 0) { throw "wstunnel v$Version could not be cloned" }
}

$targets = if ($Arch -eq 'both') { @('x64', 'arm64') } else { @($Arch) }
foreach ($target in $targets) {
    $triple = if ($target -eq 'x64') { 'x86_64-pc-windows-msvc' } else { 'aarch64-pc-windows-msvc' }
    $output = Join-Path $tools $(if ($target -eq 'x64') { 'wstunnel.exe' } else { 'wstunnel-arm64.exe' })
    Write-Host "== build $target ($triple) =="
    if (Test-Path $rustup) {
        & $rustup target add $triple
        if ($LASTEXITCODE -ne 0) { throw "the $triple target could not be added" }
    }

    # The C runtime goes into the image instead of vcruntime140.dll.
    Set-Item ('env:CARGO_TARGET_{0}_RUSTFLAGS' -f $triple.ToUpperInvariant().Replace('-', '_')) '-C target-feature=+crt-static'
    Push-Location $source
    try {
        & $cargo build --release --locked --package wstunnel-cli --target $triple
    }
    finally { Pop-Location }
    if ($LASTEXITCODE -ne 0) { throw "wstunnel build failed for $target" }

    $built = Join-Path $source "target\$triple\release\wstunnel.exe"
    & (Join-Path $PSScriptRoot 'check-native-deps.ps1') -Path $built
    Copy-Item $built $output -Force
    Write-Host ("   -> {0} ({1:N1} MB)" -f $output, ((Get-Item $output).Length / 1MB))
}
