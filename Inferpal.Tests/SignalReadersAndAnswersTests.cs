using System.IO;
using Inferpal.Services.Signals;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// The files Visual Studio and the extension talk through: a reader never refuses a writer, an answer takes away its own
/// request only, and the debugger's capture for <c>/tdd</c> stops waiting once its runner has ended.
/// </summary>
[Collection(SignalCollection.Name)]
public sealed class SignalReadersAndAnswersTests : IDisposable
{
    private readonly SignalScratchDir _signals = new();

    public void Dispose() => _signals.Dispose();

    /// <summary>
    /// ⚠ A signal read at the moment the other side replaces it: opened as <c>File.ReadAllText</c> opens it, the reader
    /// made the replace fail, swallowed — the debugger's withdrawal lost, a "paused" state left over a running program.
    /// </summary>
    [Fact]
    public void ASignalBeingRead_CanStillBeReplaced()
    {
        if (!OperatingSystem.IsWindows()) return;   // the sharing rules are Windows'
        var path = ErrorListSelectionSignal.RequestPath;
        Assert.True(SignalFile.Write(path, new ErrorListAsk("first", Environment.ProcessId, 0), "test"));

        bool replaced;
        using (new FileStream(path, FileMode.Open, FileAccess.Read, SignalFile.ReaderShare))   // a reader, mid-read
            replaced = SignalFile.Write(path, new ErrorListAsk("second", Environment.ProcessId, 0), "test");

        Assert.True(replaced);
        Assert.Equal("second", SignalFile.TryRead<ErrorListAsk>(path)?.Id);
    }

    /// <summary>⚠ A late answer to request A takes away A only: B, asked since, stays to be served — as the Output pane's
    /// twin already did.</summary>
    [Fact]
    public void ALateErrorListAnswer_LeavesTheNewerRequest()
    {
        SignalFile.Write(ErrorListSelectionSignal.RequestPath, new ErrorListAsk("B", Environment.ProcessId, 0), "test");

        ErrorListSelectionSignal.Answer(new ErrorListAnswer("A", []));

        Assert.Equal("B", SignalFile.TryRead<ErrorListAsk>(ErrorListSelectionSignal.RequestPath)?.Id);

        // Reference arm: its own request goes.
        ErrorListSelectionSignal.Answer(new ErrorListAnswer("B", []));
        Assert.False(File.Exists(ErrorListSelectionSignal.RequestPath));
    }

    /// <summary>⚠ The solution is published once, not once per project it loads: a 300-project solution made some 600
    /// writes of the same path on devenv's UI thread. The tracker needs Visual Studio: its code is read.</summary>
    [Fact]
    public void TheSolution_IsPublishedOncePerSolution_NotPerProject()
    {
        var tracker = ConventionCoverageTests.CodeOnly(Path.Combine(ConversationPersistenceSilenceTests.RepoRoot(),
                                                                    "Inferpal.InProc", "GhostText", "VsSolutionTracker.cs"));
        Assert.Contains("if (full != _published && File.Exists(full))", tracker, StringComparison.Ordinal);
        Assert.Contains("dir != _published", tracker, StringComparison.Ordinal);
        // Remembered once written only: a write that failed is tried again at the next project.
        Assert.Contains("if (ActiveSolutionSignal.Write(full)) _published = full;", tracker, StringComparison.Ordinal);
        Assert.Contains("_published = null;", tracker, StringComparison.Ordinal);   // a solution closed is published again
    }

    /// <summary>⚠ The capture for /tdd stops waiting for a stop once its runner has ended: a test that passes alone never
    /// stops, and each round waited out the whole 90 s. The driver needs Visual Studio: its loop is read.</summary>
    [Fact]
    public void TheTddCapture_StopsWaitingOnceItsRunnerEnded()
    {
        var driver = ConventionCoverageTests.CodeOnly(Path.Combine(ConversationPersistenceSilenceTests.RepoRoot(),
                                                                   "Inferpal.InProc", "GhostText", "VsDebugDriver.cs"));
        var wait = driver.IndexOf("var breakDeadline = NowMs() + 90_000;", StringComparison.Ordinal);
        Assert.True(wait > 0, "the capture's wait moved: the rule measures nothing");   // WITNESS
        var loop = driver[wait..driver.IndexOf("await Task.Delay(150, ct);", wait, StringComparison.Ordinal)];
        Assert.Contains("if (runner.HasExited) break;", loop, StringComparison.Ordinal);
    }

    /// <summary>
    /// ⚠ Visual Studio's break state is captured when asked, never at each stop: captured on every mode change, the stack,
    /// up to twenty locals and the exception were read on the UI thread at each step of the user's own debugging session.
    /// </summary>
    [Fact]
    public void TheBreakState_IsCapturedWhenAsked_NeverAtEachStop()
    {
        var inProc = Path.Combine(ConventionCoverageTests.RepoRoot(), "Inferpal.InProc");
        var listeners = Directory.EnumerateFiles(inProc, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Where(f => ConventionCoverageTests.CodeOnly(f).Contains("IVsDebuggerEvents", StringComparison.Ordinal))
            .Select(Path.GetFileName).ToList();
        Assert.Equal(["VsDebugDriver.cs"], listeners);   // the one listener left: the driver, which serves requests

        // No other break event is listened to either (EnvDTE's DebuggerEvents.OnEnterBreakMode).
        Assert.DoesNotContain(Directory.EnumerateFiles(inProc, "*.cs", SearchOption.AllDirectories),
            f => ConventionCoverageTests.CodeOnly(f).Contains("OnEnterBreakMode", StringComparison.Ordinal));

        // The mode handler records the mode, and calls nothing else — no capture, directly or through a helper.
        var driver = ConventionCoverageTests.CodeOnly(Path.Combine(inProc, "GhostText", "VsDebugDriver.cs"));
        var onMode = driver.IndexOf("int IVsDebuggerEvents.OnModeChange", StringComparison.Ordinal);
        Assert.True(onMode > 0, "the driver's mode handler is gone: the rule measures nothing");   // witness
        var body = driver[onMode..driver.IndexOf("\n    }", onMode, StringComparison.Ordinal)];
        var calls = System.Text.RegularExpressions.Regex.Matches(body[body.IndexOf('{')..], @"([A-Za-z_][\w.]*)\s*\(")
            .Select(m => m.Groups[1].Value).ToList();
        Assert.True(calls.Count > 0, "no call read in the mode handler: the rule measures nothing");   // witness
        Assert.All(calls, c => Assert.Contains(c, new[] { "Volatile.Write", "Interlocked.Increment" }));
    }
}
