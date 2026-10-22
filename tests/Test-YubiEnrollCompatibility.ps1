# Windows CI only: no login, Graph calls or attached key. Run on a disposable runner.
$ErrorActionPreference = 'Stop'
$package = Join-Path $env:RUNNER_TEMP 'yubienroll-1.2.0-win64.msi'
Invoke-WebRequest 'https://downloads.yubico.com/support/yubienroll-1.2.0-win64.msi' -OutFile $package
$signature = Get-AuthenticodeSignature $package
if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'O=Yubico AB') {
    throw "Unexpected installer signature: $($signature.Status) $($signature.SignerCertificate.Subject)"
}
$destination = Join-Path $env:RUNNER_TEMP 'yubienroll-extracted'
$install = Start-Process msiexec.exe -ArgumentList @('/a', ('"' + $package + '"'), '/qn', ('TARGETDIR="' + $destination + '"')) -PassThru -Wait
if ($install.ExitCode -ne 0) { throw "Administrative extraction failed: $($install.ExitCode)" }
$exe = (Get-ChildItem $destination -Filter yubienroll.exe -Recurse -File | Select-Object -First 1).FullName
if (-not $exe) { throw 'YubiEnroll executable not found in extracted package' }
& $exe --version
if ($LASTEXITCODE -ne 0) { throw 'YubiEnroll version check failed' }
# The hosted runner is disposable. Refuse to touch a pre-existing configuration.
$modern = Join-Path $env:APPDATA 'yubienroll/yubienroll.toml'
$legacy = Join-Path $env:APPDATA 'Yubico/yubienroll/yubienroll.toml'
if ((Test-Path $modern) -or (Test-Path $legacy)) { throw 'Unexpected existing CLI configuration on CI runner' }
try {
    & ./tests/YubiEnroll.Tests/bin/Release/net10.0/YubiEnroll.Tests.exe --write-fixture-config $legacy
    if ($LASTEXITCODE -ne 0) { throw 'Could not write synthetic configuration' }
    $output = & $exe providers show UI-test 2>&1
    $code = $LASTEXITCODE
    $output | Write-Output
    if ($code -ne 0) { throw "YubiEnroll 1.2.0 did not read the Yubico configuration path (exit $code)" }
    $text = $output -join "`n"
    if ($text -notmatch '11111111-2222-3333-4444-555555555555' -or $text -notmatch 'aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee') {
        throw 'CLI provider output does not match the synthetic tenant/client'
    }
    $profileOutput = & $exe profiles list
    $profileCode = $LASTEXITCODE
    $profileOutput | Write-Output
    if ($profileCode -ne 0 -or ($profileOutput -join "`n") -notmatch 'UI-test-profile') { throw 'CLI could not read the generated enrollment profile' }
    'PASS: native YubiEnroll 1.2.0 reads the GUI-generated provider and profile.'
} finally {
    if (Test-Path $legacy) { Remove-Item -LiteralPath $legacy }
    if (Test-Path "$legacy.ui.lock") { Remove-Item -LiteralPath "$legacy.ui.lock" }
}
