using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// The VS Code webview draws its icons with <c>webview/icons.ts</c> — inline stroke SVG in the current text color, the
/// drawings of the validated design mockups. Emoji and symbol characters stood in for icons (🔍 🔧 🛠 📌 ⏸, ■ ▸ ▾ ○ ◈):
/// they render in a font the theme does not control, at sizes that do not match the text, and a screen reader reads
/// them as words. Arrows stay allowed: in a hint they name keys ("Shift+↑↓").
/// </summary>
public class WebviewIconsTests
{
    private static string Webview()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "README.md"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "vscode", "src", "webview");
    }

    // Misc technical (⏸ ⌘), geometric shapes (■ ▸ ▾ ○ ◈ ◇), misc symbols and dingbats (⚠ ✕ ✓ ✗ ⚡ ❓), and every
    // emoji plane.
    private static readonly Regex IconLike = new(@"[⌀-⏿■-◿☀-➿]|\p{Cs}",
                                                 RegexOptions.CultureInvariant);

    /// <summary>⚠ A skipped plan step is no failure: drawn with the failed cross, a plan that skipped a step it did not
    /// need read as a plan that broke (Visual Studio draws its own glyph).</summary>
    [Fact]
    public void ASkippedPlanStep_HasItsOwnGlyph_NeverTheFailedOne()
    {
        var main = File.ReadAllText(Path.Combine(Webview(), "main.ts"));
        Assert.Contains(": s.includes('skip') ? 'skipped'", main, StringComparison.Ordinal);
        Assert.DoesNotContain("s.includes('skip') ? 'failed'", main, StringComparison.Ordinal);
        Assert.Contains("state === 'skipped' ? 'skipped'", main, StringComparison.Ordinal);
        Assert.Contains("  skipped: [", File.ReadAllText(Path.Combine(Webview(), "icons.ts")), StringComparison.Ordinal);
    }

    /// <summary>⚠ "Fix with AI" on a failed step writes above a draft typed meanwhile, never over it — as a question put
    /// back does.</summary>
    [Fact]
    public void FixWithAi_WritesAboveTheDraft_NeverOverIt()
    {
        var main  = File.ReadAllText(Path.Combine(Webview(), "main.ts"));
        var start = main.IndexOf("const fixText = t('fixPrompt')", StringComparison.Ordinal);
        Assert.True(start > 0, "Fix with AI's text was not found: the rule measures nothing");   // WITNESS
        Assert.Contains("setPrompt(promptEl.value.trim() ? `${fixText}\\n\\n${promptEl.value}` : fixText);",
                        main[start..(start + 400)], StringComparison.Ordinal);
    }

    /// <summary>Each render of the theme grid makes a new one: the observer of a grid that left the page lets go.</summary>
    [Fact]
    public void TheThemeGridsObserver_LetsGoOnceItsGridLeftThePage()
    {
        var widgets = File.ReadAllText(Path.Combine(Webview(), "settingsWidgets.ts"));
        Assert.Contains("themes.disconnect();", widgets, StringComparison.Ordinal);
        Assert.Contains("if (grid.isConnected)", widgets, StringComparison.Ordinal);
    }

    [Fact]
    public void TheWebviewDrawsItsIcons_NoEmojiOrSymbolCharacterStandsInForOne()
    {
        var files = Directory.GetFiles(Webview(), "*.ts");
        Assert.True(files.Length >= 6, $"only {files.Length} webview source(s) read");                     // WITNESS
        Assert.Contains(files, f => f.EndsWith("icons.ts", StringComparison.Ordinal));

        var found = new List<string>();
        foreach (var file in files)
        {
            var code = SettingsSchemaDriftTests.NeutralizeTypeScriptComments(File.ReadAllText(file)).Split('\n');
            for (var i = 0; i < code.Length; i++)
                if (IconLike.IsMatch(code[i]))
                    found.Add($"{Path.GetFileName(file)}:{i + 1}: {code[i].Trim()}");
        }

        Assert.True(found.Count == 0, "Use webview/icons.ts instead:\n" + string.Join("\n", found));
    }

    /// <summary>
    /// The Visual Studio window draws its icons with Segoe MDL2 Assets glyphs, the font its header already used. Arrows
    /// are icons there too (↻ retry, ↩ load, ↑ send): the XAML names no key.
    /// </summary>
    [Fact]
    public void TheVisualStudioWindowDrawsItsIcons_NoEmojiOrSymbolCharacterStandsInForOne()
    {
        var dir = Path.Combine(Path.GetDirectoryName(Webview())!, "..", "..", "Inferpal", "ToolWindow");
        var files = Directory.GetFiles(dir, "*.xaml");
        Assert.True(files.Length >= 3, $"only {files.Length} XAML file(s) read");                          // WITNESS

        var iconOrArrow = new Regex(IconLike + @"|[←-⇿]", RegexOptions.CultureInvariant);
        var found = new List<string>();
        foreach (var file in files)
        {
            // XAML comments explain the markup; they may quote what they replaced.
            var markup = Regex.Replace(File.ReadAllText(file), "<!--.*?-->",
                                       m => new string('\n', m.Value.Count(c => c == '\n')), RegexOptions.Singleline);
            var lines = markup.Split('\n');
            for (var i = 0; i < lines.Length; i++)
                if (iconOrArrow.IsMatch(lines[i]))
                    found.Add($"{Path.GetFileName(file)}:{i + 1}: {lines[i].Trim()}");
        }

        Assert.True(found.Count == 0, "Use a Segoe MDL2 Assets glyph instead:\n" + string.Join("\n", found));
    }
}
