<#
  Builds the AmneziaGeo setup bundle (AmneziaGeoSetup.exe):
    1. publishes the backend (App) and GUI (Ui) as ONE self-contained win-x64 folder (stage),
    2. builds the per-machine MSI from that stage (AmneziaGeo.Windows.Installer.Package),
    3. publishes the WPF bootstrapper application self-contained (AmneziaGeo.Windows.Installer),
    4. generates a PayloadGroup over the BA publish folder (Burn does not auto-harvest it),
    5. builds the Burn bundle that hosts the BA and chains the MSI.

  The bundle Version is 1.0.1.<git-commit-count> so every build is strictly newer to Burn; combined
  with the MSI MajorUpgrade/@AllowDowngrades this makes reinstall-same-code, update and downgrade all
  work.

  Usage (on the build machine):  pwsh -File build-installer.ps1 [-Configuration Release]
#>
param([string]$Configuration = 'Release')

$ErrorActionPreference = 'Stop'
$rid = 'win-x64'

$bundleDir = $PSScriptRoot
$win       = Split-Path $bundleDir -Parent                       # ...\amneziageo-windows

$appProj    = Join-Path $win 'AmneziaGeo.Windows.App\AmneziaGeo.Windows.App.csproj'
$uiProj     = Join-Path $win 'AmneziaGeo.Windows.Ui\AmneziaGeo.Windows.Ui.csproj'
$baProj     = Join-Path $win 'AmneziaGeo.Windows.Installer\AmneziaGeo.Windows.Installer.csproj'
$msiProj    = Join-Path $win 'AmneziaGeo.Windows.Installer.Package\AmneziaGeo.Windows.Installer.Package.wixproj'
$bundleProj = Join-Path $bundleDir 'AmneziaGeo.Windows.Installer.Bundle.wixproj'

$stage     = Join-Path $bundleDir 'stage'
$baPublish = Join-Path $bundleDir 'ba-publish'
$genWxs    = Join-Path $bundleDir 'BaPayloads.generated.wxs'
$baExeName = 'AmneziaGeo.Windows.Installer.exe'

# ---- installer config (installer.config.json): a real file the build honors. Every field is optional -
# null/empty => that feature is off: no icon => no icon anywhere; no updateUrl => updates disabled (and
# their UI hidden); no signingCert => the outputs are left unsigned. ----
$cfgPath = Join-Path $bundleDir 'installer.config.json'
$cfg = if (Test-Path $cfgPath) { Get-Content $cfgPath -Raw | ConvertFrom-Json } else { $null }

$updateUrl = if ($cfg -and $cfg.updateUrl) { [string]$cfg.updateUrl } else { '' }

$iconAbs = ''
if ($cfg -and $cfg.iconPath) {
    $p = [string]$cfg.iconPath
    $iconAbs = if ([System.IO.Path]::IsPathRooted($p)) { $p } else { Join-Path $bundleDir $p }
    $iconAbs = [System.IO.Path]::GetFullPath($iconAbs)
    # A configured-but-missing icon is a config error, not an intent to disable it: fail loudly rather
    # than silently shipping a no-icon build. Only a null/empty iconPath means "no icon".
    if (-not (Test-Path $iconAbs)) { throw "iconPath '$p' set in installer.config.json but not found at $iconAbs" }
}
$hasIcon = if ($iconAbs) { 'true' } else { 'false' }

# Props appended to the relevant dotnet invocations (arrays expand to separate native args; empty = nothing).
$iconProps   = if ($hasIcon -eq 'true') { @('-p:HasIcon=true', "-p:IconFile=$iconAbs") } else { @('-p:HasIcon=false') }
$updateProps = if ($updateUrl) { @("-p:UpdateUrl=$updateUrl") } else { @() }

# Payload type: self-contained bundles the .NET runtime (installs on any machine, but large);
# framework-dependent is much lighter but needs the .NET 10 Desktop Runtime already on the target
# (App, Ui and the WPF bootstrapper all target .NET 10). Default (absent/false) = framework-
# dependent - lighter to build, copy and test. Set "selfContained": true for distribution.
$selfContained = if ($cfg -and $cfg.selfContained) { 'true' } else { 'false' }
Write-Host "== config: type=$(if ($selfContained -eq 'true') { 'self-contained' } else { 'framework-dependent' }); icon=$(if ($hasIcon -eq 'true') { $iconAbs } else { '(none)' }); updateUrl=$(if ($updateUrl) { $updateUrl } else { '(none)' }); signing=$(if ($cfg -and $cfg.signingCert) { 'on' } else { 'off' }) =="

# ---- Authenticode signing (installer.config.json -> signingCert). Off unless signingCert is set. ----
# signtool is not on PATH; resolve it once (PATH first, then the newest x64 build under the Windows SDK).
$script:Signtool = $null
function Resolve-Signtool {
    if ($script:Signtool) { return $script:Signtool }
    $c = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if ($c) { $script:Signtool = $c.Source; return $script:Signtool }
    $found = Get-ChildItem 'C:\Program Files (x86)\Windows Kits\10\bin' -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue |
             Where-Object { $_.FullName -like '*\x64\*' } | Sort-Object FullName -Descending
    if (-not $found) { throw 'signtool.exe not found - install the Windows SDK or add signtool to PATH.' }
    $script:Signtool = $found[0].FullName
    return $script:Signtool
}

