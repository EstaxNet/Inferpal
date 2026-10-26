using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using Inferpal.Config;
using Inferpal.Localization;
using Inferpal.Models;
using Inferpal.Services;
using Inferpal.Services.Commands;
using Inferpal.Services.Docs;
using Inferpal.Services.Editor;
using Inferpal.Services.Execution;
using Inferpal.Services.Lsp;
using Inferpal.Services.Mcp;
using Inferpal.Services.Rag;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// <c>/test coverage</c>: a test file is kept only when it passes three times, asserts, skips nothing and covers new lines;
/// every other one leaves the workspace with its copy kept, and <c>/undo-run</c> gives back what the command kept. Played
/// on a fake ecosystem whose test files say what they do (<c>covers: 7,8</c>, <c>FAIL</c>, <c>FLAKY</c>…).
/// </summary>
public sealed class TestCoverageCommandTests : IDisposable
{
    private const string Calc =
        "def add(a, b):\n    if a < 0:\n        return 0\n    return a + b\n\ndef sub(a, b):\n    if a < 0:\n        return 0\n    return a - b\n";

    // add (1-4): 3 and 4 uncovered; sub (6-9): 7, 8 and 9 uncovered — sub is the first target.
    private static readonly Dictionary<int, int> CalcHits = new() { [1] = 1, [2] = 1, [3] = 0, [4] = 0, [6] = 1, [7] = 0, [8] = 0, [9] = 0 };

    private readonly string _root = Path.Combine(Path.GetTempPath(), "inferpal-tests", $"coverage-{Guid.NewGuid():N}");
    private readonly InferpalConfig _config = new() { SmartFixEnabled = false };
    private readonly FakeInferenceProvider _client = new();
    private readonly ProjectIndexService _index;
    private readonly ToolRegistry _tools;
    private readonly FakeEcosystem _ecosystem;

