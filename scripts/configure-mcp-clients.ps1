#Requires -Version 5.1
<#
.SYNOPSIS
  Safely configures or upgrades the 'scio-revit' MCP server entry for installed AI clients.
.DESCRIPTION
  Adds or updates only the 'scio-revit' MCP entry pointing to Sutra's scio-revit-mcp.exe.
  Never removes or alters other MCP servers configured by the user. Creates a .bak backup
  before editing any existing file and writes UTF-8 without BOM atomically.
#>
[CmdletBinding()]
param(
    [string]$ServerExe = "$env:LOCALAPPDATA\Programs\Scio\MCP\server\scio-revit-mcp.exe",
    [string]$Clients = "",
    [switch]$AutoDetect,
    [string]$SchemaMode = "codemode",
    [string]$SummaryFile = ""
)

$ErrorActionPreference = "Stop"
$Utf8NoBom = New-Object System.Text.UTF8Encoding($false)

function Write-AtomicUtf8 {
    param([string]$Path, [string]$Content)
    $dir = Split-Path -Parent $Path
    if (-not (Test-Path -LiteralPath $dir)) {
        New-Item -ItemType Directory -Force -Path $dir | Out-Null
    }
    if (Test-Path -LiteralPath $Path) {
        Copy-Item -LiteralPath $Path -Destination "$Path.bak" -Force
    }
    $tmp = "$Path.tmp"
    [System.IO.File]::WriteAllText($tmp, $Content, $Utf8NoBom)
    Move-Item -LiteralPath $tmp -Destination $Path -Force
}

function ConvertTo-OrderedHashtable {
    param($InputObject)
    if ($null -eq $InputObject) { return [ordered]@{} }
    if ($InputObject -is [System.Collections.IDictionary]) {
        $ht = [ordered]@{}
        foreach ($k in $InputObject.Keys) {
            $ht[$k] = ConvertTo-OrderedHashtable $InputObject[$k]
        }
        return $ht
    }
    if ($InputObject -is [PSCustomObject]) {
        $ht = [ordered]@{}
        foreach ($p in $InputObject.PSObject.Properties) {
            $ht[$p.Name] = ConvertTo-OrderedHashtable $p.Value
        }
        return $ht
    }
    if ($InputObject -is [System.Collections.IEnumerable] -and -not ($InputObject -is [string])) {
        $arr = @()
        foreach ($item in $InputObject) {
            $arr += ,(ConvertTo-OrderedHashtable $item)
        }
        return $arr
    }
    return $InputObject
}

function Update-JsonMcpConfig {
    param(
        [string]$ConfigPath,
        [string]$RootKey = "mcpServers",
        [bool]$IncludeTypeStdio = $false
    )
    $root = [ordered]@{}
    if (Test-Path -LiteralPath $ConfigPath) {
        $raw = [System.IO.File]::ReadAllText($ConfigPath, [System.Text.Encoding]::UTF8)
        if (-not [string]::IsNullOrWhiteSpace($raw)) {
            $parsed = $raw | ConvertFrom-Json
            $root = ConvertTo-OrderedHashtable $parsed
        }
    }

    if (-not $root.Contains($RootKey) -or -not ($root[$RootKey] -is [System.Collections.IDictionary])) {
        $root[$RootKey] = [ordered]@{}
    }
    $servers = $root[$RootKey]

    # Preserve any custom env vars the user already had on scio-revit
    $existingEnv = [ordered]@{}
    if ($servers.Contains("scio-revit") -and ($servers["scio-revit"] -is [System.Collections.IDictionary])) {
        $oldEntry = $servers["scio-revit"]
        if ($oldEntry.Contains("env") -and ($oldEntry["env"] -is [System.Collections.IDictionary])) {
            foreach ($k in $oldEntry["env"].Keys) {
                $existingEnv[$k] = $oldEntry["env"][$k]
            }
        }
    }
    if (-not $existingEnv.Contains("SCIO_SCHEMA_MODE")) {
        $existingEnv["SCIO_SCHEMA_MODE"] = $SchemaMode
    }

    $entry = [ordered]@{}
    if ($IncludeTypeStdio) {
        $entry["type"] = "stdio"
    }
    $entry["command"] = $ServerExe
    $entry["args"] = @()
    $entry["env"] = $existingEnv

    $servers["scio-revit"] = $entry
    $json = $root | ConvertTo-Json -Depth 16
    Write-AtomicUtf8 -Path $ConfigPath -Content ($json + "`n")
}

