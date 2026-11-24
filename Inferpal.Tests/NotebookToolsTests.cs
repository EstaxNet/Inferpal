using System.IO;
using System.Text.Json;
using Inferpal.Services.Presentation;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// <c>edit_notebook</c> and <c>run_notebook</c> (pari 98): one cell changed through the ordinary write path, its card showing
/// the CELL's lines; a run read from nbconvert's executed copy, the person's file untouched.
/// </summary>
public sealed class NotebookToolsTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("inferpal-notebook-tests-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private static string Fixture(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "README.md"))) dir = dir.Parent;
        return Path.Combine(dir!.FullName, "Inferpal.Tests", "Fixtures", "notebooks", name);
    }

    private sealed class Recording : Inferpal.Services.Execution.IApprovalService
    {
        public readonly List<(string Details, Inferpal.Services.CodeActions.DiffInfo? Diff)> Asked = [];
        public Task<bool> RequestApprovalAsync(string toolName, string details, CancellationToken ct, string? subject = null,
                                               Inferpal.Services.CodeActions.DiffInfo? diff = null, bool forcePrompt = false)
        {
            Asked.Add((details, diff));
            return Task.FromResult(true);
        }
    }

    [Fact]
    public async Task ACellIsChanged_ThroughTheWritePath_ItsCardShowingTheCellsLines()
    {
        var path = Path.Combine(_root, "analysis.ipynb");
        File.Copy(Fixture("analysis.ipynb"), path);
        var approval = new Recording();
        var tool = new EditNotebookTool(approval, new Inferpal.Services.Execution.FileHistoryService(), () => _root);

        var said = await tool.ExecuteAsync(JsonSerializer.SerializeToElement(new
        {
            path, cell = "293a4b5c", source = "orders = pd.read_csv(\"data/orders.csv\")\ntotal = orders[\"total_eur\"].sum()",
        }), default);

        Assert.StartsWith("Changed cell 3 of analysis.ipynb", said);
        Assert.Contains("total_eur", File.ReadAllText(path));
        var (details, diff) = Assert.Single(approval.Asked);
        Assert.DoesNotContain("\"cell_type\"", diff!.NewText);   // the cell, never the JSON
        var card = ApprovalCard.Build(new ApprovalPrompt("edit_notebook", details, path, diff, "message"), _root);
        Assert.Contains("3", card.Title);
        Assert.Contains("analysis.ipynb", card.Title);
        Assert.Equal("+1 −2", card.Meta);   // the print line dropped too
        Assert.NotEmpty(card.Note);
    }

    [Fact]
    public void TheExecutedCopy_IsRead_FirstFailingCellAndItsTraceback()
    {
        var report = RunNotebookTool.Report("analysis.ipynb", File.ReadAllBytes(Fixture("executed.ipynb")), only: null);

        Assert.StartsWith("analysis.ipynb: 3/3 code cells ran; cell 2 failed", report);
        Assert.Contains("2 orders", report);
        Assert.Contains("KeyError", report);
        Assert.DoesNotContain("\u001b[", report, StringComparison.Ordinal);   // the traceback's colours removed
        Assert.Equal(("#2", true), RunSummary.StepStatus("run_notebook", report));   // no words on the chip: the cell, in red
    }

    [Fact]
    public void OneCellAskedFor_IsTheOnlyOutputShown()
    {
        var report = RunNotebookTool.Report("analysis.ipynb", File.ReadAllBytes(Fixture("executed.ipynb")), only: "c3");
        Assert.Contains("after", report);
        Assert.DoesNotContain("2 orders", report);
    }

    [Fact]
    public void ACellNamedWithALeadingZero_IsTheCellEditNotebookEdits()
    {
        // edit_notebook reads "03" as the number 3: the same cell here, never "no cell '03'".
        var report = RunNotebookTool.Report("analysis.ipynb", File.ReadAllBytes(Fixture("executed.ipynb")), only: "03");
        Assert.Contains("after", report);
        Assert.DoesNotContain("no cell", report);
    }

    [Fact]
    public void ATracebacksTerminalCodes_AreAllRemoved()
    {
        // A cursor code ("ESC[?25l") is a terminal code like a colour: removed as the CI logs' reader removes it.
        var notebook = JsonSerializer.Serialize(new
        {
            cells = new object[]
            {
                new { cell_type = "code", source = "x", execution_count = 1,
                      outputs = new object[] { new { output_type = "error", ename = "E", evalue = "v", traceback = new[] { "\x1b[?25lboom\x1b[0m" } } } },
            },
        });

        var report = RunNotebookTool.Report("n.ipynb", System.Text.Encoding.UTF8.GetBytes(notebook), only: null);

        Assert.Contains("boom", report);
        Assert.DoesNotContain("\x1b", report, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownCell_IsSaid_NeverNoOutput() =>
        Assert.Contains("no cell 'z9'", RunNotebookTool.Report("analysis.ipynb", File.ReadAllBytes(Fixture("executed.ipynb")), only: "z9"));

    [Fact]
    public void TheFailingCell_IsKeptWhole_WhenTheOthersAreCut()
    {
        var executed = File.ReadAllText(Fixture("executed.ipynb"))
            .Replace("\"2 orders\\n\"", "\"" + new string('x', RunNotebookTool.MaxReportChars * 2) + "\\n\"");
        var report = RunNotebookTool.Report("analysis.ipynb", System.Text.Encoding.UTF8.GetBytes(executed), only: null);
        Assert.Contains("KeyError: 'amount'", report);
        Assert.Contains("output cut", report);
    }

    [Fact]
    public void AnOtherLanguagesKernel_IsNotSentToPip() =>
        Assert.DoesNotContain("ipykernel", RunNotebookTool.NotRun("jupyter_client.kernelspec.NoSuchKernel: No such kernel named ir", "python", "a.ipynb"));

    [Fact]
    public void JupyterMissing_IsSaid_WithTheCommand_NeverInstalled()
    {
        var said = RunNotebookTool.NotRun(@"C:\repo\.venv\Scripts\python.exe: No module named jupyter", @"C:\repo\.venv\Scripts\python.exe", "analysis.ipynb");
        Assert.Contains("-m pip install nbconvert ipykernel", said);
        Assert.Contains("do not install it yourself", said);
    }
}