# Fail fast (before the long publishes) if signing is configured but the cert is missing.
function Assert-SigningCert {
    if (-not ($cfg -and $cfg.signingCert)) { return }
    $sc = $cfg.signingCert
    if ($sc.pfxPath) {
        if (-not (Test-Path $sc.pfxPath)) { throw "signingCert.pfxPath '$($sc.pfxPath)' not found." }
        return
    }
    $store = Get-ChildItem Cert:\CurrentUser\My, Cert:\LocalMachine\My -ErrorAction SilentlyContinue
    $match = if ($sc.thumbprint) { $store | Where-Object { $_.Thumbprint -eq $sc.thumbprint -and $_.HasPrivateKey } }
             elseif ($sc.subject) { $store | Where-Object { $_.Subject -like "*$($sc.subject)*" -and $_.HasPrivateKey } }
             else { $null }
    if (-not $match) {
        throw "installer.config.json enables signingCert but no matching code-signing certificate is installed. " +
              "Run dev-signing-cert.ps1 to create a self-signed dev cert, or point signingCert at a real one."
    }
}

# Sign one or more files in a single signtool call (one timestamp round-trip). No-op when signing is off.
function Invoke-Sign {
    param([Parameter(ValueFromRemainingArguments = $true)][string[]]$Files)
    if (-not ($cfg -and $cfg.signingCert)) { return }
    $Files = @($Files | Where-Object { $_ -and (Test-Path $_) })
    if (-not $Files) { return }
    $sc = $cfg.signingCert
    $a = @('sign', '/fd', 'SHA256')
    if     ($sc.subject)    { $a += @('/n', [string]$sc.subject) }       # pick from the store by subject
    elseif ($sc.thumbprint) { $a += @('/sha1', [string]$sc.thumbprint) } # ... or by thumbprint
    elseif ($sc.pfxPath)    { $a += @('/f', [string]$sc.pfxPath); if ($sc.password) { $a += @('/p', [string]$sc.password) } }
    else   { Write-Host '   signingCert set but none of subject/thumbprint/pfxPath given - skipping signing'; return }
    if ($sc.timestampUrl)   { $a += @('/tr', [string]$sc.timestampUrl, '/td', 'SHA256') }
    $a += $Files
    Write-Host "== sign $($Files.Count) file(s): $((($Files | ForEach-Object { Split-Path $_ -Leaf }) -join ', ')) =="
    & (Resolve-Signtool) @a
    if ($LASTEXITCODE -ne 0) { throw "signtool failed ($LASTEXITCODE)" }
}

# Sign OUR OWN binaries (everything named AmneziaGeo.*) under a publish folder. Third-party and .NET
# runtime assemblies keep their original publisher signatures - we never re-sign them with our cert.
function Invoke-SignLibraries([string]$dir) {
    if (-not ($cfg -and $cfg.signingCert)) { return }
    $ours = @(Get-ChildItem -Recurse -File $dir -Include 'AmneziaGeo*.dll', 'AmneziaGeo*.exe' -ErrorAction SilentlyContinue |
              Select-Object -ExpandProperty FullName)
    if ($ours) { Invoke-Sign @ours }
}

# Sign a Burn bundle the WiX-correct way: detach the engine, sign it, reattach, then sign the whole
# bundle. Signing the bundle .exe DIRECTLY shifts the file so Burn's recorded attached-container offset
# goes stale, and at runtime Burn fails to extract its own payloads ("failed to resolve source for
# WixAttachedContainer", 0x80070002 - the install dies right after the BA appears). detach/reattach
# rewrites the .wixburn section so the attached container stays locatable after the signature is added.
function Invoke-SignBundle([string]$bundle) {
    if (-not ($cfg -and $cfg.signingCert)) { return }
    if (-not (Get-Command wix -ErrorAction SilentlyContinue)) { throw 'wix CLI not found - needed to sign the Burn bundle (detach/reattach).' }
    $engine = Join-Path ([System.IO.Path]::GetDirectoryName($bundle)) 'burnengine.exe'
    $out    = "$bundle.signed"
    Write-Host '== sign bundle (detach engine -> sign -> reattach -> sign bundle) =='
    & wix burn detach $bundle -engine $engine
    if ($LASTEXITCODE -ne 0) { throw "wix burn detach failed ($LASTEXITCODE)" }
    Invoke-Sign $engine
    & wix burn reattach $bundle -engine $engine -o $out
    if ($LASTEXITCODE -ne 0) { throw "wix burn reattach failed ($LASTEXITCODE)" }
    Move-Item -Force $out $bundle
    Remove-Item -Force $engine
    Invoke-Sign $bundle
}

Assert-SigningCert

