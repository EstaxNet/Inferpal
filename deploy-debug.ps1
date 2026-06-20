#Requires -Version 5.1
<#
.SYNOPSIS
    Construit Inferpal et deploie le DLL dans l'instance experimentale VS.
.PARAMETER Launch
    Ferme VS Exp s'il tourne, vide les caches mpack, deploie, puis relance VS Exp.
.EXAMPLE
    .\deploy-debug.ps1          # build + copie DLL (VS Exp doit etre en cours ou repouvrir manuellement)
    .\deploy-debug.ps1 -Launch  # build + reinstall complet + relance VS Exp
#>
param(
    [switch]$Launch,
    [switch]$Exp      # accepte -Exp pour compatibilite, comportement identique a defaut
)

$ErrorActionPreference = "Stop"

# -- Chemins ------------------------------------------------------------------
# Detection via vswhere (l'edition/le canal peut changer : Professional, Enterprise, Insiders...)
$VsWhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$VsPath  = $null
if (Test-Path $VsWhere) {
    $VsPath = & $VsWhere -all -prerelease -products * -version "[18.0,19.0)" `
              -requires Microsoft.Component.MSBuild -property installationPath |
              Select-Object -First 1
}
if (-not $VsPath) {
    # Fallback : scan des emplacements connus de VS 18
    $VsPath = Get-ChildItem "C:\Program Files\Microsoft Visual Studio\18" -Directory -ErrorAction SilentlyContinue |
              Where-Object { Test-Path "$($_.FullName)\MSBuild\Current\Bin\MSBuild.exe" } |
              Select-Object -First 1 -ExpandProperty FullName
}
if (-not $VsPath) {
    Write-Error "Aucune installation VS 18 avec MSBuild trouvee (vswhere + scan de C:\Program Files\Microsoft Visual Studio\18)."
    exit 1
}
$MSBuild  = "$VsPath\MSBuild\Current\Bin\MSBuild.exe"
$DevEnv   = "$VsPath\Common7\IDE\devenv.exe"
Write-Host "   VS detecte : $VsPath" -ForegroundColor Cyan

$Root     = $PSScriptRoot
$Project  = "$Root\Inferpal\Inferpal.csproj"
$BinDir   = "$Root\Inferpal\bin\Debug\net8.0-windows"

# Repertoire d'installation du VSIX dans l'hive Exp (ou VS charge effectivement l'extension)
$VsExpHive = (Get-ChildItem "$env:LOCALAPPDATA\Microsoft\VisualStudio" -Directory -ErrorAction SilentlyContinue |
              Where-Object { $_.Name -match "^18\.0_.*Exp$" } |
              Select-Object -First 1).FullName

if (-not $VsExpHive) {
    Write-Error "Aucun hive VS 18 Exp trouve dans $env:LOCALAPPDATA\Microsoft\VisualStudio"
    exit 1
}

# Auto-detection : cherche Inferpal.dll dans :
#  1. Program Files (install system-wide — recommande, necessite admin)
#  2. AppData hive Exp (install user-only, fallback)
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
        Write-Host "   Extension trouvee dans Program Files (install systeme)" -ForegroundColor Cyan
    } else {
        Write-Host "   AVERTISSEMENT : extension dans AppData (install user-only)." -ForegroundColor Yellow
        Write-Host "   Les libelles de menus seront casses (extensionDir ne resolve pas)." -ForegroundColor Yellow
        Write-Host "   Reinstallez le VSIX sur l'instance VS principale pour corriger." -ForegroundColor Yellow
    }
}

if (-not $InstalledDir) {
    $VsixPath = "$BinDir\Inferpal.vsix"
    Write-Host ""
    Write-Host "Extension Inferpal non trouvee." -ForegroundColor Yellow
    Write-Host ""
    Write-Host "Etapes pour installer correctement :" -ForegroundColor Cyan
    Write-Host "  1. Fermez Visual Studio 2026 si ouvert." -ForegroundColor White
    if (Test-Path $VsixPath) {
        Write-Host "  2. Double-clic sur : $VsixPath" -ForegroundColor White
    } else {
        Write-Host "  2. Buildez d'abord, puis installez le VSIX de Inferpal\bin\Debug\net8.0-windows\" -ForegroundColor White
    }
    Write-Host "  3. IMPORTANT : selectionnez 'Visual Studio Professional 2026'" -ForegroundColor Yellow
    Write-Host "     (PAS l'instance Experimentale - VS Exp herite des extensions systeme)" -ForegroundColor Yellow
    Write-Host "  4. Acceptez l'elevation admin si demandee." -ForegroundColor White
    Write-Host "  5. Relancez ce script." -ForegroundColor White
    Write-Host ""
    exit 1
}

$ExtDir = "$VsExpHive\Extensions"

# -- Validation prerequis -----------------------------------------------------
if (-not (Test-Path $MSBuild)) {
    Write-Error "MSBuild non trouve : $MSBuild"
    exit 1
}

# -- 1. Build -----------------------------------------------------------------
Write-Host "`n[1/$(if ($Launch) { 4 } else { 3 })] Build..." -ForegroundColor Cyan
& $MSBuild $Project /p:Configuration=Debug /v:minimal /nologo
if ($LASTEXITCODE -ne 0) { Write-Error "Build echoue."; exit 1 }

if (-not (Test-Path "$BinDir\Inferpal.dll")) {
    Write-Error "Inferpal.dll non trouve dans $BinDir"
    exit 1
}
Write-Host "   Build OK" -ForegroundColor Green

# -- 2. Fermer VS Exp si -Launch ----------------------------------------------
if ($Launch) {
    $vsProcs = Get-Process -Name "devenv" -ErrorAction SilentlyContinue
    if ($vsProcs) {
        Write-Host "`n[2/4] Fermeture de VS Exp..." -ForegroundColor Cyan
        $vsProcs | ForEach-Object {
            Write-Host "   Arret PID $($_.Id) : $($_.MainWindowTitle)" -ForegroundColor Gray
            $_.CloseMainWindow() | Out-Null
        }
        $vsProcs | ForEach-Object {
            if (-not $_.WaitForExit(10000)) {
                $_.Kill()
                Write-Host "   Force kill PID $($_.Id)" -ForegroundColor Yellow
            }
        }
        Write-Host "   VS Exp ferme." -ForegroundColor Green
    }
}

# -- 3. Deploiement : copie DLL + PDB dans le repertoire installe -------------
$step = if ($Launch) { "3/4" } else { "2/3" }
Write-Host "`n[$step] Deploiement vers $InstalledDir..." -ForegroundColor Cyan

Copy-Item "$BinDir\Inferpal.dll" "$InstalledDir\" -Force
Write-Host "   Inferpal.dll  : OK" -ForegroundColor Green

if (Test-Path "$BinDir\Inferpal.pdb") {
    Copy-Item "$BinDir\Inferpal.pdb" "$InstalledDir\" -Force
    Write-Host "   Inferpal.pdb  : OK (symboles debug)" -ForegroundColor Green
}

# ── Dependances tierces + assets natifs — SYNC depuis le VSIX ────────────────
# GARDE : auparavant ce script ne poussait QUE Inferpal.dll, en supposant les
# dependances deja presentes via une install VSIX complete anterieure. Quand une
# NOUVELLE dependance est ajoutee au projet (ex. Microsoft.Data.Sqlite +
# SQLitePCLRaw + le natif e_sqlite3 pour l'index RAG), elle n'arrivait jamais
# dans l'install → l'extension plantait avec "Could not load file or assembly".
#
# On synchronise donc les DLLs de dependance + le dossier runtimes/ DEPUIS le VSIX
# fraichement builde : c'est EXACTEMENT le jeu qu'une install complete deploierait
# (les assemblies fournies par VS — Shell.15.0, Text.UI.Wpf… — en sont deja exclues
# par le packaging, donc on ne risque pas d'ecraser une version fournie par l'IDE).
$VsixForDeps = "$BinDir\Inferpal.vsix"
if (Test-Path $VsixForDeps) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [System.IO.Compression.ZipFile]::OpenRead($VsixForDeps)
    try {
        $depCount = 0
        foreach ($entry in $zip.Entries) {
            $name = $entry.FullName
            # Ignore les dossiers et Inferpal.dll (deja copie depuis bin ci-dessus)
            if ($name.EndsWith('/') -or $name -eq 'Inferpal.dll') { continue }

            # On ne synchronise que : DLLs de dependance a la racine + tout runtimes/**
            # (assets natifs par RID, ex. runtimes/win-x64/native/e_sqlite3.dll).
            $isTopLevelDll  = ($name -notmatch '/') -and $name.EndsWith('.dll')
            $isRuntimeAsset = $name.StartsWith('runtimes/')
            if (-not ($isTopLevelDll -or $isRuntimeAsset)) { continue }

            $dest    = Join-Path $InstalledDir ($name -replace '/', '\')
            $destDir = Split-Path $dest -Parent
            if (-not (Test-Path $destDir)) { New-Item -ItemType Directory -Path $destDir -Force | Out-Null }
            [System.IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $dest, $true)
            $depCount++
        }
        Write-Host "   Dependances + runtimes synchronisees (VSIX) : $depCount fichiers" -ForegroundColor Green
    } finally {
        $zip.Dispose()
    }
} else {
    Write-Host "   AVERTISSEMENT : VSIX introuvable, dependances NON synchronisees" -ForegroundColor Yellow
    Write-Host "     ($VsixForDeps)" -ForegroundColor Yellow
    Write-Host "     Une nouvelle dependance NuGet pourrait manquer a l'execution." -ForegroundColor Yellow
}

# Assemblies satellites {locale}/Inferpal.resources.dll — contiennent les traductions runtime
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

# .vsextension/ : string-resources.json (default) + sous-répertoires {locale}/string-resources.json
# VS résout les %tokens% depuis .vsextension/{locale}/string-resources.json
$VsExtDst = "$InstalledDir\.vsextension"
if (-not (Test-Path $VsExtDst)) { New-Item -ItemType Directory -Path $VsExtDst | Out-Null }

# Copie le fichier par défaut (EN)
$SrcDefault = "$BinDir\.vsextension\string-resources.json"
if (Test-Path $SrcDefault) {
    Copy-Item $SrcDefault "$VsExtDst\" -Force
    Write-Host "   string-resources.json : OK (.vsextension/)" -ForegroundColor Green
}

# Copie les sous-répertoires de locale (fr/, de/, es/, it/, ru/, ja/, ko/, pl/, zh-CN/)
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

# manifest.json : corrige extensionDir qui pointe vers [installdir]\...\VSExtensions\<random>
# (le VSIX installer ecrit un chemin invalide pour les installs user-only)
$ManifestPath = "$InstalledDir\manifest.json"
if (Test-Path $ManifestPath) {
    $mj = Get-Content $ManifestPath -Raw | ConvertFrom-Json
    if ($mj.extensionDir -ne $InstalledDir) {
        $mj.extensionDir = $InstalledDir
        [System.IO.File]::WriteAllText($ManifestPath, ($mj | ConvertTo-Json -Depth 10 -Compress), [System.Text.Encoding]::UTF8)
        Write-Host "   manifest.json    : extensionDir corrige" -ForegroundColor Green
    } else {
        Write-Host "   manifest.json    : OK (deja correct)" -ForegroundColor Green
    }
}

# Copie le pkgdef mis à jour (contient [$RootKey$\MEFComponent])
if (Test-Path "$BinDir\Inferpal.pkgdef") {
    Copy-Item "$BinDir\Inferpal.pkgdef" "$InstalledDir\" -Force
    Write-Host "   Inferpal.pkgdef : OK" -ForegroundColor Green
}

# ── Enregistrement GhostTextPackage dans HKCU ─────────────────────────────────
#
# VS lit les clés Packages / AutoLoadPackages / MEFComponent depuis deux sources :
#   1. privateregistry.bin (hive privé, VERROUILLÉ quand VS tourne → inutilisable en live)
#   2. HKCU\Software\Microsoft\VisualStudio\{ver}\...  (registre Windows standard)
#
# MEFComponent est déjà dans HKCU (écrit lors de l'install VSIX).
# Packages et AutoLoadPackages doivent y être aussi pour que GhostTextPackage se charge.
#
# On écrit dans HKCU directement — fonctionne VS ouvert ou fermé.
# Les changements prennent effet au prochain redémarrage de VS.

$pkgGuid      = "{6a7b2c3d-4e5f-4a8b-9c0d-1e2f3a4b5c6d}"  # GhostTextPackage
$ctxSolExists = "{adfc4e64-0397-11d1-9f4e-00a0c911004f}"   # SolutionExists (AutoLoad)
$ctxNoSol     = "{f1536ef8-92ec-443c-9ed7-fdadf150da82}"   # NoSolution     (AutoLoad)

$vsHkuBases = Get-ChildItem "HKCU:\Software\Microsoft\VisualStudio" -ErrorAction SilentlyContinue |
              Where-Object { $_.Name -match "18\." } |
              Select-Object -ExpandProperty PSPath

foreach ($hiveBase in $vsHkuBases) {
    $shortName = Split-Path $hiveBase -Leaf
    try {
        # 1. MEFComponent (idempotent — probablement déjà là depuis l'install VSIX)
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
        Write-Host "   [$shortName] ERREUR enregistrement HKCU : $_" -ForegroundColor Red
    }

    # ── Purge du cache MEF pour forcer la reconstruction ─────────────────────
    $vsHiveDir = "$env:LOCALAPPDATA\Microsoft\VisualStudio\$shortName"
    $mefCache  = "$vsHiveDir\ComponentModelCache"
    if (Test-Path $mefCache) {
        Remove-Item $mefCache -Recurse -Force -ErrorAction SilentlyContinue
        Write-Host "   [$shortName] ComponentModelCache purgé : OK" -ForegroundColor Green
    }

    # ── Mise à jour de privateregistry.bin si VS est fermé (double sécurité) ─
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
                Write-Host "   [$shortName] privateregistry.bin mis à jour : OK" -ForegroundColor Green
            }
        } catch { }
        finally {
            if ($loadedOk) { & reg unload $hiveHkcu 2>&1 | Out-Null }
        }
    }
}

