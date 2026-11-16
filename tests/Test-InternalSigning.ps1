$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$temporary = Join-Path ([IO.Path]::GetTempPath()) ('YubiEnroll-sign-test-' + [guid]::NewGuid())
$certificate = $null
try {
    New-Item -ItemType Directory -Path $temporary | Out-Null
    # Disposable CI-only key. It is never used for downloadable application artifacts.
    $certificate = New-SelfSignedCertificate -Type CodeSigningCert -Subject 'CN=Rory Vossepoel' `
        -FriendlyName 'Disposable YubiEnroll signing test' -CertStoreLocation Cert:\CurrentUser\My `
        -KeyExportPolicy NonExportable -HashAlgorithm SHA256 -NotAfter (Get-Date).AddDays(1)
    $publicPath = Join-Path $temporary 'test.cer'
    Export-Certificate -Cert $certificate -FilePath $publicPath | Out-Null
    # The hosted runner is elevated; machine trust avoids the interactive user-root dialog.
    Import-Certificate -FilePath $publicPath -CertStoreLocation Cert:\LocalMachine\Root | Out-Null
    $names = @('YubiEnroll.UI.exe', 'YubiEnroll.ConsoleHost.exe')
    foreach ($name in $names) {
        Copy-Item -LiteralPath "$root/artifacts/win-x64/app/$name" -Destination $temporary
        $version = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $temporary $name))
        if ($version.ProductName -ne 'YubiEnroll UI' -or $version.CompanyName -ne 'Rory Vossepoel') { throw "Incorrect product/publisher metadata: $name" }
    }
    & "$root/scripts/Sign-App.ps1" -AppDirectory $temporary -CertificateThumbprint $certificate.Thumbprint
    $zip = Join-Path $temporary 'signed.zip'
    Compress-Archive -Path "$temporary/*.exe" -DestinationPath $zip
    $expanded = Join-Path $temporary 'expanded'
    Expand-Archive -LiteralPath $zip -DestinationPath $expanded
    foreach ($name in $names) {
        $signature = Get-AuthenticodeSignature -LiteralPath (Join-Path $expanded $name)
        if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Thumbprint -ne $certificate.Thumbprint) { throw "Signature lost after packaging: $name" }
    }
    # Modifying a signed PE's DOS stub must invalidate its signature.
    $tampered = Join-Path $expanded $names[0]
    $bytes = [IO.File]::ReadAllBytes($tampered)
    $bytes[80] = $bytes[80] -bxor 1
    [IO.File]::WriteAllBytes($tampered, $bytes)
    if ((Get-AuthenticodeSignature -LiteralPath $tampered).Status -eq 'Valid') { throw 'Tampered executable still validates.' }
    Write-Host 'Internal signing passed: publisher metadata, both signatures, ZIP round-trip and tamper detection.'
} finally {
    if ($certificate) {
        Remove-Item "Cert:\LocalMachine\Root\$($certificate.Thumbprint)" -ErrorAction SilentlyContinue
        Remove-Item "Cert:\CurrentUser\My\$($certificate.Thumbprint)" -DeleteKey -ErrorAction SilentlyContinue
    }
    Remove-Item -LiteralPath $temporary -Recurse -Force -ErrorAction SilentlyContinue
}
