using System.IO;
using System.Text.Json;
using Inferpal.Localization;
using Inferpal.Services.Agent;
using Inferpal.Services.CodeActions;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

// ──────────────────────────────────────────────────────────────────────────────────────────────
//  A run that tried to edit a file and changed none says so after the answer.
//
//  Measured on the Visual Studio Magazine task with Qwen3-4B: its write_file was refused (the file
//  had not been read), and its answer said "The Counter component has been updated with a Reset
//  button" — twice in six corrected runs, the page untouched. The answer is the part the user reads.
//  Whether it CLAIMS a change is wording, in ten languages; whether a change LANDED is a fact of the
//  run: every write that lands is entered in it (FileHistoryService.CurrentRunFileCount).
// ──────────────────────────────────────────────────────────────────────────────────────────────
public sealed class EditsWithoutEffectTests : IDisposable
{
    private readonly string _ws = Directory.CreateTempSubdirectory("inferpal-noeffect-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_ws, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private sealed class Yes : IApprovalService
    {
        public Task<bool> RequestApprovalAsync(string toolName, string details, CancellationToken ct,
                                               string? subject = null, DiffInfo? diff = null, bool forcePrompt = false) =>
            Task.FromResult(true);
    }

    private static JsonElement Args(object value) =>
        JsonDocument.Parse(JsonSerializer.Serialize(value)).RootElement.Clone();

    private static ToolExecution Ran(string name) => new(name, "{}", "output");

    // ── The decision ───────────────────────────────────────────────────────

    [Fact]
    public void AnEditAttempt_InARunThatChangedNothing_IsSaid()
    {
        Assert.True(ChatTurnPolicy.EditsWithoutEffect([Ran("read_file"), Ran("write_file")], filesChangedInRun: 0));
        Assert.Contains(Strings.AgentEditsNotApplied, ChatTurnPolicy.EndNotice(false, false, false, editsWithoutEffect: true));
    }

    [Fact]
    public void ReferenceArms_SayNothing()
    {
        // A change landed, no edit was attempted, or no run tracked the writes: nothing to say.
        Assert.False(ChatTurnPolicy.EditsWithoutEffect([Ran("write_file")], filesChangedInRun: 1));
        Assert.False(ChatTurnPolicy.EditsWithoutEffect([Ran("read_file"), Ran("run_command")], filesChangedInRun: 0));
        Assert.False(ChatTurnPolicy.EditsWithoutEffect([Ran("apply_diff")], filesChangedInRun: null));
        Assert.Equal(string.Empty, ChatTurnPolicy.EndNotice(false, false, false));
    }

    [Fact]
    public void ARenameThatOnlyRehearsed_IsSaid()
    {
        // Measured in the battery (Codestral, a Python rename): rename_symbol answered its default dry run, no file
        // changed, and the answer read "I've renamed the method … and updated its references" — with nothing under it.
        Assert.True(ChatTurnPolicy.EditsWithoutEffect([Ran("search_in_files"), Ran("rename_symbol")], filesChangedInRun: 0));
    }

    /// <summary>The property the list must hold: a tool that writes a file backs it up first, and counts as an attempt.</summary>
    [Fact]
    public void EveryToolThatWritesAFile_CountsAsAnEditAttempt()
    {
        var writers = ConventionCoverageTests.CoreSources(Path.Combine("Services", "Tools"))
            .Select(ConventionCoverageTests.CodeOnly)
            .Where(code => code.Contains("BackUpBeforeChangeAsync(", StringComparison.Ordinal))
            .Select(code => System.Text.RegularExpressions.Regex.Match(code, @"string\s+Name\s*=>\s*""([a-z_]+)""").Groups[1].Value)
            .ToList();

        Assert.True(writers.Count >= 6, $"only {writers.Count} writing tools read");   // witness: the scan finds them
        Assert.All(writers, name => Assert.False(string.IsNullOrEmpty(name), "a writing tool whose name was not read"));
        Assert.All(writers, name => Assert.True(ChatTurnPolicy.IsFileEdit(name),
            $"{name} writes a file and a run where it changed nothing would say nothing"));
    }

    // ── Both front-ends ask, on both of their tool loops ───────────────────

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !System.IO.File.Exists(Path.Combine(dir.FullName, "Inferpal.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repository root not found");
    }

    [Theory]
    [InlineData("Inferpal/ToolWindow/InferpalToolWindowData.ChatTurn.cs")]
    [InlineData("Inferpal.Host/HostServer.cs")]
    public void BothFrontEnds_AskOnTheOrchestratedAndTheBasicLoop(string relative)
    {
        // Neither call site runs from the suite (Remote UI on one side, a live session on the other): read the code.
        var code = ConventionCoverageTests.CodeOnly(Path.Combine([RepoRoot(), .. relative.Split('/')]));

        Assert.Contains("ChatTurnPolicy.EndNotice(", code, StringComparison.Ordinal);   // witness
        var asks = code.Split("ChatTurnPolicy.EditsWithoutEffect(").Length - 1;
        Assert.Equal(2, asks);
    }

    // ── The fact it reads: every edit that lands is entered in the run ─────

    private string File(string content = "line one\n")
    {
        var path = Path.Combine(_ws, "Counter.razor");
        System.IO.File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public async Task ARefusedWrite_LeavesTheRunWithNoChangedFile()
    {
        var path    = File();
        var history = new FileHistoryService();
        history.BeginRun();

        var said = await new WriteFileTool(new Yes(), history, () => _ws)
            .ExecuteAsync(Args(new { path, content = "rewritten\n" }), default);

        Assert.Contains("has not been read", said);   // witness: the write really was refused
        Assert.Equal(0, history.CurrentRunFileCount);
    }

    [Fact]
    public async Task EveryEditToolThatLands_EntersItsFileInTheRun()
    {
        // The arm that keeps the notice from lying the other way: apply_edits reports no diff, so only the run can
        // tell that it landed.
        var path    = File("alpha\nbeta\n");
        var history = new FileHistoryService();
        history.BeginRun();

        await new ApplyEditsTool(new Yes(), history, () => _ws).ExecuteAsync(
            Args(new { edits = new[] { new { path, old_content = "beta", new_content = "gamma" } } }), default);

        Assert.Equal("alpha\ngamma\n", System.IO.File.ReadAllText(path));   // witness: it landed
        Assert.Equal(1, history.CurrentRunFileCount);

        var other = Path.Combine(_ws, "Other.razor");
        System.IO.File.WriteAllText(other, "x\n");
        await new ApplyDiffTool(new Yes(), history, () => _ws).ExecuteAsync(
            Args(new { path = other, old_content = "x", new_content = "y" }), default);
        Assert.Equal(2, history.CurrentRunFileCount);
    }
}
