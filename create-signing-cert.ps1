# Create self-signed code signing certificate for HostWitness.
# Run once to generate certs\HostWitness.pfx; publish.cmd invokes Sign-HostWitness.ps1 to sign the exe when the PFX exists.
# Subject uses ASCII-only (L=Taipei, S=Taiwan, C=TW) to avoid garbled text in Windows signature details.
param(
    [string]$Password = "HostWitness",
    [string]$Email = "shar@nine-security.com"
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$certsDir = Join-Path $root "certs"
$pfxPath = Join-Path $certsDir "HostWitness.pfx"

if (-not (Test-Path $certsDir)) {
    New-Item -ItemType Directory -Path $certsDir -Force | Out-Null
}

# Subject: ASCII-only to prevent ??? in Windows "Digital Signature Details" dialog
$subject = "CN=nine-security Inc., O=nine-security Inc., OU=Nine-Security, L=Taipei, S=Taiwan, C=TW, E=$Email"

Write-Host "Creating code signing certificate (E=$Email)..."
$cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject $subject -CertStoreLocation "Cert:\CurrentUser\My" -NotAfter (Get-Date).AddYears(10) -KeyAlgorithm RSA -KeyLength 2048

$secPass = ConvertTo-SecureString -String $Password -Force -AsPlainText
Export-PfxCertificate -Cert $cert -FilePath $pfxPath -Password $secPass | Out-Null

# Remove from store so only the PFX is used (optional; re-import from PFX when needed)
Remove-Item -Path "Cert:\CurrentUser\My\$($cert.Thumbprint)" -Force -ErrorAction SilentlyContinue

Write-Host "Certificate exported to: $pfxPath"
Write-Host "To sign on publish: run .\publish.cmd (PFX password: $Password or set `$env:HOSTWITNESS_PFX_PASSWORD)"
