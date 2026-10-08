using System.IO;
using System.Text.Json;
using Inferpal.Localization;
using Inferpal.Services.Execution;
using Inferpal.Services.Inference;
using Inferpal.Services.Presentation;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// The run line's "read N files" counts the FILES the run read — a file read in three pages is one, a path that was not
/// there is none.
/// </summary>
/// <remarks>
/// It counted the <c>read_file</c> calls: <c>read_file</c> pages a long file, a model reads again after an edit, and a
/// read of a missing file answers in text — a run that read one file showed "read 4 files".
/// </remarks>
[Collection(CultureSerialCollection.Name)]
public sealed class RunReadCountTests : IDisposable
{
    private readonly string _ws = Path.Combine(Path.GetTempPath(), "inferpal-tests", $"runreads-{Guid.NewGuid():N}");

    public RunReadCountTests() => Directory.CreateDirectory(_ws);

    public void Dispose()
    {
        try { Directory.Delete(_ws, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private async Task<ToolExecution> Read(ReadFileTool tool, object args)
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(args));
        return new ToolExecution("read_file", doc.RootElement.GetRawText(), await tool.ExecuteAsync(doc.RootElement, CancellationToken.None));
    }

    private string Detail(IReadOnlyList<ToolExecution> calls, HistoryRun? run)
    {
        try
        {
            Strings.ApplyLanguage("en");
            return RunSummary.Build(calls, run)!.Detail;
        }
        finally { Strings.ApplyLanguage(null); }
    }

    [Fact]
    public async Task OneFileReadInPages_AndAMissingOne_IsOneFileRead()
    {
        File.WriteAllText(Path.Combine(_ws, "Long.cs"), string.Concat(Enumerable.Range(1, 3000).Select(i => $"// line {i}\n")));
        var history = new FileHistoryService();
        var id      = history.BeginRun();
        var tool    = new ReadFileTool(() => _ws, history: history);

        List<ToolExecution> calls =
        [
            await Read(tool, new { path = "Long.cs" }),
            await Read(tool, new { path = "Long.cs", start_line = 1500 }),
            await Read(tool, new { path = "Long.cs" }),
            await Read(tool, new { path = "Missing.cs" }),
        ];

        Assert.Equal("read 1 file", Detail(calls, history.Runs.Single(r => r.Id == id)));
    }

    [Fact]
    public async Task TwoFilesRead_AreTwo()
    {
        // Reference arm.
        File.WriteAllText(Path.Combine(_ws, "A.cs"), "class A { }\n");
        File.WriteAllText(Path.Combine(_ws, "B.cs"), "class B { }\n");
        var history = new FileHistoryService();
        var id      = history.BeginRun();
        var tool    = new ReadFileTool(() => _ws, history: history);

        List<ToolExecution> calls = [await Read(tool, new { path = "A.cs" }), await Read(tool, new { path = "B.cs" })];

        Assert.Equal("read 2 files", Detail(calls, history.Runs.Single(r => r.Id == id)));
    }

    [Fact]
    public void WithoutARun_TheReadsCountByThePathTheyName()
    {
        // No history tracked (a /task run): the paths the calls name, each once.
        List<ToolExecution> calls =
        [
            new("read_file", "{\"path\":\"A.cs\"}", "…"), new("read_file", "{\"path\":\"A.cs\",\"start_line\":40}", "…"),
            new("read_file", "{\"path\":\"B.cs\"}", "…"),
        ];

        Assert.Equal("read 2 files", Detail(calls, null));
    }
}
