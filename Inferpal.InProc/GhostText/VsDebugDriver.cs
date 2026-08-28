using System.Collections;
using System.Threading;
using System.Threading.Tasks;
using Inferpal.Services.Debugging;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Threading;
using Task = System.Threading.Tasks.Task;

namespace Inferpal.GhostText;

/// <summary>
/// Serves the out-of-process host's debugger commands (roadmap §21) by driving EnvDTE automation
/// from inside devenv. The mirror image of <see cref="VsDebuggerTracker"/>, which publishes break
/// snapshots outwards: this one takes requests inwards, over
/// <see cref="Services.Signals.DebugCommandSignal"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this class exists in-process at all.</b> The §21 probe established that the
/// out-of-process debugger API is not the channel — the automation object lives here, so the
/// driver does too. No new transport was invented: this is the same file-signal family already
/// carrying build results, the active solution and inline-diff previews.
/// </para>
/// <para>
/// <b>Waiting for a stop is a transition, never a state.</b> The probe recorded a resume answering
/// "break reached" in 0,15 s — fast enough that a check on <i>being</i> in break mode would have
/// validated a resume that had not happened. Every wait below therefore keys on the mode-change
/// counter bumped by <see cref="IVsDebuggerEvents.OnModeChange"/>, not on the current mode.
/// </para>
/// <para>
/// EnvDTE is UI-thread-only, while waiting for the program to run must not occupy that thread —
/// so each automation call is a short hop onto the main thread and every wait happens off it.
/// </para>
/// </remarks>
internal sealed class VsDebugDriver : IVsDebuggerEvents, IDisposable
{
    private const int  PollMs           = 100;
    /// <summary>How long a resume from a break may show no reaction at all before we give up.</summary>
    private const int  SettleMs         = 3000;
    private const int  EvalTimeoutMs    = 5000;
    private const int  MaxFrames        = 24;
    private const int  MaxLocals        = 40;

    private readonly IVsDebugger             _debugger;
    private readonly EnvDTE.DTE              _dte;
    private readonly CancellationTokenSource _cts = new();
    private          uint                    _cookie;
    private          Task?                   _loop;

    // Written on the UI thread by OnModeChange, read from the poll loop.
    private int _mode;      // (int)DBGMODE
    private int _modeGen;   // bumped on every transition — the thing waits key on

    private static JoinableTaskFactory Jtf => ThreadHelper.JoinableTaskFactory;

    /// <summary>Subscribes to debugger events and starts serving. UI-thread only.</summary>
    internal VsDebugDriver(IVsDebugger debugger, EnvDTE.DTE dte)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        _debugger = debugger;
        _dte      = dte;

        var mode = new DBGMODE[1];
        _mode = debugger.GetMode(mode) == VSConstants.S_OK ? (int)mode[0] : (int)DBGMODE.DBGMODE_Design;
        debugger.AdviseDebuggerEvents(this, out _cookie);

