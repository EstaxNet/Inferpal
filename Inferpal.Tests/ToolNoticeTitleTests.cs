using System.IO;
using Inferpal.Localization;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// The two notices the chat writes itself — the context window's compaction, the session recap — are drawn as tool calls,
/// and folded, their title is all that shows: a translated title, never the internal name. A tool keeps its documented
/// name, and the NAME stays what a conversation saves.
/// </summary>
[Collection(CultureSerialCollection.Name)]   // compares localized sentences
public sealed class ToolNoticeTitleTests
{
    [Fact]
    public void TheChatsOwnNotices_HaveATranslatedTitle_AndATool_ItsName()
    {
        Assert.Equal(Strings.ToolTitleContextCompact, ToolNoticeTitle.For(ToolNoticeTitle.ContextCompact));
        Assert.Equal(Strings.ToolTitleOodaRecap, ToolNoticeTitle.For(ToolNoticeTitle.OodaRecap));
        Assert.NotEqual(ToolNoticeTitle.ContextCompact, Strings.ToolTitleContextCompact);
        Assert.NotEqual(ToolNoticeTitle.OodaRecap, Strings.ToolTitleOodaRecap);
        Assert.Equal("read_file", ToolNoticeTitle.For("read_file"));   // reference arm
    }

    [Fact]
    public void TheExportedLabel_ShowsTheTitle()
    {
        Assert.Equal($"🔧 {Strings.ToolTitleOodaRecap}", ConversationExporter.RoleLabel("tool", ToolNoticeTitle.OodaRecap));
        Assert.Equal("🔧 run_tests", ConversationExporter.RoleLabel("tool", "run_tests"));
    }

    /// <summary>No call of the model: the export's count of tool calls leaves them out.</summary>
    [Fact]
    public void TheChatsOwnNotices_AreNoToolCalls()
    {
        Assert.True(ToolNoticeTitle.IsChatNotice(ToolNoticeTitle.ContextCompact));
        Assert.True(ToolNoticeTitle.IsChatNotice(ToolNoticeTitle.OodaRecap));
        Assert.False(ToolNoticeTitle.IsChatNotice("read_file"));   // reference arm
        Assert.False(ToolNoticeTitle.IsChatNotice(null));

        var document = ConversationExporter.Build(
            [new ExportMessage("user", "you", "the question", "t"),
             new ExportMessage("tool", "c", "compacted", "t", ToolName: ToolNoticeTitle.ContextCompact),
             new ExportMessage("tool", "r", "a file", "t", ToolName: "read_file"),
             new ExportMessage("assistant", "a", "the answer", "t"),
             new ExportMessage("tool", "o", "recap", "t", ToolName: ToolNoticeTitle.OodaRecap)],
            asPlainText: false, "m", 0, "d", "-");

        Assert.Contains($"| {Strings.ExportToolCalls} | 1 |", document, StringComparison.Ordinal);
    }

    /// <summary>⚠ In VS Code's run, a notice would count as a step, offer "Fix with AI" on a truncation, and — degraded —
    /// make the verdict of a run without a check: a red cross on a run that only read files. It stands outside the run,
    /// and the run's verdict reads the run's list only.</summary>
    [Fact]
    public void VsCodesView_KeepsTheChatsNotices_OutOfTheRun()
    {
        var main = WebviewRebuildTests.TsCode("webview/main.ts");
        var notice = WebviewRebuildTests.Body(main, "function isChatNotice(");
        Assert.Contains($"name === '{ToolNoticeTitle.ContextCompact}' || name === '{ToolNoticeTitle.OodaRecap}'", notice, StringComparison.Ordinal);

        var bubble = WebviewRebuildTests.Body(main, "function addToolBubble(");
        Assert.Contains("const list = notice ? null : ensureRun(target);", bubble, StringComparison.Ordinal);
        Assert.Contains("if (!notice) {\n    target.steps++;", bubble.ReplaceLineEndings("\n"), StringComparison.Ordinal);
        Assert.Contains("if (item.hasErrors && !notice) {", bubble, StringComparison.Ordinal);   // no "Fix with AI"
        Assert.Contains("if (item.hasErrors && list) {", bubble, StringComparison.Ordinal);     // the run stays as it is
        // Placed by its NAME: the transcript keeps the recap before the answer, so a redrawn turn put it on top.
        Assert.Contains($"target.el.insertBefore(step, item.text === '{ToolNoticeTitle.OodaRecap}' ? null : (target.run ?? target.body));",
                        bubble, StringComparison.Ordinal);

        var summary = WebviewRebuildTests.Body(main, "function renderRunSummary(");
        Assert.Contains("const stepFailed = target.runList?.querySelector('.step.failed') != null;", summary, StringComparison.Ordinal);
        Assert.Contains($"target.el.insertBefore(bar, target.el.querySelector(':scope > [data-notice=\"{ToolNoticeTitle.OodaRecap}\"]'));",
                        summary, StringComparison.Ordinal);
    }

    /// <summary>⚠ Both editors write the same session files. A compaction that lost turns is a notice in Visual Studio's;
    /// saved by VS Code as a tool bubble, it would come back — in either editor — folded under its title with a check, the
    /// loss out of sight.</summary>
    [Fact]
    public void VsCode_SavesADegradedCompaction_AsVisualStudioDoes()
    {
        var saved = WebviewRebuildTests.Body(WebviewRebuildTests.TsCode("chatSessions.ts"), "export function toSavedMessages(");
        Assert.Contains($"item.role === 'tool' && item.text === '{ToolNoticeTitle.ContextCompact}' && item.hasErrors", saved, StringComparison.Ordinal);
        Assert.Contains("? { role: 'assistant', content: item.toolOutput ?? '', toolName: NOTICE_MARKER", saved, StringComparison.Ordinal);
        Assert.Equal("notice", SessionManager.NoticeMarker);   // the marker the TypeScript mirror spells
    }

    /// <summary>VS Code's view computes the title from the saved name with the same two names: renamed in the Core and not
    /// there, the bubble would show the internal name again with nothing turning red.</summary>
    [Fact]
    public void VsCodesView_TitlesTheSameTwoNames()
    {
        var main = SettingsSchemaDriftTests.NeutralizeTypeScriptComments(
            File.ReadAllText(Path.Combine(ConventionCoverageTests.RepoRoot(), "vscode", "src", "webview", "main.ts")));

        Assert.Contains($"name === '{ToolNoticeTitle.ContextCompact}' ? t('toolTitleContextCompact')", main, StringComparison.Ordinal);
        Assert.Contains($"name === '{ToolNoticeTitle.OodaRecap}' ? t('toolTitleOodaRecap')", main, StringComparison.Ordinal);
        Assert.Contains("name.textContent = toolTitle(item.text);", main, StringComparison.Ordinal);
    }
}
