param(
    [ValidateSet('x64', 'arm64', 'both')]
    [string]$Arch = 'x64',
    [string]$GoExecutable = 'go.exe'
)

$ErrorActionPreference = 'Stop'
$windows = Split-Path -Parent $PSScriptRoot
$tool = [IO.Path]::GetFullPath((Join-Path $windows '..\tools\http-get-build'))
$cache = Join-Path $windows '.deps\http-get'
$goExe = (Get-Command $GoExecutable -ErrorAction Stop).Source
$savedEnvironment = @{}
foreach ($name in @('GOROOT', 'GOTOOLCHAIN', 'GOOS', 'GOARCH', 'GOARM', 'GOAMD64', 'GOARM64', 'GOFIPS140', 'GOFLAGS', 'GOEXPERIMENT', 'GOWORK', 'GOENV')) {
    $savedEnvironment[$name] = [Environment]::GetEnvironmentVariable($name)
}
try {
    foreach ($name in @('GOROOT', 'GOOS', 'GOARCH', 'GOARM', 'GOAMD64', 'GOARM64', 'GOFLAGS', 'GOEXPERIMENT')) {
        [Environment]::SetEnvironmentVariable($name, $null)
    }
    $env:GOTOOLCHAIN = 'local'
    $env:GOWORK = 'off'
    $env:GOENV = 'off'
    $env:GOFIPS140 = 'off'
    if ((& $goExe env GOVERSION) -ne 'go1.27.3') {
        throw 'HTTP GET preparation requires Go1.27.3; pass -GoExecutable with that SDK.'
    }

    $architectures = if ($Arch -eq 'both') { @('amd64', 'arm64') } elseif ($Arch -eq 'arm64') { @('arm64') } else { @('amd64') }
    foreach ($target in $architectures) {
        & $goExe -C $tool run . -cache $cache -os windows -arch $target -go $goExe
        if ($LASTEXITCODE -ne 0) {
            throw "HTTP GET preparation failed for $target"
        }
    }
}
finally {
    foreach ($name in $savedEnvironment.Keys) {
        [Environment]::SetEnvironmentVariable($name, $savedEnvironment[$name])
    }
}
