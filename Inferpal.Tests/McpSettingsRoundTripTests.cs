using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// The Visual Studio settings window rewrites the MCP server list from its rows on every add, edit,
/// delete, toggle and save. What a row does not carry — the oauth block — and what never became a row —
/// invalid JSON, a rejected entry — vanished from the file. The view model cannot be instantiated
/// outside VS: the guard reads its code, and both decisions live in <c>McpServerConfig</c>, tested there.
/// </summary>
public class McpSettingsRoundTripTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "README.md")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string ViewModel() => ConventionCoverageTests.CodeOnly(
        Path.Combine(RepoRoot(), "Inferpal", "ToolWindow", "InferpalSettingsData.cs"));

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

    [Fact]
    public void EveryServerTheEditorWrites_IsBuiltByFromEditor()
    {
        var code = ViewModel();

        // Witness: the editor still rewrites the list from its rows.
        Assert.Contains("McpServerConfig.Serialize(", Body(code, "void SyncJsonFromRows()"), StringComparison.Ordinal);

        Assert.Empty(Regex.Matches(code, @"new\s+(Services\.Mcp\.)?McpServerConfig\s*\("));
        Assert.True(Regex.Matches(code, @"McpServerConfig\.FromEditor\s*\(").Count >= 2,
            "ToConfig and CommitServer must both build servers through McpServerConfig.FromEditor.");
    }

    [Theory]
    [InlineData("void SyncJsonFromRows()")]
    [InlineData("void PersistMcpServers()")]
    [InlineData("void CommitServer()")]
    [InlineData("void DeleteServer(")]
    public void EveryRewriteOfTheList_ChecksThatTheJsonReadsWithoutLoss(string method)
    {
        var code = ViewModel();

        Assert.Contains("McpServerConfig.ParseForEditor(", code, StringComparison.Ordinal);
        Assert.Contains("_mcpListRebuildable", Body(code, method), StringComparison.Ordinal);
    }
}
