# Disposable Windows CI only. Never calls reset; only version/list/reset --help.
$ErrorActionPreference = 'Stop'
$package = Join-Path $env:RUNNER_TEMP 'yubikey-manager-5.9.2-win64.msi'
Invoke-WebRequest 'https://github.com/Yubico/yubikey-manager/releases/download/5.9.2/yubikey-manager-5.9.2-win64.msi' -OutFile $package
$signature = Get-AuthenticodeSignature $package
if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'O=Yubico AB') {
    throw "Unexpected ykman installer signature: $($signature.Status) $($signature.SignerCertificate.Subject)"
}
$destination = Join-Path $env:RUNNER_TEMP 'ykman-extracted'
$install = Start-Process msiexec.exe -ArgumentList @('/a', ('"' + $package + '"'), '/qn', ('TARGETDIR="' + $destination + '"')) -PassThru -Wait
if ($install.ExitCode -ne 0) { throw "Administrative extraction failed: $($install.ExitCode)" }
$exe = (Get-ChildItem $destination -Filter ykman.exe -Recurse -File | Select-Object -First 1).FullName
if (-not $exe -or -not $exe.EndsWith('\Yubico\YubiKey Manager CLI\ykman.exe', [StringComparison]::OrdinalIgnoreCase)) {
    throw "Unexpected ykman installation folder: $exe"
}
& ./tests/YubiEnroll.Tests/bin/Release/net10.0/YubiEnroll.Tests.exe --check-ykman $exe
if ($LASTEXITCODE -ne 0) { throw 'Native ykman compatibility check failed' }
