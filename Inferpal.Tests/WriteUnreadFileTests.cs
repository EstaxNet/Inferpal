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
