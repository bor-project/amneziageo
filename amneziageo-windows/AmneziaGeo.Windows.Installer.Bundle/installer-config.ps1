<#
  Shared installer-config loader: installer.config.json plus overlays, deep-merged.

  Layers, lowest to highest precedence:
    1. installer.config.json        - base template, tracked in VCS (structure + defaults).
    2. installer.config.local.json  - machine/secret overlay, gitignored, auto-applied when present.
    3. installer.config.<env>.json  - per-environment overlay (e.g. prod/test/dev), applied when a name is given.
  On top of these each caller still lets its own CLI flags win. Dot-source this and call Read-InstallerConfig.

  Merge rules (Merge-Config): objects deep-merge by key; a present key in the overlay wins; scalars, arrays
  and explicit null replace wholesale (array = whole replace, not concat; null = feature off). An absent key
  inherits the base (JSON "missing" and JSON "null" are distinct: null overrides, missing does not).
#>

# True only for a JSON object (ConvertFrom-Json PSCustomObject); scalars, arrays and null are false.
function Test-IsConfigObject($value) {
    return ($null -ne $value) -and ($value.GetType().FullName -eq 'System.Management.Automation.PSCustomObject')
}

# Deep-merge $Overlay onto $Base. Either side that is not an object makes the overlay replace the base.
function Merge-Config {
    param($Base, $Overlay)
    if (-not (Test-IsConfigObject $Base) -or -not (Test-IsConfigObject $Overlay)) { return $Overlay }
    $merged = [ordered]@{}
    foreach ($p in $Base.PSObject.Properties) { $merged[$p.Name] = $p.Value }
    foreach ($p in $Overlay.PSObject.Properties) {
        $merged[$p.Name] = if ($merged.Contains($p.Name)) { Merge-Config -Base $merged[$p.Name] -Overlay $p.Value } else { $p.Value }
    }
    return [pscustomobject]$merged
}

# Read $Path as a config object; $null when the file is absent or empty.
function Read-ConfigFile([string]$Path) {
    if (-not (Test-Path $Path)) { return $null }
    $raw = Get-Content $Path -Raw
    if (-not $raw) { return $null }
    return $raw | ConvertFrom-Json
}

# Load the merged config for $BundleDir: base <- local overlay <- optional <Environment> overlay.
function Read-InstallerConfig {
    param(
        [string]$BundleDir,
        [string]$Environment
    )
    $cfg = Read-ConfigFile (Join-Path $BundleDir 'installer.config.json')

    $local = Read-ConfigFile (Join-Path $BundleDir 'installer.config.local.json')
    if ($null -ne $local) { $cfg = Merge-Config -Base $cfg -Overlay $local }

    if ($Environment) {
        $envPath = Join-Path $BundleDir "installer.config.$Environment.json"
        if (-not (Test-Path $envPath)) { throw "Config overlay for environment '$Environment' not found: $envPath" }
        $cfg = Merge-Config -Base $cfg -Overlay (Read-ConfigFile $envPath)
    }
    return $cfg
}
