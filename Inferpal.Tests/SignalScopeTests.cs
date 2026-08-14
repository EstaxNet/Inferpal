using System.IO;
using Inferpal.Config;
using Inferpal.Services;
using Inferpal.Services.Signals;
using Inferpal.Services.Tools;
using System.Text.Json;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// ROADMAP §22, reduced slice: a process with no in-process Visual Studio peer must not read the
/// signals a VS package publishes into the machine-wide folder.
/// </summary>
/// <remarks>
/// The defect these pin is not the two-VS collision the §21 probes raised — it is one VS plus one
/// VS Code, because the readers live in <c>Inferpal.Core</c> and are therefore served by both
/// front-ends. Each case writes a real signal first, so the guard is measured against a file that
/// <em>would</em> have answered; without that, both branches return null and the test would pin
/// nothing.
/// </remarks>
[Collection(SignalCollection.Name)]
public class SignalScopeTests : IDisposable
{
    private readonly SignalScratchDir _scratch = new();
    private readonly string _slnPath;
    private readonly string _slnPathB;

    public SignalScopeTests()
    {
        // Both readers validate the recorded path exists on disk, so it has to be a real file.
        _slnPath  = Path.Combine(Path.GetTempPath(), $"Inferpal_scope_{Guid.NewGuid():N}.sln");
        _slnPathB = Path.Combine(Path.GetTempPath(), $"Inferpal_scope_{Guid.NewGuid():N}.sln");
        File.WriteAllText(_slnPath,  "Microsoft Visual Studio Solution File");
        File.WriteAllText(_slnPathB, "Microsoft Visual Studio Solution File");
        SignalFile._isProcessAliveOverride = _ => true;
    }

    public void Dispose()
    {
        SignalFile._isProcessAliveOverride = null;
        SignalScope.ResetForTests();
        try { File.Delete(_slnPath); }  catch { }
        try { File.Delete(_slnPathB); } catch { }
        _scratch.Dispose();
    }

    private static DebuggerSnapshot Snapshot() => new(
        "Breakpoint", null,
        Frames: [new DebuggerFrame("Program.Main", @"C:\app\Program.cs", 42)],
        Locals: [new DebuggerLocal("count", "int", "3")],
        Pid: 1234, Ts: 1718000000000);

    // ── The reference arm: with a VS peer, nothing changes (G5) ─────────────────────

    [Fact]
    public void WithVsPeer_ActiveSolution_IsRead()
    {
        ActiveSolutionSignal.Write(_slnPath);

        Assert.Equal(_slnPath, ActiveSolutionSignal.TryReadSolutionPath());
    }

    [Fact]
    public void WithVsPeer_DebuggerState_IsRead()
    {
        DebuggerStateSignal.Write(Snapshot());

        Assert.NotNull(DebuggerStateSignal.TryRead());
    }

    // ── The guard ──────────────────────────────────────────────────────────────────

    [Fact]
    public void WithoutVsPeer_ActiveSolution_IsNotRead()
    {
        ActiveSolutionSignal.Write(_slnPath);
        SignalScope.DeclareNoVsInProcessPeer();

        // The file is there and valid — it is simply not ours.
        Assert.True(File.Exists(ActiveSolutionSignal.FilePath));
        Assert.Null(ActiveSolutionSignal.TryReadSolutionPath());
        Assert.Null(ActiveSolutionSignal.TryReadSolutionDir());
    }

    [Fact]
    public void WithoutVsPeer_DebuggerState_IsNotRead()
    {
        DebuggerStateSignal.Write(Snapshot());
        SignalScope.DeclareNoVsInProcessPeer();

        Assert.True(File.Exists(DebuggerStateSignal.FilePath));
        Assert.Null(DebuggerStateSignal.TryRead());
    }

    /// <summary>
    /// The tool answers "no session" rather than handing the model another editor's call stack —
    /// and it says so in the string it already had, so this costs no new localisation key.
    /// </summary>
    [Fact]
    public async Task WithoutVsPeer_GetDebuggerStateTool_ReportsNoSession()
    {
        DebuggerStateSignal.Write(Snapshot());
        SignalScope.DeclareNoVsInProcessPeer();

        var text = await new GetDebuggerStateTool()
            .ExecuteAsync(JsonDocument.Parse("{}").RootElement, CancellationToken.None);

        Assert.StartsWith("No paused debug session", text);
        Assert.DoesNotContain("Program.Main", text);
    }

    // ── The declaration itself ─────────────────────────────────────────────────────

    /// <summary>
    /// Pins the one line in <c>Inferpal.Host</c> that makes all of the above true in VS Code. The
    /// default is deliberately "has a VS peer" — a VS host that forgot to opt in would silently
    /// lose solution rooting, which is the worse failure — so the whole correction rests on this
    /// call, and a test has to hold it there.
    /// </summary>
    [Fact]
    public void Host_DeclaresItHasNoVsPeer()
    {
        Assert.True(SignalScope.HasVsInProcessPeer);

        using var server = new Inferpal.Host.HostServer(
            providerFactory: (_) => new FakeInferenceProvider(),
            configFactory:   () => new InferpalConfig());

        Assert.False(SignalScope.HasVsInProcessPeer);
    }

