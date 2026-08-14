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
/// The correction is a <b>read policy</b>, not a transport change: a process with no in-process VS
/// peer does not consult those files at all. It deliberately leaves the §21 transport
/// (<see cref="DebugCommandSignal"/>) untouched while its human validation pass is still pending —
/// scoping that one by devenv PID is tranche 2, and churning it now would invalidate the pass.
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

    /// <summary>Test seam: restores the default so cases can run in any order.</summary>
    internal static void ResetForTests() => HasVsInProcessPeer = true;
}
