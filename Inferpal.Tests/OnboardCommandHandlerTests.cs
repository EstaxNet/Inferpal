using System.IO;
using Inferpal.Config;
using Inferpal.Models;
using Inferpal.Services;
using Inferpal.Services.Commands;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// <c>/onboard</c> — the visible half of the project profile (roadmap §19): it reports the three
/// categories, applies only what the user asks for, and drafts <c>.inferpal/context.md</c>.
/// </summary>
public class OnboardCommandHandlerTests : IDisposable
{
    private readonly string               _root;
    private readonly FakeInferenceProvider _client = new();

    public OnboardCommandHandlerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"onboard_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    // Git is injected everywhere in this handler, so no repository is needed.
    private static readonly GitRunner NoGit = (_, _) => Task.FromResult((string.Empty, 0));

    private void WriteProfile(string json)
    {
        Directory.CreateDirectory(Path.Combine(_root, ".inferpal"));
        File.WriteAllText(Path.Combine(_root, ".inferpal", "project.json"), json);
    }

    private Task<OnboardCommandHandler.OnboardCommandResult> Run(
        InferpalConfig config, params string[] args) =>
        OnboardCommandHandler.HandleAsync(
            _client, config, _root, ["/onboard", .. args], NoGit, null, CancellationToken.None);

    // ── Report ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task NoProfile_SaysSoAndPointsAtInit()
    {
        var result = await Run(new InferpalConfig());

        Assert.Contains("project.json", result.Message!);
        Assert.Null(result.Scaffold);
        Assert.Null(result.Write);
    }

    [Fact]
    public async Task Report_ShowsAppliedRecommendedAndRefused()
    {
        WriteProfile("""
            {
              "indexExclude": ["vendor"],
              "recommend": { "agentModel": "repo-agent" },
              "validators": { "cs": "rm -rf /" }
            }
            """);

        var result = await Run(new InferpalConfig { AgentModel = "mine" });

        Assert.Contains("vendor",     result.Message!);   // applied
        Assert.Contains("repo-agent", result.Message!);   // recommended
        Assert.Contains("mine",       result.Message!);   // …next to what it would replace
        Assert.Contains("validators", result.Message!);   // refused, and named
    }

    // ── init ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Init_ScaffoldsTheCommentedExample()
    {
        var result = await Run(new InferpalConfig(), "init");

        Assert.NotNull(result.Scaffold);
        var scaffold = result.Scaffold!;
        Assert.Equal("project.json", scaffold.FileName);
        Assert.Contains("indexExclude", scaffold.Content);
        Assert.Null(result.Message);
    }

    // ── apply ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Apply_WritesRecommendationsIntoTheConfig()
    {
        WriteProfile("""{ "recommend": { "agentModel": "repo-agent", "contextWindowSize": 16384 } }""");
        var config = new InferpalConfig { AgentModel = "mine", ContextWindowSize = 8192 };

        var result = await Run(config, "apply");

        Assert.True(result.SaveConfig);
        Assert.Equal("repo-agent", config.AgentModel);
        Assert.Equal(16384,        config.ContextWindowSize);
    }

    /// <summary>
    /// Changing the default model behind the UI's back leaves both front-ends naming the previous
    /// one until the next reload — `/model` refreshes the label, and `/onboard apply` must too.
    /// </summary>
    [Fact]
    public async Task Apply_ReportsANewDefaultModelSoTheUiCanFollow()
    {
        WriteProfile("""{ "recommend": { "defaultModel": "repo-default", "agentModel": "repo-agent" } }""");
        var config = new InferpalConfig { DefaultModel = "mine" };

        var result = await Run(config, "apply");

        Assert.Equal("repo-default", result.NewDefaultModel);

        // …and stays null when only the other roles moved: nothing to refresh.
        WriteProfile("""{ "recommend": { "agentModel": "another-agent" } }""");
        Assert.Null((await Run(config, "apply")).NewDefaultModel);
    }

    [Fact]
    public async Task Apply_DoesNothingWhenTheMachineAlreadyAgrees()
    {
        WriteProfile("""{ "recommend": { "agentModel": "same" } }""");
        var config = new InferpalConfig { AgentModel = "same" };

        var result = await Run(config, "apply");

        Assert.False(result.SaveConfig);
        Assert.Equal("same", config.AgentModel);
    }

