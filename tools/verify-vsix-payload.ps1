#Requires -Version 5.1
<#
.SYNOPSIS
    Does this Visual Studio VSIX carry what the build says it must? Prints the refusals and exits
    1 when there are any. Dot-source it instead to get `Get-VsixPayloadRefusals` as a function.

.DESCRIPTION
    Five consecutive releases -- 1.6.0 through 1.6.5 -- shipped with assemblies the SDK had pruned
    from the package, and nothing said a word. The pruning removes what it believes Visual Studio
    provides, which is an IN-PROCESS assumption; this package has THREE consumers that do not
    resolve alike:

      * the in-process net472 assembly loaded inside devenv,
      * the net8 payload living in the out-of-process host,
      * and Inferpal.Fim, a CHILD PROCESS with neither the host's assembly closure nor devenv's
        binding redirects, resolving strictly through its own deps.json.

    "Visual Studio provides it" was true for one of the three and false for the others, three
    times over: the SQLite engine (RAG index dead), Roslyn (the semantic index held no C# file at
    all, for six versions), and the sidecar's System.Text.Json (ghost text dead on every package
    install).

    The rule here is a PROPERTY, not a list. Every `Restore*ToVsix` target of Inferpal.csproj ends
    by asserting its own postcondition, in one regular shape:

        <_XInVsix Include="@(VSIXSourceItem)" Condition="'%(Filename)%(Extension)' == 'NAME'" />

    This script reads those declarations and requires each named file in the package. A fourth
    restore target therefore extends this check on its own -- which matters, because carrying the
    doctrine in a hand-written list is exactly how Roslyn and System.Text.Json stayed out.

.NOTES
    Deliberately dependency-free, and deliberately PUBLIC. The release workflow builds and
    publishes from the public tree, where the release tooling does not exist; until this script
    the GitHub release -- the artifact people actually download, and the one a provenance check
    treats as ground truth -- was the only thing in the whole chain that no content gate inspected.

    Reading a package cannot prove a program runs. What this checks is presence, architecture and
    count; whether the sidecar actually starts is a separate, behavioural measurement.
#>
param(
    [string]$Vsix,
    [string]$RepoRoot = (Split-Path $PSScriptRoot -Parent),
    [int]$MinimumMB = 4
)

<#
.SYNOPSIS
    The files the BUILD declares must end up in the VSIX. $null when the csproj cannot be read.
.NOTES
    $null is NOT "nothing to require": the caller must turn it into a refusal. An unreadable
    declaration is an unmeasured rule, and an unmeasured rule has never been a green one.
#>
function Get-VsixBuildPostconditions {
    param([Parameter(Mandatory)][string]$RepoRoot)
    $csproj = Join-Path $RepoRoot 'Inferpal\Inferpal.csproj'
    if (-not (Test-Path $csproj)) { return $null }
    try { $text = [IO.File]::ReadAllText($csproj) } catch { return $null }
    $rx = '<_\w*InVsix\s+Include="@\(VSIXSourceItem\)"[^>]*?Condition="''%\(Filename\)%\(Extension\)''\s*==\s*''([^'']+)'''
    $names = [regex]::Matches($text, $rx) | ForEach-Object { $_.Groups[1].Value }
    # The comma is load-bearing: `return @()` UNROLLS to $null on assignment, which is the
    # value this function reserves for "unreadable". Without it, zero postconditions -- the
    # case the witness below exists for -- would be reported as a missing csproj, sending the
    # reader to look for a file that is right there. Measured on this very function the day
    # it was written, having fixed the same defect in Get-GalleryVersions hours earlier.
    return ,@($names | Sort-Object -Unique)
}

<#
.SYNOPSIS
    The SHA-256 of a package entry's bytes, as hex.
#>
function Get-VsixEntryHash {
    param([Parameter(Mandatory)]$Entry)
    $stream = $Entry.Open()
    try {
        $sha = [Security.Cryptography.SHA256]::Create()
        try { return [BitConverter]::ToString($sha.ComputeHash($stream)) } finally { $sha.Dispose() }
    } finally { $stream.Dispose() }
}

