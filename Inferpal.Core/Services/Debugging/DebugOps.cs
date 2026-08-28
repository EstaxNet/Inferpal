namespace Inferpal.Services.Debugging;

/// <summary>
/// Operation names on the <see cref="Signals.DebugCommandSignal"/> wire.
/// </summary>
/// <remarks>
/// <para>
/// The two ends of this wire are compiled into different assemblies <b>and</b> run under different
/// runtimes: the caller (<see cref="SignalDebugSession"/>) lives in the net8 core hosted
/// out-of-process, the driver (<c>Inferpal.GhostText.VsDebugDriver</c>) in the net472 in-process
/// assembly loaded by <c>devenv</c>. A typo on either side is a silent no-op, never a compile
/// error — which is why the names live in this one file, shared by source link
/// (<c>Inferpal.InProc.csproj</c>) rather than retyped.
/// </para>
/// </remarks>
internal static class DebugOps
{
    internal const string AddBreakpoint    = "add_bp";
    internal const string RemoveBreakpoint = "remove_bp";
    internal const string ListBreakpoints  = "list_bp";
    internal const string Start            = "start";
    internal const string Continue         = "continue";
    internal const string StepOver         = "step_over";
    internal const string StepInto         = "step_into";
    internal const string StepOut          = "step_out";
    internal const string State            = "state";
    internal const string Evaluate         = "evaluate";
    internal const string Stop             = "stop";

    /// <summary>§25: attach to a waiting repro runner and capture the unhandled-exception stop.</summary>
    internal const string CaptureTest      = "capture_test";
}
