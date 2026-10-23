using System.IO;
using System.Text.Json;
using Inferpal.Config;
using Inferpal.Localization;
using Inferpal.Services;
using Inferpal.Services.CodeActions;
using Inferpal.Services.Docs;
using Inferpal.Services.Editor;
using Inferpal.Services.Execution;
using Inferpal.Services.Lsp;
using Inferpal.Services.Mcp;
using Inferpal.Services.Rag;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// VS Code's Test view "Explain the Failure": the failure message the view holds, or the report of the test run again —
/// read by /tdd's own reader, a green run or one that is no failing verdict said instead of asked — and the code around
/// the test and the failing line. The run is the person's click: outside any open chat run.
/// </summary>
public sealed class TestFailureExplainTests : IDisposable
{
    private const int Budget = 60_000;
    private const string Name = "Total_SumsTheLines";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "inferpal-tests", $"testfail-{Guid.NewGuid():N}");
    private readonly string _testFile;
    private int _runs;

    public TestFailureExplainTests()
    {
        Directory.CreateDirectory(_root);
        _testFile = Path.Combine(_root, "InvoiceTests.cs");
        File.WriteAllLines(_testFile, [
            "public class InvoiceTests",
            "{",
            "    [Fact]",
            "    public void Total_SumsTheLines()",
            "    {",
            "        Assert.Equal(42m, new Invoice().Total());",
            .. Enumerable.Range(1, 60).Select(i => $"        var step{i} = {i};"),
            "        Assert.Equal(99m, new Invoice().Tax());   // the failing line",
            "    }",
            "}",
        ]);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { /* best-effort cleanup */ }
    }

    private Func<CancellationToken, Task<string>> Runs(string report) => _ =>
    {
        _runs++;
        return Task.FromResult(report);
    };

    private Task<ExplainAsk> Explain(string? failure, string report, string? file = null, int line = 0,
                                     (string?, int) failedAt = default, int budget = Budget) =>
        TestFailureExplain.ForAsync(Name, Name, failure, (file, line), failedAt, Runs(report), _root, overlay: null, budget,
                                    CancellationToken.None);

    [Fact]
    public async Task TheMessageTheViewHolds_IsExplained_WithTheCodeAroundTheTest_AndTheFailingLine_WithoutRunningIt()
    {
        var ask = await Explain("Assert.Equal() Failure\nExpected: 99\nActual:   98", "✓ never asked", _testFile, line: 4,
                                failedAt: (_testFile, 67));

        Assert.Null(ask.Refusal);
        Assert.Equal(0, _runs);
        Assert.Equal(Strings.ExplainTestFailureQuestion(Name), ask.Question);
        Assert.Contains("Actual:   98", ask.Content);
        Assert.Contains("Assert.Equal(42m, new Invoice().Total());", ask.Content);           // around the test
        Assert.Contains("Assert.Equal(99m, new Invoice().Tax());   // the failing line", ask.Content);   // where it failed
    }

    [Fact]
    public async Task WithoutAMessage_TheTestRuns_AndItsRedReportIsExplained()
    {
        var ask = await Explain(failure: null,
            "✗ 1 failed, 0 passed\n  Failed InvoiceTests.Total_SumsTheLines [12 ms]\n  Expected: 42 Actual: 41", _testFile, 4);

        Assert.Null(ask.Refusal);
        Assert.Equal(1, _runs);
        Assert.Contains("Failed InvoiceTests.Total_SumsTheLines", ask.Content);
        Assert.DoesNotContain("does not name it", ask.Content);
    }

    [Fact]
    public async Task ARedReportThatNeverNamesTheTest_IsSaidToBeAboutOthers()
    {
        // The name is a substring filter for dotnet: Total_SumsTheLinesTwice failed, the clicked test did not.
        var ask = await Explain(failure: null, "✗ 1 failed, 1 passed\n  Failed InvoiceTests.Total_SumsTheLinesTwice [3 ms]");

        Assert.Contains("this report does not name it: the failures below are other tests that the name 'Total_SumsTheLines' selected",
                        ask.Content);
    }

    [Fact]
    public async Task ATestThatPasses_OrARunThatIsNoFailingVerdict_IsSaid_NeverAsked_InAFewLines()
    {
        var rawEnd  = "\n" + string.Join("\n", Enumerable.Range(1, 400).Select(i => $"raw runner line {i}"));
        var green   = await Explain(failure: null, "✓ 1 passed");
        var none    = await Explain(failure: null, RunTestsTool.NoTestMatchedFilter + rawEnd);
        // Killed at its budget after a first project's green summary: never "passes".
        var stopped = await Explain(failure: null, "✓ 3 passed\n" + RunTestsTool.StoppedAtBudget);
        // The tool's own failure, neither green nor red: no failing test to ask about.
        var failed  = await Explain(failure: null, "Error: tool 'run_tests' timed out after 600 s.");

        Assert.Equal(Strings.TestPasses(Name), green.Refusal);
        Assert.StartsWith(Strings.TddNothingRan, none.Refusal);
        Assert.Contains(RunTestsTool.NoTestMatchedFilter, none.Refusal);
        Assert.DoesNotContain("raw runner line", none.Refusal);                  // a notification, not the raw report
        Assert.StartsWith(Strings.TddStoppedAtBudget, stopped.Refusal);
        Assert.Equal("Error: tool 'run_tests' timed out after 600 s.", failed.Refusal);
        Assert.Empty(green.Question + none.Question + stopped.Question + failed.Question);
    }

    [Fact]
    public async Task ALongFailure_NeverPushesTheTestsCodeOut()
    {
        var trace = string.Join("\n", Enumerable.Range(1, 2_000).Select(i => $"   at Frame{i}() in Somewhere.cs:line {i}"));

        var ask = await Explain("Assert.Equal() Failure\n" + trace, "", _testFile, line: 4, budget: 8_000);

        Assert.Contains("Assert.Equal(42m, new Invoice().Total());", ask.Content);
        Assert.Contains("(truncated: the first", ask.Content);
        Assert.True(ask.Content.Length <= 8_000 + 200, $"{ask.Content.Length} characters for a budget of 8,000");
    }

    [Fact]
    public async Task ATestFileOutsideTheWorkspace_IsNotRead_AndThatIsSaid()
    {
        var outside = Path.Combine(Path.GetTempPath(), "inferpal-tests", $"outside-{Guid.NewGuid():N}.cs");
        File.WriteAllText(outside, "secret code\n");
        try
        {
            var ask = await Explain("boom", "", outside, line: 1);

            Assert.Contains("boom", ask.Content);
            Assert.DoesNotContain("secret code", ask.Content);
            Assert.Contains($"{Path.GetFileName(outside)} is outside the workspace: not read", ask.Content);
        }
        finally { File.Delete(outside); }
    }

    [Fact]
    public void TheTestRuns_FromItsFile_WhenTheFileNamesItsRunner_ElseFromTheRoot()
    {
        var python = Path.Combine(_root, "test_invoice.py");
        var script = Path.Combine(_root, "src", "invoice.test.ts");

        Assert.Equal(_testFile, TestFailureExplain.RunPath(_testFile, _root));   // C#: its project
        Assert.Equal(python, TestFailureExplain.RunPath(python, _root));         // Python: that file
        Assert.Equal(_root, TestFailureExplain.RunPath(script, _root));          // npm: found from the root's package.json
        Assert.Equal(_root, TestFailureExplain.RunPath(null, _root));
        Assert.Equal(_root, TestFailureExplain.RunPath(Path.Combine(Path.GetTempPath(), "elsewhere.cs"), _root));
    }

    [Fact]
    public async Task TheRun_IsOutsideTheOpenChatRun()
    {
        var config   = new InferpalConfig();
        var client   = new FakeInferenceProvider();
        var editor   = new NullEditorSurface();
        var approval = new NoopApproval();
        using var index = new ProjectIndexService(client, config, new LspSemanticProvider());
        index.SetRoot(_root);
        var tools = new ToolRegistry(editor, approval, config, index, client, new ProjectMapService(editor),
                                     new McpToolService(config, approval), new DocsIndexService(client, config),
                                     new OpenDocumentOverlay(), new NullDebugSession());
        tools.History.BeginRun();   // the open chat turn
        var empty = Path.Combine(_root, "empty");
        Directory.CreateDirectory(empty);

        await TestFailureExplain.RunTestAsync(tools, Name, file: null, empty, CancellationToken.None);
        var afterGesture = tools.History.Runs[^1].ToolCalls.Count;
        // Reference arm: the same call made by the model's turn is in its run.
        await tools.ExecuteAsync("run_tests", JsonSerializer.SerializeToElement(new { path = empty, filter = Name }),
                                 CancellationToken.None);

        Assert.Equal(0, afterGesture);
        Assert.Single(tools.History.Runs[^1].ToolCalls);
    }
}