<#
.SYNOPSIS
    The machine field of a package entry's PE header (0x8664 = x64, 0xAA64 = ARM64).
#>
function Get-VsixEntryMachine {
    param([Parameter(Mandatory)]$Entry)
    $stream = $Entry.Open()
    $copy = New-Object IO.MemoryStream
    try { $stream.CopyTo($copy) } finally { $stream.Dispose() }
    try {
        $br = New-Object IO.BinaryReader($copy)
        $copy.Position = 0x3C
        $pe = $br.ReadInt32()
        $copy.Position = $pe + 4
        return [int]$br.ReadUInt16()
    } finally { $copy.Dispose() }
}

<#
.SYNOPSIS
    Reasons to refuse this package. An EMPTY array means it is publishable.
#>
function Get-VsixPayloadRefusals {
    param(
        [Parameter(Mandatory)][string]$Vsix,
        [Parameter(Mandatory)][string]$RepoRoot,
        [int]$MinimumMB = 4
    )
    $reasons = @()
    if (-not (Test-Path $Vsix)) { return @("missing package: $Vsix") }

    # A crude floor, and only that. 2 MB used to sit UNDER the corrupted 1.6.0 (3.11 MB measured),
    # so it could not catch the regression it was added for. What actually works is the
    # postcondition rule below; this only catches a shell.
    $mb = [math]::Round((Get-Item $Vsix).Length / 1MB, 2)
    if ($mb -lt $MinimumMB) { $reasons += "$mb MB < $MinimumMB MB: the package is a shell" }

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = $null
    try { $zip = [System.IO.Compression.ZipFile]::OpenRead($Vsix) }
    catch { return @("unreadable archive: $Vsix -- $($_.Exception.Message)") }
    try { $names = @($zip.Entries | ForEach-Object { $_.FullName.Replace('\', '/') }) }
    finally { $zip.Dispose() }
    if ($names.Count -eq 0) { return @("empty archive: $Vsix") }

    # Without these the extension does not load at all. Microsoft.Data.Sqlite.dll is in this list
    # and that is not decorative: the whole "facade implies engine" property below used to be
    # gated on the facade being PRESENT, so deleting the facade together with the engine disarmed
    # the entire block and the package passed. Measured on the published 1.6.6 VSIX.
    foreach ($required in @('extension.vsixmanifest', 'Inferpal.dll', 'Inferpal.Core.dll',
                            'Inferpal.InProc.dll', 'Inferpal.Fim.exe', 'Inferpal.pkgdef',
                            'LICENSE.txt', 'Microsoft.Data.Sqlite.dll')) {
        if ($names -notcontains $required) { $reasons += "does not contain $required" }
    }

    # Whatever ships the facade ships the engine.
    if ($names -contains 'Microsoft.Data.Sqlite.dll') {
        if (-not ($names | Where-Object { $_ -match '^SQLitePCLRaw\.provider\.' })) {
            $reasons += 'Microsoft.Data.Sqlite without a SQLitePCLRaw provider: throws on the first connection (RAG index dead)'
        }
        if (-not ($names | Where-Object { $_ -match '^SQLitePCLRaw\.core\.dll$' })) {
            $reasons += 'Microsoft.Data.Sqlite without SQLitePCLRaw.core'
        }
        if (-not ($names | Where-Object { $_ -match 'runtimes/win-x64/native/e_sqlite3\.dll$' })) {
            $reasons += 'no native win-x64 e_sqlite3.dll: the provider has nothing to call'
        }
        # Its architecture is read with every flattened native's, below.
        if ($names -notcontains 'e_sqlite3.dll') {
            $reasons += 'no flattened e_sqlite3.dll: a DllImport probes the assembly folder, not the runtimes RID layout'
        }
    }

    # The layout, as a PROPERTY. A package that ships its Windows build under runtimes/win/lib/<tfm>/
    # ships a portable one too, and the build copies the PORTABLE one flat at the root: for
    # Microsoft.Data.SqlClient a stub that throws PlatformNotSupportedException on every connection.
    # The out-of-process host loads the root copy by name -- a VSIX payload carries no deps.json to
    # resolve runtimes/ -- so the root copy must BE the Windows one, byte for byte. And a native
    # flattened beside it (a DllImport probes the folder of its assembly) must be the x64 one:
    # presence was never the question, architecture is. 1.6.2 flattened e_sqlite3 without looking,
    # the ARM64 one came out on an amd64 extension and masked the good one. Each entry is judged on
    # its own: one that cannot be read is named, and the others are still read.
    $layout = $null
    try {
        $layout = [System.IO.Compression.ZipFile]::OpenRead($Vsix)
        $byName = @{}
        foreach ($e in $layout.Entries) { $byName[$e.FullName.Replace('\', '/')] = $e }
        foreach ($lib in @($byName.Keys | Where-Object { $_ -match '^runtimes/win/lib/[^/]+/[^/]+\.dll$' })) {
            $flat = $lib.Substring($lib.LastIndexOf('/') + 1)
            if (-not $byName.ContainsKey($flat)) {
                $reasons += "no $flat at the root: the host loads by name and never reaches $lib"
                continue
            }
            try {
                if ((Get-VsixEntryHash $byName[$lib]) -ne (Get-VsixEntryHash $byName[$flat])) {
                    $reasons += "$flat at the root is not the Windows implementation in ${lib}: the host loads the root one"
                }
            } catch { $reasons += "could not compare $flat with ${lib}: $($_.Exception.Message)" }
        }
        foreach ($native in @($byName.Keys | Where-Object { $_ -match '^runtimes/win-x64/native/[^/]+\.dll$' })) {
            $flat = $native.Substring($native.LastIndexOf('/') + 1)
            if (-not $byName.ContainsKey($flat)) { continue }
            try {
                $machine = Get-VsixEntryMachine $byName[$flat]
                if ($machine -ne 0x8664) {
                    $reasons += ('the flattened {0} is not x64 (machine 0x{1:X}): it masks the good native' -f $flat, $machine)
                }
            } catch { $reasons += "could not read the architecture of the flattened ${flat}: $($_.Exception.Message)" }
        }
    } catch {
        $reasons += "could not read the package layout: $($_.Exception.Message)"
    } finally {
        if ($null -ne $layout) { $layout.Dispose() }
    }

    # What the build commits to putting there. Nothing is copied here: a fourth restore target
    # extends this rule by itself.
    $declared = Get-VsixBuildPostconditions -RepoRoot $RepoRoot
    if ($null -eq $declared) {
        $reasons += "build postconditions unreadable (no Inferpal.csproj under $RepoRoot): this rule verified nothing"
    } elseif ($declared.Count -eq 0) {
        # The witness. Zero postconditions found means the shape being read has changed, not that
        # the build requires nothing. A scan that finds nothing is a dead thermometer.
        $reasons += 'no Restore*ToVsix postcondition read from Inferpal.csproj: this rule no longer guards anything'
    } else {
        foreach ($d in $declared) {
            if ($names -notcontains $d) {
                $reasons += "does not contain $d, which the build declares as the postcondition of a Restore*ToVsix target"
            }
        }
    }

    # Satellites: without them the UI is English everywhere, silently.
    $satellites = @($names | Where-Object { $_ -match '^[a-zA-Z-]+/Inferpal\.Core\.resources\.dll$' }).Count
    if ($satellites -lt 9) { $reasons += "$satellites localization satellites (9 expected): the UI would stay in English" }

    return $reasons
}

# Script mode. Dot-sourcing it (no -Vsix) only defines the two functions above.
if ($Vsix) {
    $refusals = @(Get-VsixPayloadRefusals -Vsix $Vsix -RepoRoot $RepoRoot -MinimumMB $MinimumMB)
    if ($refusals.Count -eq 0) {
        Write-Host "OK $(Split-Path $Vsix -Leaf): carries everything the build declares"
        exit 0
    }
    Write-Host "REFUSED $(Split-Path $Vsix -Leaf):"
    foreach ($r in $refusals) { Write-Host "   - $r" }
    exit 1
}
