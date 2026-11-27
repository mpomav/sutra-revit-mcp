#Requires -Version 5.1
<#
.SYNOPSIS
  Builds and deploys the Sutra AutoCAD / Civil 3D add-in (Scio.Cad) as an Autodesk .bundle
  under %APPDATA%\Autodesk\ApplicationPlugins\SutraCad.bundle.
.DESCRIPTION
  Discovers installed AutoCAD / Civil 3D 2023-2026, refuses to run while acad.exe is open,
  compiles Scio.Cad for each installed year (net48 for 2023-2024, net8.0-windows7.0 for 2025-2026),
  generates PackageContents.xml, and verifies SHA-256 of every deployed binary.
#>
param(
    [string[]]$Years = @(),
    [switch]$SkipProcessCheck
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$projPath = Join-Path $repoRoot "src\Scio.Cad\Scio.Cad.csproj"
$bundleDir = Join-Path $env:APPDATA "Autodesk\ApplicationPlugins\SutraCad.bundle"

if (-not $SkipProcessCheck) {
    $running = Get-Process -Name "acad" -ErrorAction SilentlyContinue
    if ($running) {
        throw "AutoCAD / Civil 3D (acad.exe) is currently running (PID: $($running.Id -join ', ')). Close it before installing SutraCad."
    }
}

$supportedYears = @("2023", "2024", "2025", "2026")
$seriesMap = @{
    "2023" = @{ Min = "R24.2"; Max = "R24.2" }
    "2024" = @{ Min = "R24.3"; Max = "R24.3" }
    "2025" = @{ Min = "R25.0"; Max = "R25.0" }
    "2026" = @{ Min = "R25.1"; Max = "R25.1" }
}

$targetYears = @()
if ($Years.Count -gt 0) {
    $targetYears = $Years
} else {
    foreach ($y in $supportedYears) {
        $dll = "C:\Program Files\Autodesk\AutoCAD $y\acdbmgd.dll"
        if (Test-Path $dll) { $targetYears += $y }
    }
}

if ($targetYears.Count -eq 0) {
    throw "No supported AutoCAD / Civil 3D (2023-2026) installation found under C:\Program Files\Autodesk\AutoCAD <year>."
}

Write-Host "Sutra CAD Installer — detected years: $($targetYears -join ', ')" -ForegroundColor Cyan

$componentsXml = New-Object System.Text.StringBuilder
New-Item -ItemType Directory -Force -Path $bundleDir | Out-Null

foreach ($y in $targetYears) {
    $cadDir = "C:\Program Files\Autodesk\AutoCAD $y"
    if (-not (Test-Path (Join-Path $cadDir "acdbmgd.dll"))) {
        throw "AutoCAD $y not found at $cadDir."
    }

    Write-Host "Building Scio.Cad for AutoCAD / Civil 3D $y..." -ForegroundColor Yellow
    & dotnet build $projPath -c Release -p:CadYear=$y "-p:CadDir=$cadDir"
    if ($LASTEXITCODE -ne 0) { throw "dotnet build failed for AutoCAD $y." }

    $outDir = Join-Path $repoRoot "src\Scio.Cad\bin\Release"
    $srcDll = Join-Path $outDir "Scio.Cad.dll"
    if (-not (Test-Path $srcDll)) { throw "Expected build output missing: $srcDll" }

    $destDir = Join-Path $bundleDir "Contents\$y"
    New-Item -ItemType Directory -Force -Path $destDir | Out-Null

    $destDll = Join-Path $destDir "Scio.Cad.dll"
    Copy-Item -Force $srcDll $destDll

    $srcHash = (Get-FileHash $srcDll -Algorithm SHA256).Hash
    $dstHash = (Get-FileHash $destDll -Algorithm SHA256).Hash
    if ($srcHash -ne $dstHash) {
        throw "SHA-256 verification failed for $destDll ($srcHash vs $dstHash)."
    }

    $series = $seriesMap[$y]
    [void]$componentsXml.AppendLine("  <Components Description=`"Sutra CAD $y (AutoCAD &amp; Civil 3D)`">")
    [void]$componentsXml.AppendLine("    <RuntimeRequirements OS=`"Win64`" Platform=`"AutoCAD|AutoCAD*|Civil3D`" SeriesMin=`"$($series.Min)`" SeriesMax=`"$($series.Max)`" />")
    [void]$componentsXml.AppendLine("    <ComponentEntry AppName=`"SutraCad`" Version=`"1.0.0`" ModuleName=`"./Contents/$y/Scio.Cad.dll`" LoadOnAutoCADStartup=`"True`" />")
    [void]$componentsXml.AppendLine("  </Components>")

    Write-Host "  Deployed & verified $y (SHA-256: $($dstHash.Substring(0,12))...)" -ForegroundColor Green
}

$pkgXml = @"
<?xml version="1.0" encoding="utf-8"?>
<ApplicationPackage SchemaVersion="1.0"
                    AutodeskProduct="AutoCAD"
                    Name="Sutra — AutoCAD &amp; Civil 3D MCP"
                    Description="Transactional, local-first MCP bridge for AutoCAD and Civil 3D integrated with Sutra."
                    AppVersion="1.0.0"
                    Author="Marco Poma - SCIO DC"
                    ProductCode="{9F4C2B71-3E48-4C7A-A1F2-5D9E8A31C001}"
                    UpgradeCode="{9F4C2B71-3E48-4C7A-A1F2-5D9E8A31C002}">
  <CompanyDetails Name="SCIO DC (Grupo POVA)" Url="https://sciodc.com" />
$($componentsXml.ToString().TrimEnd())
</ApplicationPackage>
"@

$pkgPath = Join-Path $bundleDir "PackageContents.xml"
[System.IO.File]::WriteAllText($pkgPath, $pkgXml, (New-Object System.Text.UTF8Encoding($false)))

Write-Host "`nInstalled SutraCad.bundle at: $bundleDir" -ForegroundColor Cyan
Write-Host "Start AutoCAD or Civil 3D and run SUTRASTATUS (or check the Sutra ribbon tab)." -ForegroundColor Cyan
