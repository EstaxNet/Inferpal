using System.IO;
using Inferpal.Services;
using Xunit;

namespace Inferpal.Tests;

// Serialized: the signal file path and the alive-check override are process-wide statics.
[Collection(SignalCollection.Name)]
public class DebuggerStateSignalTests : IDisposable
{
    private readonly SignalScratchDir _scratch = new();

    public DebuggerStateSignalTests()
    {
        DebuggerStateSignal.Clear();
        SignalFile._isProcessAliveOverride = _ => true;
    }

    public void Dispose()
    {
        SignalFile._isProcessAliveOverride = null;
        DebuggerStateSignal.Clear();
        _scratch.Dispose();
    }

    private static DebuggerSnapshot Snapshot(
        string reason = "Breakpoint", string? exception = null) => new(
        reason,
        exception,
        Frames: [new DebuggerFrame("Program.Main", "C:\\app\\Program.cs", 42),
                 new DebuggerFrame("[External Code]", null, null)],
        Locals: [new DebuggerLocal("count", "int", "3")],
        Pid:    1234,
        Ts:     1718000000000);

    // ── Write / TryRead round-trip ──────────────────────────────────────────────

    [Fact]
    public void TryRead_NoFile_ReturnsNull()
    {
        Assert.Null(DebuggerStateSignal.TryRead());
    }

    [Fact]
    public void WriteThenRead_RoundTrips()
    {
        DebuggerStateSignal.Write(Snapshot(exception: "`System.NullReferenceException` — boom"));

        var snap = DebuggerStateSignal.TryRead();

        Assert.NotNull(snap);
        Assert.Equal("Breakpoint", snap.Reason);
        Assert.Equal("`System.NullReferenceException` — boom", snap.Exception);
        Assert.Equal(2, snap.Frames.Count);
        Assert.Equal("Program.Main", snap.Frames[0].Function);
        Assert.Equal(42, snap.Frames[0].Line);
        Assert.Null(snap.Frames[1].File);
        Assert.Equal("count", Assert.Single(snap.Locals).Name);
    }

    [Fact]
    public void TryRead_DeadVsProcess_IsStale_ReturnsNull()
    {
        // A crashed VS session leaves the file behind — the pid check must reject it.
        DebuggerStateSignal.Write(Snapshot());
        SignalFile._isProcessAliveOverride = _ => false;

        Assert.Null(DebuggerStateSignal.TryRead());
    }

    [Fact]
    public void Clear_RemovesFile()
    {
        DebuggerStateSignal.Write(Snapshot());

        DebuggerStateSignal.Clear();

        Assert.False(File.Exists(DebuggerStateSignal.FilePath));
        Assert.Null(DebuggerStateSignal.TryRead());
    }

    [Fact]
    public void TryRead_CorruptedFile_ReturnsNull()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DebuggerStateSignal.FilePath)!);
        File.WriteAllText(DebuggerStateSignal.FilePath, "{ not json");

        Assert.Null(DebuggerStateSignal.TryRead());
    }

    // ── Format ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Format_FullSnapshot_HasAllSections()
    {
        var text = DebuggerStateSignal.Format(
            Snapshot(reason: "ExceptionThrown", exception: "`System.InvalidOperationException` — bad state"));

        // One vocabulary: this channel and the §21 port describe the same thing to the same reader,
        // and used to render it with two different headers.
        Assert.StartsWith("## Debugger paused", text);
        Assert.Contains("Stop reason: ExceptionThrown", text);
        Assert.Contains("### Exception", text);
        Assert.Contains("`System.InvalidOperationException` — bad state", text);
        Assert.Contains("- Program.Main  (C:\\app\\Program.cs:42)", text);
        Assert.Contains("- [External Code]", text);
        Assert.DoesNotContain("[External Code]  (", text);   // no file → no location suffix
        Assert.Contains("- `count` (int) = 3", text);
    }

    [Fact]
    public void Format_MinimalSnapshot_OmitsEmptySections()
    {
        var text = DebuggerStateSignal.Format(
            new DebuggerSnapshot("Breakpoint", null, [], [], 1, 0));

        Assert.Contains("Stop reason: Breakpoint", text);
        Assert.DoesNotContain("### Exception", text);
        Assert.DoesNotContain("### Call stack", text);
        Assert.DoesNotContain("### Locals", text);
    }

    [Fact]
    public void Format_LongStackAndLocals_AreCapped()
    {
        // The point of sharing the §21 renderer, not a side effect of it: this path had no budget
        // at all, so @debugger on a deep stack pasted every frame and every value into a window
        // that has room for neither.
        var text = DebuggerStateSignal.Format(new DebuggerSnapshot(
            "Breakpoint", null,
            Frames: [.. Enumerable.Range(0, 40).Select(i => new DebuggerFrame($"F{i}", null, null))],
            Locals: [.. Enumerable.Range(0, 40).Select(i => new DebuggerLocal($"v{i}", "string", new string('x', 500)))],
            Pid: 1, Ts: 0));

        Assert.Contains("more frame(s)", text);
        Assert.Contains("more local(s)", text);
        Assert.DoesNotContain(new string('x', 300), text);
    }

    /// <summary>
    /// The pushed channel filters the stack like the on-demand one. It did not: the workspace root
    /// stopped at <c>DebuggerStateReader</c>'s other branch, so Visual Studio — the front-end this
    /// channel exists for — answered the full stack where VS Code answered the user's frames, for
    /// one and the same question. The caps had been shared and the filtering left behind.
    /// </summary>
    [Fact]
    public void Format_KeepsTheStackToTheUsersFrames_LikeTheOnDemandChannel()
    {
        var snap = new DebuggerSnapshot(
            "Breakpoint", null,
            Frames: [new DebuggerFrame("RuntimeHelpers.Throw", @"C:\runtime\lib.cs", 7),
                     new DebuggerFrame("Program.Compute",      @"C:\ws\src\Program.cs", 14)],
            Locals: [], Pid: 1, Ts: 0);

        var text = DebuggerStateSignal.Format(snap, @"C:\ws");

        Assert.Contains("Program.Compute", text);
        Assert.DoesNotContain("RuntimeHelpers.Throw", text);
        Assert.Contains("1 runtime frame(s) outside the workspace hidden", text);

        // Witness: with no root, nothing is filtered -- the rule measures the argument, not luck.
        Assert.Contains("RuntimeHelpers.Throw", DebuggerStateSignal.Format(snap));
    }

    /// <summary>
    /// And the single reader passes that root on BOTH branches -- which is where it was dropped.
    /// </summary>
    [Fact]
    public void Reader_PassesTheWorkspaceRoot_OnThePushedBranchToo()
    {
        DebuggerStateSignal.Write(new DebuggerSnapshot(
            "Breakpoint", null,
            Frames: [new DebuggerFrame("RuntimeHelpers.Throw", @"C:\runtime\lib.cs", 7),
                     new DebuggerFrame("Program.Compute",      @"C:\ws\src\Program.cs", 14)],
            Locals: [], Pid: 1, Ts: 0));

        var text = Services.Debugging.DebuggerStateReader
                           .TryReadAsync(session: null, @"C:\ws", CancellationToken.None)
                                      .GetAwaiter().GetResult();

        Assert.NotNull(text);                                   // witness: the pushed branch did answer
        Assert.DoesNotContain("RuntimeHelpers.Throw", text);
    }
}
