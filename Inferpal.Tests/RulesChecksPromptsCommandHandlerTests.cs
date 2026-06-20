using System.IO;
using Inferpal.Localization;
using Inferpal.Services.Commands;
using Xunit;

namespace Inferpal.Tests;

public class RulesChecksPromptsCommandHandlerTests : IDisposable
{
    private readonly string _root;

    public RulesChecksPromptsCommandHandlerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"rcp_cmd_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private void WriteFile(string kind, string fileName, string content)
    {
        var dir = Path.Combine(_root, ".inferpal", kind);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, fileName), content);
    }

    private static string[] Init() => ["/x", "init"];
    private static string[] List() => ["/x"];

    // ── /rules ────────────────────────────────────────────────────────────────--

    [Fact]
    public void Rules_Init_ReturnsScaffoldRequest()
    {
        var result = RulesChecksPromptsCommandHandler.Rules(_root, Init());

        Assert.Null(result.Message);
        Assert.NotNull(result.Scaffold);
        Assert.Equal("example.md", result.Scaffold!.FileName);
        Assert.EndsWith(Path.Combine(".inferpal", "rules"), result.Scaffold.Dir);
        Assert.Equal(RulesChecksPromptsCommandHandler.RulesExampleContent, result.Scaffold.Content);
    }

    [Fact]
    public void Rules_EmptyDir_ReturnsNone()
    {
        var result = RulesChecksPromptsCommandHandler.Rules(_root, List());

        Assert.Equal(Strings.RulesNone, result.Message);
        Assert.Null(result.Scaffold);
    }

    [Fact]
    public void Rules_WithFile_ListsRuleNameAndScope()
    {
        WriteFile("rules", "naming.md", "---\ndescription: Naming rule\nglobs: **/*.cs\n---\nbody\n");

        var result = RulesChecksPromptsCommandHandler.Rules(_root, List());

        Assert.Null(result.Scaffold);
        Assert.Contains("Naming rule", result.Message);
        Assert.Contains("**/*.cs", result.Message);
    }

    // ── /checks ───────────────────────────────────────────────────────────────--

    [Fact]
    public void Checks_Init_ReturnsScaffoldRequest()
    {
        var result = RulesChecksPromptsCommandHandler.Checks(_root, Init());

        Assert.Equal("no-secrets.md", result.Scaffold!.FileName);
        Assert.Equal(RulesChecksPromptsCommandHandler.ChecksExampleContent, result.Scaffold.Content);
    }

    [Fact]
    public void Checks_WithFile_ListsCheckName()
    {
        WriteFile("checks", "secrets.md", "---\ndescription: No secrets\n---\nFlag secrets.\n");

        var result = RulesChecksPromptsCommandHandler.Checks(_root, List());

        Assert.Null(result.Scaffold);
        Assert.Contains("No secrets", result.Message);
    }

    [Fact]
    public void Checks_EmptyDir_ReturnsNone()
    {
        var result = RulesChecksPromptsCommandHandler.Checks(_root, List());

        Assert.Equal(Strings.ChecksNone, result.Message);
    }

    // ── /prompts ──────────────────────────────────────────────────────────────--

    [Fact]
    public void Prompts_Init_ReturnsScaffoldRequest()
    {
        var result = RulesChecksPromptsCommandHandler.Prompts(_root, Init());

        Assert.Equal("review-security.md", result.Scaffold!.FileName);
        Assert.Equal(RulesChecksPromptsCommandHandler.PromptsExampleContent, result.Scaffold.Content);
    }

    [Fact]
    public void Prompts_WithFile_ListsCommandName()
    {
        WriteFile("prompts", "review.md", "---\ndescription: Sec review\n---\nReview {args}\n");

        var result = RulesChecksPromptsCommandHandler.Prompts(_root, List());

        Assert.Null(result.Scaffold);
        Assert.Contains("review", result.Message);
    }

    [Fact]
    public void Prompts_EmptyDir_ReturnsNone()
    {
        var result = RulesChecksPromptsCommandHandler.Prompts(_root, List());

        Assert.Equal(Strings.PromptsNone, result.Message);
    }
}