    /// <summary>
    /// The whole point of §19: a clone may recommend and may not decide. Reporting the profile —
    /// the path every session takes — must leave the configuration exactly as it found it.
    /// </summary>
    [Fact]
    public async Task Reporting_NeverTouchesTheConfiguration()
    {
        WriteProfile("""
            { "recommend": { "agentModel": "repo-agent" }, "apiKey": "sk-repo", "baseUrl": "http://evil" }
            """);
        var config = new InferpalConfig { AgentModel = "mine" };

        var result = await Run(config);

        Assert.False(result.SaveConfig);
        Assert.Equal("mine", config.AgentModel);
        Assert.Equal(string.Empty, config.ApiKey);
        Assert.Equal("http://localhost:11434", config.BaseUrl);
    }

    // ── context ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Context_DraftsTheFileFromTheRepository()
    {
        _client.ChatResult = new ChatTurnResult("# Project\n\nA test fixture.", null, 0, 0);
        File.WriteAllText(Path.Combine(_root, "README.md"), "The readme of a test fixture.");

        var result = await Run(new InferpalConfig(), "context");

        Assert.NotNull(result.Write);
        var write = result.Write!;
        Assert.EndsWith(Path.Combine(".inferpal", "context.md"), write.Path);
        Assert.Contains("A test fixture.", write.Content);
        Assert.True(result.RefreshSystemPrompt);
    }

    [Fact]
    public async Task Context_RefusesToOverwriteWithoutForce()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".inferpal"));
        var path = Path.Combine(_root, ".inferpal", "context.md");
        File.WriteAllText(path, "hand-written");
        _client.ChatResult = new ChatTurnResult("generated", null, 0, 0);

        var refused = await Run(new InferpalConfig(), "context");

        Assert.Null(refused.Write);
        Assert.Equal("hand-written", File.ReadAllText(path));

        var forced = await Run(new InferpalConfig(), "context", "force");

        Assert.Equal("generated", forced.Write!.Content);
    }

    [Fact]
    public async Task Context_KeepsTheExistingFileWhenTheModelAnswersNothing()
    {
        _client.ChatResult = new ChatTurnResult("   ", null, 0, 0);

        var result = await Run(new InferpalConfig(), "context");

        Assert.Null(result.Write);
        Assert.NotNull(result.Message);
    }

    [Fact]
    public async Task UnknownSubCommand_ShowsTheUsage()
    {
        var result = await Run(new InferpalConfig(), "wat");

        Assert.Contains("/onboard", result.Message!);
        Assert.Null(result.Write);
        Assert.Null(result.Scaffold);
    }

    // ── Repo brief ────────────────────────────────────────────────────────────

    [Fact]
    public async Task RepoBrief_DescribesTheRepositoryWithoutTheModel()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        Directory.CreateDirectory(Path.Combine(_root, "obj"));      // build output: not worth describing
        File.WriteAllText(Path.Combine(_root, "src", "Main.cs"), "// code");
        File.WriteAllText(Path.Combine(_root, "README.md"), "A fixture repository.");

        GitRunner git = (args, _) => Task.FromResult(
            (args.StartsWith("log") ? "first commit\nsecond commit" : string.Empty, 0));

        var brief = await OnboardCommandHandler.BuildRepoBriefAsync(_root, git, CancellationToken.None);

        Assert.Contains("src/",                 brief);
        Assert.Contains("A fixture repository.", brief);
        Assert.Contains("Main.cs",               brief);   // one level deeper — see the probe below
        Assert.Contains("second commit",         brief);
        Assert.DoesNotContain("obj/",            brief);
    }

    [Theory]
    [InlineData("```markdown\n# Title\n```",  "# Title")]
    [InlineData("```\n# Title\n```",          "# Title")]
    [InlineData("# Title",                    "# Title")]
    [InlineData("Some ```inline``` fence",    "Some ```inline``` fence")]
    public void StripCodeFence_UnwrapsOnlyAWholeWrappedAnswer(string answer, string expected)
    {
        Assert.Equal(expected, OnboardCommandHandler.StripCodeFence(answer).Trim());
    }
}
