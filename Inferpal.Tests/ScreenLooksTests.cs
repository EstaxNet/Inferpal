using System.IO;
using Inferpal.Services.Documents;
using Inferpal.Services.Execution;
using Inferpal.Services.Presentation;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// What the app showed before the run changed anything and after its last change: the agent's own first and last look at
/// the same target, side by side in the result bar — never a "before" made up afterwards (pari 143).
/// </summary>
public class ScreenLooksTests
{
    private static ChatImage Image(int width) =>
        ChatImages.Read(ChatImagesTests.Png(width, width / 2), $"page-{width}.png").Image!;

    private static ScreenLook Look(string target, int writesBefore, int minute, int width = 64) =>
        new(target, Image(width), writesBefore, new DateTime(2026, 11, 21, 15, minute, 0));

    [Fact]
    public void ALookBeforeTheFirstWrite_AndOneAfterTheLast_ArePaired()
    {
        var screen = ScreenLooks.Pair([(Look("localhost:5173/cart · 390 px", 0, 20), Look("localhost:5173/cart · 390 px", 2, 21))], writes: 2);

        Assert.NotNull(screen);
        Assert.StartsWith("data:image/png;base64,", screen!.Before);
        Assert.StartsWith("data:image/png;base64,", screen.After);
        Assert.Contains("localhost:5173/cart · 390 px", screen.Title);
        Assert.True(File.Exists(screen.BeforeFile) && File.Exists(screen.AfterFile));   // opened full size on a click
    }

    [Theory]
    [InlineData(0, 0, 0)]   // the run wrote nothing
    [InlineData(1, 2, 2)]   // the first look came after a write: no "before"
    [InlineData(0, 1, 2)]   // the last look came before the last write: no "after"
    public void WithoutALookOnBothSidesOfTheWrites_ThereIsNoStrip(int firstWrites, int lastWrites, int writes) =>
        Assert.Null(ScreenLooks.Pair([(Look("app", firstWrites, 20), Look("app", lastWrites, 21))], writes));

    [Fact]
    public void ASingleLook_IsNoPair()
    {
        var once = Look("app", 0, 20);
        Assert.Null(ScreenLooks.Pair([(once, once)], writes: 1));
    }

    [Fact]
    public void OfTwoPairedTargets_TheOneLookedAtLastIsShown()
    {
        var screen = ScreenLooks.Pair(
            [(Look("cart · 1280 px", 0, 10), Look("cart · 1280 px", 1, 12)), (Look("cart · 390 px", 0, 11), Look("cart · 390 px", 1, 13))],
            writes: 1);
        Assert.Contains("cart · 390 px", screen!.Title);
    }

    [Fact]
    public void TheRun_KeepsTheFirstAndTheLastLookOfEachTarget_WithTheWritesBeforeEach()
    {
        var history = new FileHistoryService();
        var id = history.BeginRun();
        history.RecordLook("app", Image(64));
        history.RecordLook("app", Image(66));                 // a second look before any write: the first one stays
        history.NoteCreated(Path.Combine(Path.GetTempPath(), "inferpal-looks-test.css"));   // the run writes
        history.RecordLook("app", Image(68));
        var run = history.Runs.Last(r => r.Id == id);

        var (first, last) = Assert.Single(run.Looks);
        Assert.Equal(64, first.Image.Width);
        Assert.Equal(0, first.WritesBefore);
        Assert.Equal(68, last.Image.Width);
        Assert.Equal(1, last.WritesBefore);
        Assert.NotNull(RunSummary.Build([new("view_page", "{}", "ok")], run)?.Screen);
    }

    [Fact]
    public void ALook_IsSentToTheModel_AndNotedForTheRun()
    {
        var inbox = new ToolImageInbox();
        string? noted = null;
        inbox.Looked = (target, _) => noted = target;
        inbox.Look("cart · 390 px", Image(64));
        Assert.Single(inbox.Take());
        Assert.Equal("cart · 390 px", noted);
    }

