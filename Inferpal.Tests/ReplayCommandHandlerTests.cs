using System.IO;
using Inferpal.Localization;
using Inferpal.Services.Commands;
using Xunit;

namespace Inferpal.Tests;

// Tool-call journal behind /replay: recording on HistoryRun / FileHistoryService (pure) plus the
// markdown rendering of ReplayCommandHandler. Same session memory as /undo-run.
public class ReplayCommandHandlerTests
{
    private static readonly string[] NoArgs = ["/replay"];

    // ── Journal recording (pure) ───────────────────────────────────────────────

    [Fact]
    public void RecordToolCall_AssignsIncrementingSequence()
    {
        var run = new HistoryRun("r1");
        run.RecordToolCall("read_file",  TestPaths.P(@"C:\a.cs"), 10, error: false);
        run.RecordToolCall("apply_diff", TestPaths.P(@"C:\a.cs"), 250, error: true);

        Assert.Equal(2, run.ToolCallCount);
        Assert.Equal([1, 2], run.ToolCalls.Select(c => c.Seq));
        Assert.True(run.ToolCalls[1].Error);
    }

    [Fact]
    public void RecordToolCall_WithoutActiveRun_IsANoOp()
    {
        var svc = new FileHistoryService();                 // no BeginRun
        svc.RecordToolCall("read_file", "a.cs", 5, error: false);

        Assert.Empty(svc.Runs);
    }

    [Fact]
    public void RecordToolCall_AttachesToCurrentRun()
    {
        var svc = new FileHistoryService();
        svc.BeginRun();
        svc.RecordToolCall("list_files", ".", 3, error: false);
        svc.BeginRun();                                     // new run — journal starts empty
        svc.RecordToolCall("read_file", "b.cs", 7, error: false);

        Assert.Equal("read_file",  svc.Runs[0].ToolCalls.Single().Tool);
        Assert.Equal("list_files", svc.Runs[1].ToolCalls.Single().Tool);
    }

    // ── Rendering ──────────────────────────────────────────────────────────────

    [Fact]
    public void Handle_NoRuns_ReturnsNone()
    {
        Assert.Equal(Strings.ReplayNone, ReplayCommandHandler.Handle([], NoArgs, root: null));
    }

    [Fact]
    public void Handle_RendersToolLines_DurationsAndErrorMarker()
    {
        var run = new HistoryRun("r1");
        run.RecordToolCall("read_file", TestPaths.P(@"C:\proj\Services\A.cs"), 85,   error: false);
        run.RecordToolCall("run_command", "dotnet build",         2340, error: true);

        var text = ReplayCommandHandler.Handle([run], NoArgs, root: TestPaths.P(@"C:\proj"));

        Assert.Contains("1. `read_file`", text);
        Assert.Contains(Path.Combine("Services", "A.cs"), text);   // absolute path relativised to root
        Assert.Contains("85 ms", text);
        Assert.Contains("2. `run_command` dotnet build", text);
        Assert.True(text.Contains("2.3 s") || text.Contains("2,3 s"),   // decimal separator is culture-dependent
                    $"seconds-formatted duration missing in: {text}");
        Assert.Contains("⚠", text);
    }

    [Fact]
    public void Handle_ListsCreatedAndModifiedFiles()
    {
        var run = new HistoryRun("r1");
        run.RecordToolCall("write_file", TestPaths.P(@"C:\proj\new.cs"), 12, error: false);
        run.RecordFirst(TestPaths.P(@"C:\proj\new.cs"), snapshot: null);        // created
        run.RecordFirst(TestPaths.P(@"C:\proj\old.cs"), snapshot: "snap");      // modified

        var text = ReplayCommandHandler.Handle([run], NoArgs, root: TestPaths.P(@"C:\proj"));

        Assert.Contains(Strings.ReplayFilesHeader, text);
        Assert.Contains("🆕 new.cs", text);
        Assert.Contains("✏ old.cs", text);
    }

    [Fact]
    public void Handle_SkipsRunsWithoutToolCalls()
    {
        var withCalls = new HistoryRun("old");
        withCalls.RecordToolCall("read_file", "a.cs", 5, error: false);
        var silent = new HistoryRun("new");                        // most recent, but empty

        var text = ReplayCommandHandler.Handle([silent, withCalls], NoArgs, root: null);

        Assert.Contains("`read_file`", text);
    }

    [Fact]
    public void Handle_IndexOutOfRange_ReturnsNone()
    {
        var run = new HistoryRun("r1");
        run.RecordToolCall("read_file", "a.cs", 5, error: false);

        Assert.Equal(Strings.ReplayNone, ReplayCommandHandler.Handle([run], ["/replay", "2"], root: null));
    }

    [Fact]
    public void Handle_NonNumericIndex_ReturnsUsage()
    {
        Assert.Equal(Strings.SlashUsage("/replay [n]"),
                     ReplayCommandHandler.Handle([], ["/replay", "abc"], root: null));
    }

    [Fact]
    public void Handle_SecondRun_SelectableByIndex()
    {
        var newer = new HistoryRun("r2");
        newer.RecordToolCall("read_file", "new.cs", 5, error: false);
        var older = new HistoryRun("r1");
        older.RecordToolCall("read_file", "old.cs", 5, error: false);

        var text = ReplayCommandHandler.Handle([newer, older], ["/replay", "2"], root: null);

        Assert.Contains("old.cs", text);
        Assert.DoesNotContain("new.cs", text);
    }
}
