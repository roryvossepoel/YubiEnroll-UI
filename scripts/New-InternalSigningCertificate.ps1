[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
if ($env:OS -ne 'Windows_NT') { throw 'Run this script on the Windows PC that will sign the application.' }

# Reuse one identity across builds. Never export its private key or create a key per build.
$subject = 'CN=Rory Vossepoel'
$friendlyName = 'YubiEnroll UI - internal code signing'
$certificates = @(Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert | Where-Object {
    $_.Subject -eq $subject -and $_.FriendlyName -eq $friendlyName -and $_.HasPrivateKey -and
    $_.NotBefore -le (Get-Date) -and $_.NotAfter -gt (Get-Date)
})
if ($certificates.Count -gt 1) { throw 'Multiple internal signing certificates found. Select the intended thumbprint explicitly; no new certificate was created.' }
if ($certificates.Count -eq 1) { $certificate = $certificates[0] }
else {
    $certificate = New-SelfSignedCertificate -Type CodeSigningCert -Subject $subject -FriendlyName $friendlyName `
        -CertStoreLocation Cert:\CurrentUser\My -KeyAlgorithm RSA -KeyLength 3072 -HashAlgorithm SHA256 `
        -KeyExportPolicy NonExportable -NotAfter (Get-Date).AddYears(2)
}
$publicDirectory = Join-Path $PSScriptRoot '../artifacts/signing'
New-Item -ItemType Directory -Path $publicDirectory -Force | Out-Null
$publicPath = Join-Path $publicDirectory 'Rory-Vossepoel-YubiEnroll-UI.cer'
Export-Certificate -Cert $certificate -FilePath $publicPath -Type CERT -Force | Out-Null
Write-Host "Publisher: $($certificate.Subject)"
Write-Host "Thumbprint: $($certificate.Thumbprint)"
Write-Host "Expires: $($certificate.NotAfter.ToString('yyyy-MM-dd'))"
Write-Host "Public certificate: $([IO.Path]::GetFullPath($publicPath))"
Write-Host 'The private key stays in this Windows account. Trust is not installed automatically.'
$certificate
