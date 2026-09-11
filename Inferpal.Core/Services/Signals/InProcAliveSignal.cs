using System.Text.Json;

namespace Inferpal.Services.Signals;

/// <summary>
/// The heartbeat of the <b>in-process</b> half: "am I loaded in this devenv?", and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// ⚠ When the in-process half fails to load, it dies in <b>silence</b>: the chat window is
/// out-of-process and keeps working, so nothing looks broken while ghost text, the inline-edit diff
/// preview and the <c>/tdd</c> debugger driver have ceased to exist. This channel is the only place
/// the product can say so itself, within reach of a user.
/// </para>
/// <para>
/// ⚠ This is not <see cref="ActiveSolutionSignal"/>: that one is cleared when a solution closes, so
/// its absence proves nothing. This one is never cleared from the inside — written once at
/// initialization, and only the death of the process invalidates it.
/// </para>
/// <para>
/// <b>Three independent doors</b> — the package (<c>GhostTextPackage</c>, autoloaded), MEF
/// (<c>GhostTextViewListener</c>, at the first editor) and the debugger driver
/// (<c>VsDebugDriver</c>). Each lives without the others: MEF alone gives ghost text but no driver.
/// Every door registers by name, so a reader can say <em>which</em> one is missing.
/// </para>
/// <para>
/// ⚠ Driver start-up failures travel through <see cref="RecordDebuggerUnavailable"/>: their
/// <c>Diagnostics.Swallow</c> lands in the <em>in-process</em> ring, while <c>/diagnostics</c> reads
/// the out-of-process one — nobody could read them otherwise.
/// </para>
/// </remarks>
internal static class InProcAliveSignal
{
    /// <summary>The classic VS package — autoloaded by the pkgdef. <b>Host</b> of the debugger driver.</summary>
    /// <remarks>
    /// ⚠ Host, not door: a loaded package says <em>nothing</em> about the driver — measured, the two
    /// do come apart. <see cref="ComponentDebugger"/> answers for the driver, and it alone.
    /// </remarks>
    internal const string ComponentPackage = "package";

    /// <summary>The MEF bootstrap — when the first editor opens. Carries ghost text.</summary>
    internal const string ComponentMef = "mef";

    /// <summary>
    /// The <c>/tdd</c> debugger driver, started by the package once the IDE services answer.
    /// A third door because it is a third failure.
    /// </summary>
    internal const string ComponentDebugger = "debugger";

    /// <summary>Path of the heartbeat, scoped to the declared IDE instance (§22 slice 2).</summary>
    internal static string FilePath => SignalFile.ScopedPathFor("inproc_alive");

    /// <summary>
    /// The doors initialize on different threads (package init on a background thread,
    /// <c>TextViewCreated</c> on the UI thread) and registering is a read-modify-write. Without this
    /// lock, whichever arrives second can overwrite the first.
    /// </summary>
    private static readonly object Gate = new();

    // ── In-process side (Inferpal.InProc) ──────────────────────────────────────

    /// <summary>
    /// Records <paramref name="component"/> as loaded in this process, keeping whatever another door
    /// already recorded. Never throws.
    /// </summary>
    internal static void Record(string component) => Update(component, null);

    /// <summary>
    /// Records <b>why</b> the <c>/tdd</c> debugger driver did not start in this process.
    /// Never throws.
    /// </summary>
    /// <remarks>
    /// The caller is <c>GhostTextPackage</c>, the only site that knows all three branches. A reason
    /// is not a door: it does not join <c>components</c>, or a reader counting doors would read a
    /// failure as a load.
    /// </remarks>
    internal static void RecordDebuggerUnavailable(string reason) => Update(null, reason);

    private static void Update(string? component, string? reason)
    {
        lock (Gate)
        {
            var known = new List<string>();
            string? priorReason = null;
            var prior = SignalFile.TryRead<Payload>(FilePath);
            // Previous state is only carried over when it came from THIS process: a file left by a
            // dead devenv that happened to hold the same PID would advertise components that are
            // not there.
            if (prior?.pid == SignalFile.CurrentPid)
            {
                if (prior.components is not null) known.AddRange(prior.components);
                priorReason = prior.debuggerReason;
            }
            if (component is not null && !known.Contains(component)) known.Add(component);

            // Recording the debugger door clears the reason, and the converse does not exist: the
            // two cannot both be true, and the door has the last word.
            var finalReason = component == ComponentDebugger ? null : reason ?? priorReason;

            SignalFile.Write(FilePath, new Payload
            {
                pid            = SignalFile.CurrentPid,
                components     = known.ToArray(),
                debuggerReason = finalReason,
                version        = typeof(InProcAliveSignal).Assembly.GetName().Version?.ToString(3),
                ts             = SignalFile.Now.ToUnixTimeMilliseconds(),
            }, "InProcAliveSignal.Record");
        }
    }

    // ── Out-of-process side (ToolWindow, Host, /diagnostics) ───────────────────

