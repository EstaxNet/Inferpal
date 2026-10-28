using System.IO;
using Inferpal.Localization;
using Inferpal.Services.Persistence;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// The composer's mode selector, in both editors: what a mode found here says of itself (its model, what it may do, its
/// file), the line where it starts holding the conversation (screen only), and the window and the extension that hold it
/// — its tools, model and effort for a turn, left by Chat, Agent and Plan, held again after a restarted host.
/// </summary>
public class ModeSelectorTests
{
    private static ChatMode Mode(ModeTools? tools, params string[] models) =>
        new("Docs Writer", "Writes the documentation", "", models, tools, null, ".roomodes (docs-writer)", ChatModeOrigin.Roo, []);

    [Fact]
    public void AModeSays_ItsModel_WhatItMayDo_AndItsFile()
    {
        var reads  = Mode(ModeTools.FromCopilot(["read", "search"]), "qwen3.8:27b");
        var edits  = Mode(ModeTools.FromRoo([("read", null, null, false), ("edit", @"\.(md|mdx)$", "Markdown files only", false)]));
        var anyTool = Mode(null);

        Assert.Equal($"qwen3.8:27b · {Strings.ModeReadsOnly} · .roomodes (docs-writer)", ModePresenter.Summary(reads, "qwen3.8:27b"));
        // No model of its own: the conversation's answers.
        Assert.StartsWith(Strings.ModePickerChatModel + " · ", ModePresenter.Summary(edits, null), StringComparison.Ordinal);
        Assert.Contains(Strings.ModesEditsOnly(@"\.(md|mdx)$"), ModePresenter.Summary(edits, null), StringComparison.Ordinal);
        Assert.Contains(Strings.ModesToolsAll, ModePresenter.Summary(anyTool, null), StringComparison.Ordinal);

        Assert.Equal(Strings.ModeHeldLine("Docs Writer", Strings.ModeReadsOnly), ModePresenter.HeldLine(reads));
        Assert.Equal(Strings.ModeHeldLine("Docs Writer", Strings.ModeMayWrite), ModePresenter.HeldLine(edits));
        Assert.Equal(Strings.ModeHeldLine("Docs Writer", Strings.ModeMayWrite), ModePresenter.HeldLine(anyTool));
    }

    [Fact]
    public void TheLineWhereAModeStarts_IsNeverSaved()
    {
        var saved = SessionManager.BuildSnapshot(
        [
            ("user", "Where is the total rounded?", "", ""),
            ("mode", "Mode: Review — from this question", "", ""),
            ("assistant", "Line 88.", "", ""),
        ]);

        Assert.Equal(["user", "assistant"], saved.Select(m => m.Role));
    }

    [Fact]
    public void TheVisualStudioWindow_HoldsTheModeForTheTurn_AndChatAgentAndPlanLeaveIt()
    {
        var dir = Path.Combine(ConversationPersistenceSilenceTests.RepoRoot(), "Inferpal", "ToolWindow");
        string Code(string file) => ConventionCoverageTests.CodeOnly(Path.Combine(dir, file));
        var turn = Code("InferpalToolWindowData.ChatTurn.cs");

        // Read again with the question, before the system prompt it joins.
        Assert.True(turn.IndexOf("RereadHeldMode();", StringComparison.Ordinal) is var reread and > 0
                    && reread < turn.IndexOf("RefreshSystemPrompt(", reread, StringComparison.Ordinal));
        // Its tools, its model, its effort; never the agent loop.
        Assert.Contains("&& turnMode is null", turn);
        Assert.Contains("ChatModes.PickModelAsync(_client, turnMode, effectiveModel, localCts!.Token)", turn);
        Assert.Contains("effectiveTools = ChatModes.ToolsFor(_tools, turnMode, _indexService.RootDir);", turn);
        Assert.Contains("ChatTurnPolicy.TurnEffort(_config, codeAction: oneTimeModel is not null, turnMode)", turn);
        // A code action: without its instructions either.
        Assert.Contains("RefreshSystemPrompt(withMode: oneTimeModel is null);", turn);
        // Its instructions, and what its tools weigh.
        var rag = Code("InferpalToolWindowData.Rag.cs");
        Assert.Contains("_heldMode);", rag);
        Assert.Contains("NextTurnToolTokens(_tools, _toolsEnabled, _planMode, _heldMode)", rag);
        // /task and /tdd keep their own tools: the mode's instructions never steer them, as in the host.
        var slash = Code("InferpalToolWindowData.SlashCommands.cs");
        Assert.Equal(2, slash.Split("SystemPromptWithoutMode()").Length - 1);
        Assert.DoesNotContain("_history[0].Content", slash);
        // One selector.
        Assert.Contains("var leftMode = LeaveHeldMode();", Code("InferpalToolWindowData.Chrome.cs"));
        Assert.Contains("if (IsPlanMode) LeaveHeldMode();", Code("InferpalToolWindowData.Connection.cs"));
    }

