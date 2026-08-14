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
            case SignalDebugSession.OpAddBreakpoint:
            {
                await Jtf.SwitchToMainThreadAsync(ct);
                var bound = AddBreakpoint(request.File!, request.Line);
                await TaskScheduler.Default.SwitchTo();
                return new(request.Id, Ok: true, Breakpoints: bound);
            }

            case SignalDebugSession.OpRemoveBreakpoint:
            {
                await Jtf.SwitchToMainThreadAsync(ct);
                var removed = RemoveBreakpoint(request.File!, request.Line);
                await TaskScheduler.Default.SwitchTo();
                return new(request.Id, Ok: true, Flag: removed);
            }

            case SignalDebugSession.OpListBreakpoints:
            {
                await Jtf.SwitchToMainThreadAsync(ct);
                var all = ListBreakpoints();
                await TaskScheduler.Default.SwitchTo();
                return new(request.Id, Ok: true, Breakpoints: all);
            }

            case SignalDebugSession.OpStart:
            case SignalDebugSession.OpContinue:
            case SignalDebugSession.OpStepOver:
            case SignalDebugSession.OpStepInto:
            case SignalDebugSession.OpStepOut:
                return new(request.Id, Ok: true, State: await ResumeAndWaitAsync(request.Op, ct));

            case SignalDebugSession.OpState:
            {
                if (!IsPaused) return new(request.Id, Ok: true, State: null);
                await Jtf.SwitchToMainThreadAsync(ct);
                var state = CaptureState();
                await TaskScheduler.Default.SwitchTo();
                return new(request.Id, Ok: true, State: state);
            }

            case SignalDebugSession.OpEvaluate:
            {
                await Jtf.SwitchToMainThreadAsync(ct);
                var text = Evaluate(request.Expression ?? string.Empty, request.FrameId);
                await TaskScheduler.Default.SwitchTo();
                return text is null
                    ? new(request.Id, Ok: false, Error: "The expression could not be evaluated.")
                    : new(request.Id, Ok: true, Text: text);
            }

            case SignalDebugSession.OpStop:
            {
                await Jtf.SwitchToMainThreadAsync(ct);
                _dte.Debugger.Stop(false);
                await TaskScheduler.Default.SwitchTo();
                return new(request.Id, Ok: true);
            }

            default:
                return new(request.Id, Ok: false, Error: $"Unknown debugger operation '{request.Op}'.");
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
            case SignalDebugSession.OpStart:
            case SignalDebugSession.OpContinue: _dte.ExecuteCommand("Debug.Start"); break;
            case SignalDebugSession.OpStepOver: _dte.Debugger.StepOver(false);      break;
            case SignalDebugSession.OpStepInto: _dte.Debugger.StepInto(false);      break;
            case SignalDebugSession.OpStepOut:  _dte.Debugger.StepOut(false);       break;
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
                    // adds « int.ToString retourné » after a step. Never match on them.
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
