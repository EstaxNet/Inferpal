using System.Text.Json;
using Inferpal.Localization;
using Inferpal.Models;
using Inferpal.Services.CodeActions;
using Inferpal.Services.Commands;
using Xunit;

namespace Inferpal.Tests;

// Applying a proposal (roadmap §18, tranche 2): the staleness rule, the routing of
// `/task propose` and `/task apply`, and the promise that applying goes through the real tools.
public class TaskProposalApplyTests
{
    private static TaskProposal Write(string path, string oldText, string newText) =>
        new("write_file", path, $"write {path}", new DiffInfo(oldText, newText, path));

    // ── Applicability ──────────────────────────────────────────────────────────

    [Fact]
    public void AProposalIsApplicable_OnlyAgainstTheStateItWasComputedFrom()
    {
        var plan = TaskProposalApplication.Decide(Write("a.cs", "old\n", "new\n"), "old\n");

        Assert.True(plan.Ready);
        Assert.Equal("new\n", plan.Content);
        Assert.False(plan.Delete);
    }

    [Fact]
    public void AFileChangedSinceTheTaskRan_IsStaleAndRefused()
    {
        // The whole reason this check exists: a background task runs while the user keeps working,
        // so writing the recorded "new" content would silently destroy whatever was typed in
        // between. The product refuses and says so; it does not merge and it does not guess.
        var plan = TaskProposalApplication.Decide(Write("a.cs", "old\n", "new\n"), "edited by hand\n");

        Assert.Equal(ProposalVerdict.Stale, plan.Verdict);
        Assert.Null(plan.Content);
    }

    [Fact]
    public void AFileAlreadyInTheProposedState_IsANoOp_NotAConflict()
    {
        // Checked before staleness: someone else reaching the same result is not a collision.
        var plan = TaskProposalApplication.Decide(Write("a.cs", "old\n", "new\n"), "new\n");

        Assert.Equal(ProposalVerdict.AlreadyApplied, plan.Verdict);
    }

    [Fact]
    public void ACreation_IsApplicableOnlyWhenNothingIsThere()
    {
        Assert.True(TaskProposalApplication.Decide(Write("new.cs", "", "body\n"), null).Ready);
        Assert.Equal(ProposalVerdict.Missing,
                     TaskProposalApplication.Decide(Write("a.cs", "old\n", "new\n"), null).Verdict);
    }

    [Fact]
    public void ADeletion_NeedsTheFileToStillExist()
    {
        var proposal = new TaskProposal("delete_file", "a.cs", "delete a.cs", null);

        Assert.True(TaskProposalApplication.Decide(proposal, "anything").Ready);
        Assert.True(TaskProposalApplication.Decide(proposal, "anything").Delete);
        Assert.Equal(ProposalVerdict.Missing, TaskProposalApplication.Decide(proposal, null).Verdict);
    }

    [Fact]
    public void AProposalWithNoRecordedDiff_IsRefusedRatherThanGuessed()
    {
        var proposal = new TaskProposal("write_file", "a.cs", "write a.cs", Diff: null);

        Assert.Equal(ProposalVerdict.Unusable, TaskProposalApplication.Decide(proposal, "x").Verdict);
    }

    // ── Applying goes through the real tools ───────────────────────────────────

    /// <summary>Stands in for the session registry; records what it was asked to do.</summary>
    private sealed class RecordingRegistry(bool succeed) : IToolRegistry
    {
        public List<(string Tool, string Json)> Calls { get; } = [];
        public IReadOnlyList<ToolDefinition> Definitions => [];
        public DiffInfo? ConsumeDiff() => null;

        public Task<string> ExecuteAsync(string name, JsonElement args, CancellationToken ct)
        {
            Calls.Add((name, args.ToString()));
            return Task.FromResult(succeed ? "written" : "Cancelled by the user.");
        }
    }

    [Fact]
    public async Task ApplyingAWrite_CallsTheRealWriteTool_AndConfirmsOnlyIfItLanded()
    {
        var registry = new RecordingRegistry(succeed: true);
        var content  = "old\n";

        var message = await TaskProposalApplication.ApplyAsync(
            Write("a.cs", "old\n", "new\n"), registry,
            readFile: _ => content is "old\n" ? Interlocked.Exchange(ref content, "new\n") : content,
            ct: default);

        var call = Assert.Single(registry.Calls);
        Assert.Equal("write_file", call.Tool);
        Assert.Contains("new", call.Json);
        Assert.Contains(Strings.TaskProposalApplied("a.cs"), message);
    }

