<#
.SYNOPSIS
  Fails when a native binary imports the Visual C++ runtime and no copy of it lies beside the binary.

.DESCRIPTION
  A clean Windows carries the UCRT but not vcruntime140.dll or msvcp140.dll: those come only with the VC++
  Redistributable, and a binary that imports them without a copy in its own folder dies at start with
  0xC0000135. Every .exe and .dll under the given paths is read for its import and delay-load tables.
#>
param(
    [Parameter(Mandatory = $true)]
    [string[]]$Path
)

$ErrorActionPreference = 'Stop'

# The Visual C++ runtime libraries a clean Windows does not have.
$runtime = '^(vcruntime|msvcp|msvcr|concrt|vccorlib)1[0-4]0(_\w+)?\.dll$'

# The file offset of an RVA, by the section that holds it.
function ConvertTo-Offset([uint32]$rva, $sections) {
    foreach ($s in $sections) {
        $span = [Math]::Max($s.VirtualSize, $s.RawSize)
        if ($rva -ge $s.VirtualAddress -and $rva -lt $s.VirtualAddress + $span) {
            return [int64]($rva - $s.VirtualAddress + $s.RawPointer)
        }
    }

    return [int64]$rva
}

# The zero-terminated ASCII string at a file offset.
function Read-Name($stream, [int64]$offset) {
    $stream.Position = $offset
    $bytes = New-Object System.Collections.Generic.List[byte]
    while ($bytes.Count -lt 260) {
        $b = $stream.ReadByte()
        if ($b -le 0) { break }
        $bytes.Add([byte]$b)
    }

    return [System.Text.Encoding]::ASCII.GetString($bytes.ToArray())
}

# The DLL names a PE image imports, directly and by delay load; nothing for a file that is not a PE image.
function Get-Imports([string]$file) {
    $stream = [System.IO.File]::OpenRead($file)
    try {
        $reader = New-Object System.IO.BinaryReader($stream)
        if ($stream.Length -lt 64 -or $reader.ReadUInt16() -ne 0x5A4D) { return @() }
        $stream.Position = 0x3C
        $pe = [int64]$reader.ReadUInt32()
        if ($pe + 24 -gt $stream.Length) { return @() }
        $stream.Position = $pe
        if ($reader.ReadUInt32() -ne 0x00004550) { return @() }

        $stream.Position = $pe + 6
        $sectionCount = $reader.ReadUInt16()
        $stream.Position = $pe + 20
        $optionalSize = $reader.ReadUInt16()
        $optional = $pe + 24
        $stream.Position = $optional
        $plus = $reader.ReadUInt16() -eq 0x20B
        $countAt = if ($plus) { 108 } else { 92 }
        $stream.Position = $optional + $countAt
        $directoryCount = $reader.ReadUInt32()

        $sections = @()
        for ($i = 0; $i -lt $sectionCount; $i++) {
            $stream.Position = $optional + $optionalSize + ($i * 40) + 8
            $sections += [pscustomobject]@{
                VirtualSize    = $reader.ReadUInt32()
                VirtualAddress = $reader.ReadUInt32()
                RawSize        = $reader.ReadUInt32()
                RawPointer     = $reader.ReadUInt32()
            }
        }

        $names = @()
        # Directory 1 is the import table (20-byte descriptors, name at 12); 13 is the delay-load table
        # (32-byte descriptors, name at 4).
        foreach ($directory in @(@{ Index = 1; Size = 20; Name = 12 }, @{ Index = 13; Size = 32; Name = 4 })) {
            if ($directory.Index -ge $directoryCount) { continue }
            $stream.Position = $optional + $countAt + 4 + ($directory.Index * 8)
            $rva = $reader.ReadUInt32()
            if ($rva -eq 0) { continue }

            $table = ConvertTo-Offset $rva $sections
            for ($i = 0; $i -lt 4096; $i++) {
                $stream.Position = $table + ($i * $directory.Size) + $directory.Name
                $nameRva = $reader.ReadUInt32()
                if ($nameRva -eq 0) { break }
                $names += Read-Name $stream (ConvertTo-Offset $nameRva $sections)
            }
        }

        return $names
    }
    catch [System.IO.EndOfStreamException] {
        return @()
    }
    finally {
        $stream.Dispose()
    }
}

# powershell -File hands a comma-separated list over as one string.
$Path = @($Path | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })

$files = foreach ($item in $Path) {
    if (Test-Path -LiteralPath $item -PathType Leaf) {
        Get-Item -LiteralPath $item
    }
    elseif (Test-Path -LiteralPath $item -PathType Container) {
        Get-ChildItem -LiteralPath $item -Recurse -File | Where-Object { $_.Extension -in '.exe', '.dll' }
    }
    else {
        throw "nothing to check at $item"
    }
}

$failures = @()
$checked = 0
foreach ($file in $files) {
    $checked++
    foreach ($name in Get-Imports $file.FullName) {
        if ($name -match $runtime -and -not (Test-Path -LiteralPath (Join-Path $file.DirectoryName $name))) {
            $failures += "{0} imports {1}, and no copy of it lies beside it" -f $file.FullName, $name
        }
    }
}

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Host "   $_" }
    throw "$($failures.Count) native import(s) of the VC++ runtime would not load on a clean Windows"
}

Write-Host "== native imports: $checked file(s), none needs the VC++ Redistributable =="
