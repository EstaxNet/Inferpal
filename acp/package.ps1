# Builds the archive an Agent Client Protocol client runs Inferpal from: the self-contained Inferpal.Host for one
# platform, started with `--acp` (Zed, the JetBrains IDEs, Neovim, Emacs).
# Usage: pwsh ./acp/package.ps1 -Target win32-x64|linux-x64|darwin-arm64 [-OutDir <folder>]
# Output: inferpal-acp-<target>-<version>.zip (Windows) or .tar.gz (Linux, macOS), at the archive's root: the executable,
# SQLite's native library, README.txt (acp/README.txt) and LICENSE.txt (the GPL, verbatim: conveying the program needs it).
#
# ⚠ One executable (PublishSingleFile), here only: the settings window's files and the names of the framework's
#   assemblies become resources of it (Inferpal.Host.csproj). SQLite's native library stays BESIDE it: inside, it would
#   be extracted at the first start, and an agent whose home folder is not writable (a container, a sandbox) would not
#   start at all. The VS Code VSIX publishes the host as a folder (vscode/package.ps1), unchanged.
#
# ⚠ PowerShell 7: the tarball is written with System.Formats.Tar (.NET 7+), which PowerShell 5.1 does not have.
# ⚠ The modes are SET, never inherited: built on Windows, the files have no execute bit, and a client that extracts
#   the archive then fails to start ./Inferpal.Host ("permission denied") — the VS Code extension re-asserts the bit
#   at activation, an ACP client does not.
param(
    [Parameter(Mandatory)][ValidateSet('win32-x64', 'linux-x64', 'darwin-arm64')][string]$Target,
    [string]$OutDir
)
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -lt 7) { throw 'acp/package.ps1 needs PowerShell 7 (pwsh): the tarball uses System.Formats.Tar.' }

$root = Split-Path $PSScriptRoot -Parent
if (-not $OutDir) { $OutDir = Join-Path $PSScriptRoot 'out' }
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

# One version for every deliverable: the one Directory.Build.props declares.
$version = ([xml](Get-Content (Join-Path $root 'Directory.Build.props') -Raw)).Project.PropertyGroup |
    ForEach-Object { $_.Version } | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw 'No <Version> in Directory.Build.props.' }

$rid = @{ 'win32-x64' = 'win-x64'; 'linux-x64' = 'linux-x64'; 'darwin-arm64' = 'osx-arm64' }[$Target]
$stage = Join-Path ([IO.Path]::GetTempPath()) "inferpal-acp-$Target-$([guid]::NewGuid().ToString('N').Substring(0, 8))"

