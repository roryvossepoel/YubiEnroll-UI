[CmdletBinding()]
param(
    [ValidateSet('win-x64', 'win-arm64')][string]$Runtime = 'win-x64',
    [switch]$SkipTests
)
$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    function Invoke-Dotnet {
        & dotnet @args
        if ($LASTEXITCODE -ne 0) { throw "dotnet failed with exit code $LASTEXITCODE" }
    }
    if (-not $SkipTests) { Invoke-Dotnet run --project tests/YubiEnroll.Tests -c Release }
    $out = Join-Path $PSScriptRoot "artifacts/$Runtime"
    if (Test-Path $out) { Remove-Item -LiteralPath $out -Recurse -Force }
    Invoke-Dotnet publish src/YubiEnroll.UI -c Release -r $Runtime --self-contained true -o "$out/app"
    Invoke-Dotnet publish src/YubiEnroll.ConsoleHost -c Release -r $Runtime --self-contained true -o "$out/host"
    # Keep the host runtime separate so publishing cannot overwrite the UI's dependencies.
    Move-Item -LiteralPath "$out/host" -Destination "$out/app/console-host"
    Copy-Item README.md, LICENSE, THIRD-PARTY-NOTICES.md -Destination "$out/app"
    Compress-Archive -Path "$out/app/*" -DestinationPath "$out/YubiEnroll-UI-$Runtime.zip" -Force
} finally { Pop-Location }
