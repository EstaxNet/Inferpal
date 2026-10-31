using System.IO;
using Inferpal.Localization;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A result bar whose run is undone says so in both editors ("Run undone · 1 change kept") in place of its files, its
/// check and Undo — after <c>/undo-run</c>, typed or from the bar, and after each gesture of the run's review — and shows
/// its files again once the run stands. The line itself is <c>RunPieceGesturesTests.UndoRun_*</c>.
/// </summary>
[Collection(CultureSerialCollection.Name)]
public class RunUndoneBarTests
{
    private static string Repo(params string[] parts) => Path.Combine([ConversationPersistenceSilenceTests.RepoRoot(), .. parts]);

    [Fact]
    public void TheLines_AreTheMockupsWords()
    {
        try
        {
            Strings.ApplyLanguage("en");
            Assert.Equal("Run undone · 1 change kept", Strings.RunUndoneBarKept(1));
            Assert.Equal("undone, but 2 kept changes", Strings.RunFileUndoneButKeptLabel(2));
        }
        finally { Strings.ApplyLanguage(null); }
    }

    [Fact]
    public void VisualStudio_RefreshesItsBars_AfterAnUndoAndAfterAReviewGesture()
    {
        var history = ConventionCoverageTests.CodeOnly(Repo("Inferpal", "ToolWindow", "InferpalToolWindowData.PromptHistory.cs"));
        Assert.Contains("await RunOnVMContextAsync(RefreshUndoneBars);", history, StringComparison.Ordinal);
        var chrome = ConventionCoverageTests.CodeOnly(Repo("Inferpal", "ToolWindow", "InferpalToolWindowData.Chrome.cs"));
        Assert.Contains("if (action is not null) RefreshUndoneBars();", chrome, StringComparison.Ordinal);
        Assert.Contains("bar.ShowRunUndone(run is null ? null : Services.Presentation.RunReview.UndoneBar(_tools.History, run));",
                        chrome, StringComparison.Ordinal);
        var xaml = File.ReadAllText(Repo("Inferpal", "ToolWindow", "InferpalToolWindowContent.xaml"));
        Assert.Contains(@"Visibility=""{Binding ShowsRunDetail, Converter={StaticResource b2v}}""", xaml, StringComparison.Ordinal);
        Assert.Contains(@"Visibility=""{Binding IsRunUndone, Converter={StaticResource b2v}}""", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void VsCode_TheHostSaysWhichBarsMoved_AndTheWebviewTogglesThem()
    {
        var slash = ConventionCoverageTests.CodeOnly(Repo("Inferpal.Host", "HostSlashCommands.cs"));
        Assert.Contains("new SlashEffectDto(\"runUndone\", r.Line, r.Id)", slash, StringComparison.Ordinal);

        var provider = SettingsSchemaDriftTests.NeutralizeTypeScriptComments(File.ReadAllText(Repo("vscode", "src", "chatViewProvider.ts")));
        Assert.Contains("this.post({ type: 'runUndone', runId: e.name, line: e.value ?? null });", provider, StringComparison.Ordinal);
        // A rebuilt view draws each bar from the transcript, as its run ended: the undone ones are said again.
        var hydrate = ConversationPersistenceSilenceTests.TsMethodBody(Repo("vscode", "src", "chatViewProvider.ts"), "private hydrate(");
        Assert.Contains("this.post({ type: 'runUndone', runId, line });", hydrate, StringComparison.Ordinal);
        Assert.Contains("this.undoneRuns.set(msg.runId, result.review.undone ?? null);", provider, StringComparison.Ordinal);

        var main = Repo("vscode", "src", "webview", "main.ts");
        var review = ConversationPersistenceSilenceTests.TsMethodBody(main, "function renderRunReview(");
        Assert.Contains("showRunUndone(runId, review.undone ?? null);", review, StringComparison.Ordinal);
        var src = SettingsSchemaDriftTests.NeutralizeTypeScriptComments(File.ReadAllText(main));
        Assert.Contains("showRunUndone(msg.runId, msg.line);", src, StringComparison.Ordinal);
    }
}