    [Fact]
    public void BothLookingTools_NoteTheirLooks_AndBothEditorsDrawThem()
    {
        // Read without comments (one reader per language): a call commented out is a call disabled.
        var root = ConversationPersistenceSilenceTests.RepoRoot();
        string Path_(params string[] parts) => Path.Combine([root, .. parts]);
        string Cs(params string[] parts) => ConventionCoverageTests.CodeOnly(Path_(parts));
        string Ts(params string[] parts) => SettingsSchemaDriftTests.NeutralizeTypeScriptComments(File.ReadAllText(Path_(parts)));
        Assert.Contains("_images.Look(", Cs("Inferpal.Core", "Services", "Tools", "ViewPageTool.cs"));
        Assert.Contains("images.Look(", Cs("Inferpal.Core", "Services", "Tools", "ViewWindowTool.cs"));
        Assert.Contains("_readImages.Looked = _fileHistory.RecordLook", Cs("Inferpal.Core", "Services", "Execution", "ToolRegistry.cs"));
        // VS Code: the strip, a bar drawn for it alone, a picture opened in the editor's image viewer (never as text),
        // and the strip hidden once the run is undone.
        var webview = Ts("vscode", "src", "webview", "main.ts");
        Assert.Contains("bar.appendChild(screenStrip(run.screen))", webview);
        Assert.Contains("run.check === 'none' && !run.screen", webview);
        Assert.Contains("post({ type: 'openImage', path: file })", webview);
        Assert.Contains("executeCommand('vscode.open', vscode.Uri.file(msg.path))", Ts("vscode", "src", "chatViewProvider.ts"));
        Assert.Contains(".result.undone .result-screen", File.ReadAllText(Path_("vscode", "media", "chat.css")));
        // Visual Studio: the strip under the result bar, each picture a button that opens it in the system's viewer,
        // hidden once undone; a bar for a strip alone.
        var xaml = System.Text.RegularExpressions.Regex.Replace(File.ReadAllText(Path_("Inferpal", "ToolWindow", "InferpalToolWindowContent.xaml")),
                                                                "<!--.*?-->", string.Empty, System.Text.RegularExpressions.RegexOptions.Singleline);
        var strip = xaml[xaml.IndexOf("ItemsSource=\"{Binding ScreenLooks}\"", StringComparison.Ordinal)..];
        Assert.Contains("Command=\"{Binding OpenCommand}\" IsEnabled=\"{Binding CanOpen}\"", strip);
        var above = xaml[..xaml.IndexOf("ItemsSource=\"{Binding ScreenLooks}\"", StringComparison.Ordinal)];
        Assert.Contains("<DataTrigger Binding=\"{Binding IsRunUndone}\" Value=\"True\">", above[above.LastIndexOf("<StackPanel.Style>", StringComparison.Ordinal)..]);
        var item = Cs("Inferpal", "ToolWindow", "ChatMessageItem.cs");
        Assert.Contains("screen.AfterFile, OpenPicture)", item);
        Assert.Contains("UseShellExecute = true", item);
        Assert.Contains("|| summary.Screen is not null", Cs("Inferpal", "ToolWindow", "InferpalToolWindowData.ChatTurn.cs"));
    }

    [Theory]
    [InlineData("http://localhost:5173/cart/", "localhost:5173/cart")]
    [InlineData("http://127.0.0.1:8080/my%20page.html", "127.0.0.1:8080/my page.html")]
    [InlineData("site/index.html", "site/index.html")]   // a workspace file, served on a fresh port at each call: its path
    public void APage_IsKeyedByWhatItIs_NeverTheRandomPortItIsServedOn(string shown, string key) =>
        Assert.Equal(key, Inferpal.Services.Tools.WorkspacePages.LookTarget(shown));

    [Fact]
    public void APairWhoseThumbnailCannotBeMade_GivesWayToTheNext()
    {
        var broken = new ScreenLook("window", new ChatImage("w.png", "bm90IGFuIGltYWdl", "image/png", 64, 32, 9), 0, new DateTime(2026, 11, 21, 15, 30, 0));
        var brokenLast = broken with { WritesBefore = 1, At = new DateTime(2026, 11, 21, 15, 31, 0) };
        var screen = ScreenLooks.Pair([(broken, brokenLast), (Look("cart · 390 px", 0, 20), Look("cart · 390 px", 1, 21))], writes: 1);
        Assert.Contains("cart · 390 px", screen!.Title);
    }

    [Fact]
    public void TheLooksOfAnEndedRun_AreFreed_WhenTheNextBegins()
    {
        var history = new FileHistoryService();
        var first = history.BeginRun();
        history.RecordLook("app", Image(64));
        history.EndRun();
        Assert.Single(history.Runs.Last(r => r.Id == first).Looks);   // the ending run still pairs them
        history.BeginRun();
        Assert.Empty(history.Runs.Last(r => r.Id == first).Looks);
    }
}
