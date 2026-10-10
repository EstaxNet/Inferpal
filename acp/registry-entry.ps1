# Writes the ACP registry entry (agent.json) for a PUBLISHED release: the archives' URLs and their SHA-256, read from
# the release's own SHA256SUMS.txt — never from a local build, whose bytes are not the ones people download.
# Usage: pwsh ./acp/registry-entry.ps1 -Version 1.8.0 [-Sums <SHA256SUMS.txt>] [-Out <agent.json>]
# Without -Sums, the release's SHA256SUMS.txt is downloaded from GitHub.
#
# The entry goes to agentclientprotocol/registry as <id>/agent.json beside acp/icon.svg — by a pull request someone
# opens deliberately: this script writes the file, it publishes nothing.
param(
    [Parameter(Mandatory)][string]$Version,
    [string]$Sums,
    [string]$Out = (Join-Path $PSScriptRoot 'out/agent.json')
)
$ErrorActionPreference = 'Stop'
$base = "https://github.com/EstaxNet/Inferpal/releases/download/v$Version"

if (-not $Sums) {
    $Sums = Join-Path ([IO.Path]::GetTempPath()) "inferpal-$Version-SHA256SUMS.txt"
    Invoke-WebRequest "$base/SHA256SUMS.txt" -OutFile $Sums -UseBasicParsing
}
$hash = @{}
foreach ($line in Get-Content $Sums) {
    if ($line -match '^([0-9a-f]{64})\s+(\S+)$') { $hash[$Matches[2]] = $Matches[1] }
}

# Registry platform → (archive, command to run once extracted).
$platforms = [ordered]@{
    'windows-x86_64' = @("inferpal-acp-win32-x64-$Version.zip",       './Inferpal.Host.exe')
    'linux-x86_64'   = @("inferpal-acp-linux-x64-$Version.tar.gz",    './Inferpal.Host')
    'darwin-aarch64' = @("inferpal-acp-darwin-arm64-$Version.tar.gz", './Inferpal.Host')
}
$binary = [ordered]@{}
foreach ($p in $platforms.Keys) {
    $archive, $cmd = $platforms[$p]
    if (-not $hash.ContainsKey($archive)) { throw "$archive is not in ${Sums}: the release does not carry it." }
    $binary[$p] = [ordered]@{ archive = "$base/$archive"; sha256 = $hash[$archive]; cmd = $cmd; args = @('--acp'); env = @{} }
}

$entry = [ordered]@{
    id           = 'inferpal'
    name         = 'Inferpal'
    version      = $Version
    description  = 'A coding agent that runs on your own model server (Ollama, LM Studio or any OpenAI-compatible endpoint): no account, no telemetry.'
    repository   = 'https://github.com/EstaxNet/Inferpal'
    website      = 'https://github.com/EstaxNet/Inferpal/blob/master/docs/acp.md'
    authors      = @('EstaxNet')
    license      = 'GPL-3.0-only'
    license_url  = 'https://github.com/EstaxNet/Inferpal/blob/master/LICENSE'
    icon         = 'https://raw.githubusercontent.com/EstaxNet/Inferpal/master/acp/icon.svg'
    distribution = [ordered]@{ binary = $binary }
}
New-Item -ItemType Directory -Force -Path (Split-Path $Out -Parent) | Out-Null
$json = $entry | ConvertTo-Json -Depth 6
[IO.File]::WriteAllText($Out, $json + "`n", [Text.UTF8Encoding]::new($false))
Write-Host "  wrote $Out"
