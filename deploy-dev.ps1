#Requires -Version 5.1
<#
.SYNOPSIS
    Dev deployment for the Inferpal Visual Studio 2026 extension.
.DESCRIPTION
    One entry point for day-to-day deployment (former deploy-debug.ps1 merged in):
    build + deploy into the installed VS extension:
      . first run (nothing installed)  -> silent VSIXInstaller bootstrap
      . sources unchanged              -> skips everything (use -Force)
      . VS running (no -Launch)        -> locked assemblies are swapped via
        rename. HOT APPLY only applies while the extension is hosted
        out-of-process (kill the ServiceHub Extensibility host, VS respawns
        it on the fresh DLL). Under in-process hosting (2026-08-23) there is
        no such host: the running VS keeps the old DLL until a restart, and
        the script says so instead of claiming success. Ghost text always
        needed the restart anyway: use -Launch.
      . also auto-heals Properties\launchSettings.json (F5) when its
        devenv path doesn't exist on this machine
.PARAMETER Launch
    Close VS if running, deploy, then relaunch it ON THE SAME SOLUTION (the open
    solutions are recorded before the close). This is the ONLY path in the script
    that closes a devenv: the VSIX bootstrap refuses and asks the user to close VS
    instead - it does not know how to reopen what it would close.
.PARAMETER Force
    Deploy even when the sources are older than the deployed DLL.
.EXAMPLE
    .\deploy-dev.ps1            # hot apply into the running VS (no restart)
    .\deploy-dev.ps1 -Launch    # full VS restart (needed for ghost-text changes)
#>
param(
    [switch]$Launch,
    [switch]$Force,
    # Internal: set by the self-elevation relaunch so the elevated window stays
    # open long enough to read the result.
    [switch]$PauseAtEnd
)

$ErrorActionPreference = 'Stop'
$Root = $PSScriptRoot

# The self-elevated instance runs in its own console — keep a transcript so the
# outcome is inspectable even after the window closes.
if ($PauseAtEnd) {
    try { Start-Transcript -Path "$env:TEMP\inferpal-deploy-vs.log" -Force | Out-Null } catch { }
}

# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------

# Newest LastWriteTimeUtc among the given roots (filtered by extensions), or MinValue.
function Get-NewestSourceTime([string[]]$Roots, [string[]]$Extensions) {
    $newest = [DateTime]::MinValue
    foreach ($r in $Roots) {
        if (-not (Test-Path $r)) { continue }
        Get-ChildItem $r -Recurse -File -ErrorAction SilentlyContinue |
            Where-Object { $Extensions -contains $_.Extension } |
            ForEach-Object { if ($_.LastWriteTimeUtc -gt $newest) { $newest = $_.LastWriteTimeUtc } }
    }
    return $newest
}

function Get-MTime([string]$Path) {
    if (Test-Path $Path) { return (Get-Item $Path).LastWriteTimeUtc }
    return [DateTime]::MinValue
}

# ---------------------------------------------------------------------------
# Visual Studio
# ---------------------------------------------------------------------------

# VS 18 install root via vswhere (edition/channel vary — never hardcode).
function Get-VsPath {
    $vsWhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
    if (Test-Path $vsWhere) {
        $p = & $vsWhere -all -prerelease -products * -version '[18.0,19.0)' `
             -requires Microsoft.Component.MSBuild -property installationPath 2>$null |
             Select-Object -First 1
        if ($p) { return $p }
    }
    return Get-ChildItem 'C:\Program Files\Microsoft Visual Studio\18' -Directory -ErrorAction SilentlyContinue |
           Where-Object { Test-Path "$($_.FullName)\Common7\IDE\devenv.exe" } |
           Select-Object -First 1 -ExpandProperty FullName
}

# Where the extension is actually installed (Program Files first, Exp hive fallback).
function Get-InstalledInferpalDll([string]$VsPath) {
    $dll = Get-ChildItem "$VsPath\Common7\IDE" -Recurse -Filter 'Inferpal.dll' -ErrorAction SilentlyContinue |
           Select-Object -First 1
    if (-not $dll) {
        $dll = Get-ChildItem "$env:LOCALAPPDATA\Microsoft\VisualStudio\18.0_*Exp\Extensions" -Recurse `
                   -Filter 'Inferpal.dll' -ErrorAction SilentlyContinue | Select-Object -First 1
    }
    return $dll
}

# F5 auto-heal: launchSettings.json carries a machine-specific devenv path.
function Repair-LaunchSettings([string]$VsPath) {
    $file = "$Root\Inferpal\Properties\launchSettings.json"
    if (-not (Test-Path $file)) { return }
    $raw = Get-Content $file -Raw
    if ($raw -match '"executablePath":\s*"([^"]+)"') {
        $current = $Matches[1] -replace '\\\\', '\'
        if (-not (Test-Path $current)) {
            $good = "$VsPath\Common7\IDE\devenv.exe" -replace '\\', '\\'
            $raw  = $raw -replace '"executablePath":\s*"[^"]+"', "`"executablePath`": `"$good`""
            [System.IO.File]::WriteAllText($file, $raw, (New-Object System.Text.UTF8Encoding($false)))
            Write-Host "  launchSettings.json healed: F5 now targets $VsPath" -ForegroundColor Green
        }
    }
}