    /// <summary>Modes an import wrote show at once in the three clients: one effect from the host, read by VS Code's
    /// extension and the ACP agent; the Visual Studio window reads the command's own flag.</summary>
    [Fact]
    public void ModesAnImportWrote_AreReadAgainByEveryClient()
    {
        var repo = ConversationPersistenceSilenceTests.RepoRoot();
        string Cs(params string[] path) => ConventionCoverageTests.CodeOnly(Path.Combine([repo, .. path]));

        Assert.Contains("if (result.ModesWritten) saved.Add(new SlashEffectDto(\"modesChanged\"));", Cs("Inferpal.Host", "HostSlashCommands.cs"));
        Assert.Contains("case \"modesChanged\":", Cs("Inferpal.Host", "Acp", "AcpSession.cs"));
        Assert.Contains("if (result.ModesWritten) _ = RefreshModesQuietlyAsync();", Cs("Inferpal", "ToolWindow", "InferpalToolWindowData.SlashCommands.cs"));
        var provider = SettingsSchemaDriftTests.NeutralizeTypeScriptComments(
            File.ReadAllText(Path.Combine(repo, "vscode", "src", "chatViewProvider.ts")));
        Assert.Matches(@"case 'modesChanged':\s*void this\.refreshModes\(\);", provider);
    }

    [Fact]
    public void TheVsCodeExtension_LeavesTheModeForChatAgentAndPlan_AndHoldsItAgainAfterARestart()
    {
        var src = Path.Combine(ConversationPersistenceSilenceTests.RepoRoot(), "vscode", "src");
        var provider = SettingsSchemaDriftTests.NeutralizeTypeScriptComments(File.ReadAllText(Path.Combine(src, "chatViewProvider.ts")));
        var sessions = SettingsSchemaDriftTests.NeutralizeTypeScriptComments(File.ReadAllText(Path.Combine(src, "chatSessions.ts")));

        // Held again after a restart — or said, as Plan is.
        Assert.Contains("const set = await host.modeSet(this.heldMode);", provider);
        Assert.Contains("void vscode.window.showWarningMessage(refused ?? '');", provider);
        // Chat, Agent and Plan leave it; refused, nothing else changes.
        Assert.Contains("await host.modeSet(null);", provider);
        Assert.Contains("this.gestureFailed('mode/set', err);\n        void this.refreshModes();\n        return;",
                        provider.Replace("\r\n", "\n"));
        Assert.Contains("transcript.filter((item) => !item.modeLine)", sessions);
        // A mode's line alone is no conversation: neither archived nor saved under a name.
        Assert.Contains("this.transcript.every((m) => m.modeLine)", WebviewRebuildTests.Body(provider, "async saveSessionCommand("));
        Assert.Contains("this.transcript.every((m) => m.modeLine)", WebviewRebuildTests.Body(provider, "private archiveConversation("));
        // The line is never saved, so "Go back here" does not count it among the saved messages either.
        Assert.Contains("return this.hiddenEntries.length + (this.droppedEntries > 0 ? i - 1 : i) - lines;", provider);
    }
}
