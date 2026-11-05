param(
    [ValidateSet('x64', 'arm64', 'both')]
    [string]$Arch = 'x64'
)

$ErrorActionPreference = 'Stop'
$version = '26.10.31'
$windows = Split-Path -Parent $PSScriptRoot
$cache = [System.IO.Path]::GetFullPath((Join-Path $windows ".deps\xray\$version"))
$packages = @{
    x64 = @{ Name = 'Xray-windows-64.zip'; Sha256 = '2e04a9995a6f2ad1b28c0bb25c708ecb2695e43a1c9c7889c4b6ac2b48cf7a6a'; Machine = 0x8664 }
    arm64 = @{ Name = 'Xray-windows-arm64-v8a.zip'; Sha256 = 'db062e8a177facacc8ae53d4c9c0df4132b13fc3a89acda6f21d92d36633077e'; Machine = 0xaa64 }
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
New-Item -ItemType Directory -Path $cache -Force | Out-Null
$architectures = if ($Arch -eq 'both') { @('x64', 'arm64') } else { @($Arch) }
foreach ($target in $architectures) {
    $package = $packages[$target]
    $archive = Join-Path $cache $package.Name
    if (-not (Test-Path -LiteralPath $archive) -or (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $package.Sha256) {
        $download = Join-Path $cache ($package.Name + '.' + [Guid]::NewGuid().ToString('N') + '.download')
        try {
            Invoke-WebRequest -Uri "https://github.com/XTLS/Xray-core/releases/download/v$version/$($package.Name)" -OutFile $download -UseBasicParsing
            if ((Get-FileHash -LiteralPath $download -Algorithm SHA256).Hash -ne $package.Sha256) {
                throw "Xray $version $target checksum mismatch"
            }

            Move-Item -LiteralPath $download -Destination $archive -Force
        }
        finally {
            if (Test-Path -LiteralPath $download) { Remove-Item -LiteralPath $download -Force }
        }
    }

    $destination = Join-Path $cache $target
    New-Item -ItemType Directory -Path $destination -Force | Out-Null
    $zip = [System.IO.Compression.ZipFile]::OpenRead($archive)
    try {
        foreach ($leaf in @('xray.exe', 'LICENSE', 'README.md')) {
            $entry = $zip.GetEntry($leaf)
            if (-not $entry) { throw "Xray archive is missing $leaf" }
            $final = Join-Path $destination $leaf
            $temporary = Join-Path $destination ($leaf + '.' + [Guid]::NewGuid().ToString('N') + '.tmp')
            try {
                $entryStream = $entry.Open()
                try {
                    $output = [System.IO.File]::Create($temporary)
                    try { $entryStream.CopyTo($output) } finally { $output.Dispose() }
                }
                finally { $entryStream.Dispose() }

                if ($leaf -eq 'xray.exe') {
                    $stream = [System.IO.File]::OpenRead($temporary)
                    $reader = [System.IO.BinaryReader]::new($stream)
                    try {
                        if ($reader.ReadUInt16() -ne 0x5a4d) { throw 'Xray PE header is missing' }
                        $stream.Position = 0x3c
                        $stream.Position = $reader.ReadUInt32()
                        if ($reader.ReadUInt32() -ne 0x00004550 -or $reader.ReadUInt16() -ne $package.Machine) {
                            throw "Xray executable architecture does not match $target"
                        }
                    }
                    finally { $reader.Dispose(); $stream.Dispose() }
                }

                Move-Item -LiteralPath $temporary -Destination $final -Force
            }
            finally {
                if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force }
            }
        }
    }
    finally { $zip.Dispose() }

    Write-Host "Xray $version ${target}: verified $destination"
}