try {
    # The settings window (`Inferpal.Host --settings`, the ACP client's first terminal method) serves the very form VS Code
    # shows: its bundle is built from vscode\ BEFORE the publish, whose project copies it only when it exists.
    Push-Location (Join-Path $root 'vscode')
    try {
        if (-not (Test-Path 'node_modules')) { npm ci; if ($LASTEXITCODE -ne 0) { throw 'npm ci failed in vscode/' } }
        npm run build
        if ($LASTEXITCODE -ne 0) { throw 'npm run build failed: the settings window would ship without its form.' }
    }
    finally { Pop-Location }
    # The project embeds the form only when its files exist: missing, the executable would build without them, silently.
    # The same three files as InferpalSettingsWebFiles (Inferpal.Host.csproj) and SettingsWindowServer.WebFiles.
    $webFiles = 'settings.css', 'settings-standalone.css', 'settings-standalone.js'
    foreach ($web in $webFiles) {
        if (-not (Test-Path (Join-Path $root "vscode/media/$web"))) { throw "vscode/media/$web is missing: the settings window could not open." }
    }

    dotnet publish (Join-Path $root 'Inferpal.Host/Inferpal.Host.csproj') -c Release -f net10.0 -r $rid --self-contained true `
        -p:PublishSingleFile=true -p:DebugType=none -o $stage -v minimal -nologo
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $rid" }
    $executable = if ($Target -eq 'win32-x64') { 'Inferpal.Host.exe' } else { 'Inferpal.Host' }
    $sqlite = @{ 'win32-x64' = 'e_sqlite3.dll'; 'linux-x64' = 'libe_sqlite3.so'; 'darwin-arm64' = 'libe_sqlite3.dylib' }[$Target]
    $published = (@(Get-ChildItem $stage -Recurse -File | ForEach-Object { [IO.Path]::GetRelativePath($stage, $_.FullName) }) | Sort-Object) -join '|'
    $expected  = (@($executable, $sqlite) | Sort-Object) -join '|'
    if ($published -ne $expected) {
        throw "The publish holds $($published.Replace('|', ', ')) instead of $executable and $sqlite alone."
    }
    # What the executable must carry inside: the form, and the framework's names (without them the C# analysis binds no
    # .NET type). The bundle is not compressed, so the resource names read in its bytes — by blocks that overlap by more
    # than the longest name, never the whole 100 MB as one string.
    $missing = [Collections.Generic.HashSet[string]]::new([string[]](@($webFiles | ForEach-Object { "web/$_" }) + 'framework-assemblies.txt'))
    $stream = [IO.File]::OpenRead((Join-Path $stage $executable))
    try {
        $block = [byte[]]::new(16MB); $tail = ''
        while ($missing.Count -gt 0 -and ($read = $stream.Read($block, 0, $block.Length)) -gt 0) {
            $text = $tail + [Text.Encoding]::Latin1.GetString($block, 0, $read)
            foreach ($resource in @($missing)) { if ($text.Contains($resource)) { [void]$missing.Remove($resource) } }
            $tail = $text.Substring([Math]::Max(0, $text.Length - 64))
        }
    }
    finally { $stream.Dispose() }
    if ($missing.Count -gt 0) { throw "The executable does not carry $($missing -join ', ')." }

    # README.txt from acp/README.txt, and the GPL verbatim. Windows line ends in the Windows archive.
    $docs = "https://github.com/EstaxNet/Inferpal/blob/v$version/docs/acp.md"
    $readme = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'README.txt')).Replace("`r`n", "`n")
    foreach ($fill in @{ '{version}' = $version; '{executable}' = $executable; '{sqlite}' = $sqlite; '{docs}' = $docs }.GetEnumerator()) {
        $readme = $readme.Replace($fill.Key, $fill.Value)
    }
    if ($readme.Contains('{')) { throw 'acp/README.txt holds a placeholder this script does not fill.' }
    $license = [IO.File]::ReadAllText((Join-Path $root 'vscode/LICENSE.txt')).Replace("`r`n", "`n")
    if ($Target -eq 'win32-x64') { $readme = $readme.Replace("`n", "`r`n"); $license = $license.Replace("`n", "`r`n") }
    $utf8 = [Text.UTF8Encoding]::new($false)
    [IO.File]::WriteAllText((Join-Path $stage 'README.txt'), $readme, $utf8)
    [IO.File]::WriteAllText((Join-Path $stage 'LICENSE.txt'), $license, $utf8)

    $name = "inferpal-acp-$Target-$version"
    if ($Target -eq 'win32-x64') {
        $archive = Join-Path $OutDir "$name.zip"
        Remove-Item $archive -ErrorAction SilentlyContinue
        Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $archive
    }
    else {
        $archive = Join-Path $OutDir "$name.tar.gz"
        Remove-Item $archive -ErrorAction SilentlyContinue
        # The host is the one file run; the native library is loaded, the texts are read.
        $executables = @('Inferpal.Host')
        $exec = [IO.UnixFileMode]'UserRead, UserWrite, UserExecute, GroupRead, GroupExecute, OtherRead, OtherExecute'
        $data = [IO.UnixFileMode]'UserRead, UserWrite, GroupRead, OtherRead'
        $file = [IO.File]::Create($archive)
        try {
            $gzip = [IO.Compression.GZipStream]::new($file, [IO.Compression.CompressionLevel]::Optimal)
            $tar = [Formats.Tar.TarWriter]::new($gzip, [Formats.Tar.TarEntryFormat]::Pax, $false)
            try {
                foreach ($f in Get-ChildItem $stage -Recurse -File | Sort-Object FullName) {
                    $relative = [IO.Path]::GetRelativePath($stage, $f.FullName).Replace('\', '/')
                    $entry = [Formats.Tar.PaxTarEntry]::new([Formats.Tar.TarEntryType]::RegularFile, $relative)
                    $entry.Mode = if ($executables -contains $f.Name) { $exec } else { $data }
                    $entry.ModificationTime = [DateTimeOffset]::new($f.LastWriteTimeUtc)
                    $stream = [IO.File]::OpenRead($f.FullName)
                    try { $entry.DataStream = $stream; $tar.WriteEntry($entry) }
                    finally { $stream.Dispose() }
                }
            }
            finally { $tar.Dispose(); $gzip.Dispose() }
        }
        finally { $file.Dispose() }
    }
    $size = [math]::Round((Get-Item $archive).Length / 1MB, 1)
    Write-Host "  $(Split-Path $archive -Leaf)  ($size MB)"
}
finally {
    Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
}
