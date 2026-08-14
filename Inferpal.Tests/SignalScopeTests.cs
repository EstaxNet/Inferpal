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

    public SignalScopeTests()
    {
        // Both readers validate the recorded path exists on disk, so it has to be a real file.
        _slnPath = Path.Combine(Path.GetTempPath(), $"Inferpal_scope_{Guid.NewGuid():N}.sln");
        File.WriteAllText(_slnPath, "Microsoft Visual Studio Solution File");
        SignalFile._isProcessAliveOverride = _ => true;
    }

    public void Dispose()
    {
        SignalFile._isProcessAliveOverride = null;
        SignalScope.ResetForTests();
        try { File.Delete(_slnPath); } catch { }
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
}
