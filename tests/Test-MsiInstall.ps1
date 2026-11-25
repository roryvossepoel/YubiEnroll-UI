# Uses a fresh, disposable Windows runner. Never replace an existing installation.
param([Parameter(Mandatory)][string]$Package)
$ErrorActionPreference = 'Stop'
$directory = Join-Path $env:ProgramFiles 'Passkey Desk'
$shortcut = Join-Path ([Environment]::GetFolderPath('CommonPrograms')) 'PassKey Desk.lnk'
$uninstallKey = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall'
$productName = 'Passkey Desk for YubiKeys'
function Get-ProductRegistration {
    Get-ItemProperty "$uninstallKey\*" | Where-Object { $_.DisplayName -eq $productName }
}
function Invoke-Installer([string]$Action) {
    $result = Start-Process msiexec.exe -ArgumentList "$Action `"$Package`" /qn /norestart" -PassThru -Wait
    if ($result.ExitCode -notin 0, 3010) { throw "msiexec $Action returned $($result.ExitCode)." }
}
if ((Test-Path -LiteralPath $directory) -or (Test-Path -LiteralPath $shortcut) -or (Get-ProductRegistration)) {
    throw 'The MSI test requires a clean machine.'
}
Invoke-Installer '/i'
try {
    $exe = Get-Item -LiteralPath (Join-Path $directory 'PasskeyDesk.exe')
    foreach ($path in @('PasskeyDesk.ConsoleHost.exe', 'policy/PasskeyDesk.admx', 'policy/en-US/PasskeyDesk.adml')) {
        if (!(Test-Path -LiteralPath (Join-Path $directory $path))) { throw "Missing installed file: $path" }
    }
    if (!(Test-Path -LiteralPath $shortcut)) { throw 'Missing all-users Start menu shortcut.' }
    $registration = @(Get-ProductRegistration)
    [xml]$metadata = Get-Content -Raw (Join-Path $PSScriptRoot '../Directory.Build.props')
    $expected = $metadata.Project.PropertyGroup
    $visibleEntries = @(Get-ItemProperty "$uninstallKey\*" | Where-Object { $_.DisplayName -like '*Passkey*' } |
        Select-Object DisplayName, Publisher, DisplayVersion, DisplayIcon, UninstallString)
    Write-Host "Programs and Features: $(ConvertTo-Json -Compress -InputObject $visibleEntries)"
    Write-Host "Expected: $($expected.Product), $($expected.Company), $($expected.Version)"
    if ($registration.Count -ne 1 -or $registration[0].Publisher -ne $expected.Company -or
        $registration[0].DisplayVersion -ne $expected.Version -or
        !$registration[0].UninstallString -or $registration[0].DisplayIcon -ne ('"{0}",0' -f $exe.FullName)) {
        throw 'Programs and Features metadata does not match the application release metadata.'
    }
} finally {
    Invoke-Installer '/x'
}
if ((Test-Path -LiteralPath $directory) -or (Test-Path -LiteralPath $shortcut) -or (Get-ProductRegistration)) {
    throw 'Uninstall left application files, a shortcut or a product registration behind.'
}
