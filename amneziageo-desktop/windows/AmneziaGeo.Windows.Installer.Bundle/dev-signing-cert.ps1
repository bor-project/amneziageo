<#
  Creates (idempotently) a self-signed Authenticode code-signing certificate for DEV/TEST signing of the
  AmneziaGeo binaries and installer, and trusts it on THIS machine so the signatures verify locally.

  This is a throwaway development identity, NOT a real publisher identity: end users on other machines
  still see an "unknown publisher" warning, because the certificate is self-signed (it chains only to
  itself). Its purpose is to exercise the whole signing pipeline (installer.config.json -> signtool) end
  to end before a real certificate exists. When a real cert is available, point installer.config.json's
  signingCert at it (by thumbprint, or by pfxPath+password) and nothing else changes.

  The private key is generated straight into the CurrentUser\My store and never leaves it (no PFX export),
  so no key material lands in the repo. The build selects the cert by Subject (signtool /n), so the
  committed installer.config.json stays machine-independent - just run this once on each build machine.

  Run:  powershell -File dev-signing-cert.ps1
#>
param(
    [string]$Subject = 'AmneziaGeo (Self-Signed Dev)',
    [int]$Years = 5
)

$ErrorActionPreference = 'Stop'
$dn = "CN=$Subject"
$codeSigningOid = '1.3.6.1.5.5.7.3.3'

# Reuse an existing, still-valid code-signing cert with this subject instead of minting a new one each run.
$cert = Get-ChildItem Cert:\CurrentUser\My |
    Where-Object { $_.Subject -eq $dn -and $_.HasPrivateKey -and $_.NotAfter -gt (Get-Date) -and
                   ($_.EnhancedKeyUsageList.ObjectId -contains $codeSigningOid) } |
    Sort-Object NotAfter -Descending | Select-Object -First 1

if ($cert) {
    Write-Host "Reusing existing code-signing cert (valid to $($cert.NotAfter.ToString('yyyy-MM-dd')))."
} else {
    Write-Host "Creating self-signed code-signing cert '$dn' ..."
    $cert = New-SelfSignedCertificate `
        -Type CodeSigningCert `
        -Subject $dn `
        -KeyAlgorithm RSA -KeyLength 3072 `
        -HashAlgorithm SHA256 `
        -CertStoreLocation Cert:\CurrentUser\My `
        -NotAfter (Get-Date).AddYears($Years)
}

# Trust it on THIS machine so signtool /verify and Windows accept the signature. A self-signed cert is its
# own root, so it must sit in Root (chain trust) and TrustedPublisher (trust the signer). Adding to the
# per-USER Root store needs an interactive consent dialog (blocked under SSH/automation), so we trust it in
# the LocalMachine stores when elevated. Best-effort: signing works regardless of trust - trust only decides
# whether the signature shows as TRUSTED on this machine. Only the public cert (no private key) is exported.
$cer = Join-Path ([System.IO.Path]::GetTempPath()) 'ag-dev-signing.cer'
Export-Certificate -Cert $cert -FilePath $cer -Force | Out-Null
$elevated = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltinRole]::Administrator)
if ($elevated) {
    $pub = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new($cer)
    foreach ($storeName in 'Root', 'TrustedPublisher') {
        $store = [System.Security.Cryptography.X509Certificates.X509Store]::new($storeName, 'LocalMachine')
        $store.Open('ReadWrite')
        if (-not $store.Certificates.Find('FindByThumbprint', $cert.Thumbprint, $false).Count) {
            $store.Add($pub)
            Write-Host "  trusted in LocalMachine\$storeName"
        }
        $store.Close()
    }
} else {
    Write-Host '  (not elevated) certificate left untrusted - signatures verify as untrusted. Trust it via certmgr if needed.'
}
Remove-Item $cer -Force -ErrorAction SilentlyContinue

Write-Host ''
Write-Host "Subject    : $dn"
Write-Host "Thumbprint : $($cert.Thumbprint)"
Write-Host "Valid to   : $($cert.NotAfter.ToString('yyyy-MM-dd'))"
Write-Host ''
Write-Host "installer.config.json selects this cert by subject; signing is on once this cert exists:"
Write-Host "  `"signingCert`": { `"subject`": `"$Subject`" }"
