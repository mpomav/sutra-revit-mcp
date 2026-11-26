<#
.SYNOPSIS
    Clean-room provenance & inventory audit for Sutra — Revit MCP.
#>
[CmdletBinding()]
param(
    [string]$Root = "",
    [string[]]$ForbiddenPatterns = @(),
    [string]$ReferenceRoot = ""
)

$ErrorActionPreference = "Stop"
if ([string]::IsNullOrWhiteSpace($Root)) {
    $Root = Split-Path -Parent $PSScriptRoot
}

Write-Host "=== Sutra — Revit MCP Clean-Room Audit ===" -ForegroundColor Cyan

# 1. Scan source files
$sourceFiles = Get-ChildItem -Path $Root -Recurse -File | Where-Object {
    $_.FullName -notmatch "\\(\.git|bin|obj)\\" -and $_.Extension -in @(".cs",".csproj",".props",".md",".ps1",".py",".json",".slnx")
}

$patternHits = @()
if ($ForbiddenPatterns.Count -gt 0) {
    foreach ($f in $sourceFiles) {
        if ($f.Name -eq "audit-clean-room.ps1") { continue }
        foreach ($pat in $ForbiddenPatterns) {
            if ([string]::IsNullOrWhiteSpace($pat)) { continue }
            $m = Select-String -Path $f.FullName -Pattern $pat -CaseSensitive:$false
            if ($m) { $patternHits += $m }
        }
    }
}

Write-Host "Scanned $($sourceFiles.Count) source files."
Write-Host "Forbidden pattern matches: $($patternHits.Count)" -ForegroundColor ($(if ($patternHits.Count -eq 0) { "Green" } else { "Red" }))

# 2. Count modules & [ScioTool] commands
$csFiles = $sourceFiles | Where-Object { $_.Extension -eq ".cs" -and $_.FullName -match "\\src\\" }
$toolMatches = $csFiles | Select-String -Pattern '\[ScioTool\("([^"]+)"' -AllMatches
$attrTools = $toolMatches | ForEach-Object { $_.Matches | ForEach-Object { $_.Groups[1].Value } } | Sort-Object -Unique
Write-Host "Declarative [ScioTool] commands: $($attrTools.Count)" -ForegroundColor Cyan

# 3. Optional SHA-256 comparison against a local reference directory
if (-not [string]::IsNullOrWhiteSpace($ReferenceRoot) -and (Test-Path $ReferenceRoot)) {
    $currentHashes = @{}
    foreach ($f in ($sourceFiles | Where-Object { $_.Extension -eq ".cs" })) {
        $h = (Get-FileHash -Path $f.FullName -Algorithm SHA256).Hash
        $currentHashes[$h] = $f.FullName
    }
    $refSrc = Join-Path $ReferenceRoot "src"
    $refPath = if (Test-Path $refSrc) { $refSrc } else { $ReferenceRoot }
    $refCs = Get-ChildItem -Path $refPath -Recurse -Filter "*.cs" | Where-Object { $_.FullName -notmatch "\\(bin|obj)\\" }
    $hashCollisions = @()
    foreach ($rf in $refCs) {
        $hr = (Get-FileHash -Path $rf.FullName -Algorithm SHA256).Hash
        if ($currentHashes.ContainsKey($hr)) {
            $hashCollisions += [PSCustomObject]@{ Reference = $rf.Name; Current = Split-Path -Leaf $currentHashes[$hr] }
        }
    }
    Write-Host "Identical .cs file hashes vs reference: $($hashCollisions.Count)" -ForegroundColor Cyan
    foreach ($c in $hashCollisions) { Write-Host "  - $($c.Reference) -> $($c.Current)" }
}

if ($patternHits.Count -gt 0) {
    foreach ($hit in $patternHits) { Write-Host "  HIT: $($hit.Path):$($hit.LineNumber): $($hit.Line.Trim())" -ForegroundColor Red }
    throw "Clean-room audit failed."
}

Write-Host "Clean-room audit PASSED." -ForegroundColor Green