function Test-IsElevated {
    return [Security.Principal.WindowsPrincipal]::new(
        [Security.Principal.WindowsIdentity]::GetCurrent()
    ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

# -- Closing a devenv: only if we reopen it ----------------------------------
# Closing a VS you did not start is a dead loss: the working session goes with the
# process. Since in-process hosting, the temptation is permanent, because every DLL
# update needs a restart - which is exactly where a guard rail belongs. Rule: we
# close ONLY if we reopen behind us, and we reopen ON THE SAME SOLUTION (a devenv
# relaunched bare is still a loss of context). Hence recording what was open BEFORE
# closing anything.
$script:VsToRestore = @()

# The open solution is read first from the command line (VS started by double-click
# on a .sln), otherwise from the window title ("Solution - File - Microsoft Visual
# Studio") resolved against the repository. Neither route is guaranteed:
# $null = we will reopen bare, and say so rather than keep quiet about it.
function Get-VsSolutionOf($Proc) {
    try {
        $cmd = (Get-CimInstance Win32_Process -Filter "ProcessId=$($Proc.Id)" -ErrorAction Stop).CommandLine
        if ($cmd) {
            foreach ($m in [regex]::Matches($cmd, '"?([A-Za-z]:\\[^"]+?\.slnx?)"?')) {
                if (Test-Path $m.Groups[1].Value) { return $m.Groups[1].Value }
            }
        }
    } catch { }
    if ($Proc.MainWindowTitle) {
        $parts = $Proc.MainWindowTitle -split ' - '
        if ($parts.Count -ge 2) {
            $name = $parts[0].Trim()
            foreach ($ext in @('.sln', '.slnx')) {
                $candidate = Join-Path $Root "$name$ext"
                if (Test-Path $candidate) { return $candidate }
            }
        }
    }
    return $null
}

function Get-HostingDevenvId {
    # Walk up the parent chain: is this process a descendant of a devenv?
    $seen = @{}
    $id = $PID
    while ($id -and -not $seen.ContainsKey($id)) {
        $seen[$id] = $true
        $p = Get-CimInstance Win32_Process -Filter "ProcessId=$id" -ErrorAction SilentlyContinue
        if (-not $p) { return 0 }
        if ($p.ProcessId -ne $PID -and $p.Name -eq 'devenv.exe') { return [int]$p.ProcessId }
        $id = [int]$p.ParentProcessId
    }
    return 0
}

function Close-VsInstances([string]$StepLabel) {
    $vsProcs = @(Get-Process -Name 'devenv' -ErrorAction SilentlyContinue)
    if (-not $vsProcs.Count) { return }

    # Is this process hosted by one of the devenv instances it is about to close (the
    # VS integrated terminal, an agent launched from VS)? Then the promise to reopen is
    # worthless: closing takes its own console with it before the finally runs, and VS
    # stays closed. No amount of consent makes that case safe.
    $hosting = Get-HostingDevenvId
    if ($hosting) {
        Write-Host "   [ERR] Refused: this script runs INSIDE the devenv PID $hosting it would have to close." -ForegroundColor Red
        Write-Host "         Run it again from a console independent of Visual Studio." -ForegroundColor Gray
        exit 4
    }
    Write-Host "`n$StepLabel Closing VS..." -ForegroundColor Cyan
    foreach ($p in $vsProcs) {
        $sln = Get-VsSolutionOf $p
        $script:VsToRestore += , $sln
        $what = if ($sln) { $sln } else { "(solution not identified: '$($p.MainWindowTitle)')" }
        Write-Host "   Stopping PID $($p.Id) -> will be reopened on $what" -ForegroundColor Gray
    }

    # On disk BEFORE any closing gesture: that is the only moment where the
    # information still exists AND this process can still write it. A kill after this
    # point is recoverable; before it, there is nothing to recover.
    Save-VsRestoreState
    foreach ($p in $vsProcs) { $p.CloseMainWindow() | Out-Null }
    foreach ($p in $vsProcs) {
        if (-not $p.WaitForExit(10000)) {
            $p.Kill()
            Write-Host "   Force-killed PID $($p.Id)" -ForegroundColor Yellow
        }
    }
    Write-Host "   VS closed." -ForegroundColor Green
}

# Idempotent: the list is emptied on the first pass.
# --- Recovery after an interruption ---------------------------------------------
# "I reopen what I close" only holds as long as THIS process lives. A deployment
# script was once killed between the close and the reopen: VS closed, the finally
# never ran, the session lost. So the list goes to DISK before closing anything, and
# the next script to start reopens what a dead process left closed. The file is
# shared by every script that closes VS: whichever one dies, the next one repairs.
$script:VsRestoreFile = Join-Path $env:TEMP 'inferpal-vs-restore.txt'

# One line per closed instance; an empty line means the solution was not identified.
function Save-VsRestoreState {
    try {
        $lines = @($script:VsToRestore | ForEach-Object { if ($_) { "$_" } else { '' } })
        Set-Content -LiteralPath $script:VsRestoreFile -Value $lines -Encoding UTF8
    } catch { }
}

function Clear-VsRestoreState {
    try { if (Test-Path $script:VsRestoreFile) { Remove-Item $script:VsRestoreFile -Force } } catch { }
}

# Reopens whatever a previous run closed without reopening (kill, crash, power cut).
# A solution that is already open is never opened twice.
function Resume-InterruptedVsRestore([string]$DevEnvPath) {
    if (-not (Test-Path $script:VsRestoreFile)) { return }
    if (-not $DevEnvPath) {
        $vsWhereExe = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
        if (Test-Path $vsWhereExe) {
            $vp = @(& $vsWhereExe -latest -prerelease -products * -property installationPath)
            if ($vp.Count) { $DevEnvPath = Join-Path $vp[0] "Common7\IDE\devenv.exe" }
        }
    }
    $pending = @()
    try { $pending = @(Get-Content -LiteralPath $script:VsRestoreFile) } catch { }
    Clear-VsRestoreState
    if (-not $pending.Count -or -not $DevEnvPath -or -not (Test-Path $DevEnvPath)) { return }

    $open = @(Get-Process -Name 'devenv' -ErrorAction SilentlyContinue | ForEach-Object { Get-VsSolutionOf $_ })
    $reopened = 0
    foreach ($sln in $pending) {
        if ($sln -and ($open -contains $sln)) { continue }   # deja rouvert a la main
        if ($sln) { Start-Process $DevEnvPath -ArgumentList "`"$sln`"" } else { Start-Process $DevEnvPath }
        $reopened++
    }
    if ($reopened) {
        Write-Host "   Reprise : un run precedent a ferme VS sans le rouvrir -> $reopened instance(s) relancee(s)." -ForegroundColor Yellow
    }
}

function Restore-VsInstances([string]$DevEnvPath) {
    if (-not $script:VsToRestore.Count) { return }
    $toOpen = $script:VsToRestore
    $script:VsToRestore = @()
    Clear-VsRestoreState
    foreach ($sln in $toOpen) {
        if ($sln) {
            Write-Host "   Reopening $sln" -ForegroundColor Gray
            Start-Process $DevEnvPath -ArgumentList "`"$sln`""
        } else {
            Write-Host "   Reopening VS (solution unknown - reopen it by hand)" -ForegroundColor Yellow
            Start-Process $DevEnvPath
        }
    }
}

# First gesture of the script, before anything else: repair a session the previous
# run left closed. The file is deleted as soon as it is read, so this is a no-op in
# the normal case.
Resume-InterruptedVsRestore

# A running VS holds locks on the deployed assemblies (devenv loads the in-proc
# MEF/ghost-text part, the ServiceHub Extensibility host loads the rest). Windows
# still allows RENAMING a mapped file: swap the locked one aside and put the fresh
# copy in its place — live processes keep the old mapping, the next (re)load gets
# the new file. The .stale leftovers are purged on the next run once unlocked.
function Copy-WithLockSwap([string]$Source, [string]$DestDir) {
    $name = Split-Path $Source -Leaf
    $dest = Join-Path $DestDir $name
    try {
        Copy-Item $Source $dest -Force
        return $true
    } catch [System.IO.IOException] {
        try {
            $aside = "$dest.stale"
            if (Test-Path $aside) { Remove-Item $aside -Force -ErrorAction SilentlyContinue }
            Move-Item $dest "$dest.stale" -Force
            Copy-Item $Source $dest -Force
            Write-Host "   $name : was locked -> swapped via rename" -ForegroundColor Yellow
            return $true
        } catch {
            Write-Host "   $name : LOCKED, could not swap ($($_.Exception.Message))" -ForegroundColor Red
            return $false
        }
    }
}

# Former deploy-debug.ps1, inlined: builds Inferpal and deploys the DLL + deps +
# localization assets into the installed extension, registers GhostTextPackage,
# purges the MEF/mpack caches. Returns $true on success.
function Invoke-VsDeployCore([string]$VsPath) {
    $MSBuild = "$VsPath\MSBuild\Current\Bin\MSBuild.exe"
    $DevEnv  = "$VsPath\Common7\IDE\devenv.exe"
    Write-Host "   VS detected: $VsPath" -ForegroundColor Cyan

    $Project = "$Root\Inferpal\Inferpal.csproj"
    $BinDir  = "$Root\Inferpal\bin\Debug\net8.0-windows"

    # VSIX install directory in the Exp hive (where VS actually loads the extension from)
    $VsExpHive = (Get-ChildItem "$env:LOCALAPPDATA\Microsoft\VisualStudio" -Directory -ErrorAction SilentlyContinue |
                  Where-Object { $_.Name -match "^18\.0_.*Exp$" } |
                  Select-Object -First 1).FullName

    if (-not $VsExpHive) {
        Write-Host "   No VS 18 Exp hive found under $env:LOCALAPPDATA\Microsoft\VisualStudio" -ForegroundColor Red
        return $false
    }

    # Auto-detection: look for Inferpal.dll in:
    #  1. Program Files (system-wide install -- recommended, requires admin)
    #  2. AppData Exp hive (user-only install, fallback)
    $PfExtDir  = "$VsPath\Common7\IDE"
    # Two roots coexist: Extensions\ (the classic model, the one where VS processes the pkgdef AND
    # the MEF catalog -- so the in-process half) and VSExtensions\ (pure out-of-process model).
    # Moving to hybrid hosting moves the extension from the second to the first; as long as an old
    # install lingers in the other one, a naive "Select-Object -First 1" can deploy into the DEAD
    # folder without anything saying so. Hence the explicit choice.
    $allInstalls = @(Get-ChildItem $PfExtDir -Recurse -Filter "Inferpal.dll" -ErrorAction SilentlyContinue |
                     Select-Object -ExpandProperty DirectoryName -Unique)
    $classic     = @($allInstalls | Where-Object { $_ -like '*\Common7\IDE\Extensions\*' })
    $InstalledDir = if ($classic.Count -gt 0) { $classic[0] } elseif ($allInstalls.Count -gt 0) { $allInstalls[0] } else { $null }

    if ($allInstalls.Count -gt 1) {
        Write-Host "   WARNING: $($allInstalls.Count) Inferpal installs found under Common7\IDE." -ForegroundColor Yellow
        foreach ($d in $allInstalls) {
            $tag = if ($d -eq $InstalledDir) { "deploying here" } else { "stale -- delete it" }
            Write-Host "     $d  ($tag)" -ForegroundColor $(if ($d -eq $InstalledDir) { 'Green' } else { 'Yellow' })
        }
        Write-Host "   The stale one is invisible to VS but shadows this script's detection." -ForegroundColor Yellow
    }

    if (-not $InstalledDir) {
        $InstalledDir = (Get-ChildItem "$VsExpHive\Extensions" -Recurse -Filter "Inferpal.dll" -ErrorAction SilentlyContinue |
                         Select-Object -First 1).DirectoryName
    }

    if ($InstalledDir) {
        $inProgramFiles = $InstalledDir.StartsWith($VsPath, [System.StringComparison]::OrdinalIgnoreCase)
        if ($inProgramFiles) {
            Write-Host "   Extension found in Program Files (system-wide install)" -ForegroundColor Cyan
        } else {
            Write-Host "   WARNING: extension is in AppData (user-only install)." -ForegroundColor Yellow
            Write-Host "   Menu labels will be broken (extensionDir won't resolve)." -ForegroundColor Yellow
            Write-Host "   Reinstall the VSIX on your main VS instance to fix this." -ForegroundColor Yellow
        }
    }

    if (-not $InstalledDir) {
        # Normally unreachable: Deploy-Vs bootstraps the VSIX before calling this.
        $VsixPath = "$BinDir\Inferpal.vsix"
        Write-Host ""
        Write-Host "Inferpal extension not found." -ForegroundColor Yellow
        Write-Host ""
        Write-Host "Steps to install it properly:" -ForegroundColor Cyan
        Write-Host "  1. Close Visual Studio 2026 if it's open." -ForegroundColor White
        if (Test-Path $VsixPath) {
            Write-Host "  2. Double-click: $VsixPath" -ForegroundColor White
        } else {
            Write-Host "  2. Build first, then install the VSIX from Inferpal\bin\Debug\net8.0-windows\" -ForegroundColor White
        }
        Write-Host "  3. IMPORTANT: select your main Visual Studio 2026 instance" -ForegroundColor Yellow
        Write-Host "     (NOT the Experimental instance -- VS Exp inherits system-wide extensions)" -ForegroundColor Yellow
        Write-Host "  4. Accept the admin elevation prompt if asked." -ForegroundColor White
        Write-Host "  5. Re-run this script." -ForegroundColor White
        Write-Host ""
        return $false
    }

    $ExtDir = "$VsExpHive\Extensions"

    # -- Prerequisite validation ----------------------------------------------
    if (-not (Test-Path $MSBuild)) {
        Write-Host "   MSBuild not found: $MSBuild" -ForegroundColor Red
        return $false
    }

    # -- 1. Build ---------------------------------------------------------------
    Write-Host "`n[1/$(if ($Launch) { 4 } else { 3 })] Build..." -ForegroundColor Cyan
    & $MSBuild $Project /p:Configuration=Debug /v:minimal /nologo
    if ($LASTEXITCODE -ne 0) { Write-Host "   Build failed." -ForegroundColor Red; return $false }

    if (-not (Test-Path "$BinDir\Inferpal.dll")) {
        Write-Host "   Inferpal.dll not found in $BinDir" -ForegroundColor Red
        return $false
    }
    Write-Host "   Build OK" -ForegroundColor Green

    # -- 2. Close VS when -Launch ------------------------------------------------
    # The only place in the script allowed to close VS: -Launch is an explicit restart
    # request, and step 4 reopens what Close-VsInstances recorded.
    if ($Launch) { Close-VsInstances "[2/4]" }

    # -- 3. Deploy: copy DLL + PDB into the installed directory ------------------
    $step = if ($Launch) { "3/4" } else { "2/3" }
    Write-Host "`n[$step] Deploying to $InstalledDir..." -ForegroundColor Cyan

    # Purge swap leftovers from previous runs (ignored while still mapped).
    Get-ChildItem $InstalledDir -Recurse -Filter '*.stale' -ErrorAction SilentlyContinue |
        ForEach-Object { Remove-Item $_.FullName -Force -ErrorAction SilentlyContinue }

    if (-not (Copy-WithLockSwap "$BinDir\Inferpal.dll" $InstalledDir)) {
        Write-Host "   Could not deploy Inferpal.dll (locked). Close Visual Studio and retry." -ForegroundColor Red
        return $false
    }
    Write-Host "   Inferpal.dll  : OK" -ForegroundColor Green

    if (Test-Path "$BinDir\Inferpal.pdb") {
        Copy-WithLockSwap "$BinDir\Inferpal.pdb" $InstalledDir | Out-Null
        Write-Host "   Inferpal.pdb  : OK (debug symbols)" -ForegroundColor Green
    }

    # -- Third-party dependencies + native assets -- SYNC from the VSIX ---------
    # GUARD: this step used to push ONLY Inferpal.dll, assuming dependencies were
    # already present from an earlier full VSIX install. When a NEW dependency is
    # added to the project (e.g. Microsoft.Data.Sqlite + SQLitePCLRaw + the native
    # e_sqlite3 used by the RAG index), it never reached the install -> the extension
    # crashed with "Could not load file or assembly".
    #
    # So we sync the dependency DLLs + the runtimes/ folder FROM the freshly built
    # VSIX: it's EXACTLY the set a full install would deploy (the assemblies provided
    # by VS -- Shell.15.0, Text.UI.Wpf... -- are already excluded by packaging, so we
    # never risk overwriting an IDE-provided version).
    $VsixForDeps = "$BinDir\Inferpal.vsix"
    if (Test-Path $VsixForDeps) {
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $zip = [System.IO.Compression.ZipFile]::OpenRead($VsixForDeps)
        try {
            $depCount = 0
            foreach ($entry in $zip.Entries) {
                $name = $entry.FullName
                # Skip folders and Inferpal.dll (already copied from bin above)
                if ($name.EndsWith('/') -or $name -eq 'Inferpal.dll') { continue }

                # Sync only: top-level dependency DLLs + everything under runtimes/**
                # (per-RID native assets, e.g. runtimes/win-x64/native/e_sqlite3.dll).
                $isTopLevelDll  = ($name -notmatch '/') -and $name.EndsWith('.dll')
                $isRuntimeAsset = $name.StartsWith('runtimes/')
                # ...plus the FIM sidecar, which is an EXE with its two .json launch files and
                # would otherwise never reach the install: ghost-text would then find no
                # Inferpal.Fim.exe next to Inferpal.InProc.dll and stay silent, by design.
                $isFimSidecar   = $name -in @('Inferpal.Fim.exe',
                                              'Inferpal.Fim.runtimeconfig.json',
                                              'Inferpal.Fim.deps.json')
                if (-not ($isTopLevelDll -or $isRuntimeAsset -or $isFimSidecar)) { continue }

                $dest    = Join-Path $InstalledDir ($name -replace '/', '\')
                $destDir = Split-Path $dest -Parent
                if (-not (Test-Path $destDir)) { New-Item -ItemType Directory -Path $destDir -Force | Out-Null }
                try {
                    [System.IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $dest, $true)
                } catch [System.IO.IOException] {
                    # Locked by a running VS — same rename-swap as the main DLL.
                    $aside = "$dest.stale"
                    if (Test-Path $aside) { Remove-Item $aside -Force -ErrorAction SilentlyContinue }
                    Move-Item $dest $aside -Force
                    [System.IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $dest, $true)
                }
                $depCount++
            }
            Write-Host "   Dependencies + runtimes synced (VSIX): $depCount files" -ForegroundColor Green
        } finally {
            $zip.Dispose()
        }
    } else {
        Write-Host "   WARNING: VSIX not found, dependencies were NOT synced" -ForegroundColor Yellow
        Write-Host "     ($VsixForDeps)" -ForegroundColor Yellow
        Write-Host "     A newly added NuGet dependency could be missing at runtime." -ForegroundColor Yellow
    }

    # Purge the stray ROOT-LEVEL Inferpal.Core.resources.dll (a Culture=de satellite the
    # old VSIX packaging flattened next to Inferpal.Core.dll). The Extensibility host ALC
    # resolves "Inferpal.Core.resources" against it FIRST; the culture mismatch makes the
    # bind fail and every UI language silently falls back to English. The csproj target
    # PruneFlattenedCoreSatelliteFromVsix keeps it out of new VSIXes; this removes the
    # leftover from already-deployed installs.
    $strayRootSat = "$InstalledDir\Inferpal.Core.resources.dll"
    if (Test-Path $strayRootSat) {
        try {
            Remove-Item $strayRootSat -Force -ErrorAction Stop
            Write-Host "   Inferpal.Core.resources.dll (root, breaks localization) : REMOVED" -ForegroundColor Yellow
        } catch {
            # Locked by a running VS -- same rename trick, the .stale purge above cleans it next run.
            try {
                Move-Item $strayRootSat "$strayRootSat.stale" -Force
                Write-Host "   Inferpal.Core.resources.dll (root, breaks localization) : swapped aside (.stale)" -ForegroundColor Yellow
            } catch {
                Write-Host "   WARNING: stray root Inferpal.Core.resources.dll is locked - localization stays broken until VS restarts" -ForegroundColor Red
            }
        }
    }

    # Satellite assemblies {locale}/Inferpal.Core.resources.dll -- hold the runtime translations
    # (the .resx moved into Inferpal.Core with the editor-agnostic core extraction, July 2026)
    $satCopied = 0
    Get-ChildItem $BinDir -Directory -ErrorAction SilentlyContinue |
        ForEach-Object {
            $locale = $_.Name
            $srcSat = "$BinDir\$locale\Inferpal.Core.resources.dll"
            if (Test-Path $srcSat) {
                $dstSat = "$InstalledDir\$locale"
                if (-not (Test-Path $dstSat)) { New-Item -ItemType Directory -Path $dstSat | Out-Null }
                Copy-WithLockSwap $srcSat $dstSat | Out-Null
                Write-Host "   $locale\Inferpal.Core.resources.dll : OK" -ForegroundColor Green
                $satCopied++
            }
        }
    if ($satCopied -eq 0) {
        Write-Host "   WARNING: no translation satellite found under $BinDir - UI locales will fall back to English" -ForegroundColor Yellow
    }

    # .vsextension/ : string-resources.json (default) + {locale}/string-resources.json subfolders
    # VS resolves the %tokens% from .vsextension/{locale}/string-resources.json
    $VsExtDst = "$InstalledDir\.vsextension"
    if (-not (Test-Path $VsExtDst)) { New-Item -ItemType Directory -Path $VsExtDst | Out-Null }

    # Copy the default file (EN)
    $SrcDefault = "$BinDir\.vsextension\string-resources.json"
    if (Test-Path $SrcDefault) {
        Copy-Item $SrcDefault "$VsExtDst\" -Force
        Write-Host "   string-resources.json : OK (.vsextension/)" -ForegroundColor Green
    }

    # Copy the locale subfolders (fr/, de/, es/, it/, ru/, ja/, ko/, pl/, zh-CN/)
    Get-ChildItem "$BinDir\.vsextension" -Directory -ErrorAction SilentlyContinue |
        ForEach-Object {
            $locale = $_.Name
            $srcLocale = "$BinDir\.vsextension\$locale\string-resources.json"
            if (Test-Path $srcLocale) {
                $dstLocale = "$VsExtDst\$locale"
                if (-not (Test-Path $dstLocale)) { New-Item -ItemType Directory -Path $dstLocale | Out-Null }
                Copy-Item $srcLocale "$dstLocale\" -Force
                Write-Host "   $locale\string-resources.json : OK (.vsextension/$locale/)" -ForegroundColor Green
            }
        }

    $ExtJsonSrc = "$BinDir\.vsextension\extension.json"
    if (Test-Path $ExtJsonSrc) {
        Copy-Item $ExtJsonSrc "$VsExtDst\" -Force
        Write-Host "   extension.json   : OK" -ForegroundColor Green
    }

    # manifest.json : fixes extensionDir, which points to [installdir]\...\VSExtensions\<random>
    # (the VSIX installer writes an invalid path for user-only installs)
    $ManifestPath = "$InstalledDir\manifest.json"
    if (Test-Path $ManifestPath) {
        $mj = Get-Content $ManifestPath -Raw | ConvertFrom-Json
        if ($mj.extensionDir -ne $InstalledDir) {
            $mj.extensionDir = $InstalledDir
            [System.IO.File]::WriteAllText($ManifestPath, ($mj | ConvertTo-Json -Depth 10 -Compress), [System.Text.Encoding]::UTF8)
            Write-Host "   manifest.json    : extensionDir fixed" -ForegroundColor Green
        } else {
            Write-Host "   manifest.json    : OK (already correct)" -ForegroundColor Green
        }
    }

    # Copy the updated pkgdef. ⚠ Copying it does NOT apply it: it only enters the VS
    # configuration through a merge (extension inventory, or devenv /updateconfiguration
    # with VS closed).
    if (Test-Path "$BinDir\Inferpal.pkgdef") {
        Copy-Item "$BinDir\Inferpal.pkgdef" "$InstalledDir\" -Force
        Write-Host "   Inferpal.pkgdef : OK" -ForegroundColor Green
    }

    # -- This script writes NOTHING to the registry any more --------------------
    #
    # ⚠ Measured. There used to be two registry-writing blocks here, and the second one was
    # not dead code: it wrote into privateregistry.bin, THE hive VS actually reads. It wrote
    # Packages\{6a7b2c3d-...} with "CodeBase" = <install>\Inferpal.dll -- the .NET 8 assembly,
    # the one devenv cannot load -- and "AllowsBackgroundLoading", which is the name of the C#
    # PROPERTY, not the name of the registry value it writes ("AllowsBackgroundLoad"). In other
    # words: every deployment run with VS closed rewrote both in-process bugs on top of a correct
    # registration, and printed a green "written and read back: OK" while doing it.
    #
    # A key being present says neither who wrote it nor what is inside it. The only path that
    # registers the package is the pkgdef merge, triggered by the extension inventory (the
    # Microsoft.VisualStudio.VsPackage asset of the manifest) or forced by
    # `devenv.exe /updateconfiguration` with VS closed. The other deleted block wrote under HKCU,
    # which VS 2026 was measured not to read at all.
    #
    # What remains below is a cache purge, not a configuration write.
    foreach ($hiveDir in @(Get-ChildItem "$env:LOCALAPPDATA\Microsoft\VisualStudio" -Directory -ErrorAction SilentlyContinue |
                           Where-Object { $_.Name -match '^18\.' })) {
        $shortName = $hiveDir.Name
        $mefCache  = Join-Path $hiveDir.FullName 'ComponentModelCache'
        if (Test-Path $mefCache) {
            Remove-Item $mefCache -Recurse -Force -ErrorAction SilentlyContinue
            Write-Host "   [$shortName] ComponentModelCache purged: OK" -ForegroundColor Green
        }
    }

    # Remove the mpack caches (Exp hive + main VS hive)
    Get-ChildItem $ExtDir -Filter "*.mpack" -ErrorAction SilentlyContinue |
        ForEach-Object { Remove-Item $_.FullName -Force; Write-Host "   Cache removed (Exp): $($_.Name)" -ForegroundColor Gray }

    $vsMainHive = (Get-ChildItem "$env:LOCALAPPDATA\Microsoft\VisualStudio" -Directory -ErrorAction SilentlyContinue |
                   Where-Object { $_.Name -match "^18\.0" -and $_.Name -notmatch "Exp$" } |
                   Select-Object -First 1).FullName
    if ($vsMainHive) {
        Get-ChildItem "$vsMainHive\Extensions" -Filter "*.mpack" -ErrorAction SilentlyContinue |
            ForEach-Object { Remove-Item $_.FullName -Force; Write-Host "   Cache removed (main): $($_.Name)" -ForegroundColor Gray }
    }

    Write-Host "   Deployment complete." -ForegroundColor Green

    # -- 4. Optional: relaunch the main VS instance -----------------------------
    if ($Launch) {
        Write-Host "`n[4/4] Relaunching Visual Studio 2026..." -ForegroundColor Cyan
        # No /rootsuffix Exp -- the main instance is where the VSIX is installed.
        if ($script:VsToRestore.Count) {
            Restore-VsInstances $DevEnv
        } else {
            # Rien n'etait ouvert : -Launch reste une demande de lancement.
            $sln = Get-ChildItem $Root -Filter '*.sln' -ErrorAction SilentlyContinue | Select-Object -First 1
            if ($sln) { Start-Process $DevEnv -ArgumentList "`"$($sln.FullName)`"" } else { Start-Process $DevEnv }
        }
        Write-Host "   VS restarted." -ForegroundColor Green
        Write-Host "   -> Open a .cs file, right-click > Inferpal > Edit with AI..." -ForegroundColor Cyan
    }

    return $true
}

function Deploy-Vs {
    Write-Host "`n=== Visual Studio 2026 (dev) ===" -ForegroundColor Cyan

    $vsPath = Get-VsPath
    if (-not $vsPath) { Write-Host "  No VS 18 installation found." -ForegroundColor Red; return $false }
    Repair-LaunchSettings $vsPath

    # ── First run: silent VSIX bootstrap instead of a manual checklist ────────
    $installed = Get-InstalledInferpalDll $vsPath
    if (-not $installed) {
        Write-Host "  Extension not installed -> bootstrap via VSIXInstaller..." -ForegroundColor Yellow

        # Installing a VSIX requires VS to be closed. This block will NOT close it: it does
        # not know how to reopen what it closes (the elevation that follows relaunches the
        # script in another process, and the restore list does not travel with it), and a
        # devenv killed without a relaunch costs the user their working session. So it
        # refuses and hands back control. This path also triggers when the install changed
        # root (Extensions\ <-> VSExtensions\ after an ExtensionType change): the extension
        # IS installed, it is just no longer where we look for it -- one more reason not to
        # kill anything.
        if (Get-Process -Name 'devenv' -ErrorAction SilentlyContinue) {
            Write-Host "  Visual Studio is running, and installing the VSIX requires it closed." -ForegroundColor Red
            Write-Host "  Close VS yourself, then run this script again - nothing has been touched." -ForegroundColor Yellow
            return $false
        }

        $msbuild = "$vsPath\MSBuild\Current\Bin\MSBuild.exe"
        & $msbuild "$Root\Inferpal\Inferpal.csproj" /p:Configuration=Debug /v:minimal /nologo
        if ($LASTEXITCODE -ne 0) { Write-Host "  Build failed." -ForegroundColor Red; return $false }

        $vsix = "$Root\Inferpal\bin\Debug\net8.0-windows\Inferpal.vsix"
        if (-not (Test-Path $vsix)) { Write-Host "  VSIX not found: $vsix" -ForegroundColor Red; return $false }

        # /a = machine-wide (Program Files, what the deploy core expects); expect a UAC prompt.
        & "$vsPath\Common7\IDE\VSIXInstaller.exe" /q /a $vsix | Out-Null
        if ($LASTEXITCODE -ne 0) {
            Write-Host "  Silent install failed (code $LASTEXITCODE) -> retrying with UI..." -ForegroundColor Yellow
            & "$vsPath\Common7\IDE\VSIXInstaller.exe" /a $vsix | Out-Null
        }
        $installed = Get-InstalledInferpalDll $vsPath
        if (-not $installed) { Write-Host "  Install failed - extension still not found." -ForegroundColor Red; return $false }
        Write-Host "  Installed: $($installed.DirectoryName)" -ForegroundColor Green
    }

    # ── Skip-if-fresh: nothing changed since the deployed DLL was written ─────
    if (-not $Force) {
        $srcNewest = Get-NewestSourceTime @("$Root\Inferpal", "$Root\Inferpal.Core") `
                                          @('.cs', '.csproj', '.resx', '.json', '.props', '.xaml')
        if ($srcNewest -le $installed.LastWriteTimeUtc) {
            Write-Host "  Already up to date - nothing to do (use -Force to redeploy)." -ForegroundColor Green
            return $true
        }
    }

    # ── Elevation: a Program Files install dir is not writable from a normal
    # shell — relaunch this script elevated (UAC prompt) instead of failing
    # halfway through the copy.
    if ($installed.DirectoryName -like "$env:ProgramFiles*" -and -not (Test-IsElevated)) {
        Write-Host "  Install dir is under Program Files -> relaunching elevated (UAC prompt)..." -ForegroundColor Yellow
        $argList = @('-NoProfile', '-ExecutionPolicy', 'Bypass',
                     '-File', "$Root\deploy-dev.ps1", '-PauseAtEnd')
        if ($Launch) { $argList += '-Launch' }
        if ($Force)  { $argList += '-Force' }
        try {
            Start-Process powershell.exe -Verb RunAs -ArgumentList $argList
            Write-Host "  Elevated window launched - the deployment continues there." -ForegroundColor Green
            return $true
        } catch {
            Write-Host "  Elevation refused - run this from an administrator terminal." -ForegroundColor Red
            return $false
        }
    }

    $ok = Invoke-VsDeployCore $vsPath

    # -- Hot apply only works while the extension is hosted OUT of process. The principle is to
    # kill the ServiceHub Extensibility host after the copy so VS respawns it on the fresh DLL.
    # Since RequiresInProcessHosting, the extension lives INSIDE devenv: there is no host left to
    # restart, the loaded DLL stays the old one, and announcing "hot apply done" would be a lie -
    # the kind that makes you hunt a bug in code that is not running yet. The presence of an
    # Extensibility host is therefore the detector: present = old mode, absent (with a devenv
    # open) = in-process, restart required.
    if ($ok -and -not $Launch -and (Get-Process -Name 'devenv' -ErrorAction SilentlyContinue)) {
        $hubs = Get-Process -Name 'ServiceHub.Host.Extensibility*' -ErrorAction SilentlyContinue
        Write-Host ""
        if ($hubs) {
            Write-Host "  Hot apply: restarting the Extensibility host on the fresh build..." -ForegroundColor Cyan
            $hubs | Stop-Process -Force -ErrorAction SilentlyContinue
            Write-Host "  Hot apply done: close/reopen the Inferpal tool window in VS to load the new build." -ForegroundColor Green
            Write-Host "  (Ghost-text changes still need a full VS restart: re-run with -Launch.)" -ForegroundColor Gray
        } else {
            Write-Host "  [IMPORTANT] In-process hosting: no Extensibility host to restart." -ForegroundColor Yellow
            Write-Host "  The running VS keeps the OLD Inferpal.dll until it is restarted:" -ForegroundColor Yellow
            Write-Host "    close VS, then .\deploy-dev.ps1 -Launch" -ForegroundColor Yellow
        }
    } elseif ($ok -and -not $Launch) {
        Write-Host ""
        Write-Host "  [IMPORTANT] VS must be restarted to apply the changes." -ForegroundColor Yellow
    }
    return $ok
}

# ---------------------------------------------------------------------------
# Run + summary
# ---------------------------------------------------------------------------
$ok = Deploy-Vs

Write-Host "`n=== Summary ===" -ForegroundColor Cyan
if ($ok) {
    Write-Host "  [OK]   Visual Studio" -ForegroundColor Green
} else {
    Write-Host "  [FAIL] Visual Studio" -ForegroundColor Red
}
if ($PauseAtEnd) {
    try { Stop-Transcript | Out-Null } catch { }
    Write-Host ""
    Read-Host "Press Enter to close this window"
}
if (-not $ok) { exit 1 }
