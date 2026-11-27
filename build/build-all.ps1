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

$RevitProjects = @(
    "src/Scio.Revit.Host/Scio.Revit.Host.csproj",
    "src/Scio.Revit.Python/Scio.Revit.Python.csproj",
    "src/Scio.Revit.CSharp/Scio.Revit.CSharp.csproj",
    "src/Scio.Revit.Ui/Scio.Revit.Ui.csproj",
    "src/Modules/Scio.Module.CoreRead/Scio.Module.CoreRead.csproj",
    "src/Modules/Scio.Module.ScioNative/Scio.Module.ScioNative.csproj",
    "src/Modules/Scio.Module.Modeling/Scio.Module.Modeling.csproj",
    "src/Modules/Scio.Module.Documentation/Scio.Module.Documentation.csproj",
    "src/Modules/Scio.Module.AuditQa/Scio.Module.AuditQa.csproj",
    "src/Modules/Scio.Module.Mep/Scio.Module.Mep.csproj",
    "src/Modules/Scio.Module.Structure/Scio.Module.Structure.csproj",
    "src/Modules/Scio.Module.CadIfc/Scio.Module.CadIfc.csproj",
    "src/Modules/Scio.Module.IsoInterop/Scio.Module.IsoInterop.csproj"
)

Push-Location $RepoRoot
try {
    Write-Host "=== Building Scio.Server ($Configuration) ===" -ForegroundColor Cyan
    dotnet build src/Scio.Server/Scio.Server.csproj -c $Configuration --nologo -v minimal
    if ($LASTEXITCODE -ne 0) { throw "Scio.Server build failed." }

    foreach ($yr in $Years) {
        $tfm = if ($yr -le 2024) { "net48" } elseif ($yr -le 2026) { "net8.0-windows" } else { "net10.0-windows" }
        $apiDll = "C:\Program Files\Autodesk\Revit $yr\RevitAPI.dll"
        $apiSource = if ($tfm -eq "net8.0-windows") { "Nice3point.Revit.Api net8 universal" } elseif (Test-Path $apiDll) { "local RevitAPI.dll" } else { "Nice3point.Revit.Api NuGet" }
        Write-Host "=== Building Revit $yr payload ($Configuration, $tfm, $apiSource, $($RevitProjects.Count) projects) ===" -ForegroundColor Cyan
        foreach ($proj in $RevitProjects) {
            dotnet build $proj -p:RevitYear=$yr -c $Configuration --nologo -v quiet
            if ($LASTEXITCODE -ne 0) { throw "$proj build failed for Revit $yr." }
        }
        Write-Host "  Revit $yr OK ($($RevitProjects.Count)/$($RevitProjects.Count) projects)." -ForegroundColor Green
    }

    if (-not $SkipTests) {
        Write-Host "=== Running Unit & IPC Tests ===" -ForegroundColor Cyan
        dotnet test tests/Scio.Core.Tests/Scio.Core.Tests.csproj -c $Configuration --nologo -v minimal
        if ($LASTEXITCODE -ne 0) { throw "Tests failed." }
    }

    Write-Host "SCIO Revit MCP v4 build succeeded across all targets." -ForegroundColor Green
}
finally {
    Pop-Location
}
