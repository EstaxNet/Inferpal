using System.IO;
using System.Text.Json;
using Inferpal.Models;
using Inferpal.Services.CodeActions;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

// ──────────────────────────────────────────────────────────────────────────────────────────────
//  write_file does not replace a file the model has not read in the run.
//
//  A whole-file write is the one edit that does not quote what it changes. Asked to "modify this
//  Counter page" with the page open, a small model (Qwen3-4B, measured on the stock Blazor
//  template) wrote a counter page from its idea of one without ever reading the file: the Reset
//  button was there, `@page "/counter"` and `@rendermode InteractiveServer` were gone, and the
//  project still built. The refusal comes before the approval prompt and names the gesture that
//  works; a read earlier in the run, a creation, an empty file, and any write outside a run are
//  left alone.
// ──────────────────────────────────────────────────────────────────────────────────────────────
public sealed class WriteUnreadFileTests : IDisposable
{
    private readonly string _ws = Directory.CreateTempSubdirectory("inferpal-unread-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_ws, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private sealed class CountingApproval : IApprovalService
    {
        public int Asked { get; private set; }
        public Task<bool> RequestApprovalAsync(string toolName, string details, CancellationToken ct,
                                               string? subject = null, DiffInfo? diff = null, bool forcePrompt = false)
        {
            Asked++;
            return Task.FromResult(true);
        }
    }

    private static JsonElement Args(object value) =>
        JsonDocument.Parse(JsonSerializer.Serialize(value)).RootElement.Clone();

    private string Page(string content = "@page \"/counter\"\n<h1>Counter</h1>\n")
    {
        var path = Path.Combine(_ws, "Counter.razor");
        File.WriteAllText(path, content);
        return path;
    }

    private (WriteFileTool Write, ReadFileTool Read, FileHistoryService History, CountingApproval Approval) Tools()
    {
        var history  = new FileHistoryService();
        var approval = new CountingApproval();
        return (new WriteFileTool(approval, history, () => _ws), new ReadFileTool(() => _ws, history: history),
                history, approval);
    }

    [Fact]
    public async Task AnExistingFileNeverRead_IsRefused_BeforeTheApprovalPrompt_AndLeftIntact()
    {
        var path = Page();
        var (write, _, history, approval) = Tools();
        history.BeginRun();

        var result = await write.ExecuteAsync(Args(new { path, content = "<h1>Counter</h1>\n" }), default);

        Assert.Contains("has not been read in this run", result);
        Assert.Contains("read_file", result);
        Assert.Equal("@page \"/counter\"\n<h1>Counter</h1>\n", File.ReadAllText(path));
        Assert.Equal(0, approval.Asked);
    }

    [Fact]
    public async Task AFileReadEarlierInTheRun_IsReplaced()
    {
        var path = Page();
        var (write, read, history, approval) = Tools();
        history.BeginRun();

        await read.ExecuteAsync(Args(new { path }), default);
        var result = await write.ExecuteAsync(Args(new { path, content = "@page \"/counter\"\n<h1>Reset</h1>\n" }), default);

        Assert.DoesNotContain("has not been read", result);
        Assert.Equal("@page \"/counter\"\n<h1>Reset</h1>\n", File.ReadAllText(path));
        Assert.Equal(1, approval.Asked);
    }

    [Fact]
    public async Task AReadInAnEarlierRun_DoesNotCount()
    {
        // Only the question and the answer outlive a run: what the model read in the previous turn is no longer in
        // front of it.
        var path = Page();
        var (write, read, history, _) = Tools();
        history.BeginRun();
        await read.ExecuteAsync(Args(new { path }), default);
        history.EndRun();
        history.BeginRun();

        var result = await write.ExecuteAsync(Args(new { path, content = "x\n" }), default);

        Assert.Contains("has not been read in this run", result);
        Assert.Equal("@page \"/counter\"\n<h1>Counter</h1>\n", File.ReadAllText(path));
    }

    [Fact]
    public async Task ACreation_AnEmptyFile_AndTheModelsOwnEarlierWrite_AreNotBlind()
    {
        var (write, _, history, _) = Tools();
        history.BeginRun();

        var created = Path.Combine(_ws, "New.razor");
        await write.ExecuteAsync(Args(new { path = created, content = "one\n" }), default);
        Assert.Equal("one\n", File.ReadAllText(created));

        // It wrote every line of it: writing it again is not a blind rewrite.
        await write.ExecuteAsync(Args(new { path = created, content = "two\n" }), default);
        Assert.Equal("two\n", File.ReadAllText(created));

        // Nothing to lose in an empty file.
        var empty = Page(content: string.Empty);
        await write.ExecuteAsync(Args(new { path = empty, content = "filled\n" }), default);
        Assert.Equal("filled\n", File.ReadAllText(empty));
    }

    [Fact]
    public async Task OutsideARun_NothingIsRefused()
    {
        // Reference arm: a code action or a tool a slash command calls runs outside any run, where nobody tracks
        // reads — "not tracked" must not read as "not read".
        var path = Page();
        var (write, _, _, _) = Tools();

        var result = await write.ExecuteAsync(Args(new { path, content = "replaced\n" }), default);

        Assert.DoesNotContain("has not been read", result);
        Assert.Equal("replaced\n", File.ReadAllText(path));
    }

    // ── Read means every line: a long file comes back a page at a time ─────

    /// <summary>A file of numbered lines, several read_file pages long.</summary>
    private string LongFile(int lines = 600)
    {
        var path = Path.Combine(_ws, "Long.cs");
        File.WriteAllText(path, string.Concat(Enumerable.Range(1, lines)
            .Select(i => $"// line {i} of the original file, long enough to fill several pages\n")));
        return path;
    }

    [Fact]
    public async Task AFirstPage_IsNotTheFile_AndTheRefusalNamesTheLineToReadOnFrom()
    {
        var path     = LongFile();
        var original = File.ReadAllText(path);
        var (write, read, history, approval) = Tools();
        history.BeginRun();

        var page = await read.ExecuteAsync(Args(new { path }), default);
        // Witness: the file is paged, and the footer names where the next page starts.
        var next = System.Text.RegularExpressions.Regex.Match(page, @"start_line=(\d+) to read on");
        Assert.True(next.Success, page[^200..]);

        var result = await write.ExecuteAsync(Args(new { path, content = "// rewritten\n" }), default);

        Assert.Contains("only been read in part", result);
        Assert.Contains("of 600 lines", result);
        Assert.Contains($"start_line={next.Groups[1].Value}", result);
        Assert.Equal(original, File.ReadAllText(path));
        Assert.Equal(0, approval.Asked);
    }

    [Fact]
    public async Task ARange_IsNotTheFileEither()
    {
        var path = LongFile();
        var (write, read, history, approval) = Tools();
        history.BeginRun();

        await read.ExecuteAsync(Args(new { path, start_line = 1, end_line = 3 }), default);
        var result = await write.ExecuteAsync(Args(new { path, content = "// rewritten\n" }), default);

        Assert.Contains("(3 of 600 lines)", result);
        Assert.Contains("start_line=4", result);
        Assert.Equal(0, approval.Asked);
    }

    [Fact]
    public async Task EveryPage_ReadInTurn_IsTheWholeFile()
    {
        // Reference arm: reading a long file to its end, page after page, is reading it — then rewriting it is not blind.
        var path = LongFile();
        var (write, read, history, approval) = Tools();
        history.BeginRun();

        var pages = 0;
        var start = 0;
        while (true)
        {
            var page = await read.ExecuteAsync(Args(new { path, start_line = start }), default);
            pages++;
            var next = System.Text.RegularExpressions.Regex.Match(page, @"start_line=(\d+) to read on");
            if (!next.Success) break;
            start = int.Parse(next.Groups[1].Value);
            Assert.True(pages < 50, "the pages never reached the end of the file");
        }
        Assert.True(pages > 1, $"Only {pages} page: the file is not long enough to measure anything.");

        var result = await write.ExecuteAsync(Args(new { path, content = "// rewritten\n" }), default);

        Assert.DoesNotContain("read in part", result);
        Assert.Equal("// rewritten\n", File.ReadAllText(path));
        Assert.Equal(1, approval.Asked);
    }

    // ── A /task proposal is applied through write_file, in a run of its own ─────

    /// <summary>The smallest registry that runs the real write tool.</summary>
    private sealed class WriteOnlyRegistry(WriteFileTool write) : IToolRegistry
    {
        public IReadOnlyList<ToolDefinition> Definitions => [];
        public DiffInfo? ConsumeDiff() => null;
        public Task<string> ExecuteAsync(string name, JsonElement args, CancellationToken ct) =>
            name == "write_file" ? write.ExecuteAsync(args, ct) : Task.FromResult("unexpected " + name);
    }

    [Fact]
    public async Task AnAppliedProposal_IsWritten_BecauseItsRunStartsWithTheFileRead()
    {
        // The person applying it reviews its diff in the approval prompt: it is not a blind rewrite.
        var path = Page();
        var (write, _, history, approval) = Tools();
        var proposal = new TaskProposal("write_file", path, $"write {path}",
                                        new DiffInfo(File.ReadAllText(path), "@page \"/counter\"\n<h1>New</h1>\n", path));

        await TaskProposalApplication.ApplyAsync(proposal, new WriteOnlyRegistry(write),
            TaskProposalApplication.ReadCurrent, default, beginRun: p => history.BeginRun(alreadyRead: p));

        Assert.Equal("@page \"/counter\"\n<h1>New</h1>\n", File.ReadAllText(path));
        Assert.Equal(1, approval.Asked);
    }

    [Fact]
    public async Task AnAppliedProposal_WhoseRunDoesNotDeclareTheRead_IsRefused()
    {
        // Witness that the path handed to beginRun is what lets the application through.
        var path = Page();
        var (write, _, history, _) = Tools();
        var proposal = new TaskProposal("write_file", path, $"write {path}",
                                        new DiffInfo(File.ReadAllText(path), "changed\n", path));

        await TaskProposalApplication.ApplyAsync(proposal, new WriteOnlyRegistry(write),
            TaskProposalApplication.ReadCurrent, default, beginRun: _ => history.BeginRun());

        Assert.Equal("@page \"/counter\"\n<h1>Counter</h1>\n", File.ReadAllText(path));
    }
}