        // Advertised last: the host must never find a marker for a driver that is not yet looping.
        // ⚠ One marker per machine, so a second devenv overwrites the first. Acceptable and not
        // silently wrong — the host talks to whichever devenv advertised last, and a request from a
        // dead host is refused by the PID guard.
        Services.Signals.DebugCommandSignal.MarkReady(System.Diagnostics.Process.GetCurrentProcess().Id);
        _loop = Task.Run(() => ServeAsync(_cts.Token));
    }

    int IVsDebuggerEvents.OnModeChange(DBGMODE dbgmodeNew)
    {
        Volatile.Write(ref _mode, (int)dbgmodeNew);
        Interlocked.Increment(ref _modeGen);
        return VSConstants.S_OK;
    }

    // ── Serving loop ────────────────────────────────────────────────────────────────

    private async Task ServeAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            Services.Signals.DebugCommandRequest? request = null;
            try { request = Services.Signals.DebugCommandSignal.ClaimRequest(); }
            catch (Exception ex) { Services.Diagnostics.Swallow("VsDebugDriver.Claim", ex); }

            if (request is null)
            {
                try { await Task.Delay(PollMs, ct); } catch (OperationCanceledException) { return; }
                continue;
            }

            Services.Signals.DebugCommandResponse response;
            try
            {
                response = await ExecuteAsync(request, ct);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                // Every ordinary debugger condition is an answer, not a crash: the host renders the
                // message as a sentence and the agent moves on.
                Services.Diagnostics.Swallow("VsDebugDriver.Execute", ex);
                response = new(request.Id, Ok: false, Error: ex.Message);
            }

            Services.Signals.DebugCommandSignal.WriteResponse(response);
        }
    }

    private async Task<Services.Signals.DebugCommandResponse> ExecuteAsync(
        Services.Signals.DebugCommandRequest request, CancellationToken ct)
    {
        switch (request.Op)
        {
            case DebugOps.AddBreakpoint:
            {
                await Jtf.SwitchToMainThreadAsync(ct);
                var bound = AddBreakpoint(request.File!, request.Line);
                await TaskScheduler.Default.SwitchTo();
                return new(request.Id, Ok: true, Breakpoints: bound);
            }

            case DebugOps.RemoveBreakpoint:
            {
                await Jtf.SwitchToMainThreadAsync(ct);
                var removed = RemoveBreakpoint(request.File!, request.Line);
                await TaskScheduler.Default.SwitchTo();
                return new(request.Id, Ok: true, Flag: removed);
            }

            case DebugOps.ListBreakpoints:
            {
                await Jtf.SwitchToMainThreadAsync(ct);
                var all = ListBreakpoints();
                await TaskScheduler.Default.SwitchTo();
                return new(request.Id, Ok: true, Breakpoints: all);
            }

            case DebugOps.Start:
            {
                // Built explicitly first, and the launch is abandoned when it fails. Measured
                // before being written (probe 3, 2026-08-06): `Debug.Start` on a solution that does
                // not compile opens a modal — "There were build errors. Would you like to continue
                // and run the last successful build?" — 2 s later, and it sits on
                // the UI thread until a human answers. Every later request would then block on its
                // hop to that thread: the whole driver stops, not just this call. Building through
                // the automation instead reports the same failure in 0,34 s with no dialog at all.
                var failure = await BuildBeforeLaunchAsync(ct);
                if (failure is not null) return new(request.Id, Ok: false, Error: failure);

                return new(request.Id, Ok: true, State: await ResumeAndWaitAsync(request.Op, ct));
            }

            case DebugOps.Continue:
            case DebugOps.StepOver:
            case DebugOps.StepInto:
            case DebugOps.StepOut:
                return new(request.Id, Ok: true, State: await ResumeAndWaitAsync(request.Op, ct));

            case DebugOps.State:
            {
                if (!IsPaused) return new(request.Id, Ok: true, State: null);
                await Jtf.SwitchToMainThreadAsync(ct);
                var state = CaptureState();
                await TaskScheduler.Default.SwitchTo();
                return new(request.Id, Ok: true, State: state);
            }

            case DebugOps.Evaluate:
            {
                await Jtf.SwitchToMainThreadAsync(ct);
                var text = Evaluate(request.Expression ?? string.Empty, request.FrameId);
                await TaskScheduler.Default.SwitchTo();
                return text is null
                    ? new(request.Id, Ok: false, Error: "The expression could not be evaluated.")
                    : new(request.Id, Ok: true, Text: text);
            }

            case DebugOps.Stop:
            {
                await Jtf.SwitchToMainThreadAsync(ct);
                _dte.Debugger.Stop(false);
                await TaskScheduler.Default.SwitchTo();
                return new(request.Id, Ok: true);
            }

            case DebugOps.CaptureTest:
            {
                var state = await CaptureTestAsync(request, ct);
                return state is null
                    ? new(request.Id, Ok: false, Error: "The failing test could not be captured under the debugger.")
                    : new(request.Id, Ok: true, State: state);
            }

            default:
                return new(request.Id, Ok: false, Error: $"Unknown debugger operation '{request.Op}'.");
        }
    }

    // ── Building before a launch ────────────────────────────────────────────────────

    /// <summary>How long the pre-launch build may take before we stop waiting for it.</summary>
    private const int BuildBudgetMs = 5 * 60 * 1000;

    /// <summary>
    /// Builds the solution and returns <c>null</c> when it is ready to run, or the sentence to hand
    /// back when it is not.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Asynchronous on purpose.</b> <c>Build(true)</c> would hold the UI thread for the whole
    /// build — minutes on a real solution, with a frozen IDE — so the build is started and its
    /// state polled instead. Each poll is a short hop onto the UI thread to read a property, never
    /// a wait held there.
    /// </para>
    /// <para>
    /// <c>LastBuildInfo</c> is the number of projects that failed. Measured, not assumed: 1 on the
    /// probe's deliberately broken solution.
    /// </para>
    /// </remarks>
    private async Task<string?> BuildBeforeLaunchAsync(CancellationToken ct)
    {
        await Jtf.SwitchToMainThreadAsync(ct);
        try { _dte.Solution.SolutionBuild.Build(false); }
        catch (Exception ex)
        {
            // A solution that cannot even be asked to build is an ordinary answer, not a crash.
            Services.Diagnostics.Swallow("VsDebugDriver.StartBuild", ex);
            await TaskScheduler.Default.SwitchTo();
            return "The solution could not be built: " + ex.Message;
        }
        await TaskScheduler.Default.SwitchTo();

        var waited = 0;
        while (waited < BuildBudgetMs)
        {
            await Task.Delay(PollMs, ct);
            waited += PollMs;

            await Jtf.SwitchToMainThreadAsync(ct);
            var state = EnvDTE.vsBuildState.vsBuildStateInProgress;
            try { state = _dte.Solution.SolutionBuild.BuildState; }
            catch (Exception ex) { Services.Diagnostics.Swallow("VsDebugDriver.BuildState", ex); }
            var failed = state == EnvDTE.vsBuildState.vsBuildStateDone ? FailedProjectCount() : 0;
            await TaskScheduler.Default.SwitchTo();

            if (state != EnvDTE.vsBuildState.vsBuildStateDone) continue;

            return failed == 0
                ? null
                : $"The build failed ({failed} project(s) with errors), so nothing was launched. "
                + "Fix the build first — get_diagnostics reports the errors.";
        }

        return $"The build did not finish within {BuildBudgetMs / 60_000} minutes, so nothing was launched.";
    }

    /// <summary>Number of projects that failed the last build. UI thread only.</summary>
    private int FailedProjectCount()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try { return _dte.Solution.SolutionBuild.LastBuildInfo; }
        catch (Exception ex)
        {
            Services.Diagnostics.Swallow("VsDebugDriver.LastBuildInfo", ex);
            return 0;   // unreadable → let the launch proceed rather than block it on our own doubt
        }
    }

    // ── Waiting ─────────────────────────────────────────────────────────────────────

    private bool IsPaused => Volatile.Read(ref _mode) == (int)DBGMODE.DBGMODE_Break;

    /// <summary>
    /// Issues a resume and waits for the <b>next</b> stop. <c>null</c> when the program ended
    /// instead of stopping, or when nothing happened before the host's own timeout — the host
    /// renders both as "no stop", which is the truth in either case.
    /// </summary>
    /// <remarks>
    /// Each hop back to <see cref="TaskScheduler.Default"/> is load-bearing, not tidiness: the wait
    /// below must not hold the UI thread, or the program it is waiting for would never get to run.
    /// </remarks>
    private async Task<DebugStopState?> ResumeAndWaitAsync(string op, CancellationToken ct)
    {
        var generation       = Volatile.Read(ref _modeGen);
        var resumingFromBreak = IsPaused;

        await Jtf.SwitchToMainThreadAsync(ct);
        switch (op)
        {
            // F5, both times. In design mode it builds and launches; in break mode it continues —
            // which is what F5 does. Debugger.Go() is avoided on purpose: the §21 probe recorded it
            // failing where ExecuteCommand succeeded.
            case DebugOps.Start:
            case DebugOps.Continue: _dte.ExecuteCommand("Debug.Start"); break;
            case DebugOps.StepOver: _dte.Debugger.StepOver(false);      break;
            case DebugOps.StepInto: _dte.Debugger.StepInto(false);      break;
            case DebugOps.StepOut:  _dte.Debugger.StepOut(false);       break;
        }
        await TaskScheduler.Default.SwitchTo();

        var idleMs = 0;
        while (!ct.IsCancellationRequested)
        {
            // Only a transition counts. Being in break mode proves nothing: the resume may not have
            // taken effect yet, and the state would be the one the caller already had.
            if (Volatile.Read(ref _modeGen) != generation)
            {
                switch ((DBGMODE)Volatile.Read(ref _mode))
                {
                    case DBGMODE.DBGMODE_Break:
                    {
                        await Jtf.SwitchToMainThreadAsync(ct);
                        var state = CaptureState();
                        await TaskScheduler.Default.SwitchTo();
                        return state;
                    }
                    case DBGMODE.DBGMODE_Design: return null;   // ran to completion
                    default: break;                             // still running
                }
            }
            else if (resumingFromBreak && (idleMs += PollMs / 2) >= SettleMs)
            {
                // Resuming from a break and nothing moved: the command did not take (no startup
                // project, session already gone). Give up rather than sit on the host's multi-minute
                // budget — an agent told "no stop" is better off than one blocked for two minutes.
                //
                // ⚠ Only from a break. A launch legitimately stays in design mode for the whole
                // build, so there the long host timeout is the right budget and this shortcut would
                // cut a build short.
                return null;
            }
            await Task.Delay(PollMs / 2, ct);
        }
        return null;
    }

    // ── §25: capture one failing test under the debugger ─────────────────────────────

    /// <summary>
    /// Launches the repro runner in wait-for-debugger mode, attaches through
    /// <c>LocalProcesses</c>, waits for the unhandled-exception break at the original throw site
    /// (the runner invokes with <c>DoNotWrapExceptions</c>) and snapshots it. Every step was
    /// probed before being written (2026-08-20, <c>docs/probes/tdd-debug-launch/</c>): attach
    /// ~2 s, break ~3 s — and the current frame is <b>empty at the break signal</b>, so the
    /// snapshot retries until the stack settles.
    /// </summary>
    private async Task<DebugStopState?> CaptureTestAsync(
        Services.Signals.DebugCommandRequest request, CancellationToken ct)
    {
        if (request.Program is null || request.Args is null) return null;
        // Never hijack a session the user (or the model) is actually driving.
        if (Volatile.Read(ref _mode) != (int)DBGMODE.DBGMODE_Design) return null;

        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName         = "dotnet",
            WorkingDirectory = request.Cwd ?? string.Empty,
            UseShellExecute  = false,
            CreateNoWindow   = true,
        };
        // net472 has no ArgumentList: the line is assembled and escaped here (see QuoteArg).
        psi.Arguments = string.Join(" ",
            new[] { request.Program }.Concat(request.Args ?? Array.Empty<string>()).Select(QuoteArg));
        psi.EnvironmentVariables["INFERPAL_WAIT_DEBUGGER"] = "1";

        using var runner = System.Diagnostics.Process.Start(psi);
        if (runner is null) return null;
        try
        {
            var generation = Volatile.Read(ref _modeGen);

            var attached = false;
            var deadline = NowMs() + 30_000;
            while (!attached && NowMs() < deadline && !runner.HasExited)
            {
                ct.ThrowIfCancellationRequested();
                await Jtf.SwitchToMainThreadAsync(ct);
                try
                {
                    foreach (EnvDTE.Process p in _dte.Debugger.LocalProcesses)
                    {
                        try { if (p.ProcessID == runner.Id) { p.Attach(); attached = true; break; } }
                        catch (Exception ex) { Services.Diagnostics.Swallow("VsDebugDriver.Attach", ex); }
                    }
                }
                catch (Exception ex) { Services.Diagnostics.Swallow("VsDebugDriver.LocalProcesses", ex); }
                await TaskScheduler.Default.SwitchTo();
                if (!attached) await Task.Delay(250, ct);
            }
            if (!attached) return null;

            // Only the TRANSITION to break counts (§21 lesson) — attach machinery can flicker.
            var breakDeadline = NowMs() + 90_000;
            while (NowMs() < breakDeadline)
            {
                ct.ThrowIfCancellationRequested();
                if (Volatile.Read(ref _modeGen) != generation &&
                    (DBGMODE)Volatile.Read(ref _mode) == DBGMODE.DBGMODE_Break) break;
                await Task.Delay(150, ct);
            }
            if ((DBGMODE)Volatile.Read(ref _mode) != DBGMODE.DBGMODE_Break) return null;

            // CurrentStackFrame is empty when the break is first signalled — retry until it settles.
            for (var attempt = 0; attempt < 20; attempt++)
            {
                await Task.Delay(250, ct);
                await Jtf.SwitchToMainThreadAsync(ct);
                var state = CaptureState();
                await TaskScheduler.Default.SwitchTo();
                if (state.Frames.Count > 0 && !string.IsNullOrEmpty(state.Frames[0].Function))
                    return state with { Reason = "exception" };
            }
            return null;
        }
        finally
        {
            // The capture session and its runner never outlive the call, whatever happened above.
            try
            {
                await Jtf.SwitchToMainThreadAsync(CancellationToken.None);
                try { _dte.Debugger.Stop(false); } catch (Exception ex) { Services.Diagnostics.Swallow("VsDebugDriver.CaptureStop", ex); }
                await TaskScheduler.Default.SwitchTo();
            }
            catch { }
            try { if (!runner.HasExited) KillTree(runner); } catch { }
        }
    }

    // ── net472 supplements ──────────────────────────────────────────────────────────
    // Three modern .NET BCL APIs do not exist on .NET Framework, and this assembly is
    // en net472 par obligation (devenv est un process Framework — docs/probes/inproc-net8-verdict.md).

    /// <summary>Windows escaping of a command-line argument (replaces ArgumentList).</summary>
    private static string QuoteArg(string value)
    {
        if (value.Length > 0 && value.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return value;

        var sb = new System.Text.StringBuilder("\"");
        for (var i = 0; i < value.Length; i++)
        {
            var slashes = 0;
            while (i < value.Length && value[i] == '\\') { slashes++; i++; }

            if (i == value.Length)            { sb.Append('\\', slashes * 2); break; }
            if (value[i] == '"')              { sb.Append('\\', slashes * 2 + 1); }
            else                              { sb.Append('\\', slashes); }
            sb.Append(value[i]);
        }
        return sb.Append('"').ToString();
    }

    /// <summary>Horloge monotone en millisecondes (remplace le TickCount64 du BCL moderne).</summary>
    private static long NowMs() =>
        System.Diagnostics.Stopwatch.GetTimestamp() / (System.Diagnostics.Stopwatch.Frequency / 1000);

    /// <summary>Tue le process et sa descendance (remplace Kill(entireProcessTree: true)).</summary>
    private static void KillTree(System.Diagnostics.Process process)
    {
        try
        {
            using var killer = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName        = "taskkill",
                Arguments       = "/T /F /PID " + process.Id,
                UseShellExecute = false,
                CreateNoWindow  = true,
            });
            killer?.WaitForExit(5000);
        }
        catch { /* nettoyage */ }
        try { if (!process.HasExited) process.Kill(); } catch { /* nettoyage */ }
    }

    // ── EnvDTE automation (UI thread only) ──────────────────────────────────────────

    private IReadOnlyList<DebugBreakpointInfo> AddBreakpoint(string file, int line)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        // The debugger may bind the breakpoint somewhere else (next executable line), and it may
        // bind it in several places (generics, multiple modules). Report what it actually did.
        _dte.Debugger.Breakpoints.Add(string.Empty, file, line);
        return ListBreakpoints().Where(b => SamePath(b.File, file)).ToList();
    }

    private bool RemoveBreakpoint(string file, int line)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        // Collected first: deleting while enumerating a COM collection skips entries.
        var doomed = new List<EnvDTE.Breakpoint>();
        foreach (EnvDTE.Breakpoint bp in _dte.Debugger.Breakpoints)
        {
            try { if (bp.FileLine == line && SamePath(bp.File, file)) doomed.Add(bp); }
            catch (Exception ex) { Services.Diagnostics.Swallow("VsDebugDriver.MatchBreakpoint", ex); }
        }

        foreach (var bp in doomed)
        {
            try { bp.Delete(); }
            catch (Exception ex) { Services.Diagnostics.Swallow("VsDebugDriver.DeleteBreakpoint", ex); }
        }
        return doomed.Count > 0;
    }

    private IReadOnlyList<DebugBreakpointInfo> ListBreakpoints()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var all = new List<DebugBreakpointInfo>();
        try
        {
            foreach (EnvDTE.Breakpoint bp in _dte.Debugger.Breakpoints)
            {
                try { all.Add(new DebugBreakpointInfo(bp.File ?? string.Empty, bp.FileLine, bp.Enabled)); }
                catch (Exception ex) { Services.Diagnostics.Swallow("VsDebugDriver.ReadBreakpoint", ex); }
            }
        }
        catch (Exception ex) { Services.Diagnostics.Swallow("VsDebugDriver.Breakpoints", ex); }
        return all;
    }

    /// <summary>
    /// Snapshots the paused debugger. Each section degrades to empty on its own: every property
    /// here is COM automation that can throw on native frames, detached sessions or evaluation
    /// timeouts, and losing the locals must not lose the call stack.
    /// </summary>
    private DebugStopState CaptureState()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var dbg = _dte.Debugger;

        var reason = "break";
        try { reason = dbg.LastBreakReason.ToString().Replace("dbgEventReason", string.Empty); }
        catch (Exception ex) { Services.Diagnostics.Swallow("VsDebugDriver.BreakReason", ex); }

        string? exception = null;
        try
        {
            var ex = dbg.GetExpression("$exception", false, EvalTimeoutMs);
            if (ex is not null && ex.IsValidValue) exception = $"`{ex.Type}` — {ex.Value}";
        }
        catch (Exception ex) { Services.Diagnostics.Swallow("VsDebugDriver.Exception", ex); }

        var threadId = 0;
        try { threadId = dbg.CurrentThread?.ID ?? 0; }
        catch (Exception ex) { Services.Diagnostics.Swallow("VsDebugDriver.Thread", ex); }

        // Frame ids are the 1-based position in this stack, which is also how a later `evaluate`
        // addresses a frame. They are meaningful only until execution resumes — which is fine,
        // because so is everything else in this snapshot.
        var frames = new List<DebugFrame>();
        try
        {
            var index = 0;
            foreach (EnvDTE.StackFrame frame in dbg.CurrentThread?.StackFrames ?? (IEnumerable)Array.Empty<object>())
            {
                index++;
                if (frames.Count >= MaxFrames) break;
                string? file = null;
                int?    line = null;
                try
                {
                    // FileName/LineNumber live on StackFrame2; going through IDispatch avoids the
                    // extra interop assembly and simply yields null on native/external frames.
                    dynamic f2 = frame;
                    file = f2.FileName as string;
                    line = (int)f2.LineNumber;
                    if (string.IsNullOrEmpty(file)) { file = null; line = null; }
                }
                catch (Exception ex) { Services.Diagnostics.Swallow("VsDebugDriver.FrameLocation", ex); }
                frames.Add(new DebugFrame(index, frame.FunctionName, file, line));
            }
        }
        catch (Exception ex) { Services.Diagnostics.Swallow("VsDebugDriver.StackFrames", ex); }

        var locals = new List<DebugVariable>();
        try
        {
            var current = dbg.CurrentStackFrame;
            if (current is not null)
            {
                foreach (EnvDTE.Expression local in current.Locals)
                {
                    if (locals.Count >= MaxLocals) break;
                    // ⚠ Names here include the IDE's own pseudo-variables, localised — a French VS
                    // adds "int.ToString returned" after a step. Never match on them.
                    try { locals.Add(new DebugVariable(local.Name, local.Type, local.Value)); }
                    catch (Exception ex) { Services.Diagnostics.Swallow("VsDebugDriver.LocalValue", ex); }
                }
            }
        }
        catch (Exception ex) { Services.Diagnostics.Swallow("VsDebugDriver.Locals", ex); }

        return new DebugStopState(reason, threadId, frames, locals, exception);
    }

    private string? Evaluate(string expression, int? frameId)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (string.IsNullOrWhiteSpace(expression) || !IsPaused) return null;

        var dbg = _dte.Debugger;
        if (frameId is > 0)
        {
            // Scoping an evaluation means making that frame current — that is what the automation
            // offers, and it is also what the user sees, which is honest.
            try
            {
                var index = 0;
                foreach (EnvDTE.StackFrame frame in dbg.CurrentThread?.StackFrames ?? (IEnumerable)Array.Empty<object>())
                    if (++index == frameId) { dbg.CurrentStackFrame = frame; break; }
            }
            catch (Exception ex) { Services.Diagnostics.Swallow("VsDebugDriver.SelectFrame", ex); }
        }

        try
        {
            var value = dbg.GetExpression(expression, false, EvalTimeoutMs);
            // An invalid expression is an ordinary answer: VS returns the diagnostic in Value.
            return value is null ? null
                 : value.IsValidValue ? value.Value
                 : null;
        }
        catch (Exception ex)
        {
            Services.Diagnostics.Swallow("VsDebugDriver.Evaluate", ex);
            return null;
        }
    }

    private static bool SamePath(string? a, string? b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
        try { return string.Equals(System.IO.Path.GetFullPath(a), System.IO.Path.GetFullPath(b),
                                   StringComparison.OrdinalIgnoreCase); }
        catch { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
    }

    /// <summary>Stops serving and withdraws the advertisement. Must be called on the UI thread.</summary>
    public void Dispose()
    {
        // Withdrawn first: a host that asks after this point is told there is no driver, instead of
        // waiting out a five-minute timeout on a devenv that is closing.
        Services.Signals.DebugCommandSignal.ClearReady();
        _cts.Cancel();

        if (_cookie != 0)
        {
            try
            {
#pragma warning disable VSTHRD010  // caller (GhostTextPackage.Dispose) switches to the main thread
                _debugger.UnadviseDebuggerEvents(_cookie);
#pragma warning restore VSTHRD010
            }
            catch (Exception ex) { Services.Diagnostics.Swallow("VsDebugDriver.Unsubscribe", ex); }
            _cookie = 0;
        }

        // Not waited on: this runs on the UI thread, and the loop may be mid-hop *onto* that same
        // thread — waiting would deadlock until the timeout and freeze VS's shutdown for nothing.
        // The token disposal rides on the loop instead, so it cannot be disposed under it.
        _ = _loop?.ContinueWith(_ => _cts.Dispose(), TaskScheduler.Default);
        _loop = null;
    }
}