# ---- version: 1.0.1.<commit count> (4th field always increasing) ----
$build = (& git -C $win rev-list --count HEAD).Trim()
if (-not $build) { $build = '0' }
$version = "1.0.1.$build"
Write-Host "== bundle version $version =="

# ---- 1. stage the self-contained app (App first, UI second, same folder) ----
if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
New-Item -ItemType Directory -Force -Path $stage | Out-Null

Write-Host "== publish backend (AmneziaGeo.Windows.App, $(if ($selfContained -eq 'true') { 'self-contained' } else { 'framework-dependent' })) =="
dotnet publish $appProj -c $Configuration -r $rid --self-contained $selfContained -p:PublishTrimmed=false -p:PublishSingleFile=false -p:Version=$version $updateProps -o $stage
if ($LASTEXITCODE -ne 0) { throw "App publish failed ($LASTEXITCODE)" }

Write-Host "== publish GUI (AmneziaGeo.Windows.Ui, $(if ($selfContained -eq 'true') { 'self-contained' } else { 'framework-dependent' })) =="
dotnet publish $uiProj -c $Configuration -r $rid --self-contained $selfContained -p:PublishTrimmed=false -p:PublishSingleFile=false -p:Version=$version $iconProps -o $stage
if ($LASTEXITCODE -ne 0) { throw "UI publish failed ($LASTEXITCODE)" }

# Sign our libraries/exes in the stage BEFORE the MSI packs them, so the installed files are signed.
Invoke-SignLibraries $stage

# ---- 2. build the MSI from the stage ----
Write-Host '== build MSI =='
dotnet build $msiProj -c $Configuration -p:StageDir=$stage "-p:HasIcon=$hasIcon"
if ($LASTEXITCODE -ne 0) { throw "MSI build failed ($LASTEXITCODE)" }

$msiBin = Join-Path (Split-Path $msiProj -Parent) 'bin'
$msi = Get-ChildItem -Recurse $msiBin -Filter *.msi -ErrorAction SilentlyContinue |
       Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $msi) { throw 'MSI not found under bin after build.' }
Write-Host "   MSI: $($msi.FullName)"
Invoke-Sign $msi.FullName

# ---- 3. publish the bootstrapper application (self-contained, so no .NET needed to show the UI) ----
if (Test-Path $baPublish) { Remove-Item -Recurse -Force $baPublish }
Write-Host "== publish bootstrapper application ($(if ($selfContained -eq 'true') { 'self-contained' } else { 'framework-dependent' })) =="
dotnet publish $baProj -c $Configuration -r $rid --self-contained $selfContained -p:PublishTrimmed=false -p:PublishSingleFile=false -o $baPublish
if ($LASTEXITCODE -ne 0) { throw "BA publish failed ($LASTEXITCODE)" }

$baExe = Join-Path $baPublish $baExeName
if (-not (Test-Path $baExe)) { throw "BA exe not found: $baExe" }

# Sign our bootstrapper binaries before Burn embeds them as payloads (incl. the BA exe itself).
Invoke-SignLibraries $baPublish

# ---- 4. generate the PayloadGroup over the BA publish folder (every file except the BA exe) ----
Write-Host '== generate BA payload group =='
$prefix = $baPublish.TrimEnd('\') + '\'
$lines = [System.Collections.Generic.List[string]]::new()
$lines.Add('<?xml version="1.0" encoding="utf-8"?>')
$lines.Add('<Wix xmlns="http://wixtoolset.org/schemas/v4/wxs">')
$lines.Add('  <Fragment>')
$lines.Add('    <PayloadGroup Id="BaPayloads">')
foreach ($f in Get-ChildItem -Recurse -File $baPublish) {
    $rel = $f.FullName.Substring($prefix.Length)
    if ($rel -ieq $baExeName) { continue }
    $name = [System.Security.SecurityElement]::Escape($rel)
    $src  = [System.Security.SecurityElement]::Escape($f.FullName)
    $lines.Add("      <Payload Name=`"$name`" SourceFile=`"$src`" />")
}
$lines.Add('    </PayloadGroup>')
$lines.Add('  </Fragment>')
$lines.Add('</Wix>')
Set-Content -Path $genWxs -Value $lines -Encoding UTF8

# ---- 5. build the bundle ----
Write-Host '== build bundle =='
dotnet build $bundleProj -c $Configuration -p:Platform=x64 -p:BundleVersion=$version -p:BaExe=$baExe -p:MsiPath=$($msi.FullName) $iconProps
if ($LASTEXITCODE -ne 0) { throw "Bundle build failed ($LASTEXITCODE)" }

$setupExe = Get-ChildItem -Recurse (Join-Path $bundleDir 'bin') -Filter AmneziaGeoSetup.exe -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTime -Descending | Select-Object -First 1
if ($setupExe) { Invoke-SignBundle $setupExe.FullName }

Write-Host '== result =='
Get-ChildItem -Recurse $bundleDir -Filter AmneziaGeoSetup.exe |
    Select-Object FullName, @{N='MB';E={[math]::Round($_.Length/1MB,1)}}
