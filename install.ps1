<#
.SYNOPSIS
    Clean-room local source installer for Sutra — Revit MCP (Apache-2.0, SCIO DC).
    Cleans legacy manifests/payloads and builds + deploys per Revit year so every
    year receives binaries compiled against its exact RevitAPI.dll.
#>
[CmdletBinding()]
param(
    [int[]]$Years = @(2023, 2024, 2025, 2026, 2027),
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$RepoRoot = $PSScriptRoot

if (Get-Process -Name "Revit" -ErrorAction SilentlyContinue) {
    throw "Autodesk Revit is currently running. Close all Revit windows before installing Sutra — Revit MCP."
}

# Stop any idle scio-revit-mcp server process before replacing binaries
Get-Process -Name "scio-revit-mcp" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

Write-Host "=== Building Scio.Server ($Configuration) ===" -ForegroundColor Cyan
dotnet build (Join-Path $RepoRoot "src\Scio.Server\Scio.Server.csproj") -c $Configuration --nologo -v minimal
if ($LASTEXITCODE -ne 0) { throw "Scio.Server build failed." }

$ServerDst = Join-Path $env:LOCALAPPDATA "Programs\Scio\MCP\server"
if (Test-Path $ServerDst) { Remove-Item -Path $ServerDst -Recurse -Force }
New-Item -ItemType Directory -Force -Path $ServerDst | Out-Null
Copy-Item -Path (Join-Path $RepoRoot "src\Scio.Server\bin\$Configuration\net8.0\*") -Destination $ServerDst -Recurse -Force
$ServerExe = Join-Path $ServerDst "scio-revit-mcp.exe"

$RevitProjects = @(
    "src\Scio.Revit.Host\Scio.Revit.Host.csproj",
    "src\Scio.Revit.Python\Scio.Revit.Python.csproj",
    "src\Scio.Revit.Ui\Scio.Revit.Ui.csproj",
    "src\Modules\Scio.Module.CoreRead\Scio.Module.CoreRead.csproj",
    "src\Modules\Scio.Module.ScioNative\Scio.Module.ScioNative.csproj",
    "src\Modules\Scio.Module.Modeling\Scio.Module.Modeling.csproj",
    "src\Modules\Scio.Module.Documentation\Scio.Module.Documentation.csproj",
    "src\Modules\Scio.Module.AuditQa\Scio.Module.AuditQa.csproj",
    "src\Modules\Scio.Module.Mep\Scio.Module.Mep.csproj",
    "src\Modules\Scio.Module.Structure\Scio.Module.Structure.csproj",
    "src\Modules\Scio.Module.CadIfc\Scio.Module.CadIfc.csproj",
    "src\Modules\Scio.Module.IsoInterop\Scio.Module.IsoInterop.csproj"
)

$InstalledYears = @()
foreach ($yr in $Years) {
    if (-not (Test-Path "C:\Program Files\Autodesk\Revit $yr\RevitAPI.dll")) { continue }
    $tfm = if ($yr -le 2024) { "net48" } else { "net10.0-windows" }

    Write-Host "=== Building & Installing Revit $yr ($tfm) ===" -ForegroundColor Cyan
    foreach ($proj in $RevitProjects) {
        dotnet build (Join-Path $RepoRoot $proj) -p:RevitYear=$yr -c $Configuration --nologo -v quiet
        if ($LASTEXITCODE -ne 0) { throw "$proj build failed for Revit $yr." }
    }

    $addinRoot = Join-Path $env:APPDATA "Autodesk\Revit\Addins\$yr"
    New-Item -ItemType Directory -Force -Path $addinRoot | Out-Null

    # Remove legacy manifests and stale Scio payload directory
    foreach ($oldManifest in @("Scio.addin", "ScioRevitMcp.addin")) {
        $oldPath = Join-Path $addinRoot $oldManifest
        if (Test-Path $oldPath) { Remove-Item -Path $oldPath -Force }
    }
    $payloadDir = Join-Path $addinRoot "Scio"
    if (Test-Path $payloadDir) { Remove-Item -Path $payloadDir -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $payloadDir | Out-Null

    foreach ($proj in $RevitProjects) {
        $projDir = Split-Path -Parent $proj
        Copy-Item -Path (Join-Path $RepoRoot "$projDir\bin\$Configuration\$tfm\*") -Destination $payloadDir -Recurse -Force
    }

    $manifest = @"
<?xml version="1.0" encoding="utf-8"?>
<RevitAddIns>
  <AddIn Type="Application">
    <Name>Sutra — Revit MCP (SCIO DC)</Name>
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
    Write-Host "  Installed Revit $yr -> $payloadDir" -ForegroundColor Green
}

Write-Host ""
Write-Host "Sutra — Revit MCP installed cleanly." -ForegroundColor Green
Write-Host "  MCP Server : $ServerExe" -ForegroundColor Cyan
Write-Host "  Revit Years: $($InstalledYears -join ', ')" -ForegroundColor Cyan