# Suppression des caches mpack (Exp hive + hive principal VS)
Get-ChildItem $ExtDir -Filter "*.mpack" -ErrorAction SilentlyContinue |
    ForEach-Object { Remove-Item $_.FullName -Force; Write-Host "   Cache supprime (Exp) : $($_.Name)" -ForegroundColor Gray }

$vsMainHive = (Get-ChildItem "$env:LOCALAPPDATA\Microsoft\VisualStudio" -Directory -ErrorAction SilentlyContinue |
               Where-Object { $_.Name -match "^18\.0" -and $_.Name -notmatch "Exp$" } |
               Select-Object -First 1).FullName
if ($vsMainHive) {
    Get-ChildItem "$vsMainHive\Extensions" -Filter "*.mpack" -ErrorAction SilentlyContinue |
        ForEach-Object { Remove-Item $_.FullName -Force; Write-Host "   Cache supprime (Pro) : $($_.Name)" -ForegroundColor Gray }
}

Write-Host "   Deploiement complete." -ForegroundColor Green

# -- 4. Optionnel : relancer VS Professional (instance principale) ------------
if ($Launch) {
    Write-Host "`n[4/4] Relancement de Visual Studio Professional 2026..." -ForegroundColor Cyan
    Start-Process $DevEnv   # Sans /rootsuffix Exp — instance principale = là où le VSIX est installé
    Write-Host "   VS Professional redemarre." -ForegroundColor Green
    Write-Host "   → Ouvrez un fichier .cs, faites clic-droit > Ask Inferpal > Modifier avec l'IA" -ForegroundColor Cyan
}

Write-Host "`n[IMPORTANT] VS doit etre redemarree pour appliquer les changements :" -ForegroundColor Yellow
Write-Host "   privateregistry.bin (Packages/AutoLoadPackages/MEFComponent) est lu au demarrage." -ForegroundColor Yellow

Write-Host "`n[3/3] Pour debugger :" -ForegroundColor Cyan
Write-Host "   1. Dans VS (apres redemarrage), clic droit dans l'editeur > Ask Inferpal" -ForegroundColor White
Write-Host "   2. Ou Outils > Inferpal > Inferpal Chat" -ForegroundColor White
Write-Host "   3. Pour attacher le debugger : Debug > Attach to Process > ServiceHub.Host.dotnet.exe" -ForegroundColor White
Write-Host "`nTermine." -ForegroundColor Green
