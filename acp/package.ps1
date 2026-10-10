# Builds the archive an Agent Client Protocol client runs Inferpal from: the self-contained Inferpal.Host for one
# platform, started with `--acp` (Zed, the JetBrains IDEs, Neovim, Emacs).
# Usage: pwsh ./acp/package.ps1 -Target win32-x64|linux-x64|darwin-arm64 [-OutDir <folder>]
# Output: inferpal-acp-<target>-<version>.zip (Windows) or .tar.gz (Linux, macOS), the executable at the archive's root.
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
    dotnet publish (Join-Path $root 'Inferpal.Host/Inferpal.Host.csproj') -c Release -r $rid --self-contained true -o $stage -v minimal -nologo
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $rid" }
    Remove-Item (Join-Path $stage '*.pdb') -ErrorAction SilentlyContinue

    $name = "inferpal-acp-$Target-$version"
    if ($Target -eq 'win32-x64') {
        $archive = Join-Path $OutDir "$name.zip"
        Remove-Item $archive -ErrorAction SilentlyContinue
        Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $archive
    }
    else {
        $archive = Join-Path $OutDir "$name.tar.gz"
        Remove-Item $archive -ErrorAction SilentlyContinue
        # Executables: the host itself and the runtime's crash-dump helper. Everything else is read, never run.
        $executables = @('Inferpal.Host', 'createdump')
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
