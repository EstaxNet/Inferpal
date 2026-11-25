using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Inferpal.Services.Documents;

/// <summary>One step the model asked to play in an app's window (<c>interact_window</c>): its control is an AutomationId or
/// accessible id, or a name as the tree shows it.</summary>
internal sealed record WindowStep(string Action, string Target, string? Text = null, string? Item = null, int TimeoutMs = 5000);

/// <summary>What a step did: done, or why not — said in English, to the model.</summary>
internal sealed record WindowStepResult(bool Done, string Detail);

/// <summary>The steps played, or why none could be — or, with <c>MayHavePlayed</c>, why the steps stopped before their end
/// when the one after the last reported may have run (stopped at the budget, a script that ended without a word).</summary>
internal sealed record WindowActing(IReadOnlyList<WindowStepResult> Steps, string? Refusal, bool MayHavePlayed = false);

/// <summary>
/// Acts in the windows of an app the agent started (pari 126) — a background job of <c>run_command</c> and the processes it
/// started, the same windows <see cref="WindowCapture"/> looks at — through the system's accessibility, never the person's
/// mouse or keyboard: Windows UI Automation (Value, Invoke, SelectionItem, ExpandCollapse, Toggle), Linux AT-SPI (editable
/// text, actions, selection), macOS Accessibility through System Events (AXPress, values).
/// </summary>
/// <remarks>
/// <para>
/// ⚠ Never a key sent to the system, never the focus taken: what a person types goes to the window they are in.
/// </para>
/// <para>
/// ⚠ A file dialog is never searched: only its window itself (its title) can be waited for, so no step tells which names the
/// person's folders hold; and while one is open, no step acts — the app waits on it, and what a step would reach behind it a
/// person could not.
/// </para>
/// <para>
/// A control is matched by its id, then its name, case aside; a part of its name only for <c>wait_for</c>, which acts on
/// nothing — a click meant for "Delete" never lands on "Delete all". Each result names the control it reached. The steps stop
/// at the first one not done: the next ones were written for a state the window is not in.
/// </para>
/// </remarks>
internal static class WindowActions
{
    /// <summary>The actions a step may take.</summary>
    internal static readonly string[] Actions = ["type", "select", "click", "toggle", "wait_for"];

