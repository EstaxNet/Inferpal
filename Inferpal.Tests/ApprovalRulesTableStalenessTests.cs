using System.IO;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// VS Code settings: a row of the approval rules table deletes by the index of its line, so it deletes only from the
/// text the table was built from.
/// </summary>
/// <remarks>
/// The table comes from the host (<c>permissions/table</c>), asynchronously, and not at all while the host is down.
/// After a delete the shown rows kept their old indices until the answer came: a second delete in that window, or any
/// delete once the host had died, removed the rule BELOW the one clicked. Same after an edit as text. The Visual Studio
/// window rebuilds its table in-process on every change of the text: not affected.
/// </remarks>
public sealed class ApprovalRulesTableStalenessTests
{
    private static string Editor() => ConversationPersistenceSilenceTests.TsMethodBody(
        Path.Combine(ConversationPersistenceSilenceTests.RepoRoot(), "vscode", "src", "webview", "settingsEditors.ts"),
        "function approvalRulesEditor(");

    [Fact]
    public void ADelete_ChecksTheTableStillDescribesTheText_BeforeRemovingALine()
    {
        var body = Editor();

        var splice = body.IndexOf("all.splice(r.machineLine, 1)", StringComparison.Ordinal);
        Assert.True(splice >= 0, "the delete by line index was not found: this test would have measured nothing.");

        var guard = body.LastIndexOf("tableText !== area.value", splice, StringComparison.Ordinal);
        var closure = body.LastIndexOf("rowActions(", splice, StringComparison.Ordinal);
        Assert.True(guard > closure && closure >= 0, "the delete removes a line without checking the table describes the text.");
    }

    [Fact]
    public void ADelete_MovesTheRowsBelowUp_WithoutWaitingForTheHost()
    {
        var body = Editor();
        var splice = body.IndexOf("all.splice(r.machineLine, 1)", StringComparison.Ordinal);
        Assert.True(splice >= 0, "the delete by line index was not found: this test would have measured nothing.");

        var after = body[splice..];
        Assert.Contains("withoutMachineLine(table, r.machineLine)", after, StringComparison.Ordinal);
        Assert.Contains("tableText = area.value", after, StringComparison.Ordinal);
    }

    [Fact]
    public void TheTable_RemembersWhichTextItDescribes_AndOffersDeleteOnlyForThatText()
    {
        var body = Editor();

        Assert.Contains("askedText = area.value", body, StringComparison.Ordinal);   // the text sent with the request
        Assert.Contains("tableText = askedText", body, StringComparison.Ordinal);    // … is the one the answer describes
        Assert.Contains("r.machineLine >= 0 && tableText === area.value", body, StringComparison.Ordinal);
    }
}
