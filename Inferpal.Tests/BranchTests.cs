using Inferpal.Services.Commands;
using Inferpal.Services.Persistence;
using Xunit;

namespace Inferpal.Tests;

// Conversation branching (ROADMAP 1.4.0 §7): turn splitting, truncation, branch naming, the
// fork plan and the /branch command routing. All pure — no store, no editor.
public class BranchTests
{
    private static SavedMessage U(string text) => new("user", text);
    private static SavedMessage A(string text) => new("assistant", text);
    private static SavedMessage T(string text) => new("tool", text, "read_file");

    /// <summary>3 turns; turn 2 spans a tool bubble + an answer.</summary>
    private static List<SavedMessage> Conversation() =>
    [
        U("first question"), A("first answer"),
        U("second question"), T("tool output"), A("second answer"),
        U("third question"), A("third answer"),
    ];

    // ── Turn splitting ─────────────────────────────────────────────────────────

    [Fact]
    public void SplitTurns_GroupsEverythingUntilTheNextUserMessage()
    {
        var turns = BranchManager.SplitTurns(Conversation());

        Assert.Equal(3, turns.Count);
        Assert.Equal([1, 2, 3], turns.Select(t => t.Index));
        Assert.Equal([2, 3, 2], turns.Select(t => t.MessageCount));
        Assert.Equal("second question", turns[1].Preview);
    }

    [Fact]
    public void SplitTurns_EmptyOrAssistantOnlyTranscript_HasNoTurn()
    {
        Assert.Empty(BranchManager.SplitTurns([]));
        Assert.Empty(BranchManager.SplitTurns([A("greeting")]));
    }

    [Fact]
    public void SplitTurns_TruncatesLongPreviews()
    {
        var preview = BranchManager.SplitTurns([U(new string('x', 200))])[0].Preview;

        Assert.EndsWith("…", preview);
        Assert.Equal(71, preview.Length);            // 70 chars + the ellipsis
    }

    // ── Truncation ─────────────────────────────────────────────────────────────

    [Fact]
    public void TruncateAtTurn_KeepsTheTurnComplete()
    {
        var kept = BranchManager.TruncateAtTurn(Conversation(), 2);

        Assert.NotNull(kept);
        Assert.Equal(5, kept!.Count);                // turns 1 and 2, tool bubble included
        Assert.Equal("second answer", kept[^1].Content);
    }

    [Fact]
    public void TruncateAtTurn_KeepsThePreamblePrecedingTheFirstUserMessage()
    {
        List<SavedMessage> withGreeting = [A("greeting"), U("question"), A("answer")];

        var kept = BranchManager.TruncateAtTurn(withGreeting, 1);

        Assert.Equal(3, kept!.Count);
        Assert.Equal("greeting", kept[0].Content);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(4)]
    public void TruncateAtTurn_OutOfRange_ReturnsNull(int turn)
        => Assert.Null(BranchManager.TruncateAtTurn(Conversation(), turn));

    // ── Naming ─────────────────────────────────────────────────────────────────

    [Fact]
    public void MakeBranchName_PicksTheFirstFreeSuffix()
    {
        Assert.Equal("chat__b2", BranchManager.MakeBranchName("chat", []));
        Assert.Equal("chat__b3", BranchManager.MakeBranchName("chat", ["chat__b2"]));
        Assert.Equal("chat__b4", BranchManager.MakeBranchName("chat", ["chat__b2", "CHAT__B3"]));
    }

    [Fact]
    public void MakeBranchName_BranchingABranch_StaysOnTheSameBase()
    {
        // No __b2__b2 tails: the family stays adjacent in the name-sorted session list.
        Assert.Equal("chat__b3", BranchManager.MakeBranchName("chat__b2", ["chat__b2"]));
    }

    [Fact]
    public void MakeBranchName_KeepsANonNumericSuffixIntact()
        => Assert.Equal("chat__brainstorm__b2", BranchManager.MakeBranchName("chat__brainstorm", []));

    // ── Fork planning ──────────────────────────────────────────────────────────

    [Fact]
    public void Plan_NamedSession_ForksAndRefreshesTheParentFile()
    {
        var plan = BranchManager.Plan(Conversation(), 2, "my_session", [S("my_session")], new DateTime(2026, 7, 30, 10, 12, 0));

        Assert.NotNull(plan);
        Assert.False(plan!.ParentIsNew);
        Assert.Equal("my_session", plan.ParentName);
        Assert.Equal("my_session__b2", plan.BranchName);
        Assert.Equal(2, plan.ForkTurn);
        Assert.Equal(5, plan.BranchMessages.Count);
        // The parent is rewritten with the conversation as it stands — turns added since it was
        // loaded would otherwise be lost when the chat switches to the branch.
        Assert.Equal(7, plan.ParentMessages.Count);
    }

    [Fact]
    public void Plan_BranchingABranch_KeepsTheParentsOwnLink()
    {
        // Re-saving the parent must not flatten the tree it already belongs to.
        var plan = BranchManager.Plan(
            Conversation(), 1, "root__b2", [S("root"), S("root__b2", "root", 2)], DateTime.Now);

        Assert.Equal("root__b2", plan!.ParentName);
        Assert.Equal("root", plan.ParentParent);
        Assert.Equal(2, plan.ParentForkTurn);
        Assert.Equal("root__b3", plan.BranchName);
    }

