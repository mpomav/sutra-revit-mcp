<#
.SYNOPSIS
    Clean-room local source installer for SCIO Revit MCP v4.0 (Apache-2.0, SCIO DC).
#>
[CmdletBinding()]
param(
    [int[]]$Years = @(2023, 2024, 2025, 2026, 2027),
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$RepoRoot = $PSScriptRoot

if (Get-Process -Name "Revit" -ErrorAction SilentlyContinue) {
    throw "Autodesk Revit is currently running. Close all Revit windows before installing SCIO Revit MCP v4."
}

& (Join-Path $RepoRoot "build\build-all.ps1") -Years $Years -Configuration $Configuration -SkipTests

$ServerDst = Join-Path $env:LOCALAPPDATA "Programs\Scio\MCP\server"
New-Item -ItemType Directory -Force -Path $ServerDst | Out-Null
Copy-Item -Path (Join-Path $RepoRoot "src\Scio.Server\bin\$Configuration\net8.0\*") -Destination $ServerDst -Recurse -Force
$ServerExe = Join-Path $ServerDst "scio-revit-mcp.exe"

$InstalledYears = @()
foreach ($yr in $Years) {
    if (-not (Test-Path "C:\Program Files\Autodesk\Revit $yr\RevitAPI.dll")) { continue }
    $tfm = if ($yr -le 2024) { "net48" } else { "net10.0-windows" }
    $addinRoot = Join-Path $env:APPDATA "Autodesk\Revit\Addins\$yr"
    $payloadDir = Join-Path $addinRoot "Scio"
    New-Item -ItemType Directory -Force -Path $payloadDir | Out-Null

    $sources = @(
        "src\Scio.Revit.Host\bin\$Configuration\$tfm\*",
        "src\Scio.Revit.Python\bin\$Configuration\$tfm\*",
        "src\Scio.Revit.Ui\bin\$Configuration\$tfm\*",
        "src\Modules\Scio.Module.CoreRead\bin\$Configuration\$tfm\*",
        "src\Modules\Scio.Module.ScioNative\bin\$Configuration\$tfm\*",
        "src\Modules\Scio.Module.Modeling\bin\$Configuration\$tfm\*",
        "src\Modules\Scio.Module.Documentation\bin\$Configuration\$tfm\*",
        "src\Modules\Scio.Module.AuditQa\bin\$Configuration\$tfm\*",
        "src\Modules\Scio.Module.Mep\bin\$Configuration\$tfm\*",
        "src\Modules\Scio.Module.Structure\bin\$Configuration\$tfm\*",
        "src\Modules\Scio.Module.CadIfc\bin\$Configuration\$tfm\*",
        "src\Modules\Scio.Module.IsoInterop\bin\$Configuration\$tfm\*"
    )
    foreach ($s in $sources) {
        Copy-Item -Path (Join-Path $RepoRoot $s) -Destination $payloadDir -Recurse -Force
    }

    $manifest = @"
<?xml version="1.0" encoding="utf-8"?>
<RevitAddIns>
  <AddIn Type="Application">
    <Name>SCIO Revit MCP v4</Name>
    <Assembly>$payloadDir\Scio.Revit.Host.dll</Assembly>
    <AddInId>8F7B4E21-9A3C-4D1B-B62E-5C04A1F9E204</AddInId>
    <FullClassName>Scio.Revit.Host.RevitBridgeApp</FullClassName>
    <VendorId>SCIO</VendorId>
    <VendorDescription>SCIO DC (Grupo POVA) - https://sciodc.com</VendorDescription>
  </AddIn>
</RevitAddIns>
"@
    Set-Content -Path (Join-Path $addinRoot "ScioRevitMcp.addin") -Value $manifest -Encoding UTF8
    $InstalledYears += $yr
}

Write-Host ""
Write-Host "SCIO Revit MCP v4 installed successfully." -ForegroundColor Green
Write-Host "  MCP Server : $ServerExe" -ForegroundColor Cyan
Write-Host "  Revit Years: $($InstalledYears -join ', ')" -ForegroundColor Cyan
