[CmdletBinding()]
param(
    [ValidateSet('win-x64')][string]$Runtime = 'win-x64',
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
    $bundle = @('-p:PublishSingleFile=true', '-p:PublishTrimmed=false', '-p:DebugType=None', '-p:DebugSymbols=false')
    Invoke-Dotnet publish src/YubiEnroll.UI -c Release -r $Runtime --self-contained false @bundle -o "$out/app"
    Invoke-Dotnet publish src/YubiEnroll.ConsoleHost -c Release -r $Runtime --self-contained false @bundle -o "$out/host"
    # Bundle application assemblies only; .NET 10 Desktop Runtime is a prerequisite.
    Move-Item -LiteralPath "$out/host/YubiEnroll.ConsoleHost.exe" -Destination "$out/app/YubiEnroll.ConsoleHost.exe"
    Copy-Item README.md, LICENSE, THIRD-PARTY-NOTICES.md -Destination "$out/app"
    $files = @(Get-ChildItem -LiteralPath "$out/app" -Recurse -File)
    if ($files.Count -ne 5) { throw "Expected two executables and three documentation files; got $($files.Count) files." }
    Compress-Archive -Path "$out/app/*" -DestinationPath "$out/YubiEnroll-UI-$Runtime.zip" -Force
} finally { Pop-Location }
