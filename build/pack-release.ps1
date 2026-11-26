<#
.SYNOPSIS
    Stages Sutra — Revit MCP (server + Revit 2023-2027 add-ins), builds the
    portable ZIP, compiles the Inno Setup installer, and writes SHA256SUMS.txt.
#>
[CmdletBinding()]
param(
    [int[]]$Years = @(2023, 2024, 2025, 2026, 2027),
    [string]$Configuration = "Release",
    [switch]$SkipTests
)

$ErrorActionPreference = "Stop"
$RepoRoot = Split-Path -Parent $PSScriptRoot
$PropsPath = Join-Path $RepoRoot "Directory.Build.props"
$PropsXml = [xml](Get-Content -LiteralPath $PropsPath)
$Version = [string]($PropsXml.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1)
if ([string]::IsNullOrWhiteSpace($Version)) {
    throw "Could not read <Version> from $PropsPath"
}

$DistDir = Join-Path $RepoRoot "dist"
$StageDir = Join-Path $DistDir "sutra-$Version"
$ServerStage = Join-Path $StageDir "server"
$AddinsStage = Join-Path $StageDir "addins"

$RevitProjects = @(
    "src/Scio.Revit.Host/Scio.Revit.Host.csproj",
    "src/Scio.Revit.Python/Scio.Revit.Python.csproj",
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
    Write-Host "=== Packaging Sutra — Revit MCP v$Version ($Configuration) ===" -ForegroundColor Cyan

    if (-not $SkipTests) {
        Write-Host "=== Running unit & IPC tests ===" -ForegroundColor Cyan
        dotnet test tests/Scio.Core.Tests/Scio.Core.Tests.csproj -c $Configuration --nologo -v minimal
        if ($LASTEXITCODE -ne 0) { throw "Tests failed." }
    }

    if (Test-Path $StageDir) { Remove-Item -Path $StageDir -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $ServerStage, $AddinsStage | Out-Null

    Write-Host "=== Publishing self-contained Scio.Server (win-x64) ===" -ForegroundColor Cyan
    dotnet publish src/Scio.Server/Scio.Server.csproj -c $Configuration -r win-x64 --self-contained true `
        -p:PublishSingleFile=false -p:PublishTrimmed=false -o $ServerStage --nologo -v minimal
    if ($LASTEXITCODE -ne 0) { throw "Scio.Server publish failed." }

    $BuiltYears = @()
    foreach ($yr in $Years) {
        $apiDll = "C:\Program Files\Autodesk\Revit $yr\RevitAPI.dll"
        if (-not (Test-Path $apiDll)) {
            Write-Host "  Skipping Revit $yr (not installed)." -ForegroundColor Yellow
            continue
        }
        $tfm = if ($yr -le 2024) { "net48" } else { "net10.0-windows" }
        Write-Host "=== Building & staging Revit $yr ($tfm) ===" -ForegroundColor Cyan
        $yearOut = Join-Path $AddinsStage "$yr"
        New-Item -ItemType Directory -Force -Path $yearOut | Out-Null

        foreach ($proj in $RevitProjects) {
            dotnet build $proj -p:RevitYear=$yr -c $Configuration --nologo -v quiet
            if ($LASTEXITCODE -ne 0) { throw "$proj build failed for Revit $yr." }
            $projDir = Split-Path -Parent $proj
            Copy-Item -Path (Join-Path $RepoRoot "$projDir\bin\$Configuration\$tfm\*") -Destination $yearOut -Recurse -Force
        }
        $BuiltYears += $yr
        Write-Host "  Staged Revit $yr -> $yearOut" -ForegroundColor Green
    }

    if ($BuiltYears.Count -eq 0) {
        throw "No Revit installations (2023-2027) found to package."
    }

    foreach ($doc in @("README.md", "README.es.md", "CHANGELOG.md", "LICENSE", "PROVENANCE.md", "THIRD-PARTY-NOTICES.md", "install.ps1")) {
        $src = Join-Path $RepoRoot $doc
        if (Test-Path $src) { Copy-Item -Path $src -Destination $StageDir -Force }
    }

    $ZipPath = Join-Path $DistDir "sutra-revit-mcp-$Version-win-x64.zip"
    if (Test-Path $ZipPath) { Remove-Item -Path $ZipPath -Force }
    Write-Host "=== Creating portable archive: $(Split-Path -Leaf $ZipPath) ===" -ForegroundColor Cyan
    Compress-Archive -Path (Join-Path $StageDir "*") -DestinationPath $ZipPath -CompressionLevel Optimal

    $Iscc = @(
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
    ) | Where-Object { Test-Path $_ } | Select-Object -First 1

    if (-not $Iscc) {
        throw "Inno Setup 6 (ISCC.exe) not found."
    }

    $SetupPath = Join-Path $DistDir "sutra-revit-mcp-$Version-setup.exe"
    if (Test-Path $SetupPath) { Remove-Item -Path $SetupPath -Force }

    Write-Host "=== Compiling Windows Setup installer: $(Split-Path -Leaf $SetupPath) ===" -ForegroundColor Cyan
    & $Iscc (Join-Path $RepoRoot "build\sutra-revit-mcp.iss") /Q "/DAppVersion=$Version" "/DStageDir=$StageDir"
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path $SetupPath)) {
        throw "ISCC compilation failed."
    }

    $SumPath = Join-Path $DistDir "SHA256SUMS.txt"
    $Hashes = @()
    foreach ($artifact in @($SetupPath, $ZipPath)) {
        $h = (Get-FileHash -Path $artifact -Algorithm SHA256).Hash.ToLowerInvariant()
        $Hashes += "$h  $(Split-Path -Leaf $artifact)"
    }
    Set-Content -Path $SumPath -Value $Hashes -Encoding ascii

    Write-Host ""
    Write-Host "Release v$Version packaged successfully!" -ForegroundColor Green
    Write-Host "  Setup : $SetupPath ($([Math]::Round((Get-Item $SetupPath).Length / 1MB, 2)) MB)" -ForegroundColor Cyan
    Write-Host "  Zip   : $ZipPath ($([Math]::Round((Get-Item $ZipPath).Length / 1MB, 2)) MB)" -ForegroundColor Cyan
    Write-Host "  Sums  : $SumPath" -ForegroundColor Cyan
    Write-Host "  Years : $($BuiltYears -join ', ')" -ForegroundColor Cyan
}
finally {
    Pop-Location
}
