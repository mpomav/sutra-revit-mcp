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

function Set-JsonProperty {
    param(
        [Parameter(Mandatory = $true)]$Object,
        [Parameter(Mandatory = $true)][string]$Name,
        $Value
    )
    if ($Object -is [System.Collections.IDictionary]) {
        $Object[$Name] = $Value
        return
    }
    $prop = $Object.PSObject.Properties[$Name]
    if ($null -ne $prop) {
        $prop.Value = $Value
    } else {
        $Object | Add-Member -MemberType NoteProperty -Name $Name -Value $Value -Force
    }
}

function Get-JsonProperty {
    param($Object, [string]$Name)
    if ($null -eq $Object) { return $null }
    if ($Object -is [System.Collections.IDictionary]) {
        if ($Object.Contains($Name)) { return $Object[$Name] }
        return $null
    }
    $prop = $Object.PSObject.Properties[$Name]
    if ($null -ne $prop) { return $prop.Value }
    return $null
}

function Update-JsonMcpConfig {
    param(
        [string]$ConfigPath,
        [string]$RootKey = "mcpServers",
        [bool]$IncludeTypeStdio = $false
    )
    $root = New-Object PSObject
    if (Test-Path -LiteralPath $ConfigPath) {
        $raw = [System.IO.File]::ReadAllText($ConfigPath, [System.Text.Encoding]::UTF8)
        if (-not [string]::IsNullOrWhiteSpace($raw)) {
            # Keep the parsed PSCustomObject graph untouched so 0- and 1-element arrays
            # in unrelated sections (e.g. Claude Desktop's preferences.*) are never unrolled.
            $root = $raw | ConvertFrom-Json
        }
    }

    $servers = Get-JsonProperty -Object $root -Name $RootKey
    if ($null -eq $servers -or ($servers -is [string]) -or ($servers -is [System.Array])) {
        $servers = New-Object PSObject
        Set-JsonProperty -Object $root -Name $RootKey -Value $servers
    }

    # Preserve any custom env vars the user already had on scio-revit
    $existingEnv = [ordered]@{}
    $oldEntry = Get-JsonProperty -Object $servers -Name "scio-revit"
    $oldEnv = Get-JsonProperty -Object $oldEntry -Name "env"
    if ($null -ne $oldEnv) {
        if ($oldEnv -is [System.Collections.IDictionary]) {
            foreach ($k in $oldEnv.Keys) { $existingEnv[$k] = $oldEnv[$k] }
        } else {
            foreach ($p in $oldEnv.PSObject.Properties) { $existingEnv[$p.Name] = $p.Value }
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

    Set-JsonProperty -Object $servers -Name "scio-revit" -Value $entry
    $json = $root | ConvertTo-Json -Depth 32
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
    $msixPkgs = Get-ChildItem -Path "$env:LOCALAPPDATA\Packages" -Directory -Filter "Claude_*" -ErrorAction SilentlyContinue
    foreach ($pkg in $msixPkgs) {
        # Always target every Claude MSIX package even if LocalCache\Roaming\Claude does not exist yet;
        # Write-AtomicUtf8 creates parent directories automatically so fresh Microsoft Store installs work.
        $paths += (Join-Path $pkg.FullName "LocalCache\Roaming\Claude\claude_desktop_config.json")
    }
    # Always keep classic %APPDATA%\Claude in sync too (used by standalone .exe and MSIX "Edit Config" button).
    $paths += (Join-Path $env:APPDATA "Claude\claude_desktop_config.json")
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
