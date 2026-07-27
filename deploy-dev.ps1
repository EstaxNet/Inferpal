#Requires -Version 5.1
<#
.SYNOPSIS
    Dev deployment for the Inferpal Visual Studio 2026 extension.
.DESCRIPTION
    One entry point for day-to-day deployment (former deploy-debug.ps1 merged in):
    build + deploy into the installed VS extension:
      . first run (nothing installed)  -> silent VSIXInstaller bootstrap
      . sources unchanged              -> skips everything (use -Force)
      . VS running (no -Launch)        -> HOT APPLY: locked assemblies are
        swapped via rename, then the ServiceHub Extensibility host is
        killed so VS respawns it with the fresh DLL — no VS restart
        (ghost text = in-proc MEF, still needs a restart: use -Launch)
      . also auto-heals Properties\launchSettings.json (F5) when its
        devenv path doesn't exist on this machine
.PARAMETER Launch
    Close VS if running, deploy, then relaunch it.
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
    $InstalledDir = (Get-ChildItem $PfExtDir -Recurse -Filter "Inferpal.dll" -ErrorAction SilentlyContinue |
                     Select-Object -First 1).DirectoryName

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
    if ($Launch) {
        $vsProcs = Get-Process -Name "devenv" -ErrorAction SilentlyContinue
        if ($vsProcs) {
            Write-Host "`n[2/4] Closing VS..." -ForegroundColor Cyan
            $vsProcs | ForEach-Object {
                Write-Host "   Stopping PID $($_.Id): $($_.MainWindowTitle)" -ForegroundColor Gray
                $_.CloseMainWindow() | Out-Null
            }
            $vsProcs | ForEach-Object {
                if (-not $_.WaitForExit(10000)) {
                    $_.Kill()
                    Write-Host "   Force-killed PID $($_.Id)" -ForegroundColor Yellow
                }
            }
            Write-Host "   VS closed." -ForegroundColor Green
        }
    }

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
                if (-not ($isTopLevelDll -or $isRuntimeAsset)) { continue }

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

    # Copy the updated pkgdef (contains [$RootKey$\MEFComponent])
    if (Test-Path "$BinDir\Inferpal.pkgdef") {
        Copy-Item "$BinDir\Inferpal.pkgdef" "$InstalledDir\" -Force
        Write-Host "   Inferpal.pkgdef : OK" -ForegroundColor Green
    }

    # -- Register GhostTextPackage in HKCU --------------------------------------
    #
    # VS reads the Packages / AutoLoadPackages / MEFComponent keys from two sources:
    #   1. privateregistry.bin (private hive, LOCKED while VS is running -> unusable live)
    #   2. HKCU\Software\Microsoft\VisualStudio\{ver}\...  (standard Windows registry)
    #
    # MEFComponent is already in HKCU (written during the VSIX install).
    # Packages and AutoLoadPackages must be there too for GhostTextPackage to load.
    #
    # We write to HKCU directly -- works whether VS is open or closed.
    # Changes take effect on the next VS restart.

    $pkgGuid      = "{6a7b2c3d-4e5f-4a8b-9c0d-1e2f3a4b5c6d}"  # GhostTextPackage
    $ctxSolExists = "{adfc4e64-0397-11d1-9f4e-00a0c911004f}"   # SolutionExists (AutoLoad)
    $ctxNoSol     = "{f1536ef8-92ec-443c-9ed7-fdadf150da82}"   # NoSolution     (AutoLoad)

    $vsHkuBases = Get-ChildItem "HKCU:\Software\Microsoft\VisualStudio" -ErrorAction SilentlyContinue |
                  Where-Object { $_.Name -match "18\." } |
                  Select-Object -ExpandProperty PSPath

    foreach ($hiveBase in $vsHkuBases) {
        $shortName = Split-Path $hiveBase -Leaf
        try {
            # 1. MEFComponent (idempotent -- probably already there from the VSIX install)
            $mefPath = "$hiveBase\MEFComponent"
            if (-not (Test-Path $mefPath)) { New-Item -Path $mefPath -Force | Out-Null }
            Set-ItemProperty -Path $mefPath -Name "Inferpal" -Value $InstalledDir\Inferpal.dll
            Write-Host "   [$shortName] MEFComponent : OK" -ForegroundColor Green

            # 2. Packages\{GhostTextPackage}
            $pkgPath = "$hiveBase\Packages\$pkgGuid"
            if (-not (Test-Path $pkgPath)) { New-Item -Path $pkgPath -Force | Out-Null }
            Set-ItemProperty -Path $pkgPath -Name "(Default)"               -Value "Inferpal GhostText"
            Set-ItemProperty -Path $pkgPath -Name "InprocServer32"          -Value "C:\Windows\SYSTEM32\MSCOREE.DLL"
            Set-ItemProperty -Path $pkgPath -Name "Class"                   -Value "Inferpal.GhostText.GhostTextPackage"
            Set-ItemProperty -Path $pkgPath -Name "CodeBase"                -Value "$InstalledDir\Inferpal.dll"
            Set-ItemProperty -Path $pkgPath -Name "AllowsBackgroundLoading" -Value 1 -Type DWord
            Write-Host "   [$shortName] Packages\$pkgGuid : OK" -ForegroundColor Green

            # 3. AutoLoadPackages (SolutionExists + NoSolution)
            foreach ($ctx in @($ctxSolExists, $ctxNoSol)) {
                $alPath = "$hiveBase\AutoLoadPackages\$ctx"
                if (-not (Test-Path $alPath)) { New-Item -Path $alPath -Force | Out-Null }
                Set-ItemProperty -Path $alPath -Name $pkgGuid -Value 2 -Type DWord
            }
            Write-Host "   [$shortName] AutoLoadPackages : OK" -ForegroundColor Green

        } catch {
            Write-Host "   [$shortName] HKCU registration ERROR: $_" -ForegroundColor Red
        }

        # -- Purge the MEF cache to force a rebuild ------------------------------
        $vsHiveDir = "$env:LOCALAPPDATA\Microsoft\VisualStudio\$shortName"
        $mefCache  = "$vsHiveDir\ComponentModelCache"
        if (Test-Path $mefCache) {
            Remove-Item $mefCache -Recurse -Force -ErrorAction SilentlyContinue
            Write-Host "   [$shortName] ComponentModelCache purged: OK" -ForegroundColor Green
        }

        # -- Update privateregistry.bin when VS is closed (belt and braces) -----
        $privReg = "$vsHiveDir\privateregistry.bin"
        $vsRunning = (Get-Process -Name "devenv" -ErrorAction SilentlyContinue).Count -gt 0
        if (-not $vsRunning -and (Test-Path $privReg)) {
            $hiveAlias = "TempInferpalPR_$(Get-Random)"
            $hiveHkcu  = "HKU\$hiveAlias"
            $loadedOk  = $false
            try {
                $loadOut = & reg load $hiveHkcu $privReg 2>&1
                if ($LASTEXITCODE -eq 0) {
                    $loadedOk = $true
                    & reg add "$hiveHkcu\MEFComponent"                  /v "Inferpal"              /t REG_SZ    /d "$InstalledDir\Inferpal.dll"              /f 2>&1 | Out-Null
                    & reg add "$hiveHkcu\Packages\$pkgGuid"             /ve                           /d "Inferpal GhostText"                                  /f 2>&1 | Out-Null
                    & reg add "$hiveHkcu\Packages\$pkgGuid"             /v "InprocServer32"           /t REG_SZ    /d "C:\Windows\SYSTEM32\MSCOREE.DLL"            /f 2>&1 | Out-Null
                    & reg add "$hiveHkcu\Packages\$pkgGuid"             /v "Class"                    /t REG_SZ    /d "Inferpal.GhostText.GhostTextPackage"     /f 2>&1 | Out-Null
                    & reg add "$hiveHkcu\Packages\$pkgGuid"             /v "CodeBase"                 /t REG_SZ    /d "$InstalledDir\Inferpal.dll"              /f 2>&1 | Out-Null
                    & reg add "$hiveHkcu\Packages\$pkgGuid"             /v "AllowsBackgroundLoading"  /t REG_DWORD /d 1                                           /f 2>&1 | Out-Null
                    foreach ($ctx in @($ctxSolExists, $ctxNoSol)) {
                        & reg add "$hiveHkcu\AutoLoadPackages\$ctx" /v "$pkgGuid" /t REG_DWORD /d 2 /f 2>&1 | Out-Null
                    }
                    Write-Host "   [$shortName] privateregistry.bin updated: OK" -ForegroundColor Green
                }
            } catch { }
            finally {
                if ($loadedOk) { & reg unload $hiveHkcu 2>&1 | Out-Null }
            }
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
        Start-Process $DevEnv   # No /rootsuffix Exp -- the main instance is where the VSIX is installed
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
        $msbuild = "$vsPath\MSBuild\Current\Bin\MSBuild.exe"
        & $msbuild "$Root\Inferpal\Inferpal.csproj" /p:Configuration=Debug /v:minimal /nologo
        if ($LASTEXITCODE -ne 0) { Write-Host "  Build failed." -ForegroundColor Red; return $false }

        $vsix = "$Root\Inferpal\bin\Debug\net8.0-windows\Inferpal.vsix"
        if (-not (Test-Path $vsix)) { Write-Host "  VSIX not found: $vsix" -ForegroundColor Red; return $false }

        $devenv = Get-Process -Name 'devenv' -ErrorAction SilentlyContinue
        if ($devenv) {
            Write-Host "  Closing Visual Studio (required by the installer)..." -ForegroundColor Yellow
            $devenv | ForEach-Object { $_.CloseMainWindow() | Out-Null }
            $devenv | ForEach-Object { if (-not $_.WaitForExit(15000)) { $_.Kill() } }
        }

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

    # ── Hot apply: the extension runs OUT-OF-PROCESS in the ServiceHub
    # Extensibility host. The deploy core swaps locked files via rename, so the
    # copy succeeds while VS runs — but the live host still executes the OLD
    # mapping. Killing it AFTER the copy makes VS respawn it on the fresh DLL:
    # no VS restart. Only the in-proc ghost text (MEF) keeps the old version
    # until a real restart (-Launch).
    if ($ok -and -not $Launch -and (Get-Process -Name 'devenv' -ErrorAction SilentlyContinue)) {
        $hubs = Get-Process -Name 'ServiceHub.Host.Extensibility*' -ErrorAction SilentlyContinue
        if ($hubs) {
            Write-Host "  Hot apply: restarting the Extensibility host on the fresh build..." -ForegroundColor Cyan
            $hubs | Stop-Process -Force -ErrorAction SilentlyContinue
        }
        Write-Host ""
        Write-Host "  Hot apply done: close/reopen the Inferpal tool window in VS to load the new build." -ForegroundColor Green
        Write-Host "  (Ghost-text changes still need a full VS restart: re-run with -Launch.)" -ForegroundColor Gray
    } elseif ($ok -and -not $Launch) {
        Write-Host ""
        Write-Host "  [IMPORTANT] VS must be restarted to apply the changes:" -ForegroundColor Yellow
        Write-Host "  privateregistry.bin (Packages/AutoLoadPackages/MEFComponent) is read at startup." -ForegroundColor Yellow
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
