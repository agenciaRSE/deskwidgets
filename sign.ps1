# Signs DeskWidgets.exe with Azure Artifact Signing (formerly Trusted Signing), so Smart App Control
# and SmartScreen accept it. Needs signing.json (copy signing.example.json) and an Azure login (`az login`).
param([string]$File = (Join-Path $PSScriptRoot 'DeskWidgets.exe'))
$ErrorActionPreference = 'Stop'

$cfgPath = Join-Path $PSScriptRoot 'signing.json'
if (-not (Test-Path $cfgPath)) { throw "Falta signing.json. Copia signing.example.json y rellena los datos de tu cuenta de Artifact Signing." }
$cfg = Get-Content $cfgPath -Raw | ConvertFrom-Json

if (-not (Get-Module -ListAvailable -Name TrustedSigning)) {
    Install-Module TrustedSigning -Scope CurrentUser -Force
}

Invoke-TrustedSigning `
    -Endpoint $cfg.endpoint `
    -CodeSigningAccountName $cfg.account `
    -CertificateProfileName $cfg.profile `
    -Files $File `
    -FileDigest SHA256 `
    -TimestampRfc3161 'http://timestamp.acs.microsoft.com' `
    -TimestampDigest SHA256

$sig = Get-AuthenticodeSignature $File
if ($sig.Status -ne 'Valid') { throw "Firma no válida: $($sig.Status) $($sig.StatusMessage)" }
Write-Host "Firmado: $($sig.SignerCertificate.Subject)"