    public TestCoverageCommandTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        Directory.CreateDirectory(Path.Combine(_root, "tests"));
        File.WriteAllText(Path.Combine(_root, "src", "calc.py"), Calc);
        _ecosystem = new FakeEcosystem(_root) { Baseline = { ["src/calc.py"] = CalcHits } };
        _index = new ProjectIndexService(_client, _config, new LspSemanticProvider());
        _index.SetRoot(_root);
        _tools = Registry(new NoopApproval());
    }

    private ToolRegistry Registry(IApprovalService approval)
    {
        var editor = new NullEditorSurface();
        return new ToolRegistry(editor, approval, _config, _index, _client, new ProjectMapService(editor),
                                new McpToolService(_config, approval), new DocsIndexService(_client, _config),
                                new OpenDocumentOverlay(), new NullDebugSession());
    }

    public void Dispose()
    {
        _held?.Dispose();
        _index.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    /// <summary>The model answers each target with <paramref name="reply"/>(target, attempt) as a code block; null throws.</summary>
    private void Answer(Func<string, int, string?> reply)
    {
        var attempts = new Dictionary<string, int>();
        _client.OnChatRequest = (_, messages, _, _) =>
        {
            var target = Regex.Match(messages[1].Content ?? string.Empty, @"TARGET: (\w+)").Groups[1].Value;
            attempts[target] = attempts.GetValueOrDefault(target) + 1;
            var text = reply(target, attempts[target]) ?? throw new HttpRequestException("connection refused");
            return Task.FromResult(new ChatTurnResult($"```python\n{text}\n```", null, 0, 0));
        };
    }

    private Task<string> RunAsync(ToolRegistry? tools = null, params string[] args) =>
        TestCoverageCommandHandler.HandleAsync(
            _client, _config, tools ?? _tools, (tools ?? _tools).History, ["/test", "coverage", .. args], _root,
            onProgress: null, CancellationToken.None,
            detect: (_, _, _) => Task.FromResult<(ICoverageEcosystem?, string?)>((_ecosystem, null)));

    private string TestPath(string stem = "calc") => Path.Combine(_root, "tests", $"test_{stem}_coverage.py");

    private HistoryRun TheRun => _tools.History.Runs[^1];

    [Fact]
    public async Task ATestThatPassesAssertsAndCoversNewLines_IsKept_AndTheReportSaysWhatItGained()
    {
        Answer((target, _) => target == "sub" ? "assert sub(-1, 1) == 0\n# covers: 7,8,9" : "# FAIL");

        var report = await RunAsync();

        Assert.True(File.Exists(TestPath()));
        Assert.Contains(Strings.CoverageKeptHeader(1), report, StringComparison.Ordinal);
        Assert.Contains(Strings.CoverageKeptRow("tests/test_calc_coverage.py", "sub", 3), report, StringComparison.Ordinal);
        // 3 of 8 lines before, 6 of 8 after.
        Assert.Contains(Strings.CoverageSummary(37.5.ToString("0.0"), 75.0.ToString("0.0"), 37.5.ToString("+0.0;-0.0;0.0"), 6, 8),
                        report, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("# FAIL\n# covers: 7,8,9\nassert True", "NoPass")]
    [InlineData("# FLAKY\n# covers: 7,8,9\nassert True", "NoPass")]
    [InlineData("# covers: 7,8,9\nsub(1, 2)", "NoAssertion")]
    [InlineData("# covers: 7,8,9\nassert True  # pytest.mark.skip", "Skips")]
    [InlineData("# covers: 1,2\nassert True", "NothingNew")]
    [InlineData("# UNMEASURED\n# covers: 7\nassert True", "NotMeasured")]
    public async Task ATestThatFailsAFilter_LeavesTheWorkspace_WithItsCopyKept(string body, string verdict)
    {
        Answer((target, _) => target == "sub" ? body : "# FAIL");

        var report = await RunAsync();

        Assert.False(File.Exists(TestPath()));
        var copy = TheRun.RedoSourceOf(TestPath());
        Assert.NotNull(copy);
        Assert.True(File.Exists(copy));
        Assert.Contains(Strings.CoverageSetAsideHeader(2), report, StringComparison.Ordinal);
        Assert.Contains(Strings.CoverageSetAsideRow("sub", WhyOf(Enum.Parse<CoverageVerdict>(verdict))), report, StringComparison.Ordinal);
    }

    private static string WhyOf(CoverageVerdict verdict) => verdict switch
    {
        CoverageVerdict.NoPass      => Strings.CoverageWhyNoPass,
        CoverageVerdict.NoAssertion => Strings.CoverageWhyNoAssertion,
        CoverageVerdict.Skips       => Strings.CoverageWhySkips,
        CoverageVerdict.NothingNew  => Strings.CoverageWhyNothingNew,
        _                           => Strings.CoverageWhyNotMeasured,
    };

    [Fact]
    public async Task AFailingFile_IsRepairedOnce_WithTheEndOfItsFailure()
    {
        var asked = new List<List<ChatMessageDto>>();
        Answer((target, attempt) => target != "sub" ? "# FAIL" : attempt == 1 ? "# FAIL" : "assert True\n# covers: 7,8");
        var answer = _client.OnChatRequest!;
        _client.OnChatRequest = (model, messages, tools, onToken) => { asked.Add([.. messages]); return answer(model, messages, tools, onToken); };

        var report = await RunAsync();

        Assert.True(File.Exists(TestPath()));
        Assert.Contains(Strings.CoverageKeptRow("tests/test_calc_coverage.py", "sub", 2), report, StringComparison.Ordinal);
        var repair = asked[1][^1].Content!;
        Assert.Contains("Running that file failed", repair, StringComparison.Ordinal);
        Assert.Contains("AssertionError: boom", repair, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ATestSetAsideMidRun_LeavesTheRunUndoable_ForTheTestKeptAfterIt()
    {
        File.WriteAllText(Path.Combine(_root, "src", "text.py"), "def shout(s):\n    if not s:\n        return ''\n    return s.upper()\n");
        _ecosystem.Baseline["src/text.py"] = new() { [1] = 1, [2] = 0, [3] = 0, [4] = 0 };
        Answer((target, _) => target == "shout" ? "assert True\n# covers: text 2,3,4" : "# FAIL");

        await RunAsync();

        Assert.False(TheRun.Undone);
        var undo = await _tools.History.UndoRunAsync(TheRun, CancellationToken.None);
        Assert.Contains(TestPath("text"), undo.Deleted, PathComparer.Default);
        Assert.False(File.Exists(TestPath("text")));
    }

    [Fact]
    public async Task ATestKeptWhereAnotherWasSetAside_IsGivenBackByUndoRun()
    {
        // sub's file is set aside, then add's is written at the same place and kept.
        Answer((target, _) => target == "sub" ? "# FAIL" : "assert True\n# covers: 3,4");

        await RunAsync();

        Assert.True(File.Exists(TestPath()));
        Assert.False(TheRun.IsGivenBack(TestPath()));
        var undo = await _tools.History.UndoRunAsync(TheRun, CancellationToken.None);
        Assert.Contains(TestPath(), undo.Deleted, PathComparer.Default);
    }

    [Fact]
    public async Task AStop_TakesBackTheFileBeingJudged_AndSaysWhatWasKept()
    {
        File.WriteAllText(Path.Combine(_root, "src", "text.py"), "def shout(s):\n    if not s:\n        return ''\n    return s.upper()\n");
        _ecosystem.Baseline["src/text.py"] = new() { [1] = 1, [2] = 0, [3] = 0, [4] = 0 };
        Answer((target, _) => target == "shout" ? "assert True\n# covers: text 2,3,4" : "assert True\n# covers: 7,8,9");
        using var stop = new CancellationTokenSource();
        _ecosystem.OnRun = file => { if (file.Contains("test_text")) stop.Cancel(); };   // the second target, mid-check

        var report = await TestCoverageCommandHandler.HandleAsync(
            _client, _config, _tools, _tools.History, ["/test", "coverage"], _root, null, stop.Token,
            detect: (_, _, _) => Task.FromResult<(ICoverageEcosystem?, string?)>((_ecosystem, null)));

        Assert.True(File.Exists(TestPath()));                 // sub's, kept before the Stop
        Assert.False(File.Exists(TestPath("text")));          // shout's, being judged
        Assert.Contains(Strings.CoverageKeptRow("tests/test_calc_coverage.py", "sub", 3), report, StringComparison.Ordinal);
        Assert.Contains(Strings.CoverageStopped(1, 3), report, StringComparison.Ordinal);
        Assert.DoesNotContain(Strings.CoverageAfterFailed, report, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AWriteThePersonDeclines_StopsTheCommand()
    {
        Answer((_, _) => "assert True\n# covers: 7,8,9");
        var tools = Registry(new RefuseApproval());

        var report = await RunAsync(tools);

        Assert.Single(_client.ChatModels);
        Assert.False(File.Exists(TestPath()));
        Assert.Contains(Strings.CoverageSetAsideRow("sub", Strings.CoverageWhyDeclined), report, StringComparison.Ordinal);
        Assert.Contains(Strings.CoverageLeft(1), report, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFailedRequest_NamesItsCause_AndStopsTheCommand()
    {
        Answer((_, _) => null);

        var report = await RunAsync();

        Assert.Single(_client.ChatModels);
        Assert.Contains(Strings.CoverageWhyModelFailed("connection refused"), report, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AReplyCutAtTheLengthLimit_IsNeverWritten()
    {
        _client.OnChatRequest = (_, _, _, _) =>
            Task.FromResult(new ChatTurnResult("```python\nassert True\n# covers: 7,8,9", null, 0, 0, CutAtLimit: true));

        var report = await RunAsync();

        Assert.False(File.Exists(TestPath()));
        Assert.Null(TheRun.RedoSourceOf(TestPath()));   // nothing was written to set aside
        Assert.Contains(Strings.CoverageSetAsideRow("sub", Strings.CoverageWhyModelCut), report, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnExistingFile_IsNeverTheNewTest_AndNothingIsAsked()
    {
        _ecosystem.FixedPath = Path.Combine(_root, "src", "calc.py");
        Answer((_, _) => "assert True\n# covers: 7,8,9");

        var report = await RunAsync();

        Assert.Empty(_client.ChatModels);
        Assert.Equal(Calc, File.ReadAllText(Path.Combine(_root, "src", "calc.py")));
        Assert.Contains(Strings.CoverageSetAsideRow("sub", Strings.CoverageWhyNotATestPath), report, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFileExcludedFromAiTools_IsNeitherATargetNorRead()
    {
        File.WriteAllText(Path.Combine(_root, ".inferpalignore"), "src/calc.py\n");
        Answer((_, _) => "assert True\n# covers: 7,8,9");

        var report = await RunAsync();

        Assert.Empty(_client.ChatModels);
        Assert.Contains(Strings.CoverageNothingToCover(0.0.ToString("0.0")), report, StringComparison.Ordinal);
        Assert.Contains(Strings.CoverageExcluded(1), report, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoEcosystem_IsTheDetectionsNotice()
    {
        var report = await TestCoverageCommandHandler.HandleAsync(
            _client, _config, _tools, _tools.History, ["/test", "coverage"], _root, null, CancellationToken.None,
            detect: (_, _, _) => Task.FromResult<(ICoverageEcosystem?, string?)>((null, "add coverlet")));

        Assert.Equal("add coverlet", report);
    }

    [Theory]
    [InlineData("nowhere")]
    [InlineData("../outside")]
    public async Task AFolderThatIsNotInTheWorkspace_IsNamed(string folder)
    {
        Assert.Equal(Strings.CoverageNoSuchFolder(folder), await RunAsync(null, folder));
    }

    [Fact]
    public async Task NoWorkspace_SaysSo()
    {
        Assert.Equal(Services.Tools.PathSanitizer.NoWorkspaceRoot, await TestCoverageCommandHandler.HandleAsync(
            _client, _config, _tools, _tools.History, ["/test", "coverage"], string.Empty, null, CancellationToken.None));
    }

    [Fact]
    public async Task ASourceFile_NarrowsTheTargets_ToItsMethods()
    {
        File.WriteAllText(Path.Combine(_root, "src", "text.py"), "def shout(s):\n    if not s:\n        return ''\n    return s.upper()\n");
        _ecosystem.Baseline["src/text.py"] = new() { [1] = 1, [2] = 0, [3] = 0, [4] = 0 };
        var targets = new List<string>();
        Answer((target, _) => { targets.Add(target); return "# FAIL"; });

        await RunAsync(null, "src/text.py");

        Assert.Equal(["shout", "shout"], targets);   // asked, then the one repair
    }

    // ── Routing ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("/test coverage")]
    [InlineData("/test coverage src")]
    [InlineData("/test coverage \"My Folder\"")]
    [InlineData("/TEST Coverage src/app")]
    public void CoverageAloneOrBeforeOneFolder_IsTheCommand(string typed)
    {
        var action = Assert.IsType<SlashDelegatedAction>(SlashCommandRouter.Route(typed, []));
        Assert.Equal(SlashCommandId.TestCoverage, action.Id);
    }

    [Theory]
    [InlineData("/test coverage of the error paths")]
    [InlineData("/test coverages")]
    [InlineData("/test the coverage")]
    public void ARequestThatBeginsWithTheWord_IsARequestToTest(string typed)
    {
        var action = Assert.IsType<SlashCodeAction>(SlashCommandRouter.Route(typed, []));
        Assert.Equal(SlashCodeActionKind.Test, action.Kind);
        Assert.Equal(typed["/test ".Length..], action.Request);
    }

    [Fact]
    public void VsCode_SendsTestCoverageToTheHost_InsteadOfTheActiveFilesTests()
    {
        // The chat writes /test's tests by an editor edit of its own: unguarded, it took "/test coverage" for one.
        var src = SettingsSchemaDriftTests.NeutralizeTypeScriptComments(File.ReadAllText(
            Path.Combine(ConversationPersistenceSilenceTests.RepoRoot(), "vscode", "src", "chatViewProvider.ts")));

        Assert.Contains("if (first === '/test' && !ChatViewProvider.isTestCoverage(prompt))", src, StringComparison.Ordinal);
        Assert.Contains("static isTestCoverage(prompt: string): boolean", src, StringComparison.Ordinal);
    }

    // ── Review fixes ────────────────────────────────────────────────────────

    [Fact]
    public async Task AStopDuringTheLastMeasure_StillSaysWhatWasKept()
    {
        Answer((target, _) => target == "sub" ? "assert True\n# covers: 7,8,9" : "# FAIL");
        using var stop = new CancellationTokenSource();
        _ecosystem.OnSuiteMeasure = () => { if (_ecosystem.SuiteMeasures == 2) stop.Cancel(); };   // the measure after

        var report = await TestCoverageCommandHandler.HandleAsync(
            _client, _config, _tools, _tools.History, ["/test", "coverage"], _root, null, stop.Token,
            detect: (_, _, _) => Task.FromResult<(ICoverageEcosystem?, string?)>((_ecosystem, null)));

        Assert.Contains(Strings.CoverageKeptRow("tests/test_calc_coverage.py", "sub", 3), report, StringComparison.Ordinal);
        Assert.Contains(Strings.CoverageStopped(2, 2), report, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AJudgeThatThrows_SetsThatTestAside_WithItsCause_AndTheNextTargetsRun()
    {
        Answer((target, _) => target == "sub" ? "# THROW\nassert True\n# covers: 7,8,9" : "assert True\n# covers: 3,4");

        var report = await RunAsync();

        Assert.Contains(Strings.CoverageSetAsideRow("sub", Strings.CoverageWhyFailed("disk gone")), report, StringComparison.Ordinal);
        Assert.Contains(Strings.CoverageKeptRow("tests/test_calc_coverage.py", "add", 2), report, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ATestThatCannotLeave_IsSaidToBeStillThere()
    {
        if (!OperatingSystem.IsWindows()) return;   // an open file can be deleted elsewhere
        Answer((_, _) => "# FAIL");
        // The file is held: the run's undo cannot remove it.
        _ecosystem.OnRun = file => _held ??= new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);

        var report = await RunAsync();

        Assert.Contains(Strings.CoverageSetAsideRow("sub", Strings.CoverageWhyNoPass) + " " + Strings.CoverageStillThere,
                        report, StringComparison.Ordinal);
    }

    private FileStream? _held;

    [Fact]
    public async Task AWriteTheWorkspaceRefuses_IsNotSaidToBeThePersons()
    {
        // Excluded from AI tools: write_file refuses it before any card.
        File.WriteAllText(Path.Combine(_root, ".inferpalignore"), "tests/test_calc_coverage.py\n");
        Answer((_, _) => "assert True\n# covers: 7,8,9");

        var report = await RunAsync();

        Assert.DoesNotContain(Strings.CoverageWhyDeclined, report, StringComparison.Ordinal);
        Assert.Contains(Strings.CoverageSetAsideRow("sub", Strings.CoverageWhyNotWritten("CAUSE").Split("CAUSE")[0]),
                        report, StringComparison.Ordinal);
        Assert.Single(_client.ChatModels);   // the workspace would refuse the next one too
    }

    [Fact]
    public async Task KeptTestsThatBreakTheSuiteTogether_AreSaidToBreakIt()
    {
        Answer((target, _) => target == "sub" ? "assert True\n# covers: 7,8,9" : "# FAIL");
        _ecosystem.SuitePasses = measure => measure == 1;   // green before, red after

        var report = await RunAsync();

        Assert.Contains(Strings.CoverageSuiteBrokenAfter, report, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFolderOfTests_IsSaidToHoldNoSource_NotZeroPercent()
    {
        var report = await RunAsync(null, "tests");

        Assert.Contains(Strings.CoverageNoSourceInScope("`tests`"), report, StringComparison.Ordinal);
        Assert.Empty(_client.ChatModels);
    }

    [Fact]
    public async Task ARunWhoseTestsWereAllSetAside_IsUndone_SoUndoRunDoesNotTakeIt()
    {
        Answer((_, _) => "# FAIL");

        await RunAsync();

        Assert.True(TheRun.FileCount > 0);
        Assert.True(TheRun.Undone);
    }

    [Theory]
    [InlineData("MoveNext", "Lib.Store/<SaveAsync>d__3", "SaveAsync")]
    [InlineData("<Save>b__0_0", "Lib.Store/<>c", "Save")]
    [InlineData("<Save>g__Local|1_0", "Lib.Store", "Save")]
    [InlineData("Save", "Lib.Store", "Save")]
    public void ACompiledMember_IsNamedAsItsSourceNamesIt(string method, string className, string name)
    {
        Assert.Equal(name, CoberturaReport.SourceName(method, className));
    }

    [Theory]
    [InlineData("src/Latest.cs", true)]
    [InlineData("src/Contest.cs", true)]
    [InlineData("src/StoreTests.cs", false)]
    [InlineData("src/StoreTest.cs", false)]
    public void ASourceWhoseNameEndsInTest_IsStillSource(string file, bool isSource)
    {
        Assert.Equal(isSource, CoberturaReport.IsSource(file));
    }

    [Fact]
    public void ALongTarget_IsBoundedInThePrompt_AndTheCutSaid()
    {
        var lines = Enumerable.Range(1, 1000).Select(n => $"x{n} = {n}").ToArray();

        var shown = TestCoverageCommandHandler.Numbered(lines, 1, 1000, [999]);

        Assert.Contains("x300 = 300", shown, StringComparison.Ordinal);
        Assert.DoesNotContain("x301 = 301", shown, StringComparison.Ordinal);
        Assert.Contains("lines 301-1000 of the target not shown", shown, StringComparison.Ordinal);
    }

    [Fact]
    public void AMinifiedTarget_IsBoundedInCharacters()
    {
        var lines = Enumerable.Range(1, 20).Select(n => new string('x', 50_000)).ToArray();

        var shown = TestCoverageCommandHandler.Numbered(lines, 1, 20, [5]);

        Assert.True(shown.Length < 20_000, $"{shown.Length} characters");
        Assert.Contains("of the target not shown", shown, StringComparison.Ordinal);
    }

    /// <summary>The source file beside the target (<c>Bounded</c>) stays within the same budget: a minified file or a method
    /// of thousands of lines filled the model's window, and the failed request dropped every target after it.</summary>
    [Theory]
    [InlineData(20, 50_000, 5)]        // minified: twenty lines of 50,000 characters
    [InlineData(4_000, 80, 2_000)]     // a method thousands of lines long
    public void TheSourceBesideTheTarget_IsBoundedToo(int count, int width, int target)
    {
        var lines = Enumerable.Range(1, count).Select(n => new string('x', width)).ToArray();

        var shown = TestCoverageCommandHandler.Bounded(lines, target, count - 1);

        Assert.True(shown.Length < 13_000, $"{shown.Length} characters");
        Assert.EndsWith("# ... (rest not shown)", shown, StringComparison.Ordinal);
    }

    // ── New test paths ──────────────────────────────────────────────────────

    [Theory]
    [InlineData("FooCoverageTests.cs", "FooCoverage2Tests.cs")]
    [InlineData("foo.coverage.test.ts", "foo.coverage2.test.ts")]
    [InlineData("test_foo_coverage.py", "test_foo_coverage2.py")]
    public void ASecondTestForTheSameFile_IsStillATestFile(string first, string second)
    {
        var taken = Path.Combine(_root, first);
        var next  = CoverageTools.Unique(taken, p => p == taken);

        Assert.Equal(Path.Combine(_root, second), next);
        Assert.True(Services.CodeActions.TestFilePathResolver.IsTestFileName(Path.GetFileNameWithoutExtension(next)));
    }

    private sealed class RefuseApproval : IApprovalService
    {
        public Task<bool> RequestApprovalAsync(string toolName, string details, CancellationToken ct, string? subject = null,
                                               Services.CodeActions.DiffInfo? diff = null, bool forcePrompt = false) =>
            Task.FromResult(false);
    }

    /// <summary>A test ecosystem whose files say what they do: <c># covers: 7,8</c> (or <c># covers: text 2,3</c> for
    /// <c>src/text.py</c>), <c># FAIL</c>, <c># FLAKY</c> (fails its third run), <c># UNMEASURED</c>.</summary>
    private sealed class FakeEcosystem(string root) : ICoverageEcosystem
    {
        public Dictionary<string, Dictionary<int, int>> Baseline { get; } = [];
        public string? FixedPath { get; set; }
        public Action<string>? OnRun { get; set; }
        public Action? OnSuiteMeasure { get; set; }
        public int SuiteMeasures { get; private set; }
        public Func<int, bool> SuitePasses { get; set; } = _ => true;
        private readonly Dictionary<string, int> _runs = [];

        public string Name => "fake";
        public string FrameworkOf(string testPath) => "pytest";

        public Task<CoverageRun> MeasureAsync(string? testFile, string outDir, CancellationToken ct)
        {
            if (testFile is not null)
            {
                var content = File.ReadAllText(testFile);
                return Task.FromResult(new CoverageRun(content.Contains("UNMEASURED") ? null : Report(Covers(content)), true, "", false));
            }
            SuiteMeasures++;
            OnSuiteMeasure?.Invoke();
            ct.ThrowIfCancellationRequested();
            var hits = Baseline.ToDictionary(b => b.Key, b => new Dictionary<int, int>(b.Value));
            foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "tests")))
                foreach (var (source, lines) in Covers(File.ReadAllText(file)))
                    foreach (var line in lines.Keys) hits[source][line] = 1;
            return Task.FromResult(new CoverageRun(Report(hits), SuitePasses(SuiteMeasures), "", false));
        }

        private static Dictionary<string, Dictionary<int, int>> Covers(string content)
        {
            var covered = new Dictionary<string, Dictionary<int, int>>();
            foreach (Match m in Regex.Matches(content, @"covers: (?:(\w+) )?([\d,]+)"))
            {
                var source = $"src/{(m.Groups[1].Success ? m.Groups[1].Value : "calc")}.py";
                if (!covered.TryGetValue(source, out var lines)) covered[source] = lines = [];
                foreach (var n in m.Groups[2].Value.Split(',')) lines[int.Parse(n)] = 1;
            }
            return covered;
        }

        private CoberturaReport Report(Dictionary<string, Dictionary<int, int>> files)
        {
            var xml = new StringBuilder($"<coverage><sources><source>{root}</source></sources><packages><package><classes>");
            foreach (var (file, lines) in files)
            {
                xml.Append($"<class filename=\"{file}\"><lines>");
                foreach (var (line, hits) in lines) xml.Append($"<line number=\"{line}\" hits=\"{hits}\"/>");
                xml.Append("</lines></class>");
            }
            return CoberturaReport.Parse(xml.Append("</classes></package></packages></coverage>").ToString(), root, File.Exists);
        }

        public Task<(bool Passed, string Output, bool TimedOut)> RunAsync(string testFile, CancellationToken ct)
        {
            OnRun?.Invoke(testFile);
            ct.ThrowIfCancellationRequested();
            var content = File.ReadAllText(testFile);
            if (content.Contains("THROW")) throw new IOException("disk gone");
            _runs[testFile] = _runs.GetValueOrDefault(testFile) + 1;
            var passed = !content.Contains("FAIL") && !(content.Contains("FLAKY") && _runs[testFile] % 3 == 0);
            return Task.FromResult((passed, passed ? "1 passed" : "E   AssertionError: boom\n1 failed", false));
        }

        public string NewTestPath(string sourceFile) => FixedPath
            ?? CoverageTools.Unique(Path.Combine(root, "tests", $"test_{Path.GetFileNameWithoutExtension(sourceFile)}_coverage.py"), File.Exists);

        public string Hint(string testPath) => string.Empty;
        public bool HasAssertion(string content) => content.Contains("assert");
        public bool SkipsOrExpectsFailure(string content) => content.Contains("skip");
    }
}
