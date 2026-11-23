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
# Login has no force option; our dedicated screen uses the documented browser suppression flag.
$loginHelp = & $exe login --help 2>&1
if ($LASTEXITCODE -ne 0 -or ($loginHelp -join "`n") -notmatch '--no-launch-browser') { throw 'Missing native login browser option' }
# Inspect the actual 1.2.0 read and remove command contracts; no tenant call or deletion.
$listHelp = & $exe credentials list --help
if ($LASTEXITCODE -ne 0 -or ($listHelp -join ' ') -notmatch 'USER_ID') { throw 'Unexpected passkey list command' }
$deleteHelp = & $exe credentials delete --help
if ($LASTEXITCODE -ne 0 -or ($deleteHelp -join ' ') -notmatch 'CREDENTIAL_IDS' -or ($deleteHelp -join ' ') -notmatch '--force') { throw 'Unexpected passkey removal command' }
# Confirm the documented enrollment contract against the exact supported executable.
$enrollmentHelp = & $exe credentials add --help 2>&1
$enrollmentHelpCode = $LASTEXITCODE
$enrollmentHelp | Write-Output
if ($enrollmentHelpCode -ne 0 -or ($enrollmentHelp -join "`n") -notmatch '--profile' -or ($enrollmentHelp -join "`n") -notmatch '--reader') {
    throw 'Unexpected native enrollment command interface; review target-selection compatibility'
}
foreach ($option in @('--no-reset', '--no-random-pin', '--no-force-pin-change', '--no-require-always-uv', '--no-require-ea')) {
    if (($enrollmentHelp -join "`n") -notmatch [regex]::Escape($option)) { throw "Missing add-passkey option $option" }
}
# Exercise the real native program through the published terminal renderer, without sign-in.
$terminalArgs = '--terminal-check "' + $exe + '"'
$terminalCheck = Start-Process ./artifacts/win-x64/app/PasskeyDesk.exe -ArgumentList $terminalArgs -PassThru
if (-not $terminalCheck.WaitForExit(30000)) { $terminalCheck.Kill(); throw 'Native terminal rendering test timed out' }
if ($terminalCheck.ExitCode -ne 0) {
    Get-Content ./artifacts/win-x64/app/native-terminal-check.txt -ErrorAction SilentlyContinue
    throw 'Native terminal rendering test failed'
}
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
    $profilesText = ($profileOutput -join "`n") -replace '[│┃]', ' '
    if ($profilesText -notmatch 'UI-test-profile\s+6\s+False\s+False\s+True\s+True\s+True\s+6' -or
        $profilesText -notmatch 'Second profile\s+8\s+False\s+False\s+False\s+False\s+True\s+10') {
        throw 'CLI did not read all seven edited profile settings and the independent second profile'
    }
    if ($profilesText -notmatch 'Preserve existing key\s+4\s+False\s+False\s+False\s+False\s+False\s+4') {
        throw 'Native preservation profile did not retain the detected PIN floor with reset, random PIN, forced change and UV changes disabled'
    }
    & ./tests/YubiEnroll.Tests/bin/Release/net10.0/YubiEnroll.Tests.exe --rename-fixture-config $legacy
    if ($LASTEXITCODE -ne 0) { throw 'Rename fixture failed' }
    $renamed = & $exe providers show 'UI test renamed' 2>&1
    if ($LASTEXITCODE -ne 0 -or ($renamed -join "`n") -notmatch 'Renamed profile') { throw 'CLI did not read renamed tenant/profile with spaces' }
    & $exe providers activate 'UI test renamed'
    if ($LASTEXITCODE -ne 0) { throw 'Native activation of renamed provider failed' }
    & ./tests/YubiEnroll.Tests/bin/Release/net10.0/YubiEnroll.Tests.exe --remove-fixture-config $legacy
    if ($LASTEXITCODE -ne 0) { throw 'Remove fixture failed' }
    & $exe providers list
    if ($LASTEXITCODE -ne 0) { throw 'CLI did not accept no providers/no active provider' }
    & ./tests/YubiEnroll.Tests/bin/Release/net10.0/YubiEnroll.Tests.exe --write-policy-fixture $legacy
    if ($LASTEXITCODE -ne 0) { throw 'Could not materialize policy TOML from shipped templates' }
    $policyProvider = & $exe providers show 'Pilot tenant'
    if ($LASTEXITCODE -ne 0 -or ($policyProvider -join ' ') -notmatch 'aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee') { throw 'Native CLI did not read policy tenant' }
    $policyProfiles = & $exe profiles list
    if ($LASTEXITCODE -ne 0 -or ($policyProfiles -join ' ') -notmatch 'Profile5') { throw 'Native CLI did not read policy profiles' }
    $fixture = './tests/YubiEnroll.Tests/bin/Release/net10.0/YubiEnroll.Tests.exe'
    $before = Join-Path $env:RUNNER_TEMP 'policy-before-activation.toml'
    & $fixture --activation-fixture prepare $before $legacy
    if ($LASTEXITCODE -ne 0) { throw 'Could not prepare three managed profiles' }
    & $exe providers activate 'Second pilot tenant'
    if ($LASTEXITCODE -ne 0) { throw 'Native activation failed' }
    & $fixture --activation-fixture verify $before $legacy
    if ($LASTEXITCODE -ne 0) {
        # Only synthetic CI data: show what the supported native CLI changed.
        Compare-Object (Get-Content $before) (Get-Content $legacy) | Out-Host
        throw 'Native activation invalidated tenant or managed profile selections'
    }
    'PASS: all three managed profiles retain the same meaning after native tenant activation.'
} finally {
    if (Test-Path $legacy) { Remove-Item -LiteralPath $legacy }
    if (Test-Path "$legacy.ui.lock") { Remove-Item -LiteralPath "$legacy.ui.lock" }
}
