using System.IO;
using Inferpal.Localization;
using Inferpal.Services.Debugging;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// "Analyze with Inferpal" on a debugger stop: what reaches the chat is the stop as <c>@debugger</c> renders it and the
/// code around the user's frames — read through the same reader as <c>@debugger</c>, from the workspace only, and
/// sized for the answering model — or nothing, with its cause.
/// </summary>
public sealed class DebugStopAnalysisTests : IDisposable
{
    private const int Budget = 60_000;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "inferpal-tests", $"stop-{Guid.NewGuid():N}");
    private readonly string _elsewhere = Path.Combine(Path.GetTempPath(), "inferpal-tests", $"elsewhere-{Guid.NewGuid():N}");

    public DebugStopAnalysisTests()
    {
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_elsewhere);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
        try { Directory.Delete(_elsewhere, recursive: true); } catch { /* best-effort cleanup */ }
    }

    /// <summary>A debugger that only answers where it stopped; driving it is no part of an analysis.</summary>
    private sealed class PausedSession(DebugStopState? state, bool silent = false) : IDebugSession
    {
        public bool IsAvailable => true;
        public Task<DebugStopState?> GetStateAsync(CancellationToken ct) =>
            silent ? throw new DebuggerNotAnsweringException("no answer in 20 s") : Task.FromResult(state);
        public Task<DebugBreakpointInfo?> AddBreakpointAsync(string file, int line, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> RemoveBreakpointAsync(string file, int line, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<DebugBreakpointInfo>> ListBreakpointsAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<DebugStartResult> StartAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<DebugResumeResult> ContinueAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<DebugResumeResult> StepAsync(DebugStepKind kind, CancellationToken ct) => throw new NotSupportedException();
        public Task<string?> EvaluateAsync(string expression, int? frameId, CancellationToken ct) => throw new NotSupportedException();
        public Task StopAsync(CancellationToken ct) => throw new NotSupportedException();
    }

    private static string WriteFile(string dir, string name, int lines, string tag = "line")
    {
        var path = Path.Combine(dir, name);
        File.WriteAllText(path, string.Join("\n", Enumerable.Range(1, lines).Select(i => $"    var {tag}{i} = {i}; // {new string('x', 40)}")));
        return path;
    }

    [Fact]
    public async Task NothingPaused_SendsNothing_AndSaysSo()
    {
        var none = await DebugStopAnalysis.ForAsync(new PausedSession(null), _root, overlay: null, Budget, CancellationToken.None);
        Assert.Equal((Strings.AnalyzeStopNotPaused, true, ""), (none.Refusal, none.NothingPaused, none.Content));

        var absent = await DebugStopAnalysis.ForAsync(session: null, _root, overlay: null, Budget, CancellationToken.None);
        Assert.True(absent.NothingPaused);
    }

    [Fact]
    public async Task ADebuggerThatDoesNotAnswer_IsSaid_NeverTakenForNotPaused()
    {
        var silent = await DebugStopAnalysis.ForAsync(new PausedSession(null, silent: true), _root, overlay: null, Budget,
                                                      CancellationToken.None);

        Assert.Equal(Strings.DebugNoAnswer("no answer in 20 s"), silent.Refusal);
        Assert.False(silent.NothingPaused);
    }

    [Fact]
    public async Task APausedProgram_SendsTheStop_AndTheCodeAroundTheUsersFrames_NeverAFileOutsideTheWorkspace()
    {
        var program = WriteFile(_root, "Program.cs", 400);
        var outside = WriteFile(_elsewhere, "Runtime.cs", 50, tag: "runtimeSecret");
        var state = new DebugStopState("exception", 1,
            [new DebugFrame(0, "Program.Compute", program, 300), new DebugFrame(1, "Lib.Sum", outside, 12)],
            [new DebugVariable("total", "int", "106")],
            Exception: "System.DivideByZeroException: Attempted to divide by zero.");

        var asked = await DebugStopAnalysis.ForAsync(new PausedSession(state), _root, overlay: null, Budget, CancellationToken.None);

        Assert.Null(asked.Refusal);
        Assert.Equal((Strings.AnalyzeStopQuestion, Strings.AnalyzeStopChip), (asked.Question, asked.Chip));
        Assert.Contains("DivideByZeroException", asked.Content);
        Assert.Contains("Program.Compute", asked.Content);
        Assert.Contains("total", asked.Content);
        Assert.Contains("Code around the frames:", asked.Content);
        Assert.Contains("var line300 = 300;", asked.Content);
        Assert.DoesNotContain("var line200 = 200;", asked.Content);   // a window around the frame, not the file's start
        Assert.DoesNotContain("runtimeSecret", asked.Content);        // outside the workspace: never read
        // Answered without tools: no note names one to read the rest with.
        Assert.DoesNotContain("read_file", asked.Content);
    }

    [Fact]
    public async Task AStopInLibraryCodeOnly_ReadsNoFileOutsideTheWorkspace_AndSaysSo()
    {
        // No frame under the root: the stack shows them all, and their files are still not the workspace's.
        var outside = WriteFile(_elsewhere, "Library.cs", 50, tag: "librarySecret");
        var state = new DebugStopState("exception", 1, [new DebugFrame(0, "Lib.Parse", outside, 20)], []);

        var asked = await DebugStopAnalysis.ForAsync(new PausedSession(state), _root, overlay: null, Budget, CancellationToken.None);

        Assert.Contains("Lib.Parse", asked.Content);
        Assert.DoesNotContain("librarySecret", asked.Content);
        Assert.Contains("(1 frame file(s) outside the workspace, not read)", asked.Content);
    }

    [Fact]
    public async Task AShortFile_IsShownAroundTheFrame_UnderItsLineNumbers()
    {
        // Whole and unnumbered, the model would count to the frame's line with no tool to help.
        var program = WriteFile(_root, "Small.cs", 60);
        var state = new DebugStopState("breakpoint", 1, [new DebugFrame(0, "Small.Run", program, 40)], []);

        var asked = await DebugStopAnalysis.ForAsync(new PausedSession(state), _root, overlay: null, Budget, CancellationToken.None);

        Assert.Contains("Lines 30–50 of 60:", asked.Content);
    }

    [Fact]
    public async Task TheAttachment_FitsTheAnsweringModelsWindow_AndItsChipSaysWhenItIsCut()
    {
        var program = WriteFile(_root, "Program.cs", 400);
        var state = new DebugStopState("breakpoint", 1, [new DebugFrame(0, "Program.Run", program, 300)], []);

        var asked = await DebugStopAnalysis.ForAsync(new PausedSession(state), _root, overlay: null, budgetChars: 600,
                                                     CancellationToken.None);

        Assert.InRange(asked.Content.Length, 1, 900);   // the budget, and the marker that says where it stops
        Assert.NotEqual(Strings.AnalyzeStopChip, asked.Chip);
        Assert.StartsWith(Strings.AnalyzeStopChip, asked.Chip);
    }

}
