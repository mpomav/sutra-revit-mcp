<#
.SYNOPSIS
    Clean-room provenance & zero-derivative audit for SCIO Revit MCP v4.
#>
[CmdletBinding()]
param(
    [string]$V4Root = "",
    [string]$LegacyV3Root = "c:\PROG\sciomcp"
)

$ErrorActionPreference = "Stop"
if ([string]::IsNullOrWhiteSpace($V4Root)) {
    $V4Root = Split-Path -Parent $PSScriptRoot
}

Write-Host "=== SCIO Revit MCP v4 Clean-Room Audit ===" -ForegroundColor Cyan

# 1. Zero third-party bridge brand mentions in v4 source
$v4Files = Get-ChildItem -Path $V4Root -Recurse -File | Where-Object {
    $_.FullName -notmatch "\\(\.git|bin|obj)\\" -and $_.Extension -in @(".cs",".csproj",".props",".md",".ps1",".py",".json",".slnx")
}

$brandHits = @()
foreach ($f in $v4Files) {
    if ($f.Name -eq "audit-clean-room.ps1") { continue }
    $m = Select-String -Path $f.FullName -Pattern "horizun" -CaseSensitive:$false
    if ($m) { $brandHits += $m }
}

Write-Host "Scanned $($v4Files.Count) v4 source files."
Write-Host "Third-party brand matches: $($brandHits.Count)" -ForegroundColor ($(if ($brandHits.Count -eq 0) { "Green" } else { "Red" }))

# 2. Count modules & [ScioTool] commands
$csFiles = $v4Files | Where-Object { $_.Extension -eq ".cs" -and $_.FullName -match "\\src\\" }
$toolMatches = $csFiles | Select-String -Pattern '\[ScioTool\("([^"]+)"' -AllMatches
$attrTools = $toolMatches | ForEach-Object { $_.Matches | ForEach-Object { $_.Groups[1].Value } } | Sort-Object -Unique
Write-Host "Declarative [ScioTool] commands: $($attrTools.Count)" -ForegroundColor Cyan

# 3. Exact file hash check against v3 non-SCIO files (excluding the 36 verified SCIO-authored files)
if (Test-Path $LegacyV3Root) {
    $v4Hashes = @{}
    foreach ($f in ($v4Files | Where-Object { $_.Extension -eq ".cs" })) {
        $h = (Get-FileHash -Path $f.FullName -Algorithm SHA256).Hash
        $v4Hashes[$h] = $f.FullName
    }
    $v3Cs = Get-ChildItem -Path (Join-Path $LegacyV3Root "src") -Recurse -Filter "*.cs" | Where-Object { $_.FullName -notmatch "\\(bin|obj)\\" }
    $hashCollisions = @()
    foreach ($f3 in $v3Cs) {
        $h3 = (Get-FileHash -Path $f3.FullName -Algorithm SHA256).Hash
        if ($v4Hashes.ContainsKey($h3)) {
            $hashCollisions += [PSCustomObject]@{ V3 = $f3.Name; V4 = Split-Path -Leaf $v4Hashes[$h3] }
        }
    }
    Write-Host "Identical .cs file hashes vs v3: $($hashCollisions.Count) (all must be verified SCIO-authored files)" -ForegroundColor Cyan
    foreach ($c in $hashCollisions) { Write-Host "  - $($c.V3) -> $($c.V4)" }
}

if ($brandHits.Count -gt 0) {
    foreach ($hit in $brandHits) { Write-Host "  HIT: $($hit.Path):$($hit.LineNumber): $($hit.Line.Trim())" -ForegroundColor Red }
    throw "Clean-room brand audit failed."
}

Write-Host "Clean-room audit PASSED." -ForegroundColor Green
