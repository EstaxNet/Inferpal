using Inferpal.Services.Signals;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// The in-process heartbeat: "is the component loaded in THIS devenv?".
/// </summary>
/// <remarks>
/// <para>
/// These cases lock the one thing that made the in-process failure invisible: a run of false
/// verdicts came out of external probes guessing that fact from artefacts VS writes for itself.
/// This channel is the direct answer, and what matters here is not that it says "yes" - it is that
/// it refuses to say "yes" on a leftover.
/// </para>
/// <para>
/// ⚠ The two leftover cases are not decoration: a recycled PID is exactly what the PID-scoped
/// naming grammar (§22 slice 2) makes possible, and a green on a leftover would be worse than no
/// channel at all - it would get a legitimate bug report closed.
/// </para>
/// </remarks>
[Collection(SignalCollection.Name)]
public sealed class InProcAliveSignalTests : IDisposable
{
    private readonly SignalScratchDir _dir = new();

    public InProcAliveSignalTests() => SignalScope.ResetForTests();

    public void Dispose()
    {
        SignalScope.ResetForTests();
        SignalFile._isProcessAliveOverride = null;
        SignalFile._nowOverride            = null;
        _dir.Dispose();
    }

    [Fact]
    public void Record_ThenRead_ReportsTheComponent()
    {
        SignalScope.DeclareVsInstance(SignalFile.CurrentPid);
        InProcAliveSignal.Record(InProcAliveSignal.ComponentPackage);

        var state = InProcAliveSignal.TryRead();

        Assert.NotNull(state);
        Assert.Equal(SignalFile.CurrentPid, state!.Pid);
        Assert.True(state.HasPackage);
        Assert.False(state.HasMef);
    }

    [Fact]
    public void Record_TwoDoors_KeepsBoth()
    {
        // The two doors initialize separately (package autoload, first editor for MEF): whichever
        // arrives second must not erase the first, or the diagnostic announces "mef only" on a
        // devenv whose package is perfectly loaded.
        SignalScope.DeclareVsInstance(SignalFile.CurrentPid);
        InProcAliveSignal.Record(InProcAliveSignal.ComponentPackage);
        InProcAliveSignal.Record(InProcAliveSignal.ComponentMef);

        var state = InProcAliveSignal.TryRead();

        Assert.NotNull(state);
        Assert.True(state!.HasPackage);
        Assert.True(state.HasMef);
    }

    [Fact]
    public void Record_IsIdempotent()
    {
        SignalScope.DeclareVsInstance(SignalFile.CurrentPid);
        InProcAliveSignal.Record(InProcAliveSignal.ComponentMef);
        InProcAliveSignal.Record(InProcAliveSignal.ComponentMef);

        Assert.Equal(new[] { InProcAliveSignal.ComponentMef }, InProcAliveSignal.TryRead()!.Components);
    }

    [Fact]
    public void TryRead_NothingWritten_IsNull()
    {
        SignalScope.DeclareVsInstance(SignalFile.CurrentPid);
        Assert.Null(InProcAliveSignal.TryRead());
    }

    [Fact]
    public void TryRead_WriterProcessIsDead_IsNull()
    {
        // Leftover from a closed devenv: the file survives, the process does not.
        SignalScope.DeclareVsInstance(SignalFile.CurrentPid);
        InProcAliveSignal.Record(InProcAliveSignal.ComponentPackage);
        SignalFile._isProcessAliveOverride = _ => false;

        Assert.Null(InProcAliveSignal.TryRead());
    }

    [Fact]
    public void TryRead_HeartbeatOlderThanTheProcess_IsNull()
    {
        // Recycled PID: a dead devenv wrote the heartbeat, a NEW devenv carries the same PID and
        // did not load the in-process half. The file exists, its process is "alive", and only the
        // comparison with the start time separates the two.
        SignalScope.DeclareVsInstance(SignalFile.CurrentPid);
        SignalFile._nowOverride = () => DateTimeOffset.UtcNow.AddHours(-4);
        InProcAliveSignal.Record(InProcAliveSignal.ComponentPackage);
        SignalFile._nowOverride = null;

        // The current process necessarily started after "4 hours ago" in a test run; if it did
        // not, the case would be vacuous, so we check it.
        Assert.True(System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime()
                    > DateTime.UtcNow.AddHours(-4),
                    "test process too old: this case would measure nothing");
        Assert.Null(InProcAliveSignal.TryRead());
    }