    // ── §22 tranche 2 (gate G1): family A scoped per instance ──────────────────────
    // Two Visual Studios, two solutions: each reads ITS OWN.

    [Fact]
    public void TwoInstances_ActiveSolution_EachReadsItsOwn()
    {
        SignalScope.DeclareVsInstance(111);
        ActiveSolutionSignal.Write(_slnPath);
        SignalScope.DeclareVsInstance(222);
        ActiveSolutionSignal.Write(_slnPathB);

        SignalScope.DeclareVsInstance(111);
        Assert.Equal(_slnPath, ActiveSolutionSignal.TryReadSolutionPath());
        SignalScope.DeclareVsInstance(222);
        Assert.Equal(_slnPathB, ActiveSolutionSignal.TryReadSolutionPath());
    }

    [Fact]
    public void OtherInstancesSolution_IsNotRead()
    {
        SignalScope.DeclareVsInstance(111);
        ActiveSolutionSignal.Write(_slnPath);

        SignalScope.DeclareVsInstance(222);
        Assert.Null(ActiveSolutionSignal.TryReadSolutionPath());
    }

    [Fact]
    public void LegacyUnscopedActiveSolution_IgnoredOnceKeyDeclared()
    {
        // §22 migration (family A): the unscoped file of an earlier version is never read as
        // this instance's own — and never deleted (an old-version pair may still be using it).
        var legacy = SignalFile.PathFor("active_solution.json");
        SignalFile.Write(legacy, new { solutionPath = _slnPath, ts = 0L }, "test");

        SignalScope.DeclareVsInstance(111);
        Assert.Null(ActiveSolutionSignal.TryReadSolutionPath());
        Assert.True(File.Exists(legacy));
    }

    [Fact]
    public void NoInstanceDeclared_KeepsLegacyNameAndBehaviour()
    {
        // G5: a front-end that declared no instance keeps the pre-§22 world.
        ActiveSolutionSignal.Write(_slnPath);

        Assert.EndsWith("active_solution.json", ActiveSolutionSignal.FilePath);
        Assert.Equal(_slnPath, ActiveSolutionSignal.TryReadSolutionPath());
    }

    [Fact]
    public void TwoInstances_BuildSignal_EachReadsItsOwn()
    {
        SignalScope.DeclareVsInstance(111);
        BuildSignalFile.Write(_slnPath, new[] { "error CS0001" });

        SignalScope.DeclareVsInstance(222);
        Assert.Null(BuildSignalFile.TryRead().SolutionPath);

        SignalScope.DeclareVsInstance(111);
        Assert.Equal(_slnPath, BuildSignalFile.TryRead().SolutionPath);
    }

    [Fact]
    public void TwoInstances_InlineDiffRequest_IsNotServedToTheOther()
    {
        const string target = @"C:\x\f.cs";
        SignalScope.DeclareVsInstance(111);
        var id = InlineDiffPreviewSignal.WriteRequest(target, "a", "b");

        SignalScope.DeclareVsInstance(222);
        Assert.Null(InlineDiffPreviewSignal.TryReadRequestFor(target));

        SignalScope.DeclareVsInstance(111);
        Assert.Equal(id, InlineDiffPreviewSignal.TryReadRequestFor(target)!.Id);
    }

    // ── §22 end of slice (gate G2): the /debug transport joins the per-instance scope ──
    // Unlocked by the 1.6.0 human validation pass (done, 2026-08-15): a /debug emitted by
    // instance A must NEVER be executed by instance B.

    [Fact]
    public void DebugRequestFromOneInstance_IsNeverClaimedByAnother()
    {
        SignalScope.DeclareVsInstance(111);
        DebugCommandSignal.WriteRequest(new DebugCommandRequest(
            "id1", Environment.ProcessId, SignalFile.Now.ToUnixTimeMilliseconds(), "start"));

        SignalScope.DeclareVsInstance(222);
        Assert.Null(DebugCommandSignal.ClaimRequest());   // B sees nothing to execute

        SignalScope.DeclareVsInstance(111);
        Assert.Equal("id1", DebugCommandSignal.ClaimRequest()!.Id);   // A still serves its own
    }

    [Fact]
    public void DriverReadyAdvertisement_IsPerInstance()
    {
        SignalScope.DeclareVsInstance(111);
        DebugCommandSignal.MarkReady(Environment.ProcessId);

        SignalScope.DeclareVsInstance(222);
        Assert.False(DebugCommandSignal.IsDriverReady());   // another devenv's driver is not ours

        SignalScope.DeclareVsInstance(111);
        Assert.True(DebugCommandSignal.IsDriverReady());
    }

    [Fact]
    public void TwoInstances_DebuggerState_EachReadsItsOwn()
    {
        SignalScope.DeclareVsInstance(111);
        DebuggerStateSignal.Write(Snapshot());

        SignalScope.DeclareVsInstance(222);
        Assert.Null(DebuggerStateSignal.TryRead());

        SignalScope.DeclareVsInstance(111);
        Assert.NotNull(DebuggerStateSignal.TryRead());
    }
}
