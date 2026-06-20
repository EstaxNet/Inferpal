using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell.Interop;
using Inferpal.Services;

namespace Inferpal.GhostText;

/// <summary>
/// In-process <see cref="IVsDebuggerEvents"/> subscriber. Instantiated by
/// <see cref="GhostTextPackage.InitializeAsync"/> on the VS UI thread. When the debugger
/// enters break mode it captures a snapshot (break reason, exception, call stack, locals)
/// through the EnvDTE debugger automation and publishes it to
/// <see cref="DebuggerStateSignal"/> for the out-of-process host (<c>@debugger</c> mention,
/// <c>get_debugger_state</c> tool); the signal is cleared as soon as execution resumes.
/// </summary>
internal sealed class VsDebuggerTracker : IVsDebuggerEvents, IDisposable
{
    private const int MaxFrames        = 12;
    private const int MaxLocals        = 20;
    private const int MaxValueChars    = 200;

    private readonly IVsDebugger    _debugger;
    private readonly EnvDTE.DTE?    _dte;
    private          uint           _cookie;

    /// <summary>Subscribes to debugger mode changes. UI-thread only.</summary>
    internal VsDebuggerTracker(IVsDebugger debugger, EnvDTE.DTE? dte)
    {
        Microsoft.VisualStudio.Shell.ThreadHelper.ThrowIfNotOnUIThread();
        _debugger = debugger;
        _dte      = dte;
        debugger.AdviseDebuggerEvents(this, out _cookie);
        // A session may already be paused when the package loads (package auto-load can
        // happen mid-debug) — publish the current state if so.
        var mode = new DBGMODE[1];
        if (debugger.GetMode(mode) == VSConstants.S_OK && mode[0] == DBGMODE.DBGMODE_Break)
            PublishBreakState();
    }

    int IVsDebuggerEvents.OnModeChange(DBGMODE dbgmodeNew)
    {
        Microsoft.VisualStudio.Shell.ThreadHelper.ThrowIfNotOnUIThread();
        if (dbgmodeNew == DBGMODE.DBGMODE_Break)
            PublishBreakState();
        else
            DebuggerStateSignal.Clear();   // Run or Design: the snapshot is no longer true
        return VSConstants.S_OK;
    }

    /// <summary>
    /// Captures the EnvDTE debugger state. Every property is COM automation that can throw
    /// (detached debugger, native frames, evaluation timeouts), so each section degrades to
    /// empty independently rather than losing the whole snapshot.
    /// </summary>
    private void PublishBreakState()
    {
        Microsoft.VisualStudio.Shell.ThreadHelper.ThrowIfNotOnUIThread();
        var dbg = _dte?.Debugger;
        if (dbg is null) return;

        var reason = "break";
        try { reason = dbg.LastBreakReason.ToString().Replace("dbgEventReason", string.Empty); }
        catch { }

        string? exception = null;
        try
        {
            // "$exception" is the debugger pseudo-variable for the exception being inspected.
            var ex = dbg.GetExpression("$exception", false, 1000);
            if (ex is not null && ex.IsValidValue)
                exception = $"`{ex.Type}` — {Cap(ex.Value)}";
        }
        catch { }

        var frames = new List<DebuggerFrame>();
        try
        {
            foreach (EnvDTE.StackFrame frame in dbg.CurrentThread.StackFrames)
            {
                if (frames.Count >= MaxFrames) break;
                string? file = null;
                int?    line = null;
                try
                {
                    // FileName/LineNumber live on StackFrame2 (later EnvDTE interop); going
                    // through IDispatch avoids referencing the extra interop assembly and
                    // simply yields null on native/external frames.
                    dynamic f2 = frame;
                    file = f2.FileName as string;
                    line = (int)f2.LineNumber;
                    if (string.IsNullOrEmpty(file)) { file = null; line = null; }
                }
                catch { }
                frames.Add(new DebuggerFrame(frame.FunctionName, file, line));
            }
        }
        catch { }

        var locals = new List<DebuggerLocal>();
        try
        {
            var current = dbg.CurrentStackFrame;
            if (current is not null)
            {
                foreach (EnvDTE.Expression local in current.Locals)
                {
                    if (locals.Count >= MaxLocals) break;
                    try { locals.Add(new DebuggerLocal(local.Name, local.Type, Cap(local.Value))); }
                    catch { }
                }
            }
        }
        catch { }

        DebuggerStateSignal.Write(new DebuggerSnapshot(
            reason, exception, frames, locals,
            Pid: System.Diagnostics.Process.GetCurrentProcess().Id,
            Ts:  DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
    }

    private static string Cap(string? value) =>
        value is null               ? string.Empty :
        value.Length <= MaxValueChars ? value :
        value[..MaxValueChars] + "…";

    /// <summary>Unregisters the subscription. Must be called on the UI thread.</summary>
    public void Dispose()
    {
        if (_cookie == 0) return;
        try
        {
#pragma warning disable VSTHRD010  // caller (GhostTextPackage.Dispose) switches to the main thread
            _debugger.UnadviseDebuggerEvents(_cookie);
#pragma warning restore VSTHRD010
        }
        catch { }
        _cookie = 0;
        DebuggerStateSignal.Clear();
    }
}
