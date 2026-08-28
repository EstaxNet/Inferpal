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
# Truncated on purpose: this repo writes long, explanatory commit subjects (by convention), and
# printing one whole made the status page unreadable -- the useful lines scrolled out of sight.
$lastLog = (git -C $Root log -1 --format='%h %s' 2>$null)
if ($lastLog.Length -gt 96) { $lastLog = $lastLog.Substring(0, 93) + '...' }
Line 'Version'  $version 'Cyan'
Line 'Branch'   ("$branch" + $(if ($dirty -gt 0) { "  ($dirty modified files)" } else { '  (clean)' })) $(if ($dirty -gt 0) { 'Yellow' } else { 'Green' })
Line 'Last commit' $lastLog

# ── Visual Studio 2026 ───────────────────────────────────────────────────────
Write-Host "`n  [Visual Studio 2026]" -ForegroundColor DarkCyan
$vsDll = $null
$vsWhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
if (Test-Path $vsWhere) {
    # ⚠ No "| Select-Object -First 1" here: -First stops the pipeline as soon as it has its
    # line, PowerShell then terminates the native command, and $LASTEXITCODE is -1 more often
    # than not. Capture everything, then index.
    $vsPath = @(& $vsWhere -all -prerelease -products * -version '[18.0,19.0)' -property installationPath 2>$null)[0]
    if ($vsPath) {
        # Deux racines possibles : Extensions\ (modele classique, celle ou VS traite pkgdef +
        # catalogue MEF, donc l'in-proc) et VSExtensions\ (out-of-proc pur). Apres la bascule
        # with in-process hosting an old install can linger in the other root: taking whichever
        # comes first would show the version of a folder VS no longer loads.
        $vsDlls = @(Get-ChildItem "$vsPath\Common7\IDE" -Recurse -Filter 'Inferpal.dll' -ErrorAction SilentlyContinue)
        $vsDll  = @($vsDlls | Where-Object { $_.DirectoryName -like '*\Common7\IDE\Extensions\*' } |
                   Select-Object -First 1)[0]
        if (-not $vsDll) { $vsDll = $vsDlls | Select-Object -First 1 }
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
    if ($vsDlls -and $vsDlls.Count -gt 1) {
        Line 'Duplicate' "$($vsDlls.Count) installs under Common7\IDE - the others are dead weight" 'Yellow'
        foreach ($d in ($vsDlls | Where-Object { $_.DirectoryName -ne $vsDll.DirectoryName })) {
            Line ''      "stale: $($d.DirectoryName)" 'DarkGray'
        }
    }
    # Stale when any tracked source is newer than the deployed DLL.
    # obj\ and bin\ are excluded on purpose: they hold GENERATED .cs and .json rewritten by
    # every build (resources.cs, AssemblyInfo, project.assets.json). Counting them made this
    # line cry STALE after any compilation, including the one that had just deployed.
    $srcNewest = [DateTime]::MinValue
    foreach ($r in @("$Root\Inferpal", "$Root\Inferpal.Core")) {
        Get-ChildItem $r -Recurse -File -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -notmatch '\\(obj|bin)\\' -and
                           @('.cs', '.csproj', '.resx', '.json', '.props', '.xaml') -contains $_.Extension } |
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

# In-process leg (ghost text, inline edit, /tdd debugger driver). When it dies it dies SILENTLY:
# the chat window keeps working (it is out-of-process), so nothing looks broken.
#
# WARNING -- measured 2026-08-25. This line used to count devenv processes holding a module named
# Inferpal*, and that answer was ALWAYS zero, whatever the truth. Process.Modules reads
# EnumProcessModules, which only lists what the Windows loader mapped: native DLLs and NGen images.
# An IL-only assembly is mapped by the CLR via MapViewOfFile and never appears there. Since
# Inferpal.InProc.dll ships through VSIXInstaller it is not NGen'd, so it cannot show up -- a red
# no repair could ever clear, on a devenv whose ActivityLog said "Begin/End package load [Inferpal
# GhostText]".
#
# The witness is the signal file instead: active_solution.<devenvPid>.json. Only
# Inferpal.InProc\GhostText\VsSolutionTracker writes that channel, and the key in the name is the
# writing process's own PID (GhostTextPackage declares SignalFile.CurrentPid, because it IS
# devenv). The out-of-process host declares its PARENT pid and only ever reads it. So the file
# names the devenv our code actually ran in. Detail and the fallback proof (image lock):
# docs\probes\inproc-verify.ps1.
$devenvs = @(Get-Process devenv -ErrorAction SilentlyContinue)
if ($devenvs.Count -eq 0) {
    Line 'In-proc' 'no devenv running - start VS to check' 'DarkGray'
} else {
    $withInProc = @($devenvs | Where-Object {
        $sig = Join-Path $env:TEMP "Inferpal\active_solution.$($_.Id).json"
        if (-not (Test-Path $sig)) { return $false }
        # A leftover from a dead devenv that happened to reuse this pid would read as green.
        try { (Get-Item $sig).LastWriteTime -ge $_.StartTime } catch { $true }
    })
    if ($withInProc.Count -eq $devenvs.Count) {
        Line 'In-proc' "loaded in $($devenvs.Count)/$($devenvs.Count) devenv (ghost text, inline diff)" 'Green'
    } elseif ($withInProc.Count -gt 0) {
        Line 'In-proc' "loaded in only $($withInProc.Count)/$($devenvs.Count) devenv - the others predate the deploy" 'Yellow'
    } else {
        Line 'In-proc' "no devenv published a solution signal - ghost text and inline diff look dead" 'Red'
        # One benign way to land here: VS open with NO solution. VsSolutionTracker clears the
        # signal on solution close, so the absence is real but means nothing. The probe knows the
        # difference -- it falls back to the image lock -- and the ActivityLog says why.
        Line ''         'VS open with no solution clears that signal: confirm before reinstalling' 'Yellow'
        Line ''         '.\docs\probes\inproc-verify.ps1   then   .\docs\probes\activitylog.ps1' 'DarkGray'
        # The old repair gesture ("deploy-dev -Launch") repairs NOTHING: measured, registry hive
        # keys do not enter the MEF catalog of VS 18. What
        # inventorie l'in-proc, c'est l'install du VSIX hybride (assets MefComponent/VsPackage).
        Line ''         'if genuinely dead: close all VS, then install the VSIX (elevation prompt):' 'Yellow'
        Line ''         'VSIXInstaller.exe /instanceIds:<id> Inferpal\bin\Debug\net8.0-windows\Inferpal.vsix' 'DarkGray'
        Line ''         'instance id: vswhere.exe -prerelease -products * -property instanceId' 'DarkGray'
    }

    # The /tdd debugger driver is a THIRD door and it needs its own line. Measured 2026-08-27 on
    # devenv 49388: package loaded, active_solution written, and NO debug_ready -- so /tdd ran its
    # plain red loop with no §25 capture while this section said "/tdd debugger" was fine. The
    # package hosts the driver; it does not prove it. debug_ready.<pid>.json is what the product
    # itself reads (DebugCommandSignal.IsDriverReady), and the reason it is missing is written by
    # the package into inproc_alive.<pid>.json.
    $withDriver = @($devenvs | Where-Object {
        Test-Path (Join-Path $env:TEMP "Inferpal\debug_ready.$($_.Id).json")
    })
    if ($withDriver.Count -eq $devenvs.Count -and $devenvs.Count -gt 0) {
        Line 'Debugger' "/tdd driver serving in $($devenvs.Count)/$($devenvs.Count) devenv" 'Green'
    } elseif ($withInProc.Count -eq 0) {
        Line 'Debugger' 'not advertised - the in-proc half is dead anyway, fix that first' 'DarkGray'
    } else {
        Line 'Debugger' "/tdd driver in only $($withDriver.Count)/$($devenvs.Count) devenv - /tdd degrades to its plain loop" 'Red'
        # The reason, when the build is recent enough to record it.
        foreach ($d in $devenvs) {
            if (Test-Path (Join-Path $env:TEMP "Inferpal\debug_ready.$($d.Id).json")) { continue }
            $why = $null
            try {
                $alive = Join-Path $env:TEMP "Inferpal\inproc_alive.$($d.Id).json"
                if (Test-Path $alive) { $why = (Get-Content $alive -Raw | ConvertFrom-Json).debuggerReason }
            } catch { }
            if ($why) { Line '' "pid $($d.Id): $why" 'Yellow' }
            else      { Line '' "pid $($d.Id): no reason recorded (VSIX older than 2026-08-27?)" 'DarkGray' }
        }
    }
}

# ── Local build artifacts ────────────────────────────────────────────────────
Write-Host "`n  [Local builds]" -ForegroundColor DarkCyan
# A dist folder only counts if it actually holds a deliverable: dist\ also serves as a
# scratch area (dist\wsl-validation\), and alphabetical sorting used to let it come
# devant les vraies versions - "1.10.0" passerait d'ailleurs derriere "1.9.0".
$dist = Get-ChildItem "$Root\dist" -Directory -ErrorAction SilentlyContinue |
        Where-Object { Get-ChildItem $_.FullName -Filter '*.vsix' -File -ErrorAction SilentlyContinue } |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
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
        # PowerShell 5.1 does not load System.Net.Http by default: without Add-Type, New-Object
        # fails and the catch printed a false "Reachable NO" on a backend that was reachable.
        Add-Type -AssemblyName System.Net.Http -ErrorAction Stop
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
