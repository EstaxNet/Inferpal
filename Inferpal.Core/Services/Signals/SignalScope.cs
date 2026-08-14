namespace Inferpal.Services.Signals;

/// <summary>
/// Declares whether this process has an <b>in-process Visual Studio peer</b> — that is, whether the
/// signal files under <c>%TEMP%\Inferpal</c> were written <em>for it</em>.
/// </summary>
/// <remarks>
/// <para>
/// ROADMAP §22, reduced slice. The signal directory is <b>machine-wide</b>: one folder, one fixed
/// file name per channel, shared by every Inferpal process on the box. The readers of
/// <see cref="ActiveSolutionSignal"/> and <see cref="DebuggerStateSignal"/> live in
/// <c>Inferpal.Core</c>, so they are served by <em>both</em> front-ends — and a
/// <c>Inferpal.Host</c> driving VS Code therefore answered with the solution open in <b>Visual
/// Studio</b>, and with the break state of <b>Visual Studio's</b> debugger. Not a two-VS collision:
/// one VS plus one VS Code is enough, which is this repository's daily setup.
/// </para>
/// <para>
/// The first correction (reduced slice) was a <b>read policy</b>: a process with no in-process VS
/// peer does not consult those files at all. Tranche 2 then keyed the family-A channels — the §21
/// debug transport included, once its human validation pass was done (2026-08-15) — on the devenv
/// PID via <see cref="VsInstanceKey"/>, so two VS instances stop reading each other's state.
/// </para>
/// <para>
/// <b>Why the default is <c>true</c>, and it is not laziness.</b> Failing the other way round costs
/// more than it saves: a VS host that forgot to opt <em>in</em> would silently lose
/// <c>get_solution_info</c>, <c>/map</c> and RAG rooting — a real regression, in the adapter that is
/// hardest to test. A Host that forgot to opt <em>out</em> merely keeps today's behaviour. The
/// process that knows it has no VS peer is <see cref="Inferpal.Host"/>, it says so in one line, and
/// a test pins that line so the knowledge cannot quietly disappear.
/// </para>
/// </remarks>
internal static class SignalScope
{
    /// <summary>
    /// <c>true</c> (default) when signals written by an in-process VS package are meant for this
    /// process. Set to <c>false</c> by hosts that have no such peer — see
    /// <see cref="DeclareNoVsInProcessPeer"/>.
    /// </summary>
    internal static bool HasVsInProcessPeer { get; private set; } = true;

    /// <summary>
    /// Declares that this process is <b>not</b> paired with a Visual Studio in-process package, so
    /// the VS-published signals are somebody else's state and must not be read.
    /// </summary>
    /// <remarks>
    /// Called by <c>Inferpal.Host</c> at construction. Explicit declaration rather than inference:
    /// the same reasoning as the <c>debug: true</c> capability of §21 — a process that guesses its
    /// own role guesses wrong the day a third front-end appears.
    /// </remarks>
    internal static void DeclareNoVsInProcessPeer() => HasVsInProcessPeer = false;

    /// <summary>
    /// The devenv PID this process belongs to, or <c>null</c> when no instance was declared.
    /// Family-A channels (§22 tranche 2) key their file names on it, so two Visual Studio
    /// instances stop reading — and overwriting — each other's state.
    /// </summary>
    /// <remarks>
    /// The key is the one probe 6 measured green on both sides without any VS API: the in-process
    /// package is devenv, so it declares its <b>own</b> PID; the out-of-process extensibility host
    /// is a direct child of its devenv, so it declares its <b>parent</b> PID. <c>null</c> keeps
    /// the legacy unscoped file names — today's behaviour — so a front-end that forgot to declare
    /// degrades to the pre-§22 world instead of losing its signal pairing (same "the default is
    /// chosen, not suffered" reasoning as <see cref="HasVsInProcessPeer"/>).
    /// </remarks>
    internal static int? VsInstanceKey => _vsInstanceKey == 0 ? null : _vsInstanceKey;

    // A volatile int with 0 as the "none" sentinel rather than a Nullable<int>: the key is
    // declared during startup on one thread (MEF static ctor / AsyncPackage init /
    // InitializeServices) while other threads already resolve scoped paths, and an 8-byte
    // Nullable<int> store has no CLR atomicity guarantee — a torn read could surface as
    // (HasValue: true, Value: 0) and resolve a ".0.json" path no peer ever touches. An int32
    // store is atomic and PIDs are strictly positive, so 0 is free to mean "not declared".
    private static volatile int _vsInstanceKey;

    /// <summary>Declares which devenv this process belongs to. See <see cref="VsInstanceKey"/>.
    /// A non-positive pid is ignored (0 is the "none" sentinel).</summary>
    internal static void DeclareVsInstance(int devenvPid)
    {
        if (devenvPid > 0) _vsInstanceKey = devenvPid;
    }

    /// <summary>Test seam: restores the defaults so cases can run in any order.</summary>
    internal static void ResetForTests()
    {
        HasVsInProcessPeer = true;
        _vsInstanceKey     = 0;
    }
}
