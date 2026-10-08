using System.IO;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// VS Code: Stop while a <c>/fix</c>, <c>/refactor</c> or <c>/doc</c> waits in the Refactor Preview discards the
/// preview — the only thing left to stop.
/// </summary>
/// <remarks>
/// The model step is over when the preview opens: Stop sent <c>chat/cancel</c> to a host with nothing to cancel, and
/// the turn stayed busy, Stop button and all, until the user found the preview's own Apply or Discard. The preview's
/// Discard (<c>refactorPreview.discard</c>, a workbench command) resolves the pending <c>applyEdit</c> with false, so
/// the turn ends on its ordinary "Rewrite discarded" line.
/// </remarks>
public sealed class CodeActionPreviewStopTests
{
    private static string Provider() =>
        Path.Combine(ConversationPersistenceSilenceTests.RepoRoot(), "vscode", "src", "chatViewProvider.ts");

    [Fact]
    public void Stop_WithThePreviewOpen_DiscardsIt_BeforeAskingTheHost()
    {
        var src   = SettingsSchemaDriftTests.NeutralizeTypeScriptComments(File.ReadAllText(Provider()));
        var start = src.IndexOf("case 'cancel':", StringComparison.Ordinal);
        Assert.True(start >= 0, "the Stop case was not found: this test would have measured nothing.");
        var hostCancel = src.IndexOf("chatCancel()", start, StringComparison.Ordinal);
        Assert.True(hostCancel > start, "Stop no longer asks the host to cancel: check what replaced it.");   // witness

        var before = src[start..hostCancel];
        Assert.Contains("this.codeActionPreviewOpen", before, StringComparison.Ordinal);
        Assert.Contains("executeCommand('refactorPreview.discard')", before, StringComparison.Ordinal);
    }

    [Fact]
    public void ThePreviewFlag_CoversTheWait_AndIsClearedOnEveryExit()
    {
        var body = ConversationPersistenceSilenceTests.TsMethodBody(Provider(), "private async runCodeAction(");

        var wait = body.IndexOf("vscode.workspace.applyEdit(edit", StringComparison.Ordinal);
        Assert.True(wait >= 0, "the preview's applyEdit was not found: this test would have measured nothing.");
        var set = body.LastIndexOf("this.codeActionPreviewOpen = preview", wait, StringComparison.Ordinal);
        Assert.True(set >= 0, "the flag is not set before the preview opens.");

        var finallyAt = body.IndexOf("finally", wait, StringComparison.Ordinal);
        Assert.True(finallyAt > wait, "the flag is not cleared in a finally: a throwing applyEdit would leave Stop discarding nothing.");
        Assert.Contains("this.codeActionPreviewOpen = false", body[finallyAt..], StringComparison.Ordinal);
    }
}