    [Fact]
    public async Task WhenTheUserDeclinesThePrompt_NoSuccessIsClaimed()
    {
        // The tool answers with its own refusal; asserting success on top of it would be a lie the
        // user reads as truth. The state is re-read instead of the message being matched — matching
        // it would break in ten locales.
        var registry = new RecordingRegistry(succeed: false);

        var message = await TaskProposalApplication.ApplyAsync(
            Write("a.cs", "old\n", "new\n"), registry, readFile: _ => "old\n", ct: default);

        Assert.Single(registry.Calls);
        Assert.DoesNotContain(Strings.TaskProposalApplied("a.cs"), message);
        Assert.Contains("Cancelled", message);
    }

    [Fact]
    public async Task ApplyingOpensAChangeTrackingRun_SoUndoRunReallyCoversIt()
    {
        // The message says "/undo-run covers it like any other write". Verified live on 2026-08-03
        // and it did not: the snapshot was taken but attached to no run, because a run is opened by
        // a chat turn and `/task apply` is not one. The promise was the part that was wrong.
        var registry = new RecordingRegistry(succeed: true);
        var runs     = 0;
        var content  = "old\n";

        await TaskProposalApplication.ApplyAsync(
            Write("a.cs", "old\n", "new\n"), registry,
            readFile: _ => content is "old\n" ? Interlocked.Exchange(ref content, "new\n") : content,
            ct: default, beginRun: () => runs++);

        Assert.Equal(1, runs);
    }

    [Fact]
    public async Task ARefusedProposal_OpensNoRun()
    {
        // An empty run in the list would offer to undo something that never happened.
        var registry = new RecordingRegistry(succeed: true);
        var runs     = 0;

        await TaskProposalApplication.ApplyAsync(
            Write("a.cs", "old\n", "new\n"), registry, readFile: _ => "edited\n",
            ct: default, beginRun: () => runs++);

        Assert.Equal(0, runs);
    }

    [Fact]
    public async Task AStaleProposal_NeverReachesTheTools()
    {
        var registry = new RecordingRegistry(succeed: true);

        var message = await TaskProposalApplication.ApplyAsync(
            Write("a.cs", "old\n", "new\n"), registry, readFile: _ => "edited\n", ct: default);

        Assert.Empty(registry.Calls);
        Assert.Equal(Strings.TaskProposalStale("a.cs"), message);
    }

    // ── Routing ────────────────────────────────────────────────────────────────

    private static BackgroundTaskQueue Queue() =>
        new((_, _, ct) => Task.Delay(Timeout.Infinite, ct)
                              .ContinueWith(_ => BackgroundTaskQueue.TaskRunOutcome.Of(""), ct));

    private static TaskCommandHandler.TaskCommandResult Run(BackgroundTaskQueue q, string line) =>
        TaskCommandHandler.Handle(q, line.Split(' ', StringSplitOptions.RemoveEmptyEntries));

    [Fact]
    public void Propose_SubmitsInProposalMode_AndTheBareFormStaysReadOnly()
    {
        using var queue = Queue();

        Run(queue, "/task propose tidy the RAG layer");
        Run(queue, "/task audit the RAG layer");

        var tasks = queue.List();
        Assert.True(tasks.Single(t => t.Objective.StartsWith("tidy")).ProposeWrites);
        Assert.False(tasks.Single(t => t.Objective.StartsWith("audit")).ProposeWrites);
    }

    [Fact]
    public void ProposeWithoutAnObjective_ShowsTheUsage()
    {
        using var queue = Queue();

        Assert.Contains("/task propose", Run(queue, "/task propose").Message);
        Assert.Empty(queue.List());
    }

    [Fact]
    public void Apply_NeedsAnIdAndANumber_AndReportsWhatIsMissing()
    {
        using var queue = Queue();
        var id = queue.Submit("something", proposeWrites: true)!;

        Assert.Contains("/task apply", Run(queue, "/task apply").Message);
        Assert.Contains("/task apply", Run(queue, $"/task apply {id}").Message);
        Assert.Equal(Strings.TaskUnknown("t99"), Run(queue, "/task apply t99 1").Message);
        Assert.Equal(Strings.TaskNoProposals(id), Run(queue, $"/task apply {id} 1").Message);
    }

    [Fact]
    public void ThereIsNoFormThatAppliesEveryProposalAtOnce()
    {
        // Grouped approval is what §9 refuses, and applying a whole batch would only move it from
        // submission to return. `/task apply <id>` without a number is a usage error, on purpose.
        using var queue = Queue();
        var id = queue.Submit("something", proposeWrites: true)!;

        Assert.Contains("<n>", Run(queue, $"/task apply {id}").Message);
    }
}
