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
