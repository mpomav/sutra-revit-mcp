<#
.SYNOPSIS
    Clean-room build and verification script for SCIO Revit MCP v4.
#>
[CmdletBinding()]
param(
    [int[]]$Years = @(2023, 2024, 2025, 2026, 2027),
    [string]$Configuration = "Release",
    [switch]$SkipTests
)

$ErrorActionPreference = "Stop"
$RepoRoot = Split-Path -Parent $PSScriptRoot

Push-Location $RepoRoot
try {
    Write-Host "=== Building Scio.Server ($Configuration) ===" -ForegroundColor Cyan
    dotnet build src/Scio.Server/Scio.Server.csproj -c $Configuration --nologo -v minimal
    if ($LASTEXITCODE -ne 0) { throw "Scio.Server build failed." }

    foreach ($yr in $Years) {
        $apiDll = "C:\Program Files\Autodesk\Revit $yr\RevitAPI.dll"
        if (-not (Test-Path $apiDll)) {
            Write-Host "Skipping Revit $yr (not installed)." -ForegroundColor Yellow
            continue
        }
        Write-Host "=== Building Revit $yr payload ($Configuration) ===" -ForegroundColor Cyan
        dotnet build src/Scio.Revit.Host/Scio.Revit.Host.csproj -p:RevitYear=$yr -c $Configuration --nologo -v minimal
        if ($LASTEXITCODE -ne 0) { throw "Scio.Revit.Host build failed for Revit $yr." }

        dotnet build src/Scio.Revit.Python/Scio.Revit.Python.csproj -p:RevitYear=$yr -c $Configuration --nologo -v minimal
        if ($LASTEXITCODE -ne 0) { throw "Scio.Revit.Python build failed for Revit $yr." }

        dotnet build src/Scio.Revit.Ui/Scio.Revit.Ui.csproj -p:RevitYear=$yr -c $Configuration --nologo -v minimal
        if ($LASTEXITCODE -ne 0) { throw "Scio.Revit.Ui build failed for Revit $yr." }

        dotnet build src/Modules/Scio.Module.CoreRead/Scio.Module.CoreRead.csproj -p:RevitYear=$yr -c $Configuration --nologo -v minimal
        if ($LASTEXITCODE -ne 0) { throw "Scio.Module.CoreRead build failed for Revit $yr." }

        dotnet build src/Modules/Scio.Module.ScioNative/Scio.Module.ScioNative.csproj -p:RevitYear=$yr -c $Configuration --nologo -v minimal
        if ($LASTEXITCODE -ne 0) { throw "Scio.Module.ScioNative build failed for Revit $yr." }

        dotnet build src/Modules/Scio.Module.Modeling/Scio.Module.Modeling.csproj -p:RevitYear=$yr -c $Configuration --nologo -v minimal
        if ($LASTEXITCODE -ne 0) { throw "Scio.Module.Modeling build failed for Revit $yr." }
    }

    if (-not $SkipTests) {
        Write-Host "=== Running Unit & IPC Tests ===" -ForegroundColor Cyan
        dotnet test tests/Scio.Core.Tests/Scio.Core.Tests.csproj -c $Configuration --nologo -v minimal
        if ($LASTEXITCODE -ne 0) { throw "Tests failed." }
    }

    Write-Host "SCIO Revit MCP v4 build succeeded." -ForegroundColor Green
}
finally {
    Pop-Location
}
