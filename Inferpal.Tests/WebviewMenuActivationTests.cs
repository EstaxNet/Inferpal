using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// The chat's menus in VS Code (the model, "…", the attachments) pick an item on a click, Enter or Space. Picked on
/// mousedown alone, an item answered no screen reader (it activates by a click) and no keyboard (the item sat out of the
/// tab order): "Attach the active file" could not be reached without a mouse.
/// </summary>
public sealed class WebviewMenuActivationTests
{
    private static string MenuItem()
    {
        var code = WebviewRebuildTests.TsCode("webview/main.ts");
        var at = code.IndexOf("function menuItem(", StringComparison.Ordinal);
        Assert.True(at >= 0, "webview/main.ts no longer declares menuItem: this test reads nothing.");   // WITNESS
        var end = code.IndexOf("\nfunction ", at + 1, StringComparison.Ordinal);
        return code[at..end];
    }

    [Fact]
    public void AChatMenuItem_IsPickedByAClickOrTheKeyboard_NeverOnMousedownAlone()
    {
        var item = MenuItem();
        Assert.Contains("item.tabIndex = 0;", item, StringComparison.Ordinal);
        Assert.Matches(new Regex(@"addEventListener\('click',[^}]*onPick\(\)", RegexOptions.Singleline), item);
        Assert.Matches(new Regex(@"addEventListener\('keydown',.*e\.key === 'Enter' \|\| e\.key === ' '.*onPick\(\)", RegexOptions.Singleline), item);
        Assert.DoesNotMatch(new Regex(@"addEventListener\('mousedown',[^;]*\{[^}]*onPick\(\)", RegexOptions.Singleline), item);
        // Picked from the keyboard, the next key goes to the composer, not to the page.
        Assert.Contains("promptEl.focus();", item, StringComparison.Ordinal);
    }

    /// <summary>
    /// The focus never drops to the page: the model menu drawn again while open (its list is read anew) gives the focus back
    /// to the item that had it; Escape inside a menu returns it to the button that opened it; the focused item is drawn.
    /// </summary>
    [Fact]
    public void AChatMenu_KeepsTheKeyboardFocus_WhenItIsDrawnAgainOrClosed()
    {
        var code = WebviewRebuildTests.TsCode("webview/main.ts");
        var render = code[code.IndexOf("function renderModelMenu(", StringComparison.Ordinal)..];
        render = render[..render.IndexOf("\nfunction ", 1, StringComparison.Ordinal)];
        Assert.Contains("indexOf(document.activeElement", render, StringComparison.Ordinal);
        Assert.Contains("?.focus();", render, StringComparison.Ordinal);
        Assert.Contains("[[modelPop, modelBtn], [moreMenu, moreBtn], [plusMenu, plusBtn]]", code, StringComparison.Ordinal);
        Assert.Contains("opener?.focus();", code, StringComparison.Ordinal);

        var css = File.ReadAllText(Path.Combine(WebviewRebuildTests.RepoRoot(), "vscode", "media", "chat.css"));
        Assert.Contains(".menu-item:focus-visible", css, StringComparison.Ordinal);
    }
}
