using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Inferpal.Models;
using Inferpal.Services.CodeActions;
using Inferpal.Services.Execution;
using Inferpal.Services.Tasks;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

// ──────────────────────────────────────────────────────────────────────────────────────────────
//  A /task proposal made with apply_edits can be applied.
//
//  apply_edits asked for approval without a diff, so the recorder kept a proposal it could never
//  reproduce: "/task apply" answered "no reviewable change was recorded", a batch keyed on the joined
//  paths never lined up with the same file's other proposals, and it replaced a usable apply_diff one.
// ──────────────────────────────────────────────────────────────────────────────────────────────
public sealed class TaskApplyEditsProposalTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("inferpal-taskedits-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private string File_(string name, string content)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, content);
        return path;
    }

    private static JsonElement Edits(params (string Path, string Old, string New)[] edits) =>
        JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(new
        {
            edits = edits.Select(e => new { path = e.Path, old_content = e.Old, new_content = e.New }).ToArray(),
        }));

    /// <summary>The background task's registry over a real apply_edits whose approval is the recorder.</summary>
    private sealed class OneTool(ApplyEditsTool tool) : IToolRegistry
    {
        public IReadOnlyList<ToolDefinition> Definitions => [];
        public DiffInfo? ConsumeDiff() => null;
        public Task<string> ExecuteAsync(string name, JsonElement args, CancellationToken ct) => tool.ExecuteAsync(args, ct);
    }

    private (ProposalRecorder Recorder, BackgroundTaskToolRegistry Registry) Task_()
    {
        var recorder = new ProposalRecorder();
        var tool     = new ApplyEditsTool(recorder, new FileHistoryService(), () => _root);
        return (recorder, new BackgroundTaskToolRegistry(new OneTool(tool), recorder));
    }

    [Fact]
    public async Task AnApplyEditsProposal_IsReadyToApply()
    {
        var a = File_("a.txt", "a\nb\nc\n");
        var (recorder, registry) = Task_();

        await registry.ExecuteAsync("apply_edits", Edits((a, "b", "B")), CancellationToken.None);

        var plan = TaskProposalApplication.Decide(Assert.Single(recorder.Proposals), File.ReadAllText(a));
        Assert.True(plan.Ready, plan.Verdict.ToString());
        Assert.Equal("a\nB\nc\n", plan.Content);
        Assert.Equal("a\nb\nc\n", File.ReadAllText(a));   // a proposal writes nothing
    }

    [Fact]
    public async Task ABatchOverTwoFiles_IsOneProposalPerFile_EachNamedToTheModel()
    {
        var a = File_("a.txt", "a\nb\n");
        var b = File_("b.txt", "x\ny\n");
        var (recorder, registry) = Task_();

        var said = await registry.ExecuteAsync("apply_edits", Edits((a, "b", "B"), (b, "y", "Y")), CancellationToken.None);

        Assert.Equal(2, recorder.Proposals.Count);
        Assert.Equal("a\nB\n", TaskProposalApplication.Decide(recorder.Proposals.Single(p => p.Subject == a), File.ReadAllText(a)).Content);
        Assert.Equal("x\nY\n", TaskProposalApplication.Decide(recorder.Proposals.Single(p => p.Subject == b), File.ReadAllText(b)).Content);
        Assert.Contains(a, said);
        Assert.Contains(b, said);
    }

    [Fact]
    public async Task ABatch_CombinesWithTheSameFilesEarlierProposal()
    {
        var a = File_("a.txt", "a\nb\nc\nd\ne\n");
        var (recorder, registry) = Task_();

        await registry.ExecuteAsync("apply_edits", Edits((a, "b", "B")), CancellationToken.None);
        await registry.ExecuteAsync("apply_edits", Edits((a, "d", "D")), CancellationToken.None);

        var plan = TaskProposalApplication.Decide(Assert.Single(recorder.Proposals), File.ReadAllText(a));
        Assert.Equal("a\nB\nc\nD\ne\n", plan.Content);
    }

    /// <summary>A prompt that counts what it is asked.</summary>
    private sealed class Prompt : IApprovalService
    {
        public List<(string Details, DiffInfo? Diff)> Asked { get; } = [];
        public Task<bool> RequestApprovalAsync(string toolName, string details, CancellationToken ct,
                                               string? subject = null, DiffInfo? diff = null, bool forcePrompt = false)
        {
            Asked.Add((details, diff));
            return Task.FromResult(false);
        }
    }

    [Fact]
    public async Task AHumanStillReadsOnePromptForTheWholeBatch()
    {
        // Reference arm: what a prompting approval service receives is unchanged — one prompt, both files in it.
        var a = File_("a.txt", "a\nb\n");
        var b = File_("b.txt", "x\ny\n");
        var prompt = new Prompt();

        await new ApplyEditsTool(prompt, new FileHistoryService(), () => _root)
            .ExecuteAsync(Edits((a, "b", "B"), (b, "y", "Y")), CancellationToken.None);

        var (details, _) = Assert.Single(prompt.Asked);
        Assert.Contains("a.txt", details);
        Assert.Contains("b.txt", details);
    }
}
