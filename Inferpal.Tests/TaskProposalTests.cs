using System.Text.Json;
using Inferpal.Localization;
using Inferpal.Models;
using Inferpal.Services.CodeActions;
using Xunit;

namespace Inferpal.Tests;

// Background tasks that propose writes (roadmap §18, the V2 of §9): the recorder that grants
// nothing, the registry that opens the editing tools without opening execution, and the report.
public class TaskProposalTests
{
    private static JsonElement Args(object o) =>
        JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(o));

    /// <summary>Registry that reports which tool was called and never touches anything.</summary>
    private sealed class SpyRegistry(IApprovalService approval) : IToolRegistry
    {
        public List<string> Calls { get; } = [];

        public IReadOnlyList<ToolDefinition> Definitions =>
        [
            Def("read_file"), Def("write_file"), Def("apply_diff"), Def("delete_file"),
            Def("run_command"), Def("fetch_url"), Def("rename_symbol"),
        ];

        private static ToolDefinition Def(string name) =>
            new("function", new ToolFunction(name, name, new { }));

        public DiffInfo? ConsumeDiff() => null;

        public async Task<string> ExecuteAsync(string name, JsonElement args, CancellationToken ct)
        {
            Calls.Add(name);

            // Mimics a real mutating tool: ask, and do nothing at all when refused.
            if (name is "write_file" or "apply_diff" or "apply_edits" or "delete_file")
            {
                var path = args.TryGetProperty("path", out var p) ? p.GetString() ?? "x" : "x";
                var ok = await approval.RequestApprovalAsync(
                    name, $"write {path}", ct, subject: path,
                    diff: new DiffInfo("old line\n", "new line\n", path));
                return ok ? "written" : "Cancelled by the user.";
            }
            return "read";
        }
    }

    // ── The recorder grants nothing ────────────────────────────────────────────

    [Fact]
    public async Task EveryApprovalRequest_IsRecordedAndRefused()
    {
        // The whole safety argument of §18: a mutating tool treats a refusal as "touch nothing", so
        // a task in proposal mode cannot modify the workspace however much the model insists.
        var recorder = new ProposalRecorder();

        var granted = await recorder.RequestApprovalAsync(
            "write_file", "write a.cs", default, subject: @"C:\repo\a.cs",
            diff: new DiffInfo("a\n", "b\n", "a.cs"));

        Assert.False(granted);
        var proposal = Assert.Single(recorder.Proposals);
        Assert.Equal("write_file", proposal.Tool);
        Assert.Equal(@"C:\repo\a.cs", proposal.Subject);
        Assert.NotNull(proposal.Diff);
    }

    [Fact]
    public async Task ForcePrompt_IsStillRefused()
    {
        // A repository-authored command arrives force-prompted precisely so a human reads it. There
        // is no human here, so "record and refuse" is the only correct answer — never an exception
        // that would let it through.
        var recorder = new ProposalRecorder();

        Assert.False(await recorder.RequestApprovalAsync(
            "write_file", "d", default, subject: "p", diff: null, forcePrompt: true));
    }

    [Fact]
    public async Task RewritingTheSameFile_KeepsOneProposal_TheLastOne()
    {
        // Two attempts at the same file are one intention. Keeping both would ask the user to review
        // a change that was already superseded, and the earlier diff's "new" side is not what the
        // task ended up wanting.
        var recorder = new ProposalRecorder();

        await recorder.RequestApprovalAsync("write_file", "first", default, subject: "p",
                                            diff: new DiffInfo("a\n", "b\n", "p"));
        await recorder.RequestApprovalAsync("write_file", "second", default, subject: "p",
                                            diff: new DiffInfo("a\n", "c\n", "p"));

        Assert.Equal("second", Assert.Single(recorder.Proposals).Details);
        Assert.Equal(2, recorder.RequestCount);   // superseded requests still counted
    }

    // ── The registry opens editing, not execution ──────────────────────────────

    [Fact]
    public void ReadOnlyMode_ExposesNoEditingTool()
    {
        var registry = new BackgroundTaskToolRegistry(new SpyRegistry(new ProposalRecorder()));

        var names = registry.Definitions.Select(d => d.Function.Name).ToArray();
        Assert.Contains("read_file", names);
        Assert.DoesNotContain("write_file", names);
    }

    [Fact]
    public void ProposalMode_ExposesEditing_ButNeverExecutionOrNetwork()
    {
        // Deferring a *command* would be the §9 blank cheque wearing another hat, and a command has
        // no diff to review — the user would be approving a sentence, not a change.
        var registry = new BackgroundTaskToolRegistry(new SpyRegistry(new ProposalRecorder()),
                                                      new ProposalRecorder());

        var names = registry.Definitions.Select(d => d.Function.Name).ToArray();

        Assert.Contains("write_file", names);
        Assert.Contains("apply_diff", names);
        Assert.Contains("delete_file", names);
        Assert.DoesNotContain("run_command", names);
        Assert.DoesNotContain("fetch_url", names);
        Assert.DoesNotContain("rename_symbol", names);
    }

    [Fact]
    public async Task ARefusedRun_command_IsNotEvenForwarded()
    {
        var inner    = new SpyRegistry(new ProposalRecorder());
        var registry = new BackgroundTaskToolRegistry(inner, new ProposalRecorder());

        var answer = await registry.ExecuteAsync("run_command", Args(new { command = "rm -rf /" }), default);

        Assert.Empty(inner.Calls);
        Assert.Contains("not available", answer);
    }

    [Fact]
    public async Task AProposedWrite_IsReportedToTheModelAsRecorded_NotAsCancelled()
    {
        // The tool's own answer is "Cancelled by the user." A small local model reads that as a dead
        // end and either retries or abandons the objective, so the outcome is restated as what it
        // actually is.
        var recorder = new ProposalRecorder();
        var registry = new BackgroundTaskToolRegistry(new SpyRegistry(recorder), recorder);

        var answer = await registry.ExecuteAsync("write_file", Args(new { path = "a.cs" }), default);

        Assert.Contains("Recorded as a proposal", answer);
        Assert.DoesNotContain("Cancelled", answer);
        Assert.Single(recorder.Proposals);
    }

    [Fact]
    public async Task ASecondWriteToTheSameFile_IsStillReportedAsRecorded()
    {
        // The bug this pins: detecting "a proposal landed" by the collection size fails here,
        // because the second write replaces the first in place. The raw cancellation would reach
        // the model on the very case where it is most likely to retry.
        var recorder = new ProposalRecorder();
        var registry = new BackgroundTaskToolRegistry(new SpyRegistry(recorder), recorder);

        await registry.ExecuteAsync("write_file", Args(new { path = "a.cs" }), default);
        var second = await registry.ExecuteAsync("write_file", Args(new { path = "a.cs" }), default);

        Assert.Contains("Recorded as a proposal", second);
        Assert.Single(recorder.Proposals);
    }

    [Fact]
    public async Task AReadCall_IsPassedThroughUntouched()
    {
        var recorder = new ProposalRecorder();
        var registry = new BackgroundTaskToolRegistry(new SpyRegistry(recorder), recorder);

        Assert.Equal("read", await registry.ExecuteAsync("read_file", Args(new { path = "a.cs" }), default));
        Assert.Empty(recorder.Proposals);
    }

    [Fact]
    public void TheProposalPrompt_TellsTheModelARefusalIsExpected()
    {
        // Without this, the model's first editing attempt looks like a failure of the task.
        Assert.Contains("not a failure", BackgroundTaskToolRegistry.ProposalPromptSuffix);
        Assert.Contains("proposal", BackgroundTaskToolRegistry.ProposalPromptSuffix);
    }

    // ── The report ─────────────────────────────────────────────────────────────

    [Fact]
    public void NoProposal_RendersNothing()
    {
        Assert.Equal(string.Empty, TaskProposalReport.Render("bg1", []));
    }

    [Fact]
    public void TheReport_ShowsADiffPerProposalAndHowToApplyThem()
    {
        var proposals = new List<TaskProposal>
        {
            new("write_file", @"C:\repo\a.cs", "write a.cs", new DiffInfo("old\n", "new\n", "a.cs")),
            new("delete_file", @"C:\repo\b.cs", "delete b.cs", null),
        };

        var text = TaskProposalReport.Render("bg1", proposals);

        Assert.Contains(Strings.TaskProposalsHeader(2), text);
        Assert.Contains("```diff", text);
        Assert.Contains("-old", text);
        Assert.Contains("+new", text);
        Assert.Contains("delete b.cs", text);                        // no diff, still listed
        Assert.Contains(Strings.TaskProposalsApplyHint("bg1"), text);
    }

    [Fact]
    public void BracesInProposedCode_SurviveTheReport()
    {
        // The report is assembled with string.Format for its headings, and proposed C# is full of
        // braces — `$"Hello {name}"` is the ordinary case, not an exotic one. Formatting the diff
        // too would eat it, and the user would review a truncated change while approving the real
        // one. Written after a live run showed a truncated line that turned out to be the model's
        // own doing, not ours: this test is what tells the two apart next time.
        const string before = "    {\n        return \"Hello \" + name;\n    }\n";
        const string after  = "    {\n        return $\"Hello {name}\";\n    }\n";

        var text = TaskProposalReport.Render("t1",
            [new TaskProposal("apply_diff", "Greeter.cs", "Apply change to: Greeter.cs",
                              new DiffInfo(before, after, "Greeter.cs"))]);

        Assert.Contains("{name}", text);
    }

    [Fact]
    public void ALongDiff_IsCappedOnce()
    {
        // DiffComputer already caps and appends its own marker; a second truncation here would cut
        // an already-cut diff and print a line count that is not the real one.
        var old = string.Join('\n', Enumerable.Range(0, 200).Select(i => $"line {i}"));
        var neu = string.Join('\n', Enumerable.Range(0, 200).Select(i => $"changed {i}"));

        var text = TaskProposalReport.Render("bg1",
            [new("write_file", "p", "write p", new DiffInfo(old, neu, "p"))]);

        // Exactly one truncation marker: two would mean two cappers disagreeing about the count.
        Assert.Equal(2, text.Split("more diff line(s)").Length);
        Assert.True(text.Split('\n').Count(l => l.StartsWith('+') || l.StartsWith('-'))
                    <= TaskProposalReport.MaxDiffLines);
    }
}