function Update-CodexToml {
    param([string]$ConfigPath)
    $escapedExe = $ServerExe.Replace('\', '\\')
    $block = @"
[mcp_servers.scio-revit]
command = "$escapedExe"
args = []

[mcp_servers.scio-revit.env]
SCIO_SCHEMA_MODE = "$SchemaMode"
"@

    if (-not (Test-Path -LiteralPath $ConfigPath)) {
        Write-AtomicUtf8 -Path $ConfigPath -Content ($block + "`n")
        return
    }

    $lines = [System.IO.File]::ReadAllLines($ConfigPath, [System.Text.Encoding]::UTF8)
    $out = New-Object System.Collections.Generic.List[string]
    $skip = $false
    foreach ($line in $lines) {
        if ($line -match '^\s*\[') {
            if ($line -match '^\s*\[mcp_servers\.(?:"scio-revit"|scio-revit)(?:\.env)?\]\s*$') {
                $skip = $true
                continue
            } else {
                $skip = $false
            }
        }
        if (-not $skip) {
            [void]$out.Add($line)
        }
    }

    while ($out.Count -gt 0 -and [string]::IsNullOrWhiteSpace($out[$out.Count - 1])) {
        $out.RemoveAt($out.Count - 1)
    }
    if ($out.Count -gt 0) {
        [void]$out.Add("")
    }
    [void]$out.Add($block)
    Write-AtomicUtf8 -Path $ConfigPath -Content (($out -join "`n") + "`n")
}

function Get-ClaudeDesktopPaths {
    $paths = @()
    $msixPkg = Get-ChildItem -Path "$env:LOCALAPPDATA\Packages" -Directory -Filter "Claude_*" -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($msixPkg) {
        $msixCfg = Join-Path $msixPkg.FullName "LocalCache\Roaming\Claude\claude_desktop_config.json"
        if (Test-Path -LiteralPath (Split-Path -Parent $msixCfg)) {
            $paths += $msixCfg
        }
    }
    $classicCfg = Join-Path $env:APPDATA "Claude\claude_desktop_config.json"
    if ((Test-Path -LiteralPath (Split-Path -Parent $classicCfg)) -or ($paths.Count -eq 0)) {
        $paths += $classicCfg
    }
    return $paths | Select-Object -Unique
}

$requested = @()
if (-not [string]::IsNullOrWhiteSpace($Clients)) {
    $requested = $Clients.Split(',') | ForEach-Object { $_.Trim().ToLowerInvariant() } | Where-Object { $_ }
} elseif ($AutoDetect) {
    if ((Test-Path "$env:APPDATA\Claude") -or (Get-ChildItem "$env:LOCALAPPDATA\Packages" -Directory -Filter "Claude_*" -ErrorAction SilentlyContinue)) { $requested += "claude-desktop" }
    if ((Test-Path "$env:USERPROFILE\.claude.json") -or (Test-Path "$env:USERPROFILE\.claude")) { $requested += "claude-code" }
    if (Test-Path "$env:USERPROFILE\.gemini\antigravity") { $requested += "antigravity" }
    if (Test-Path "$env:USERPROFILE\.gemini") { $requested += "gemini-cli" }
    if (Test-Path "$env:USERPROFILE\.cursor") { $requested += "cursor" }
    if (Test-Path "$env:USERPROFILE\.codeium\windsurf") { $requested += "windsurf" }
    if (Test-Path "$env:APPDATA\Code\User") { $requested += "vscode" }
    if (Test-Path "$env:USERPROFILE\.codex") { $requested += "codex" }
}

$configured = New-Object System.Collections.Generic.List[string]
$failed = New-Object System.Collections.Generic.List[string]

foreach ($c in ($requested | Select-Object -Unique)) {
    try {
        switch ($c) {
            "claude-desktop" {
                foreach ($p in (Get-ClaudeDesktopPaths)) {
                    Update-JsonMcpConfig -ConfigPath $p -RootKey "mcpServers"
                }
                [void]$configured.Add("Claude Desktop")
            }
            "claude-code" {
                Update-JsonMcpConfig -ConfigPath "$env:USERPROFILE\.claude.json" -RootKey "mcpServers"
                [void]$configured.Add("Claude Code")
            }
            "antigravity" {
                Update-JsonMcpConfig -ConfigPath "$env:USERPROFILE\.gemini\antigravity\mcp_config.json" -RootKey "mcpServers"
                [void]$configured.Add("Antigravity")
            }
            "gemini-cli" {
                Update-JsonMcpConfig -ConfigPath "$env:USERPROFILE\.gemini\settings.json" -RootKey "mcpServers"
                [void]$configured.Add("Gemini CLI")
            }
            "cursor" {
                Update-JsonMcpConfig -ConfigPath "$env:USERPROFILE\.cursor\mcp.json" -RootKey "mcpServers"
                [void]$configured.Add("Cursor")
            }
            "windsurf" {
                Update-JsonMcpConfig -ConfigPath "$env:USERPROFILE\.codeium\windsurf\mcp_config.json" -RootKey "mcpServers"
                [void]$configured.Add("Windsurf")
            }
            "vscode" {
                Update-JsonMcpConfig -ConfigPath "$env:APPDATA\Code\User\mcp.json" -RootKey "servers" -IncludeTypeStdio $true
                [void]$configured.Add("VS Code")
            }
            "codex" {
                Update-CodexToml -ConfigPath "$env:USERPROFILE\.codex\config.toml"
                [void]$configured.Add("Codex CLI")
            }
        }
    }
    catch {
        [void]$failed.Add("$c ($($_.Exception.Message))")
    }
}

$summary = if ($configured.Count -gt 0) { $configured -join ", " } else { "" }
if (-not [string]::IsNullOrWhiteSpace($SummaryFile)) {
    [System.IO.File]::WriteAllText($SummaryFile, $summary, $Utf8NoBom)
}

if ($configured.Count -gt 0) {
    Write-Host "Configured MCP clients: $summary" -ForegroundColor Green
} else {
    Write-Host "No MCP clients configured." -ForegroundColor Yellow
}
if ($failed.Count -gt 0) {
    Write-Warning "Failed clients: $($failed -join '; ')"
}
