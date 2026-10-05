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

    /// <summary>
    /// A prompt file named like a built-in command is listed as one that never runs: the router answers the
    /// built-in first, and the listing presented <c>undo-run.md</c> as a usable <c>/undo-run</c>.
    /// </summary>
    [Fact]
    public void Prompts_AFileNamedLikeABuiltIn_IsFlaggedAsNeverRunning()
    {
        WriteFile("prompts", "undo-run.md", "Summarize the last run.\n");
        WriteFile("prompts", "standup.md", "Summarize {args}\n");

        var result = RulesChecksPromptsCommandHandler.Prompts(_root, List());

        Assert.Contains("`/undo-run`" + Strings.PromptsShadowedByBuiltIn("/undo-run"), result.Message);
        Assert.DoesNotContain(Strings.PromptsShadowedByBuiltIn("/standup"), result.Message);   // witness: a free name
    }

    /// <summary>
    /// A prompt file named like a template of the settings is listed as one that never runs: the settings' template
    /// wins, and the listing showed the file's description over a command that runs another text.
    /// </summary>
    [Fact]
    public void Prompts_AFileNamedLikeASettingsTemplate_IsFlaggedAsNeverRunning()
    {
        WriteFile("prompts", "review-security.md", "---\ndescription: From the file\n---\nReview {args}\n");
        WriteFile("prompts", "standup.md", "Summarize {args}\n");

        var result = RulesChecksPromptsCommandHandler.Prompts(_root, List(), "/review-security=Check {args}");

        Assert.Contains("`/review-security`" + Strings.PromptsShadowedByConfig("/review-security"), result.Message);
        Assert.DoesNotContain(Strings.PromptsShadowedByConfig("/standup"), result.Message);   // reference arm: a free name
    }

    /// <summary>The settings' templates are optional to the handler: both front-ends hand them over, or the rule is mute.</summary>
    [Theory]
    [InlineData("Inferpal", "ToolWindow", "InferpalToolWindowData.PromptHistory.cs")]
    [InlineData("Inferpal.Host", "HostSlashCommands.cs")]
    public void BothFrontEnds_HandTheSettingsTemplatesToTheListing(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "README.md"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var code = ConventionCoverageTests.CodeOnly(Path.Combine(dir!.FullName, Path.Combine(parts)));

        Assert.Matches(@"RulesChecksPromptsCommandHandler\.Prompts\([^;]*PromptTemplates\)", code);
    }
}
