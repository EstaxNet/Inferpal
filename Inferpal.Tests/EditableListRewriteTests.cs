using System.IO;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// The two text lists of the Visual Studio settings window (custom tools, command templates) are
/// rewritten from their structured rows: every read and every rewrite goes through
/// <c>EditableListText</c>, which keeps the lines the editor does not understand. The view model cannot
/// be instantiated outside VS: the guard reads its code, the behaviour is tested in
/// <c>EditableListTextTests</c>.
/// </summary>
public class EditableListRewriteTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "README.md")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    /// <summary>Body of the method whose declaration contains <paramref name="signature"/>.</summary>
    private static string Body(string code, string signature)
    {
        var start = code.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Method not found: {signature}");
        var open = code.IndexOf('{', start);
        var depth = 0;
        for (var i = open; i < code.Length; i++)
        {
            if (code[i] == '{') depth++;
            else if (code[i] == '}' && --depth == 0) return code[open..(i + 1)];
        }
        return string.Empty;
    }

    [Theory]
    [InlineData("void BuildToolRowsFrom(",       "EditableListText.Parse(")]
    [InlineData("void BuildSlashRowsFrom(",      "EditableListText.Parse(")]
    [InlineData("void SyncToolTextFromRows()",   "EditableListText.Render(")]
    [InlineData("void SyncSlashTextFromRows()",  "EditableListText.Render(")]
    [InlineData("void CommitTool()",             "EditableListText.Render(")]
    [InlineData("void CommitSlash()",            "EditableListText.Render(")]
    public void BothTextLists_KeepTheLinesTheEditorCannotRead(string method, string call)
    {
        var code = ConventionCoverageTests.CodeOnly(
            Path.Combine(RepoRoot(), "Inferpal", "ToolWindow", "InferpalSettingsData.cs"));

        Assert.Contains(call, Body(code, method), StringComparison.Ordinal);
    }
}