    /// <summary>What the reader learns from the heartbeat.</summary>
    /// <param name="Pid">PID of the devenv that wrote it — by construction, the one hosting us.</param>
    /// <param name="Components">Loaded doors: <see cref="ComponentPackage"/>,
    /// <see cref="ComponentMef"/>, <see cref="ComponentDebugger"/>.</param>
    /// <param name="Version">Version of the in-process assembly, to spot a mismatched install.</param>
    /// <param name="DebuggerReason">Why the <c>/tdd</c> driver did not start, when it did not and
    /// the package got far enough to say so. <c>null</c> = nothing to report (driver present, or the
    /// package never reached the question).</param>
    internal sealed record State(
        int Pid, IReadOnlyList<string> Components, string? Version, string? DebuggerReason = null)
    {
        /// <summary>The package is loaded: the pkgdef autoload worked.</summary>
        /// <remarks>⚠ Does not imply <see cref="HasDebugger"/> — see <see cref="ComponentPackage"/>.</remarks>
        public bool HasPackage => Components.Contains(ComponentPackage);

        /// <summary>MEF composed our parts: ghost text and diff preview are alive.</summary>
        public bool HasMef => Components.Contains(ComponentMef);

        /// <summary>The debugger driver is serving: the §25 capture of <c>/tdd</c> is available.</summary>
        public bool HasDebugger => Components.Contains(ComponentDebugger);
    }

    /// <summary>
    /// The in-process state of <b>our</b> IDE instance, or <c>null</c> when nothing declared itself.
    /// </summary>
    /// <remarks>
    /// ⚠ <c>null</c> means "no evidence", not "it is dead": on the VS Code side there is no
    /// in-process peer at all (<see cref="SignalScope.HasVsInProcessPeer"/>), and on the VS side the
    /// MEF door only opens with the first editor. Callers must tell the two apart — that is what
    /// <see cref="SignalScope.HasVsInProcessPeer"/> is for here.
    /// </remarks>
    internal static State? TryRead()
    {
        // A host with no in-process peer (VS Code) would read the heartbeat of the Visual Studio
        // next door. Somebody else's state is not a better answer than no answer.
        if (!SignalScope.HasVsInProcessPeer) return null;

        var p = SignalFile.TryRead<Payload>(FilePath);
        if (p is null || p.pid <= 0 || p.components is null || p.components.Length == 0) return null;

        // The process that wrote it must be alive, otherwise this is a leftover.
        if (!SignalFile.IsProcessAlive(p.pid)) return null;

        // …and the heartbeat must be later than that process's start: a PID recycled by a new devenv
        // which did not load the in-process half would read green without this bound.
        // Unreachable (rights, platform) => do not degrade to red, liveness is enough.
        try
        {
            var started = System.Diagnostics.Process.GetProcessById(p.pid).StartTime.ToUniversalTime();
            if (DateTimeOffset.FromUnixTimeMilliseconds(p.ts).UtcDateTime < started) return null;
        }
        catch (Exception ex) { Diagnostics.Swallow("InProcAliveSignal.StartTime", ex); }

        return new State(p.pid, p.components, p.version, p.debuggerReason);
    }

    /// <summary>
    /// Three states, so the caller cannot confuse "it is dead" with "I do not know": <c>true</c>
    /// loaded, <c>false</c> <b>proof</b> that it is not, <c>null</c> no in-process peer (VS Code) —
    /// nothing to say.
    /// </summary>
    /// <remarks>
    /// The <c>false</c> is legitimate because the package heartbeat is written at autoload
    /// <em>with no solution open</em> (<c>AutoLoadPackages\{f1536ef8-…}</c>): its absence on a
    /// running IDE is a load failure, not a door that has not yet had the chance to open. The MEF
    /// door, by contrast, waits for the first editor — one half alone is therefore not worth a
    /// <c>false</c>, it is only qualified in <see cref="DescribeForBundle"/>.
    /// </remarks>
    internal static bool? IsLoadedOrNull() =>
        !SignalScope.HasVsInProcessPeer ? null : TryRead() is not null;

    /// <summary>A line ready for the <c>/diagnostics export</c> support bundle (English by design).</summary>
    /// <remarks>
    /// This is the highest-value point: the bundle is what a user pastes into an issue. A
    /// "ghost text does nothing" report that already carries the answer saves the round trip — and
    /// it is the only thing in the repository that observes the in-process half on somebody else's
    /// machine.
    /// </remarks>
    internal static string DescribeForBundle()
    {
        if (!SignalScope.HasVsInProcessPeer) return "n/a (no in-process peer)";
        var s = TryRead();
        if (s is null) return "NOT LOADED (ghost text, inline diff preview and the /tdd debugger driver are unavailable)";
        var half = s.HasPackage && s.HasMef ? "package+mef"
                 : s.HasPackage             ? "package only (MEF not composed yet - open a source file)"
                                            : "mef only (package autoload did not run)";
        // Said separately, because it is a separate failure: the package can be loaded and the
        // driver absent, and this line used to claim the opposite from HasPackage alone.
        var driver = s.HasDebugger                       ? "/tdd debugger driver ready"
                   : s.DebuggerReason is { Length: > 0 } r ? $"/tdd debugger driver UNAVAILABLE ({r})"
                   : s.HasPackage                        ? "/tdd debugger driver UNAVAILABLE (not advertised)"
                                                         : "/tdd debugger driver unavailable (no package)";
        return $"{half}, {driver}, pid {s.Pid}, v{s.Version ?? "unknown"}";
    }

    /// <summary>Serialized shape. Public fields named like the JSON: this is a DTO, not a model.</summary>
    private sealed class Payload
    {
        public int pid { get; set; }
        public string[]? components { get; set; }
        public string? debuggerReason { get; set; }
        public string? version { get; set; }
        public long ts { get; set; }
    }
}
