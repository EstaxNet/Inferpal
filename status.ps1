#Requires -Version 5.1
<#
.SYNOPSIS
    One-glance status of the Inferpal setup: repo version, what is deployed in
    VS 2026, local build artifacts, backend reachability.
.EXAMPLE
    .\status.ps1
#>
$ErrorActionPreference = 'SilentlyContinue'
$Root = $PSScriptRoot

function Line([string]$Label, [string]$Value, [string]$Color = 'White') {
    Write-Host ("  {0,-22} " -f $Label) -ForegroundColor Gray -NoNewline
    Write-Host $Value -ForegroundColor $Color
}

function Age([DateTime]$t) {
    if ($t -eq [DateTime]::MinValue) { return 'n/a' }
    $span = (Get-Date) - $t
    if ($span.TotalMinutes -lt 90)  { return "$([int]$span.TotalMinutes) min ago" }
    if ($span.TotalHours   -lt 48)  { return "$([int]$span.TotalHours) h ago" }
    return "$([int]$span.TotalDays) d ago"
}

Write-Host ""
Write-Host "  Inferpal - status" -ForegroundColor Cyan

# ── Repo ─────────────────────────────────────────────────────────────────────
Write-Host "`n  [Repo]" -ForegroundColor DarkCyan
$props   = Get-Content "$Root\Directory.Build.props" -Raw
$version = if ($props -match '<Version>([^<]+)</Version>') { $Matches[1] } else { '?' }
$branch  = (git -C $Root rev-parse --abbrev-ref HEAD 2>$null)
$dirty   = @(git -C $Root status --porcelain 2>$null).Count
$lastLog = (git -C $Root log -1 --format='%h %s' 2>$null)
Line 'Version'  $version 'Cyan'
Line 'Branch'   ("$branch" + $(if ($dirty -gt 0) { "  ($dirty modified files)" } else { '  (clean)' })) $(if ($dirty -gt 0) { 'Yellow' } else { 'Green' })
Line 'Last commit' $lastLog

# ── Visual Studio 2026 ───────────────────────────────────────────────────────
Write-Host "`n  [Visual Studio 2026]" -ForegroundColor DarkCyan
$vsDll = $null
$vsWhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
if (Test-Path $vsWhere) {
    $vsPath = & $vsWhere -all -prerelease -products * -version '[18.0,19.0)' -property installationPath 2>$null |
              Select-Object -First 1
    if ($vsPath) {
        $vsDll = Get-ChildItem "$vsPath\Common7\IDE" -Recurse -Filter 'Inferpal.dll' -ErrorAction SilentlyContinue |
                 Select-Object -First 1
    }
}
if (-not $vsDll) {
    $vsDll = Get-ChildItem "$env:LOCALAPPDATA\Microsoft\VisualStudio\18.0_*Exp\Extensions" -Recurse `
                 -Filter 'Inferpal.dll' -ErrorAction SilentlyContinue | Select-Object -First 1
}
if ($vsDll) {
    $fv = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($vsDll.FullName).FileVersion
    Line 'Installed' "v$fv  (deployed $(Age $vsDll.LastWriteTime))" $(if ($fv -like "$version*") { 'Green' } else { 'Yellow' })
    Line 'Location' $vsDll.DirectoryName
    # Stale when any tracked source is newer than the deployed DLL.
    $srcNewest = [DateTime]::MinValue
    foreach ($r in @("$Root\Inferpal", "$Root\Inferpal.Core")) {
        Get-ChildItem $r -Recurse -File -ErrorAction SilentlyContinue |
            Where-Object { @('.cs', '.csproj', '.resx', '.json', '.props', '.xaml') -contains $_.Extension } |
            ForEach-Object { if ($_.LastWriteTimeUtc -gt $srcNewest) { $srcNewest = $_.LastWriteTimeUtc } }
    }
    if ($srcNewest -gt $vsDll.LastWriteTimeUtc) {
        Line 'Freshness' 'STALE - sources are newer  ->  .\deploy-dev.ps1' 'Yellow'
    } else {
        Line 'Freshness' 'up to date' 'Green'
    }
} else {
    Line 'Installed' 'not found - .\deploy-dev.ps1 bootstraps it (silent VSIXInstaller)' 'Yellow'
}

# ── Local build artifacts ────────────────────────────────────────────────────
Write-Host "`n  [Local builds]" -ForegroundColor DarkCyan
$dist = Get-ChildItem "$Root\dist" -Directory -ErrorAction SilentlyContinue |
        Sort-Object Name -Descending | Select-Object -First 1
if ($dist) {
    $files = (Get-ChildItem $dist.FullName -Filter '*.vsix').Name -join ', '
    Line 'Last deliverables' "dist\$($dist.Name)\  ($files)"
} else {
    Line 'Last deliverables' 'none (.\release.ps1)'
}

# ── Backend ──────────────────────────────────────────────────────────────────
Write-Host "`n  [Backend]" -ForegroundColor DarkCyan
$cfgPath = Join-Path $env:APPDATA 'Inferpal\config.json'
if (Test-Path $cfgPath) {
    $cfg = Get-Content $cfgPath -Raw | ConvertFrom-Json
    $baseUrl  = $cfg.baseUrl
    $provider = "$($cfg.provider)"
    if (-not $baseUrl) { $baseUrl = 'http://localhost:11434'; if (-not $provider) { $provider = 'ollama' } }
    Line 'Provider' "$provider  ($baseUrl)"
    Line 'Default model' "$($cfg.defaultModel)"
    try {
        $client = New-Object System.Net.Http.HttpClient
        $client.Timeout = [TimeSpan]::FromSeconds(2)
        # Any HTTP answer (even 404) proves the server is listening.
        $null = $client.GetAsync($baseUrl).GetAwaiter().GetResult()
        Line 'Reachable' 'yes' 'Green'
    } catch {
        Line 'Reachable' 'NO - start your backend (ollama serve / LM Studio)' 'Red'
    } finally {
        if ($client) { $client.Dispose() }
    }
} else {
    Line 'Config' "not found ($cfgPath) - first run not done yet" 'Yellow'
}

Write-Host ""
