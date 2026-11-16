[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$AppDirectory,
    [Parameter(Mandatory)][ValidatePattern('^[A-Fa-f0-9]{40}$')][string]$CertificateThumbprint,
    [string]$TimestampServer
)
$ErrorActionPreference = 'Stop'
if ($env:OS -ne 'Windows_NT') { throw 'Authenticode signing requires Windows.' }
$certificate = Get-Item "Cert:\CurrentUser\My\$CertificateThumbprint" -ErrorAction Stop
if (-not $certificate.HasPrivateKey) { throw 'The selected certificate has no private key in this Windows account.' }
if ($certificate.NotBefore -gt (Get-Date) -or $certificate.NotAfter -le (Get-Date)) { throw 'The signing certificate is not currently valid.' }
if ('1.3.6.1.5.5.7.3.3' -notin @($certificate.EnhancedKeyUsageList | ForEach-Object { $_.ObjectId.Value })) {
    throw 'The certificate must have the Code Signing enhanced key usage.'
}
if ($certificate.Subject -ne 'CN=Rory Vossepoel') { throw 'Expected publisher CN=Rory Vossepoel.' }
if ($TimestampServer) {
    $timestampUri = [uri]$TimestampServer
    if (-not $timestampUri.IsAbsoluteUri -or $timestampUri.Scheme -notin @('http', 'https')) { throw 'TimestampServer must be an absolute HTTP(S) URL.' }
}
$app = (Resolve-Path -LiteralPath $AppDirectory).Path
$names = @('YubiEnroll.UI.exe', 'YubiEnroll.ConsoleHost.exe')
foreach ($name in $names) {
    if (-not (Test-Path -LiteralPath (Join-Path $app $name) -PathType Leaf)) { throw "Missing executable: $name" }
}
# Verify staged copies before replacing either executable. Never modify certificate trust here.
$staging = Join-Path ([IO.Path]::GetTempPath()) ('YubiEnroll-sign-' + [guid]::NewGuid())
New-Item -ItemType Directory -Path $staging | Out-Null
try {
    foreach ($name in $names) {
        $path = Join-Path $staging $name
        Copy-Item -LiteralPath (Join-Path $app $name) -Destination $path
        $parameters = @{ LiteralPath = $path; Certificate = $certificate; HashAlgorithm = 'SHA256'; IncludeChain = 'Signer' }
        if ($TimestampServer) { $parameters.TimestampServer = $TimestampServer }
        Set-AuthenticodeSignature @parameters | Out-Null
        $signature = Get-AuthenticodeSignature -LiteralPath $path
        if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Thumbprint -ne $certificate.Thumbprint) {
            throw "Signature verification failed for ${name}: $($signature.Status). For self-signed certificates, explicitly trust the public certificate on the signing PC first."
        }
        if ($TimestampServer -and -not $signature.TimeStamperCertificate) { throw "Timestamp missing for $name; the requested timestamp is required." }
    }
    foreach ($name in $names) { Copy-Item -LiteralPath (Join-Path $staging $name) -Destination (Join-Path $app $name) -Force }
    Write-Host "Signed and verified both executables. Publisher: Rory Vossepoel. Certificate: $($certificate.Thumbprint)"
    if (-not $TimestampServer) { Write-Host 'No trusted timestamp requested. Signatures will need renewal before certificate expiry.' }
} finally {
    Remove-Item -LiteralPath $staging -Recurse -Force
}
