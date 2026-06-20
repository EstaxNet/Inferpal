#Requires -Version 5.1
<#
.SYNOPSIS
    Builds Inferpal and deploys the DLL into the VS experimental instance.
.PARAMETER Launch
    Closes VS if running, clears the mpack caches, deploys, then relaunches VS.
.EXAMPLE
    .\deploy-debug.ps1          # build + copy DLL (VS must be running, or reopen it manually)
    .\deploy-debug.ps1 -Launch  # build + full redeploy + relaunch VS
#>
param(
    [switch]$Launch,
    [switch]$Exp      # accepted for compatibility; behaves the same as the default
)

$ErrorActionPreference = "Stop"

# -- Paths --------------------------------------------------------------------
# Detected via vswhere (edition/channel may vary: Community, Professional, Enterprise, Preview...)
$VsWhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$VsPath  = $null
if (Test-Path $VsWhere) {
    $VsPath = & $VsWhere -all -prerelease -products * -version "[18.0,19.0)" `
              -requires Microsoft.Component.MSBuild -property installationPath |
              Select-Object -First 1
}
if (-not $VsPath) {
    # Fallback: scan the well-known VS 18 install locations
    $VsPath = Get-ChildItem "C:\Program Files\Microsoft Visual Studio\18" -Directory -ErrorAction SilentlyContinue |
              Where-Object { Test-Path "$($_.FullName)\MSBuild\Current\Bin\MSBuild.exe" } |
              Select-Object -First 1 -ExpandProperty FullName
}
if (-not $VsPath) {
    Write-Error "No VS 18 installation with MSBuild found (vswhere + scan of C:\Program Files\Microsoft Visual Studio\18)."
    exit 1
}
$MSBuild  = "$VsPath\MSBuild\Current\Bin\MSBuild.exe"
$DevEnv   = "$VsPath\Common7\IDE\devenv.exe"
Write-Host "   VS detected: $VsPath" -ForegroundColor Cyan

$Root     = $PSScriptRoot
$Project  = "$Root\Inferpal\Inferpal.csproj"
$BinDir   = "$Root\Inferpal\bin\Debug\net8.0-windows"

# VSIX install directory in the Exp hive (where VS actually loads the extension from)
$VsExpHive = (Get-ChildItem "$env:LOCALAPPDATA\Microsoft\VisualStudio" -Directory -ErrorAction SilentlyContinue |
              Where-Object { $_.Name -match "^18\.0_.*Exp$" } |
              Select-Object -First 1).FullName

if (-not $VsExpHive) {
    Write-Error "No VS 18 Exp hive found under $env:LOCALAPPDATA\Microsoft\VisualStudio"
    exit 1
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
    exit 1
}

$ExtDir = "$VsExpHive\Extensions"

# -- Prerequisite validation --------------------------------------------------
if (-not (Test-Path $MSBuild)) {
    Write-Error "MSBuild not found: $MSBuild"
    exit 1
}

# -- 1. Build -----------------------------------------------------------------
Write-Host "`n[1/$(if ($Launch) { 4 } else { 3 })] Build..." -ForegroundColor Cyan
& $MSBuild $Project /p:Configuration=Debug /v:minimal /nologo
if ($LASTEXITCODE -ne 0) { Write-Error "Build failed."; exit 1 }

if (-not (Test-Path "$BinDir\Inferpal.dll")) {
    Write-Error "Inferpal.dll not found in $BinDir"
    exit 1
}
Write-Host "   Build OK" -ForegroundColor Green

# -- 2. Close VS Exp when -Launch ---------------------------------------------
if ($Launch) {
    $vsProcs = Get-Process -Name "devenv" -ErrorAction SilentlyContinue
    if ($vsProcs) {
        Write-Host "`n[2/4] Closing VS Exp..." -ForegroundColor Cyan
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
        Write-Host "   VS Exp closed." -ForegroundColor Green
    }
}

# -- 3. Deploy: copy DLL + PDB into the installed directory -------------------
$step = if ($Launch) { "3/4" } else { "2/3" }
Write-Host "`n[$step] Deploying to $InstalledDir..." -ForegroundColor Cyan

Copy-Item "$BinDir\Inferpal.dll" "$InstalledDir\" -Force
Write-Host "   Inferpal.dll  : OK" -ForegroundColor Green

if (Test-Path "$BinDir\Inferpal.pdb") {
    Copy-Item "$BinDir\Inferpal.pdb" "$InstalledDir\" -Force
    Write-Host "   Inferpal.pdb  : OK (debug symbols)" -ForegroundColor Green
}

# -- Third-party dependencies + native assets -- SYNC from the VSIX -----------
# GUARD: this script used to push ONLY Inferpal.dll, assuming dependencies were
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
            [System.IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $dest, $true)
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

# Satellite assemblies {locale}/Inferpal.resources.dll -- hold the runtime translations
Get-ChildItem $BinDir -Directory -ErrorAction SilentlyContinue |
    ForEach-Object {
        $locale = $_.Name
        $srcSat = "$BinDir\$locale\Inferpal.resources.dll"
        if (Test-Path $srcSat) {
            $dstSat = "$InstalledDir\$locale"
            if (-not (Test-Path $dstSat)) { New-Item -ItemType Directory -Path $dstSat | Out-Null }
            Copy-Item $srcSat "$dstSat\" -Force
            Write-Host "   $locale\Inferpal.resources.dll : OK" -ForegroundColor Green
        }
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

# -- Register GhostTextPackage in HKCU ----------------------------------------
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

    # -- Purge the MEF cache to force a rebuild --------------------------------
    $vsHiveDir = "$env:LOCALAPPDATA\Microsoft\VisualStudio\$shortName"
    $mefCache  = "$vsHiveDir\ComponentModelCache"
    if (Test-Path $mefCache) {
        Remove-Item $mefCache -Recurse -Force -ErrorAction SilentlyContinue
        Write-Host "   [$shortName] ComponentModelCache purged: OK" -ForegroundColor Green
    }

    # -- Update privateregistry.bin when VS is closed (belt and braces) -------
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

# -- 4. Optional: relaunch the main VS instance -------------------------------
if ($Launch) {
    Write-Host "`n[4/4] Relaunching Visual Studio 2026..." -ForegroundColor Cyan
    Start-Process $DevEnv   # No /rootsuffix Exp -- the main instance is where the VSIX is installed
    Write-Host "   VS restarted." -ForegroundColor Green
    Write-Host "   -> Open a .cs file, right-click > Inferpal > Edit with AI..." -ForegroundColor Cyan
}

Write-Host "`n[IMPORTANT] VS must be restarted to apply the changes:" -ForegroundColor Yellow
Write-Host "   privateregistry.bin (Packages/AutoLoadPackages/MEFComponent) is read at startup." -ForegroundColor Yellow

Write-Host "`n[3/3] To debug:" -ForegroundColor Cyan
Write-Host "   1. In VS (after restart), right-click in the editor > Inferpal" -ForegroundColor White
Write-Host "   2. Or Tools > Inferpal" -ForegroundColor White
Write-Host "   3. To attach the debugger: Debug > Attach to Process > ServiceHub.Host.dotnet.exe" -ForegroundColor White
Write-Host "`nDone." -ForegroundColor Green
