using System.IO;
using System.Text.RegularExpressions;
using Inferpal.Services.Persistence;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// ⚠ VS Code shows a slash command as typed, and saved it as a QUESTION: reloaded, "/models" went back to the model as
/// something it had been asked — never answered, since its output is a notice — so the next question followed it as a
/// second question in a row (which a strict chat template refuses), and <c>/branch</c> numbered the same session's
/// turns differently than Visual Studio, which shows no question for a slash command. The editor marks such an entry
/// as a notice; every reader of saved questions asks <see cref="SessionManager.IsQuestion"/>.
/// </summary>
public class SlashCommandQuestionRestoreTests
{
    private static readonly List<SavedMessage> Session =
    [
        new("user", "/models", SessionManager.NoticeMarker),
        new("assistant", "| model | size |", SessionManager.NoticeMarker),
        new("user", "Why does Cart.Total round down?"),
        new("assistant", "Because of the integer division."),
    ];

    [Fact]
    public void ARestoredSession_HandsTheModelItsQuestions_NotTheSlashCommands()
    {
        var history = SessionManager.BuildRestoredHistory("SYS", Session);

        Assert.Equal(["system", "user", "assistant"], history.Select(m => m.Role));
        Assert.Equal("Why does Cart.Total round down?", history[1].Content);
    }

    [Fact]
    public void AQuestionWithoutTheMarker_IsStillAQuestion()   // reference arm: every session saved before the marker
    {
        var history = SessionManager.BuildRestoredHistory("SYS",
            [new("user", "/models"), new("assistant", "| model |", SessionManager.NoticeMarker)]);

        Assert.Equal(["system", "user"], history.Select(m => m.Role));
    }

    [Fact]
    public void TheTurns_AreTheQuestions_InEveryReader()
    {
        var turns = BranchManager.SplitTurns(Session);
        Assert.Single(turns);
        Assert.StartsWith("Why does Cart.Total", turns[0].Preview, StringComparison.Ordinal);
        Assert.Equal(Session.Count, BranchManager.TruncateAtTurn(Session, 1)!.Count);   // the command before turn 1 stays

        Assert.Contains("Why_does_Cart_Total", BranchManager.MakeParentName(Session, new DateTime(2026, 10, 5)),
                        StringComparison.Ordinal);
    }

    [Fact]
    public void VsCode_MarksTheQuestion_OfEveryCommandItServesWithoutTheModel()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Inferpal.sln"))) root = root.Parent;
        Assert.NotNull(root);
        var code = SettingsSchemaDriftTests.NeutralizeTypeScriptComments(
            File.ReadAllText(Path.Combine(root!.FullName, "vscode", "src", "chatViewProvider.ts")));

        Assert.Contains("const question: WvTranscriptItem = { role: 'user', text: prompt, timestamp };", code);
        // A code action, a command the host served, a command the host refused: the three paths that never reach the
        // model — plus /explain and /review, marked until their turn reaches the host and unmarked then
        // (ExplainRefusedQuestionTests). A template sent as a prompt is a turn: its question stays one.
        Assert.Equal(4, Regex.Matches(code, @"question\.notice = true;").Count);
        Assert.Single(Regex.Matches(code, @"question\.notice = false;"));
        Assert.Contains("question.notice = true;\n      try {\n        await this.runCodeAction(", code.Replace("\r\n", "\n"));

        var sessions = SettingsSchemaDriftTests.NeutralizeTypeScriptComments(
            File.ReadAllText(Path.Combine(root.FullName, "vscode", "src", "chatSessions.ts")));
        Assert.Contains("...(m.toolName === NOTICE_MARKER ? { notice: true } : {})", sessions);   // read back for a user too
    }
}