    /// <summary>Plays <paramref name="steps"/> in the windows of <paramref name="rootPid"/>'s process tree, waiting up to
    /// <paramref name="wait"/> for a first window; <paramref name="onStep"/> hears each step (its number from 1) as it ends.</summary>
    internal static async Task<WindowActing> PlayAsync(int rootPid, IReadOnlyList<WindowStep> steps, TimeSpan wait, CancellationToken ct,
                                                       Action<int, WindowStepResult>? onStep = null)
    {
        var playing = 0;
        void Line(string line)
        {
            switch (Classify(line))
            {
                case (Kind.Play, var n, _): playing = n; break;
                case (Kind.Step, _, { } step) when playing > 0: onStep?.Invoke(playing, step); break;
            }
        }
        var dir = Directory.CreateTempSubdirectory("inferpal-act-").FullName;
        try
        {
            var stepsFile = Path.Combine(dir, "steps.json");
            await File.WriteAllTextAsync(stepsFile, JsonSerializer.Serialize(steps.Select(s => new
            {
                action = s.Action, target = s.Target, text = s.Text, item = s.Item, timeout_ms = s.TimeoutMs,
            })), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), ct);
            var root = rootPid.ToString(CultureInfo.InvariantCulture);
            var waitSeconds = ((int)wait.TotalSeconds).ToString(CultureInfo.InvariantCulture);
            var budget = wait + Budget(steps);
            ChildProcessResult run;
            if (OperatingSystem.IsWindows())
            {
                run = await ChildProcess.RunAsync(ScriptProcess.WindowsPowerShell(dir, "act.ps1", WindowsScript,
                    "-RootPid", root, "-StepsFile", stepsFile, "-WaitSeconds", waitSeconds), budget, ct, Line);
            }
            else if (OperatingSystem.IsLinux())
            {
                if (Shell.ShellLauncher.FindOnPath("python3") is not { } python)
                    return new([], "python3 is not installed: the window's controls cannot be reached (python3, python3-gi, gir1.2-atspi-2.0)");
                var script = Path.Combine(dir, "act.py");
                await File.WriteAllTextAsync(script, LinuxScript, ct);
                run = await ChildProcess.RunAsync(ScriptProcess.Start(python, script, root, stepsFile, waitSeconds), budget, ct, Line);
            }
            else if (OperatingSystem.IsMacOS())
            {
                var script = Path.Combine(dir, "act.js");
                await File.WriteAllTextAsync(script, MacScript, ct);
                run = await ChildProcess.RunAsync(ScriptProcess.Start("/usr/bin/osascript", "-l", "JavaScript", script, root, stepsFile, waitSeconds),
                                                  budget, ct, Line);
            }
            else return new([], "this system has no way to act in a window");
            return Read(run);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return new([], "a program this needs could not start: " + ex.Message);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* temporary folder: best-effort cleanup */ }
        }
    }

    /// <summary>The time the steps get beyond the first window: each wait it asks for, and for every step the search of its
    /// control (a large tree, read again at each step), the second it may wait for it to appear and the three seconds a
    /// call that does not return is given.</summary>
    internal static TimeSpan Budget(IReadOnlyList<WindowStep> steps) =>
        TimeSpan.FromSeconds(15 + 6 * steps.Count) + TimeSpan.FromMilliseconds(steps.Where(s => s.Action == "wait_for").Sum(s => s.TimeoutMs));

    /// <summary>The scripts' report, a line as it happens: <c>PLAY n</c> as a step begins, <c>STEP {"ok","detail"}</c> as it
    /// ends, <c>END</c> after the last; <c>REFUSED &lt;why&gt;</c> when the system or the app keeps the steps from playing.
    /// A report with no end: the steps stopped — and a step that began and did not end may have run.</summary>
    internal static WindowActing Read(ChildProcessResult run)
    {
        var steps = new List<WindowStepResult>();
        var ended = false;
        var begun = 0;
        string? refused = null;
        foreach (var line in run.Stdout.Split('\n'))
        {
            switch (Classify(line))
            {
                case (Kind.Refused, _, _): refused ??= line.Trim()["REFUSED ".Length..]; break;
                case (Kind.End, _, _): ended = true; break;
                case (Kind.Play, _, _): begun++; break;
                case (Kind.Step, _, { } step): steps.Add(step); break;
            }
        }
        if (refused is not null) return new(steps, refused);
        if (ended) return new(steps, null);
        var said = run.Stderr.Trim();
        var why = run.TimedOut ? "the steps did not finish in time and were stopped"
                : "the steps stopped before their end" + (said.Length > 0 ? ": " + SafeTruncate.Truncate(said, 400) : "");
        return new(steps, why, MayHavePlayed: begun > steps.Count);
    }

    private enum Kind { None, Play, Step, End, Refused }

    /// <summary>One line of a script's report, as both of its readers (the live one, the final one) read it.</summary>
    private static (Kind Kind, int Play, WindowStepResult? Step) Classify(string raw)
    {
        var line = raw.Trim();
        if (line == "END") return (Kind.End, 0, null);
        if (line.StartsWith("REFUSED ", StringComparison.Ordinal)) return (Kind.Refused, 0, null);
        if (line.StartsWith("PLAY ", StringComparison.Ordinal) && int.TryParse(line["PLAY ".Length..], NumberStyles.None, CultureInfo.InvariantCulture, out var n))
            return (Kind.Play, n, null);
        if (line.StartsWith("STEP ", StringComparison.Ordinal) && StepOf(line["STEP ".Length..]) is { } step) return (Kind.Step, 0, step);
        return (Kind.None, 0, null);
    }

    private static WindowStepResult? StepOf(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            return r.ValueKind == JsonValueKind.Object && r.TryGetProperty("ok", out var ok) && ok.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? new(ok.GetBoolean(), r.TryGetProperty("detail", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() ?? "" : "")
                : null;
        }
        catch (JsonException) { return null; }
    }

    // ── Windows ──

    /// <summary>Windows: UI Automation patterns on the controls of the tree's windows (<see cref="WindowCapture.WindowsSetup"/>:
    /// the windows, their process tree, the file dialogs among them, the window each control belongs to).</summary>
    private const string WindowsScript =
        """
        param([int]$RootPid, [string]$StepsFile, [int]$WaitSeconds = 20)

        """ + WindowCapture.WindowsSetup + """
        $A = [System.Windows.Automation.AutomationElement]
        $Scope = [System.Windows.Automation.TreeScope]::Descendants
        $Any = [System.Windows.Automation.Condition]::TrueCondition
        function Pattern($el, $pattern) { $p = $null; if ($el.TryGetCurrentPattern($pattern, [ref]$p)) { return $p }; return $null }
        # A name as the trees write it: its spaces and line breaks run together.
        function Flat($s) { if ($s) { return (([string]$s) -replace '\s+', ' ').Trim() }; return '' }
        # The control as the trees write it: role "name" #id.
        function Said($el) {
            $c = $el.Current
            $parts = @($c.ControlType.ProgrammaticName -replace '^ControlType\.', '')
            if ($c.Name) { $parts += '"' + (Flat $c.Name) + '"' }
            if ($c.AutomationId) { $parts += '#' + $c.AutomationId }
            return $parts -join ' '
        }
        function Say($line) { [Console]::Out.WriteLine($line); [Console]::Out.Flush() }
        # A field's text said back at most 200 characters long: a step's line stays one a status can show.
        function Short([string]$s) { if ($s.Length -gt 200) { return $s.Substring(0, 200) + '...' }; return $s }
        # The app's windows now, and the file dialogs among them. A window that closes while it is looked at (a splash
        # screen) is passed over.
        function Look {
            $wins = New-Object 'System.Collections.Generic.List[object]'
            $dialogs = New-Object 'System.Collections.Generic.HashSet[long]'
            $titles = @()
            foreach ($h in @([InferpalWin]::Windows((Get-Tree $RootPid)))) {
                try {
                    $w = $A::FromHandle($h)
                    if (Is-FileDialog $w) { [void]$dialogs.Add($h.ToInt64()); $titles += '"' + (Flat $w.Current.Name) + '"' }
                    $wins.Add($w)
                } catch { }
            }
            return @{ wins = $wins; dialogs = $dialogs; titles = $titles }
        }
        # Names and ids read with the elements, in one trip: a property read one by one is a call to the app each.
        $Cache = New-Object System.Windows.Automation.CacheRequest
        $Cache.Add($A::NameProperty)
        $Cache.Add($A::AutomationIdProperty)
        function Elements($w) {
            $on = $Cache.Activate()
            try { return , @($w.FindAll($Scope, $Any)) } finally { $on.Dispose() }
        }
        # ⚠ The cached values of an element read through Elements; the live ones otherwise — an element read without the
        # cache gives empty cached values, never an error.
        function Hit($e, [bool]$cached, [string]$pass, [string]$want, [string]$low) {
            $c = if ($cached) { $e.Cached } else { $e.Current }
            $name = Flat $c.Name
            switch ($pass) {
                'id' { return [bool]($c.AutomationId -and ([string]$c.AutomationId).ToLowerInvariant() -eq $low) }
                'name' { return $name.ToLowerInvariant() -eq $low }
                default { return $name.Contains($want) }
            }
        }
        # The control a step names: its id, else its name (case aside), else — for wait_for only — a part of its name, case
        # kept. A control is judged by the window it belongs to once it matches (Root-Of): the window searched, never another
        # (a dialog it owns, listed under it) — and a file dialog's window alone, never its contents. When none matches, the
        # names that hold the words.
        function Find([string]$target, [bool]$partial, $look) {
            $want = Flat $target
            $low = $want.ToLowerInvariant()
            $searched = @()
            foreach ($w in $look.wins) {
                try {
                    $root = [int64]$w.Current.NativeWindowHandle
                    $dialog = $look.dialogs.Contains($root)
                    $searched += , @{ w = $w; root = $root; elements = $(if ($dialog) { @() } else { Elements $w }) }
                } catch { }
            }
            $passes = if ($partial) { @('id', 'name', 'part') } else { @('id', 'name') }
            foreach ($pass in $passes) {
                foreach ($v in $searched) {
                    try { if (Hit $v.w $false $pass $want $low) { return @{ el = $v.w; near = @() } } } catch { }
                    foreach ($e in $v.elements) {
                        try { if ((Hit $e $true $pass $want $low) -and (Root-Of $e) -eq $v.root) { return @{ el = $e; near = @() } } } catch { }
                    }
                }
            }
            $near = New-Object 'System.Collections.Generic.List[string]'
            if (-not $partial) {
                foreach ($v in $searched) {
                    foreach ($e in $v.elements) {
                        try {
                            $n = Flat $e.Cached.Name
                            if ($n -and $n.ToLowerInvariant().Contains($low) -and -not $near.Contains($n) -and (Root-Of $e) -eq $v.root) { $near.Add($n) }
                        } catch { }
                        if ($near.Count -ge 5) { break }
                    }
                }
            }
            return @{ el = $null; near = @($near) }
        }
        # A pattern's method on a thread of its own, waited for 3 s: a click whose handler opens a modal window (ShowDialog)
        # does not return until that window closes. $true when it returned.
        function Bounded($target, [string]$method, [object[]]$arguments) {
            $ps = [powershell]::Create()
            [void]$ps.AddScript('param($t, $m, $a) [void]$t.$m.Invoke($a)').AddArgument($target).AddArgument($method).AddArgument($arguments)
            $h = $ps.BeginInvoke()
            if (-not $h.AsyncWaitHandle.WaitOne(3000)) { return $false }
            try {
                [void]$ps.EndInvoke($h)
                if ($ps.Streams.Error.Count -gt 0) { throw $ps.Streams.Error[0].Exception }
            } finally { $ps.Dispose() }
            return $true
        }
        $Waiting = '; it has not returned after 3 s: a window it opened may be waiting (look at the windows)'

        $deadline = (Get-Date).AddSeconds($WaitSeconds)
        $look = Look
        while ($look.wins.Count -eq 0 -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 200; $look = Look }
        if ($look.wins.Count -eq 0) { Say "REFUSED the app shows no window after $WaitSeconds s ($(Why-None $RootPid))"; [Environment]::Exit(0) }
        $steps = @()
        # ⚠ PowerShell 5.1 hands a JSON array down a pipeline as ONE object: parsed as an argument, it stays an array.
        $raw = Get-Content -Raw -Encoding UTF8 $StepsFile
        if ($raw.Trim() -ne '[]') { $steps = ConvertFrom-Json -InputObject $raw }
        $n = 0
        foreach ($s in $steps) {
            $n++
            Say "PLAY $n"
            $done = [ordered]@{ ok = $true; detail = '' }
            $target = [string]$s.target
            try {
                $partial = $s.action -eq 'wait_for'
                $look = Look
                $found = Find $target $partial $look
                # A control the step before makes appear: watched for up to 1 s, never a fixed pause.
                $until0 = (Get-Date).AddMilliseconds(1000)
                while (-not $found.el -and -not $partial -and (Get-Date) -lt $until0) { Start-Sleep -Milliseconds 50; $look = Look; $found = Find $target $false $look }
                $el = $found.el
                if ($partial) {
                    $until = (Get-Date).AddMilliseconds([Math]::Min([int]$s.timeout_ms, 10000))
                    while (-not $el -and (Get-Date) -lt $until) { Start-Sleep -Milliseconds 200; $look = Look; $el = (Find $target $true $look).el }
                    if ($el) {
                        $done.detail = "$target is there: $(Said $el)"
                        if ($look.dialogs.Contains([int64]$el.Current.NativeWindowHandle)) { $done.detail += ', a file dialog: its contents are never searched nor acted in' }
                    } else {
                        $done.ok = $false; $done.detail = "$target did not appear"
                        if ($look.titles.Count -gt 0) { $done.detail += ' (a file dialog is open, whose contents are never searched)' }
                    }
                } elseif ($look.titles.Count -gt 0) {
                    $done.ok = $false
                    $done.detail = "a file dialog is open in the app ($($look.titles -join ', ')): no step acts while it is — its contents are the person's folders, and the app waits on it"
                } elseif (-not $el) {
                    $done.ok = $false; $done.detail = "no control has the id or the name $target"
                    if ($found.near.Count -gt 0) { $done.detail += ' (names that hold it: ' + (($found.near | ForEach-Object { '"' + $_ + '"' }) -join ', ') + ')' }
                } elseif (-not $el.Current.IsEnabled) { $done.ok = $false; $done.detail = "$(Said $el) is disabled"
                } elseif (($ow = Root-Of $el) -ne 0 -and -not [InferpalWin]::IsWindowEnabled([IntPtr]$ow)) {
                    $done.ok = $false; $done.detail = "$(Said $el) is in a window the app keeps disabled: a window of its own waits in front of it"
                } elseif ($s.action -eq 'type') {
                    $v = Pattern $el ([System.Windows.Automation.ValuePattern]::Pattern)
                    if (-not $v -or $v.Current.IsReadOnly) { $done.ok = $false; $done.detail = "$(Said $el) does not take text" }
                    elseif (Bounded $v 'SetValue' @([string]$s.text)) {
                        $shown = [string]$v.Current.Value
                        $done.detail = "typed into $(Said $el)" + $(if ($shown -cne [string]$s.text) { ', which now shows "' + (Short (Flat $shown)) + '"' } else { '' })
                    } else { $done.detail = "typed into $(Said $el)" + $Waiting }
                } elseif ($s.action -eq 'select') {
                    $expand = Pattern $el ([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
                    try {
                        if ($expand) { [void](Bounded $expand 'Expand' @()) }
                        $want = (Flat $s.item).ToLowerInvariant()
                        $item = $null; $items = @()
                        # The item, watched for up to 1 s: a list filled by the step before, a drop-down that opens.
                        $until1 = (Get-Date).AddMilliseconds(1000)
                        do {
                            $items = @($el.FindAll($Scope, (New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem))))
                            foreach ($i in $items) { if ((Flat $i.Current.Name).ToLowerInvariant() -eq $want) { $item = $i; break } }
                            if (-not $item) { Start-Sleep -Milliseconds 50 }
                        } while (-not $item -and (Get-Date) -lt $until1)
                        if (-not $item) {
                            $all = @($items | ForEach-Object { Flat $_.Current.Name } | Where-Object { $_ })
                            $names = ($all | Select-Object -First 20) -join ', '
                            if ($all.Count -gt 20) { $names += ", and $($all.Count - 20) more" }
                            $done.ok = $false; $done.detail = "$(Said $el) has no item ""$($s.item)"" (items: $(if ($names) { $names } else { 'none' }))"
                        } else {
                            $sel = Pattern $item ([System.Windows.Automation.SelectionItemPattern]::Pattern)
                            if (-not $sel) { $done.ok = $false; $done.detail = "$(Said $item) cannot be chosen" }
                            elseif (-not (Bounded $sel 'Select' @())) { $done.detail = "chose $(Flat $item.Current.Name) in $(Said $el)" + $Waiting }
                            elseif ($sel.Current.IsSelected) { $done.detail = "chose $(Flat $item.Current.Name) in $(Said $el)" }
                            else { $done.ok = $false; $done.detail = "$(Said $el) did not take ""$($s.item)"": the list does not show it chosen" }
                        }
                    } finally { if ($expand) { try { [void](Bounded $expand 'Collapse' @()) } catch { } } }
                } elseif ($s.action -eq 'click') {
                    $invoke = Pattern $el ([System.Windows.Automation.InvokePattern]::Pattern)
                    $select = Pattern $el ([System.Windows.Automation.SelectionItemPattern]::Pattern)
                    # A click on an item of a list chooses it.
                    if ($invoke) { $done.detail = "clicked $(Said $el)" + $(if (Bounded $invoke 'Invoke' @()) { '' } else { $Waiting }) }
                    elseif ($select) { $done.detail = "chose $(Said $el)" + $(if (Bounded $select 'Select' @()) { '' } else { $Waiting }) }
                    else { $done.ok = $false; $done.detail = "$(Said $el) cannot be clicked: it offers no action to UI Automation" }
                } elseif ($s.action -eq 'toggle') {
                    $toggle = Pattern $el ([System.Windows.Automation.TogglePattern]::Pattern)
                    if (-not $toggle) { $done.ok = $false; $done.detail = "$(Said $el) cannot be toggled" }
                    elseif (Bounded $toggle 'Toggle' @()) { $done.detail = "toggled $(Said $el): $($toggle.Current.ToggleState)" }
                    else { $done.detail = "toggled $(Said $el)" + $Waiting }
                } else { $done.ok = $false; $done.detail = "unknown action $($s.action)" }
            } catch { $done.ok = $false; $done.detail = (($_.Exception.Message -split "`n")[0]).Trim() }
            Say ('STEP ' + ($done | ConvertTo-Json -Compress))
            if (-not $done.ok) { break }
            Start-Sleep -Milliseconds 100
        }
        Start-Sleep -Milliseconds 150
        Say 'END'
        # The thread of a call that has not returned is not waited for.
        [Environment]::Exit(0)
        """;

    // ── Linux ──

    /// <summary>Linux: AT-SPI through python3-gi, in the apps of the process tree, read again at each search. A window that
    /// holds a file chooser counts whole as a file dialog.</summary>
    private const string LinuxScript = """
        import json, os, sys, time
        try:
            import gi
            gi.require_version('Atspi', '2.0')
            from gi.repository import Atspi
        except Exception:
            print('REFUSED AT-SPI is not reachable from python3 (the packages python3-gi and gir1.2-atspi-2.0, and the desktop\'s accessibility bus)', flush=True)
            sys.exit(0)
        root = int(sys.argv[1])
        steps = json.load(open(sys.argv[2], encoding='utf-8-sig'))
        wait = int(sys.argv[3])

        def say(line):
            print(line, flush=True)

        def flat(s):
            return ' '.join(str(s or '').split())

        def tree():
            # Read at each search: the app a 'dotnet run' starts after its build, a window a click opens in a new process.
            # A child counts only if it started after its parent: a reused id never pulls another process in.
            procs = {}
            for d in os.listdir('/proc'):
                if not d.isdigit():
                    continue
                try:
                    fields = open('/proc/%s/stat' % d).read().rsplit(')', 1)[1].split()
                    procs[int(d)] = (int(fields[1]), int(fields[19]))
                except Exception:
                    pass
            found = {root} if root in procs else set()
            grew = True
            while grew:
                grew = False
                for pid, (parent, start) in procs.items():
                    if pid not in found and parent in found and start >= procs[parent][1]:
                        found.add(pid)
                        grew = True
            return found

        def role(n):
            try:
                return n.get_role_name()
            except Exception:
                return ''

        def name(n):
            try:
                return flat(n.get_name())
            except Exception:
                return ''

        def ident(n):
            try:
                return n.get_accessible_id() or ''
            except Exception:
                return ''

        def said(n):
            parts = [role(n).title().replace(' ', '')]
            if name(n):
                parts.append('"%s"' % name(n))
            if ident(n):
                parts.append('#' + ident(n))
            return ' '.join(parts)

        def contents(w):
            out = []
            def walk(n, depth):
                if depth > 60 or len(out) >= 6000:
                    return
                try:
                    out.append(n)
                    for i in range(n.get_child_count()):
                        c = n.get_child_at_index(i)
                        if c is not None:
                            walk(c, depth + 1)
                except Exception:
                    pass
            walk(w, 0)
            return out

        def look():
            # The app's windows, each with its controls and whether it holds a file chooser.
            pids = tree()
            seen = []
            desktop = Atspi.get_desktop(0)
            for i in range(desktop.get_child_count()):
                try:
                    app = desktop.get_child_at_index(i)
                    if app is None or app.get_process_id() not in pids:
                        continue
                    for j in range(app.get_child_count()):
                        w = app.get_child_at_index(j)
                        if w is not None:
                            nodes = contents(w)
                            seen.append((w, nodes, any(role(n) == 'file chooser' for n in nodes)))
                except Exception:
                    pass
            return seen

        def find(target, partial, seen):
            want = flat(target)
            low = want.casefold()
            # A file dialog's window alone (its title), never its contents.
            cands = []
            for w, nodes, dialog in seen:
                cands.extend([(w, True)] if dialog else [(n, False) for n in nodes])
            tests = [lambda n: ident(n).casefold() == low, lambda n: name(n).casefold() == low]
            if partial:
                tests.append(lambda n: want in name(n))
            for test in tests:
                for n, dialog in cands:
                    try:
                        if test(n):
                            return n, dialog, []
                    except Exception:
                        pass
            near = []
            if not partial:
                for n, _ in cands:
                    if low in name(n).casefold() and name(n) not in near:
                        near.append(name(n))
                    if len(near) >= 5:
                        break
            return None, False, near

        def state(n, kind):
            try:
                return n.get_state_set().contains(kind)
            except Exception:
                return False

        def act(n, names):
            # Only an action the step means, never the control's first action whatever it is: False when it has none of
            # them, True when it took one, 'sent' when the action went out and no answer came (it may be running).
            try:
                a = n.get_action_iface()
                if a is None:
                    return False
                index = next((i for i in range(a.get_n_actions()) if a.get_action_name(i) in names), None)
            except Exception:
                return False
            if index is None:
                return False
            try:
                return True if a.do_action(index) else False
            except Exception:
                return 'sent'

        def same(a, b):
            try:
                return a is not None and b is not None and (a == b or (a.get_index_in_parent() == b.get_index_in_parent()
                                                                        and role(a) == role(b) and name(a) == name(b)))
            except Exception:
                return False

        WAITING = ': no answer came, the action may be running (look at the windows)'

        deadline = time.time() + wait
        seen = look()
        while not seen and time.time() < deadline:
            time.sleep(0.2)
            seen = look()
        if not seen:
            say('REFUSED the app shows no window after %d s' % wait)
            sys.exit(0)

        for number, s in enumerate(steps, 1):
            say('PLAY %d' % number)
            done = {'ok': True, 'detail': ''}
            target = s.get('target') or ''
            try:
                partial = s['action'] == 'wait_for'
                seen = look()
                n, is_dialog, near = find(target, partial, seen)
                until0 = time.time() + 1
                while n is None and not partial and time.time() < until0:
                    time.sleep(0.05)
                    seen = look()
                    n, is_dialog, near = find(target, False, seen)
                dialogs = ['"%s"' % name(w) for w, _, d in seen if d]
                if partial:
                    until = time.time() + min(int(s.get('timeout_ms', 5000)), 10000) / 1000.0
                    while n is None and time.time() < until:
                        time.sleep(0.2)
                        seen = look()
                        n, is_dialog, _ = find(target, True, seen)
                    dialogs = ['"%s"' % name(w) for w, _, d in seen if d]
                    if n is not None:
                        done['detail'] = '%s is there: %s' % (target, said(n)) + (
                            ', a file dialog: its contents are never searched nor acted in' if is_dialog else '')
                    else:
                        done.update(ok=False, detail='%s did not appear' % target
                                    + (' (a file dialog is open, whose contents are never searched)' if dialogs else ''))
                elif dialogs:
                    done.update(ok=False, detail="a file dialog is open in the app (%s): no step acts while it is — its contents "
                                                 "are the person's folders, and the app waits on it" % ', '.join(dialogs))
                elif n is None:
                    done.update(ok=False, detail='no control has the id or the name %s' % target
                                + (' (names that hold it: %s)' % ', '.join('"%s"' % x for x in near) if near else ''))
                elif not state(n, Atspi.StateType.ENABLED):
                    done.update(ok=False, detail='%s is disabled' % said(n))
                elif s['action'] == 'type':
                    e = n.get_editable_text_iface()
                    if e is None or not e.set_text_contents(s.get('text') or ''):
                        done.update(ok=False, detail='%s does not take text' % said(n))
                    else:
                        done['detail'] = 'typed into %s' % said(n)
                elif s['action'] == 'select':
                    want = flat(s.get('item')).casefold()
                    items = []
                    def gather(m, depth):
                        if depth > 6:
                            return
                        for i in range(m.get_child_count()):
                            c = m.get_child_at_index(i)
                            if c is not None:
                                items.append(c)
                                gather(c, depth + 1)
                    gather(n, 0)
                    item = next((c for c in items if name(c).casefold() == want), None)
                    if item is None:
                        # A list filled only once opened: opened, then read again.
                        act(n, ('press', 'click', 'open'))
                        items = []
                        gather(n, 0)
                        item = next((c for c in items if name(c).casefold() == want), None)
                    if item is None:
                        named = [name(c) for c in items if name(c)]
                        names = ', '.join(named[:20]) + (', and %d more' % (len(named) - 20) if len(named) > 20 else '')
                        done.update(ok=False, detail='%s has no item "%s" (items: %s)' % (said(n), s.get('item'), names or 'none'))
                    else:
                        # The list's own selection, when the item is its child or an entry of its menu (a combo box sets
                        # its choice through it) — an index means nothing in another parent; the choice is read back: a
                        # highlighted entry of a menu is not a choice made.
                        index = item.get_index_in_parent()
                        parent = item.get_parent()
                        chosen = False
                        if same(parent, n) or (role(parent) == 'menu' and same(parent.get_parent(), n)):
                            sel = n.get_selection_iface()
                            try:
                                if sel is not None:
                                    sel.select_child(index)
                                    time.sleep(0.1)
                                    chosen = sel.is_child_selected(index)
                            except Exception:
                                pass
                        if not chosen and act(item, ('click', 'press', 'activate')):
                            time.sleep(0.1)
                            chosen = state(item, Atspi.StateType.SELECTED) or name(n).casefold() == want
                        done.update(ok=chosen, detail='chose %s in %s' % (name(item), said(n)) if chosen
                                    else '%s did not take "%s": the list does not show it chosen' % (said(n), s.get('item')))
                elif s['action'] == 'click':
                    parent = n.get_parent()
                    sel = parent.get_selection_iface() if parent is not None and role(n) in ('list item', 'table cell') else None
                    # A click on an item of a list chooses it.
                    if sel is not None and sel.select_child(n.get_index_in_parent()):
                        done['detail'] = 'chose %s' % said(n)
                    else:
                        took = act(n, ('click', 'press', 'activate', 'jump'))
                        if took:
                            done['detail'] = 'clicked %s' % said(n) + (WAITING if took == 'sent' else '')
                        else:
                            done.update(ok=False, detail='%s cannot be clicked: it offers no action to AT-SPI' % said(n))
                elif s['action'] == 'toggle':
                    took = act(n, ('toggle', 'click', 'press'))
                    if took == 'sent':
                        done['detail'] = 'toggled %s' % said(n) + WAITING
                    elif took:
                        time.sleep(0.1)
                        done['detail'] = 'toggled %s: %s' % (said(n), 'On' if state(n, Atspi.StateType.CHECKED) else 'Off')
                    else:
                        done.update(ok=False, detail='%s cannot be toggled' % said(n))
                else:
                    done.update(ok=False, detail='unknown action %s' % s['action'])
            except Exception as e:
                done.update(ok=False, detail=str(e).split('\n')[0])
            say('STEP ' + json.dumps(done))
            if not done['ok']:
                break
            time.sleep(0.1)
        time.sleep(0.15)
        say('END')
        """;

    // ── macOS ──

    /// <summary>macOS: System Events' accessibility on the windows of the process tree, read again at each search (the app
    /// that runs Inferpal needs Accessibility and Automation). An open or save panel counts whole as a file dialog.</summary>
    /// <remarks>shortcut: measured on no Mac; a panel is told by the markers <see cref="WindowCapture"/>'s look uses — upgrade
    /// with a measure on macOS.</remarks>
    private const string MacScript = """
        ObjC.import('Foundation');
        function readFile(p) {
          const text = $.NSString.stringWithContentsOfFileEncodingError(p, $.NSUTF8StringEncoding, null).js;
          return text.charCodeAt(0) === 0xFEFF ? text.slice(1) : text;
        }
        function refused(e) {
          const n = e && e.errorNumber;
          if (n === -1743) return 'REFUSED macOS has not let the app that runs Inferpal ask System Events for the controls: the person can allow it in System Settings → Privacy & Security → Automation';
          if (n === -1719 || n === -25211) return 'REFUSED macOS has not let the app that runs Inferpal use other apps\' controls: the person can allow it in System Settings → Privacy & Security → Accessibility';
          return null;
        }
        function run(argv) {
          const root = Number(argv[0]);
          const steps = JSON.parse(readFile(argv[1]));
          const wait = Number(argv[2]);
          // Each line written as it happens: a call stopped at its budget keeps what ran (osascript writes its answer once).
          const out = $.NSFileHandle.fileHandleWithStandardOutput;
          const say = (line) => out.writeData($(line + '\n').dataUsingEncoding($.NSUTF8StringEncoding));
          try {
            const se = Application('System Events');
            const shell = Application.currentApplication();
            shell.includeStandardAdditions = true;
            const val = (f) => { try { const v = f(); return v === null || v === undefined ? '' : String(v); } catch (e) { return ''; } };
            const flat = (s) => String(s || '').split(/\s+/).filter(Boolean).join(' ');
            const nameOf = (e) => flat(val(() => e.name()) || val(() => e.description()));
            const idOf = (e) => val(() => e.attributes.byName('AXIdentifier').value());
            const said = (e) => [val(() => e.role()).replace(/^AX/, '')].concat(nameOf(e) ? ['"' + nameOf(e) + '"'] : [])
              .concat(idOf(e) ? ['#' + idOf(e)] : []).join(' ');
            // The element's own AXPress action, never System Events' click, which may post a mouse event at the element's
            // place: false when it has none, 'sent' when it went out and no answer came (it may be running).
            const press = (e) => {
              let names = [];
              try { names = e.actions.name(); } catch (x) { return false; }
              if (!names.includes('AXPress')) return false;
              try { e.actions.byName('AXPress').perform(); return true; } catch (x) { return 'sent'; }
            };
            const WAITING = ': no answer came, the action may be running (look at the windows)';
            // Read at each search: the app a 'dotnet run' starts after its build, a window a click opens in a new process.
            // An orphan is given a new parent on macOS: a stale parent id cannot pull another tree in.
            const tree = () => {
              const kids = {};
              // ⚠ doShellScript ends its lines with a carriage return.
              for (const line of shell.doShellScript('ps -A -o pid= -o ppid=').split(/[\r\n]+/)) {
                const [pid, parent] = line.trim().split(/\s+/).map(Number);
                if (pid) (kids[parent] = kids[parent] || []).push(pid);
              }
              const found = [root];
              for (let i = 0; i < found.length; i++) for (const k of kids[found[i]] || []) if (!found.includes(k)) found.push(k);
              return found;
            };
            // The app's windows, each with its controls and whether it is an open or save panel: a column browser, or a sheet
            // holding a name field and a "Where" pop-up (the panel collapsed).
            const look = () => {
              const pids = tree();
              const seen = [];
              for (const p of se.processes()) {
                let pid = 0;
                try { pid = p.unixId(); } catch (e) { continue; }
                if (!pids.includes(pid)) continue;
                for (const w of p.windows()) {
                  const contents = w.entireContents();
                  const roles = contents.map((e) => val(() => e.role()));
                  const panel = roles.includes('AXBrowser')
                    || (roles.includes('AXSheet') && roles.includes('AXTextField') && roles.includes('AXPopUpButton'));
                  seen.push({ w, contents, panel });
                }
              }
              return seen;
            };
            // Its identifier, else its name or description, case aside; a part of its name only for wait_for. A panel's
            // window alone (its title), never its contents.
            const find = (target, partial, seen) => {
              const want = flat(target);
              const low = want.toLowerCase();
              const cands = [];
              for (const v of seen) {
                if (v.panel) cands.push([v.w, true]);
                else for (const e of v.contents) cands.push([e, false]);
              }
              const tests = [(e) => idOf(e).toLowerCase() === low, (e) => nameOf(e).toLowerCase() === low];
              if (partial) tests.push((e) => nameOf(e).includes(want));
              for (const test of tests) for (const [e, panel] of cands) if (test(e)) return [e, panel];
              return [null, false];
            };
            const deadline = Date.now() + wait * 1000;
            let seen = look();
            while (seen.length === 0 && Date.now() < deadline) { delay(0.2); seen = look(); }
            if (seen.length === 0) { say('REFUSED the app shows no window after ' + wait + ' s'); return ''; }
            let number = 0;
            for (const s of steps) {
              say('PLAY ' + ++number);
              const done = { ok: true, detail: '' };
              const target = s.target || '';
              try {
                const partial = s.action === 'wait_for';
                seen = look();
                let [e, isPanel] = find(target, partial, seen);
                const until0 = Date.now() + 1000;
                while (!e && !partial && Date.now() < until0) { delay(0.05); seen = look(); [e, isPanel] = find(target, false, seen); }
                const panels = () => seen.filter((v) => v.panel).map((v) => '"' + nameOf(v.w) + '"');
                if (partial) {
                  const until = Date.now() + Math.min(Number(s.timeout_ms ?? 5000), 10000);
                  while (!e && Date.now() < until) { delay(0.2); seen = look(); [e, isPanel] = find(target, true, seen); }
                  done.ok = !!e;
                  done.detail = e ? target + ' is there: ' + said(e) + (isPanel ? ', a file dialog: its contents are never searched nor acted in' : '')
                    : target + ' did not appear' + (panels().length ? ' (a file dialog is open, whose contents are never searched)' : '');
                } else if (panels().length) {
                  done.ok = false;
                  done.detail = 'a file dialog is open in the app (' + panels().join(', ') + "): no step acts while it is — its contents are the person's folders, and the app waits on it";
                } else if (!e) { done.ok = false; done.detail = 'no control has the id or the name ' + target; }
                else if (val(() => e.enabled()) === 'false') { done.ok = false; done.detail = said(e) + ' is disabled'; }
                else if (s.action === 'type') {
                  e.value = s.text || '';
                  done.detail = 'typed into ' + said(e) + (val(() => e.value()) === (s.text || '') ? '' : ', which now shows "' + flat(val(() => e.value())).slice(0, 200) + '"');
                }
                else if (s.action === 'select') {
                  const role = val(() => e.role());
                  const want = flat(s.item).toLowerCase();
                  let chosen = false;
                  if (role === 'AXPopUpButton' || role === 'AXComboBox') {
                    press(e);
                    delay(0.2);
                    for (const m of e.menus()) {
                      for (const it of m.menuItems()) if (nameOf(it).toLowerCase() === want) { chosen = press(it); break; }
                      // A menu left open by a choice not found is closed.
                      if (!chosen) try { m.actions.byName('AXCancel').perform(); } catch (x) {}
                    }
                  } else {
                    for (const r of e.entireContents()) if (nameOf(r).toLowerCase() === want || flat(val(() => r.value())).toLowerCase() === want) {
                      try { r.selected = true; chosen = true; } catch (x) { chosen = press(r); }
                      break;
                    }
                  }
                  done.ok = chosen;
                  done.detail = chosen ? 'chose ' + s.item + ' in ' + said(e) : said(e) + ' has no item "' + s.item + '"';
                }
                else if (s.action === 'click') {
                  const role = val(() => e.role());
                  // A click on an item of a list chooses it.
                  const took = role === 'AXRow' || role === 'AXCell' ? null : press(e);
                  if (took === null) { e.selected = true; done.detail = 'chose ' + said(e); }
                  else if (took) done.detail = 'clicked ' + said(e) + (took === 'sent' ? WAITING : '');
                  else { done.ok = false; done.detail = said(e) + ' cannot be clicked: it offers no press action'; }
                }
                else if (s.action === 'toggle') {
                  const took = press(e);
                  if (took === 'sent') done.detail = 'toggled ' + said(e) + WAITING;
                  else if (took) done.detail = 'toggled ' + said(e) + ': ' + (val(() => e.value()) === '1' ? 'On' : 'Off');
                  else { done.ok = false; done.detail = said(e) + ' cannot be toggled'; }
                }
                else { done.ok = false; done.detail = 'unknown action ' + s.action; }
              } catch (x) {
                const r = refused(x);
                if (r) { say(r); return ''; }
                done.ok = false; done.detail = String(x && x.message ? x.message : x).split('\n')[0];
              }
              say('STEP ' + JSON.stringify(done));
              if (!done.ok) break;
              delay(0.1);
            }
            delay(0.15);
            say('END');
            return '';
          } catch (x) {
            say(refused(x) || 'REFUSED ' + String(x && x.message ? x.message : x).split('\n')[0]);
            return '';
          }
        }
        """;
}