    [Fact]
    public void Plan_UnsavedConversation_GivesTheParentAGeneratedName()
    {
        // Branching must never be the operation that loses the other half of the conversation.
        var plan = BranchManager.Plan(Conversation(), 1, currentName: null, [], new DateTime(2026, 7, 30, 10, 12, 0));

        Assert.True(plan!.ParentIsNew);
        Assert.Equal("2026-07-30_1012_first_question", plan.ParentName);
        Assert.Equal("2026-07-30_1012_first_question__b2", plan.BranchName);
        Assert.Equal(7, plan.ParentMessages.Count);          // the full transcript is saved
        Assert.Equal(2, plan.BranchMessages.Count);
    }

    [Fact]
    public void Plan_AutoSaveSlot_CountsAsUnsaved()
    {
        var plan = BranchManager.Plan(Conversation(), 1, "last_session", [S("last_session")], DateTime.Now);

        Assert.True(plan!.ParentIsNew);
        Assert.NotEqual("last_session", plan.ParentName);
    }

    [Fact]
    public void Plan_UnknownTurn_ReturnsNull()
        => Assert.Null(BranchManager.Plan(Conversation(), 9, "s", [], DateTime.Now));

    // ── Family tree ────────────────────────────────────────────────────────────

    private static SessionSummary S(string name, string? parent = null, int? forkTurn = null)
        => new(name, DateTime.UtcNow, 4, "preview", parent, forkTurn);

    [Fact]
    public void FormatFamily_RendersTheWholeTreeFromTheRootAndMarksTheCurrentSession()
    {
        List<SessionSummary> sessions =
        [
            S("root"),
            S("root__b2", "root", 2),
            S("root__b3", "root__b2", 1),
            S("unrelated"),
        ];

        var tree = BranchManager.FormatFamily("root__b2", sessions);

        Assert.NotNull(tree);
        var lines = tree!.Split('\n').Select(l => l.TrimEnd()).ToList();
        Assert.Contains(lines, l => l == "- `root`");
        Assert.Contains(lines, l => l.StartsWith("  - `root__b2`") && l.Contains("←"));
        Assert.Contains(lines, l => l.StartsWith("    - `root__b3`"));
        Assert.DoesNotContain(lines, l => l.Contains("unrelated"));
    }

    [Fact]
    public void FormatFamily_LoneSessionOrNoSession_RendersNothing()
    {
        Assert.Null(BranchManager.FormatFamily("alone", [S("alone"), S("other")]));
        Assert.Null(BranchManager.FormatFamily(null, [S("alone")]));
    }

    [Fact]
    public void FormatFamily_CyclicParentLink_TerminatesInsteadOfHanging()
    {
        // Hand-edited (or corrupted) files must not spin the renderer forever.
        List<SessionSummary> cyclic = [S("a", "b", 1), S("b", "a", 1)];

        var tree = BranchManager.FormatFamily("a", cyclic);

        Assert.NotNull(tree);
        Assert.Contains("`a`", tree);
    }

    // ── /branch routing ────────────────────────────────────────────────────────

    [Fact]
    public void Handle_NoArgument_ListsTheBranchPoints()
    {
        var result = BranchCommandHandler.Handle(["/branch"], Conversation(), "s", []);

        Assert.Null(result.ForkTurn);
        Assert.Null(result.SwitchTo);
        Assert.Contains("**1.** first question", result.Message);
        Assert.Contains("**3.** third question", result.Message);
    }

    [Fact]
    public void Handle_EmptyConversation_SaysThereIsNothingToBranch()
    {
        var result = BranchCommandHandler.Handle(["/branch"], [], null, []);

        Assert.Equal(Inferpal.Localization.Strings.BranchNoConversation, result.Message);
    }

    [Fact]
    public void Handle_TurnNumber_RequestsAFork()
    {
        var result = BranchCommandHandler.Handle(["/branch", "2"], Conversation(), "s", []);

        Assert.Equal(2, result.ForkTurn);
        Assert.Null(result.Message);
    }

    [Fact]
    public void Handle_TurnOutOfRange_ExplainsInsteadOfForking()
    {
        var result = BranchCommandHandler.Handle(["/branch", "42"], Conversation(), "s", []);

        Assert.Null(result.ForkTurn);
        Assert.Equal(Inferpal.Localization.Strings.BranchInvalidTurn(3), result.Message);
    }

    [Fact]
    public void Handle_KnownSessionName_SwitchesToIt()
    {
        var result = BranchCommandHandler.Handle(["/branch", "root__B2"], Conversation(), "root", [S("root__b2", "root", 1)]);

        Assert.Equal("root__b2", result.SwitchTo);    // match is case-insensitive, answer is canonical
    }

    [Fact]
    public void Handle_UnknownName_ReportsIt()
    {
        var result = BranchCommandHandler.Handle(["/branch", "nope"], Conversation(), "root", []);

        Assert.Null(result.SwitchTo);
        Assert.Equal(Inferpal.Localization.Strings.BranchUnknown("nope"), result.Message);
    }
}
