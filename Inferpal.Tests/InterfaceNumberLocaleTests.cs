using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// VS Code writes the chat's numbers and times in the interface's language — Inferpal's choice when its words follow it,
/// else the editor's — never the browser's own locale nor the editor's alone: French words over "32,664". The page carries
/// the language (<c>&lt;html lang&gt;</c>, <c>PAGE_LOCALE</c>), the extension reads it from <c>uiLocale()</c>.
/// </summary>
public class InterfaceNumberLocaleTests
{
    private static string Source()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "README.md"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "vscode", "src");
    }

    /// <summary>Every call that writes a number, a date or a time for a locale, and the locale it is given.</summary>
    private static readonly Regex LocaleCall = new(
        @"(\.toLocale(?:String|DateString|TimeString)|\bIntl\.(?:NumberFormat|DateTimeFormat|RelativeTimeFormat|ListFormat))\(\s*([^,)]*)",
        RegexOptions.CultureInvariant);

    /// <summary>The property, not a list of call shapes: whatever the call, its locale is the interface's — or English,
    /// spelled <c>'en-US'</c>, for a text written for the model (a truncation marker), English like every text it reads.</summary>
    [Fact]
    public void EveryNumberAndTime_IsWrittenInTheInterfacesLanguage()
    {
        var files = Directory.EnumerateFiles(Source(), "*.ts", SearchOption.AllDirectories).ToList();
        Assert.True(files.Count >= 20, $"witness: {files.Count} TypeScript files read");
        var calls = 0;
        var wrong = new List<string>();
        foreach (var file in files)
        {
            var code = SettingsSchemaDriftTests.NeutralizeTypeScriptComments(File.ReadAllText(file));
            foreach (Match m in LocaleCall.Matches(code))
            {
                calls++;
                var locale = m.Groups[2].Value.Trim();
                if (!locale.StartsWith("PAGE_LOCALE", StringComparison.Ordinal) && !locale.StartsWith("uiLocale(", StringComparison.Ordinal)
                    && locale != "'en-US'")
                    wrong.Add($"{Path.GetFileName(file)}: {m.Groups[1].Value}({locale}) — not the interface's language (PAGE_LOCALE, uiLocale())");
            }
        }
        Assert.True(calls >= 6, $"witness: {calls} locale calls found");
        Assert.Empty(wrong);
    }

    [Fact]
    public void ThePage_CarriesTheInterfacesLanguage()
    {
        var html = File.ReadAllText(Path.Combine(Source(), "webview", "chatWebviewHtml.ts"));
        Assert.Contains("<html lang=\"${lang}\">", html);
        Assert.Contains("uiLocale()", html);
    }
}
