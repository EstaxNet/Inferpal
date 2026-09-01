# Builds a platform-specific VSIX with the self-contained Inferpal.Host embedded.
# Usage: .\package.ps1 [-Target win32-x64|win32-arm64|linux-x64|linux-arm64|darwin-x64|darwin-arm64]
# All platform VSIXs must share the same version (Marketplace requirement).
param([string]$Target = 'win32-x64')
$ErrorActionPreference = 'Stop'

# Shells opened before Node.js was installed don't have npm on PATH yet — re-read
# the machine+user PATH from the registry instead of failing on a stale session.
if (-not (Get-Command npm -ErrorAction SilentlyContinue)) {
    $env:Path = [Environment]::GetEnvironmentVariable('Path', 'Machine') + ';' +
                [Environment]::GetEnvironmentVariable('Path', 'User')
}

# VS Code target platform → .NET runtime identifier
$rids = @{
    'win32-x64'    = 'win-x64'
    'win32-arm64'  = 'win-arm64'
    'linux-x64'    = 'linux-x64'
    'linux-arm64'  = 'linux-arm64'
    'darwin-x64'   = 'osx-x64'
    'darwin-arm64' = 'osx-arm64'
}
if (-not $rids.ContainsKey($Target)) { throw "Unknown target '$Target'. Expected: $($rids.Keys -join ', ')" }
$rid  = $rids[$Target]
$root = Split-Path $PSScriptRoot -Parent

# 1. Self-contained host for the target platform (host/ is what extension.ts probes first)
if (Test-Path "$PSScriptRoot\host") { Remove-Item "$PSScriptRoot\host" -Recurse -Force }
dotnet publish "$root\Inferpal.Host\Inferpal.Host.csproj" -c Release -r $rid --self-contained true -o "$PSScriptRoot\host" -v minimal -nologo
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }
Remove-Item "$PSScriptRoot\host\*.pdb" -ErrorAction SilentlyContinue

# 2. Extension bundle
Push-Location $PSScriptRoot
try {
    npm run build
    if ($LASTEXITCODE -ne 0) { throw 'npm run build failed' }

    # 3. Platform VSIX — the version is STAMPED from the repo-root Directory.Build.props
    # at package time (package.json keeps a 0.0.0-dev placeholder in git): one product
    # version lives in one place, no sync question ever.
    $props = Get-Content "$root\Directory.Build.props" -Raw
    if ($props -notmatch '<Version>([^<]+)</Version>') { throw 'No <Version> in Directory.Build.props' }
    $version = $Matches[1]

    $pkgRaw = Get-Content package.json -Raw
    $stamped = $pkgRaw -replace '"version":\s*"[^"]+"', "`"version`": `"$version`""
    [System.IO.File]::WriteAllText("$PSScriptRoot\package.json", $stamped, (New-Object System.Text.UTF8Encoding($false)))
    try {
        npx vsce package --target $Target -o "inferpal-$Target-$version.vsix"
        if ($LASTEXITCODE -ne 0) { throw 'vsce package failed' }
    }
    finally {
        [System.IO.File]::WriteAllText("$PSScriptRoot\package.json", $pkgRaw, (New-Object System.Text.UTF8Encoding($false)))
    }
    Get-Item "inferpal-$Target-$version.vsix" | Select-Object Name, @{n='MB';e={[math]::Round($_.Length/1MB,1)}}
}
finally { Pop-Location }
