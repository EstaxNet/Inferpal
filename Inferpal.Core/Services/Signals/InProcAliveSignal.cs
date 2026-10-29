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
    /// ⚠ Host, not door: a loaded package says <em>nothing</em> about the driver, and the two do
    /// come apart. <see cref="ComponentDebugger"/> answers for the driver, and it alone.
    /// </remarks>
    internal const string ComponentPackage = "package";

    /// <summary>The MEF bootstrap — when the first editor opens. Carries ghost text.</summary>
    internal const string ComponentMef = "mef";

    /// <summary>
    /// The <c>/tdd</c> debugger driver, started by the package once the IDE services answer.
    /// A third door because it is a third failure.
    /// </summary>
    internal const string ComponentDebugger = "debugger";

    /// <summary>
    /// The FIM sidecar — recorded once a completion has actually come back from it.
    /// </summary>
    /// <remarks>
    /// ⚠ A fourth door for the same reason as the third: ghost text is what a user notices missing
    /// first, and its failures were written to the in-process <c>Diagnostics</c> ring — not the one
    /// <c>/diagnostics</c> reads. The sidecar is a child process with neither the host's TPA nor
    /// devenv's binding redirects, so "it does not start" is a real and invisible state.
    /// </remarks>
    internal const string ComponentFim = "fim";

    /// <summary>Path of the heartbeat, scoped to the declared IDE instance.</summary>
    internal static string FilePath => SignalFile.ScopedPathFor("inproc_alive");

    /// <summary>
    /// The doors initialize on different threads (package init on a background thread,
    /// <c>TextViewCreated</c> on the UI thread) and registering is a read-modify-write. Without this
    /// lock, whichever arrives second can overwrite the first.
    /// </summary>
    private static readonly object Gate = new();

    /// <summary>What this process recorded, per heartbeat file: every write copies it whole.</summary>
    /// <remarks>⚠ Rebuilt from the file instead (read, modify, write), a door is lost whenever the file cannot be read
    /// back — deleted from <c>%TEMP%</c>, read during a replace — and the next write drops it for good.</remarks>
    private static readonly Dictionary<string, Heartbeat> Recorded = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The doors and reasons of one heartbeat file, and whether its last write reached the disk.</summary>
    private sealed class Heartbeat
    {
        public readonly List<string> Doors = new();
        public string? DebuggerReason;
        public string? FimReason;
        public bool Written;
    }

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
    internal static void RecordDebuggerUnavailable(string reason) => Update(null, debuggerReason: reason);

    /// <summary>Why ghost text is silent, for the half of the product that cannot reach the host ring.</summary>
    internal static void RecordFimUnavailable(string reason) => Update(null, fimReason: reason);

    private static void Update(string? component, string? debuggerReason = null, string? fimReason = null)
    {
        lock (Gate)
        {
            var path = FilePath;
            if (!Recorded.TryGetValue(path, out var beat)) Recorded[path] = beat = new Heartbeat();

            var changed = false;
            if (component is not null && !beat.Doors.Contains(component)) { beat.Doors.Add(component); changed = true; }

            // A door and its reason cannot both be true, and the LATEST has the last word, both ways:
            // recording the door clears the reason, recording a reason closes the door. ⚠ The FIM
            // sidecar answers, then can die, be recycled or stop answering: a door kept open by its
            // first answer made the bundle say "answering" forever, its reason written and never read.
            if (debuggerReason is not null)
            {
                changed |= beat.Doors.Remove(ComponentDebugger) || beat.DebuggerReason != debuggerReason;
                beat.DebuggerReason = debuggerReason;
            }
            if (fimReason is not null)
            {
                changed |= beat.Doors.Remove(ComponentFim) || beat.FimReason != fimReason;
                beat.FimReason = fimReason;
            }
            if (component == ComponentDebugger && beat.DebuggerReason is not null) { beat.DebuggerReason = null; changed = true; }
            if (component == ComponentFim && beat.FimReason is not null) { beat.FimReason = null; changed = true; }

            // ⚠ Nothing new, and on disk: not written again. The MEF door is recorded at every editor opened, on the UI
            // thread — rewritten each time, a solution reopening thirty tabs reads and writes the file thirty times. A
            // write that failed, or a file deleted since, is written again.
            if (!changed && beat.Written && System.IO.File.Exists(path)) return;

            beat.Written = SignalFile.Write(path, new Payload
            {
                pid            = SignalFile.CurrentPid,
                components     = beat.Doors.ToArray(),
                debuggerReason = beat.DebuggerReason,
                fimReason      = beat.FimReason,
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
        int Pid, IReadOnlyList<string> Components, string? Version, string? DebuggerReason = null,
        string? FimReason = null)
    {
        /// <summary>The package is loaded: the pkgdef autoload worked.</summary>
        /// <remarks>⚠ Does not imply <see cref="HasDebugger"/> — see <see cref="ComponentPackage"/>.</remarks>
        public bool HasPackage => Components.Contains(ComponentPackage);

        /// <summary>MEF composed our parts: ghost text and diff preview are alive.</summary>
        public bool HasMef => Components.Contains(ComponentMef);

        /// <summary>The debugger driver is serving: the debugger capture of <c>/tdd</c> is available.</summary>
        public bool HasDebugger => Components.Contains(ComponentDebugger);

        /// <summary>The FIM sidecar has answered at least once in this process.</summary>
        public bool HasFim => Components.Contains(ComponentFim);
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

        // The process that wrote it must be alive, and the heartbeat later than its start: a PID recycled by a new devenv
        // which did not load the in-process half would read green otherwise. An unreadable start time leaves liveness
        // to decide, never red.
        if (!SignalFile.WriterAlive(p.pid, p.ts)) return null;

        return new State(p.pid, p.components, p.version, p.debuggerReason, p.fimReason);
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
        // driver absent, so HasPackage alone cannot answer for it.
        var driver = s.HasDebugger                       ? "/tdd debugger driver ready"
                   : s.DebuggerReason is { Length: > 0 } r ? $"/tdd debugger driver UNAVAILABLE ({r})"
                   : s.HasPackage                        ? "/tdd debugger driver UNAVAILABLE (not advertised)"
                                                         : "/tdd debugger driver unavailable (no package)";
        var fim = s.HasFim                              ? "FIM sidecar answering"
                : s.FimReason is { Length: > 0 } f       ? $"FIM sidecar UNAVAILABLE ({f})"
                                                         : "FIM sidecar not used yet";
        return $"{half}, {driver}, {fim}, pid {s.Pid}, v{s.Version ?? "unknown"}";
    }

    /// <summary>Serialized shape. Public fields named like the JSON: this is a DTO, not a model.</summary>
    private sealed class Payload
    {
        public int pid { get; set; }
        public string[]? components { get; set; }
        public string? debuggerReason { get; set; }
        public string? fimReason { get; set; }
        public string? version { get; set; }
        public long ts { get; set; }
    }
}