    [Fact]
    public void TryRead_NoVsInProcessPeer_IsNull()
    {
        // On the VS Code side, the heartbeat of the Visual Studio next door is not our state - and
        // a false green there would make a front-end with no in-process half claim to have one.
        SignalScope.DeclareVsInstance(SignalFile.CurrentPid);
        InProcAliveSignal.Record(InProcAliveSignal.ComponentPackage);
        SignalScope.DeclareNoVsInProcessPeer();

        Assert.Null(InProcAliveSignal.TryRead());
        Assert.Equal("n/a (no in-process peer)", InProcAliveSignal.DescribeForBundle());
    }

    [Fact]
    public void DescribeForBundle_SaysWhichHalfIsMissing()
    {
        SignalScope.DeclareVsInstance(SignalFile.CurrentPid);

        // Nothing: this is the case the support bundle must make legible in a GitHub issue.
        Assert.Contains("NOT LOADED", InProcAliveSignal.DescribeForBundle());

        InProcAliveSignal.Record(InProcAliveSignal.ComponentMef);
        Assert.Contains("mef only", InProcAliveSignal.DescribeForBundle());

        InProcAliveSignal.Record(InProcAliveSignal.ComponentPackage);
        Assert.Contains("package+mef", InProcAliveSignal.DescribeForBundle());
    }

    // ── The third door: the /tdd debugger driver (§25) ──────────────────────────────
    //
    // Measured: components = ["package"], active_solution written, and NO debug_ready. So the
    // package was loaded and the driver absent - two facts this channel used to conflate, by
    // advertising the driver as available as soon as the package was.

    [Fact]
    public void PackageAlone_DoesNotClaimTheDebuggerDriver()
    {
        SignalScope.DeclareVsInstance(SignalFile.CurrentPid);
        InProcAliveSignal.Record(InProcAliveSignal.ComponentPackage);

        Assert.False(InProcAliveSignal.TryRead()!.HasDebugger);
        Assert.Contains("UNAVAILABLE", InProcAliveSignal.DescribeForBundle());
    }

    [Fact]
    public void DebuggerDoor_IsRecordedBesideTheOthers()
    {
        SignalScope.DeclareVsInstance(SignalFile.CurrentPid);
        InProcAliveSignal.Record(InProcAliveSignal.ComponentPackage);
        InProcAliveSignal.Record(InProcAliveSignal.ComponentDebugger);

        var state = InProcAliveSignal.TryRead()!;
        Assert.True(state.HasPackage);
        Assert.True(state.HasDebugger);
        Assert.Null(state.DebuggerReason);
        Assert.Contains("/tdd debugger driver ready", InProcAliveSignal.DescribeForBundle());
    }

    [Fact]
    public void DebuggerReason_SurvivesTheOtherDoors_AndReachesTheBundle()
    {
        // The reason is written by the package, then MEF registers when the first editor opens:
        // if the second write erased it, the failure would go mute again on the first file opened -
        // that is, always, in real use.
        SignalScope.DeclareVsInstance(SignalFile.CurrentPid);
        InProcAliveSignal.Record(InProcAliveSignal.ComponentPackage);
        InProcAliveSignal.RecordDebuggerUnavailable("DTE automation unavailable");
        InProcAliveSignal.Record(InProcAliveSignal.ComponentMef);

        var state = InProcAliveSignal.TryRead()!;
        Assert.False(state.HasDebugger);
        Assert.Equal("DTE automation unavailable", state.DebuggerReason);
        Assert.Contains("DTE automation unavailable", InProcAliveSignal.DescribeForBundle());
    }

    [Fact]
    public void DebuggerReason_IsNotADoor()
    {
        // A reason must not join `components`: a reader counting doors would read a failure as a
        // load, and that is exactly the kind of green this file exists to prevent.
        SignalScope.DeclareVsInstance(SignalFile.CurrentPid);
        InProcAliveSignal.Record(InProcAliveSignal.ComponentPackage);
        InProcAliveSignal.RecordDebuggerUnavailable("VS debugger service unavailable");

        Assert.Equal(new[] { InProcAliveSignal.ComponentPackage }, InProcAliveSignal.TryRead()!.Components);
    }

    [Fact]
    public void DebuggerDoor_ClearsAStaleReason()
    {
        // The reverse order is possible (reason written by one branch, driver started afterwards):
        // the door has the last word, otherwise the bundle would accuse a driver that is serving.
        SignalScope.DeclareVsInstance(SignalFile.CurrentPid);
        InProcAliveSignal.RecordDebuggerUnavailable("driver failed to start: COMException");
        InProcAliveSignal.Record(InProcAliveSignal.ComponentDebugger);

        var state = InProcAliveSignal.TryRead()!;
        Assert.True(state.HasDebugger);
        Assert.Null(state.DebuggerReason);
    }
}
