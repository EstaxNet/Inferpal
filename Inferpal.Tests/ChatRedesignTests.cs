using System.IO;
using System.Text.Json;
using Inferpal.Localization;
using Inferpal.Services.Agent;
using Inferpal.Services.CodeActions;
using Inferpal.Services.Execution;
using Inferpal.Services.Persistence;
using Inferpal.Services.Presentation;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// The chat's presenters, the same in both editors: a question split from what went with it, a run's line and result
/// bar, an approval card, the context ring — and the rows only the screen holds, which the session file never keeps.
/// </summary>
// Builds and reads sentences of Strings: no other class may switch the language in between.
[Collection(CultureSerialCollection.Name)]
public sealed class ChatRedesignTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "inferpal-chat-" + Guid.NewGuid().ToString("N"));

    public ChatRedesignTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    // ── A question and its chips ────────────────────────────────────────────────

    [Fact]
    public void AQuestion_SplitsFromTheNamesThatWentWithIt()
    {
        var bubble = ChatTurnPolicy.BuildBubbleText("Explain this", ["Program.cs", "selection (12 lines)"]);

        var (text, names) = ChatTurnPolicy.SplitBubbleText(bubble);

        Assert.Equal("Explain this", text);
        Assert.Equal(["Program.cs", "selection (12 lines)"], names);
    }

    [Fact]
    public void ARecapAlone_GivesAnEmptyQuestionAndItsNames()
    {
        var (text, names) = ChatTurnPolicy.SplitBubbleText(ChatTurnPolicy.BuildBubbleText("", ["a.cs"]));

        Assert.Equal("", text);
        Assert.Equal(["a.cs"], names);
    }

    [Fact]
    public void AQuestionWithNothingAttached_IsKeptWhole()
    {
        const string question = "Fix the build\n\nthen run the tests";

        var (text, names) = ChatTurnPolicy.SplitBubbleText(question);

        Assert.Equal(question, text);
        Assert.Empty(names);
    }

    [Fact]
    public void TheRecapSentence_QuotedInsideAQuestion_IsNotTakenForTheRecap()
    {
        var prefix = Strings.MsgAttachedRecap("\0").Split('\0')[0];
        var quoted = $"Why does the chat write {prefix}x.cs in my question?";
        var followed = $"Look at this:\n\n{prefix}x.cs\nand tell me";

        Assert.Equal((quoted, 0), (ChatTurnPolicy.SplitBubbleText(quoted).Text, ChatTurnPolicy.SplitBubbleText(quoted).Attachments.Count));
        Assert.Equal((followed, 0), (ChatTurnPolicy.SplitBubbleText(followed).Text, ChatTurnPolicy.SplitBubbleText(followed).Attachments.Count));
    }

    // ── What a step acted on ────────────────────────────────────────────────────

    [Theory]
    [InlineData("{\"path\":\"Pages/Counter.razor\"}", "Pages/Counter.razor")]
    [InlineData("{\"cwd\":\"src\",\"command\":\"dotnet test\"}", "dotnet test")]
    [InlineData("{\"query\":\"where is Reset used\",\"top_k\":5}", "where is Reset used")]
    [InlineData("{\"top_k\":5}", "")]
    [InlineData("{\"path\":42}", "")]
    [InlineData("[\"a.cs\"]", "")]
    [InlineData("not json at all", "")]
    [InlineData("", "")]
    public void AStep_NamesWhatItActedOn(string input, string expected) =>
        Assert.Equal(expected, RunSummary.Subject(input));

    [Fact]
    public void ALongSubject_IsCut_AndSaysSo()
    {
        var subject = RunSummary.Subject(JsonSerializer.Serialize(new { command = new string('x', 200) }));

        Assert.Equal(81, subject.Length);
        Assert.EndsWith("…", subject, StringComparison.Ordinal);
    }

    // ── A run's line and its result bar ─────────────────────────────────────────

    [Fact]
    public void ARun_SaysWhatItDid_TheFilesItChanged_AndItsLastCheck()
    {
        var file = Path.Combine(_dir, "Counter.razor");
        var snapshot = Path.Combine(_dir, "Counter.razor.bak");
        File.WriteAllText(snapshot, "<h1>Counter</h1>\n<button>Click me</button>\n");
        File.WriteAllText(file, "<h1>Counter</h1>\n<button>Click me</button>\n<button>Reset</button>\n@code { }\n");
        var run = new HistoryRun("r1");
        run.RecordFirst(file, snapshot);

        var summary = RunSummary.Build(
        [
            new ToolExecution("read_file", "{\"path\":\"Counter.razor\"}", "…"),
            new ToolExecution("apply_diff", "{\"path\":\"Counter.razor\"}", "Applied."),
            new ToolExecution("get_diagnostics", "{}", Strings.DiagBuildOk("Counter.csproj")),
        ], run)!;

        Assert.Equal(3, summary.Steps);
        Assert.Equal(Strings.RunSteps(3), summary.Title);
        Assert.Equal(string.Join(" · ", Strings.RunRead1, Strings.RunEdited1, Strings.RunBuildPassed), summary.Detail);
        Assert.Equal(RunCheck.BuildPassed, summary.Check);
        var changed = Assert.Single(summary.Files);
        Assert.Equal(("Counter.razor", 2, 0, false, false), (changed.Name, changed.Added, changed.Removed, changed.Created, changed.Gone));
        Assert.Equal("r1", summary.RunId);
    }

    [Fact]
    public void ARunThatChangedNothing_OffersNoUndo_AndProvesNothing()
    {
        var summary = RunSummary.Build([new ToolExecution("read_file", "{\"path\":\"a.cs\"}", "…")], new HistoryRun("r2"))!;

        Assert.Empty(summary.Files);
        Assert.Equal(RunCheck.None, summary.Check);
        Assert.Equal("", summary.RunId);
        Assert.Equal(Strings.RunSteps1, summary.Title);
    }

    [Fact]
    public void NoToolCall_IsNoRun() => Assert.Null(RunSummary.Build([], null));

    [Fact]
    public void AFileTheRunCreated_CountsEveryLineAsAdded()
    {
        var file = Path.Combine(_dir, "New.cs");
        File.WriteAllText(file, "class A\n{\n}\n");
        var run = new HistoryRun("r3");
        run.RecordFirst(file, snapshot: null);

        var changed = Assert.Single(RunSummary.Files(run));

        Assert.True(changed.Created);
        Assert.Equal((3, 0), (changed.Added, changed.Removed));
    }

    [Theory]
    [InlineData("a\nb\nc", "a\nb\nc", 0, 0)]
    [InlineData("a\nb", "a\nX\nb", 1, 0)]
    [InlineData("a\nb\nc", "a\nc", 0, 1)]
    [InlineData("a\nb\nc", "a\nB\nc", 1, 1)]
    [InlineData("", "x\ny\n", 2, 0)]
    [InlineData("a\r\nb\r\n", "a\nb\n", 0, 0)]
    public void LineDelta_CountsTheLinesEachSideDoesNotHold(string before, string after, int added, int removed) =>
        Assert.Equal((added, removed), RunSummary.LineDelta(before, after));

    [Fact]
    public void ARunsDuration_ReadsAsItsHeaderShowsIt()
    {
        Assert.Equal(Strings.TurnSeconds(21), RunSummary.Duration(TimeSpan.FromSeconds(21)));
        Assert.Equal(Strings.TurnMinutes(2, "05"), RunSummary.Duration(TimeSpan.FromSeconds(125)));
    }

    // ── An approval card ────────────────────────────────────────────────────────

    [Fact]
    public void ANewFile_ShowsItsFirstLines_AndHowManyMore()
    {
        var path = Path.Combine(_dir, "Tests", "CounterTests.cs");
        var content = string.Join("\n", Enumerable.Range(1, ApprovalCard.PreviewLines + 3).Select(i => $"line {i}")) + "\n";

        var card = ApprovalCard.Build(new ApprovalPrompt("write_file", path, path, new DiffInfo("", content, path), "the prompt"), _dir);

        Assert.Equal(Strings.ApprovalCreateFile, card.Title);
        Assert.Equal(Path.Combine("Tests", "CounterTests.cs"), card.Subject);
        Assert.Equal(Strings.ApprovalLineCount(ApprovalCard.PreviewLines + 3), card.Meta);
        Assert.Equal(ApprovalCard.PreviewLines, card.Preview.Count);
        Assert.All(card.Preview, l => Assert.Equal("add", l.Kind));
        Assert.Equal("line 1", card.Preview[0].Text);
        Assert.Equal(Strings.ApprovalMoreLines(3), card.More);
        Assert.Equal("the prompt", card.Message);
    }

    [Fact]
    public void AChange_ShowsWhatItAddsAndRemoves()
    {
        var path = Path.Combine(_dir, "A.cs");

        var card = ApprovalCard.Build(new ApprovalPrompt("apply_diff", path, path, new DiffInfo("a\nb\nc\n", "a\nB\nc\n", path), "m"), _dir);

        Assert.Equal(Strings.ApprovalChangeFile, card.Title);
        Assert.Equal("+1 −1", card.Meta);
        Assert.Contains(card.Preview, l => l is { Kind: "del", Text: "b" });
        Assert.Contains(card.Preview, l => l is { Kind: "add", Text: "B" });
        Assert.Equal("", card.More);
    }

    [Fact]
    public void ACommand_ShowsTheCommandItself()
    {
        var card = ApprovalCard.Build(new ApprovalPrompt("run_command", "dotnet test --filter Counter", null, null, "m"), _dir);

        Assert.Equal(Strings.ApprovalRunCommand, card.Title);
        Assert.Equal("", card.Subject);
        var line = Assert.Single(card.Preview);
        Assert.Equal(("ctx", "dotnet test --filter Counter"), (line.Kind, line.Text));
    }

    [Fact]
    public void APathOutsideTheWorkspace_IsShownWhole()
    {
        var outside = Path.Combine(Path.GetTempPath(), "elsewhere", "x.cs");

        var card = ApprovalCard.Build(new ApprovalPrompt("delete_file", outside, outside, null, "m"), _dir);

        Assert.Equal(outside, card.Subject);
    }

    // ── The context ring ────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0, "M 9,2")]
    [InlineData(25, "M 9,2 A 7,7 0 0 1 16,9")]
    [InlineData(50, "M 9,2 A 7,7 0 0 1 9,16")]
    [InlineData(75, "M 9,2 A 7,7 0 1 1 2,9")]
    public void TheRing_DrawsTheShareOfTheWindowUsed(double percent, string path) =>
        Assert.Equal(path, ContextBudgetGauge.RingArc(percent));

    [Theory]
    [InlineData(100)]
    [InlineData(250)]
    public void AFullWindow_IsAFullRing_NeverAnEmptyOne(double percent)
    {
        // An arc whose two ends meet draws nothing: the end point must stay off the start.
        var end = ContextBudgetGauge.RingArc(percent).Split(' ')[^1];

        Assert.StartsWith("M 9,2 A 7,7 0 1 1 ", ContextBudgetGauge.RingArc(percent), StringComparison.Ordinal);
        Assert.NotEqual("9,2", end);
    }

    // ── What only the screen holds ──────────────────────────────────────────────

    [Fact]
    public void TheRowsOnlyTheScreenHolds_NeverReachTheSessionFile()
    {
        var saved = SessionManager.BuildSnapshot(
        [
            ("user", "q", "", ""), ("turn", "", "", ""), ("run", "", "", ""), ("tool", "out", "read_file", ""),
            ("assistant", "a", "", ""), ("result", "", "", ""), ("approval", "Allow?", "", ""), ("status", "…", "", ""),
            ("anchor", "", "", ""),
        ]);

        Assert.Equal(["user", "tool", "assistant"], saved.Select(m => m.Role));
    }
}
