using Inferpal.Config;
using Inferpal.Services.Governance;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// The committable project profile (roadmap §19). These tests are about one property above all:
/// a file that ships with a clone can restrict and suggest, and can grant nothing.
/// </summary>
public class ProjectProfileTests
{
    private static InferpalConfig Config() => new()
    {
        AgentModel        = "installed-agent",
        UtilityModel      = string.Empty,
        ContextWindowSize = 8192,
    };

    // ── Category 1 — applied, additive only ───────────────────────────────────

    [Fact]
    public void IndexExclude_IsRead()
    {
        var profile = ProjectProfile.Parse("""{ "indexExclude": ["vendor", "**/*.generated.cs"] }""");

        Assert.Equal(["vendor", "**/*.generated.cs"], profile.IndexExcludes);
        Assert.Empty(profile.Ignored);
    }

    [Fact]
    public void IndexExclude_IsCapped_InCountAndLength()
    {
        var many    = string.Join(",", Enumerable.Range(0, 150).Select(i => $"\"dir{i}\""));
        var profile = ProjectProfile.Parse($$"""{ "indexExclude": [{{many}}, "{{new string('x', 400)}}"] }""");

        Assert.Equal(100, profile.IndexExcludes.Count);
        Assert.DoesNotContain(profile.IndexExcludes, p => p.Length > 200);
    }

    // ── Category 2 — recommended, shown next to what it would replace ─────────

    [Fact]
    public void Recommendations_CarryTheCurrentValue()
    {
        var profile = ProjectProfile.Parse(
            """{ "recommend": { "agentModel": "repo-agent", "contextWindowSize": 16384 } }""", Config());

        var agent = Assert.Single(profile.Recommendations, r => r.Key == "agentModel");
        Assert.Equal("repo-agent",      agent.Proposed);
        Assert.Equal("installed-agent", agent.Current);

        var ctx = Assert.Single(profile.Recommendations, r => r.Key == "contextWindowSize");
        Assert.Equal("16384", ctx.Proposed);
        Assert.Equal("8192",  ctx.Current);
    }

    [Fact]
    public void Recommendations_AreNotAppliedByParsing()
    {
        var config = Config();
        ProjectProfile.Parse("""{ "recommend": { "agentModel": "repo-agent" } }""", config);

        Assert.Equal("installed-agent", config.AgentModel);
    }

    [Fact]
    public void Apply_WritesOnlyWhatDiffers()
    {
        var config  = Config();
        var profile = ProjectProfile.Parse(
            """{ "recommend": { "agentModel": "installed-agent", "utilityModel": "repo-utility" } }""", config);

        var changed = profile.Apply(config);

        Assert.Equal(["utilityModel"], changed);
        Assert.Equal("repo-utility", config.UtilityModel);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-4096")]
    [InlineData("999999999")]
    [InlineData("plenty")]
    public void Apply_RejectsAnAbsurdContextWindow(string proposed)
    {
        var config  = Config();
        var profile = ProjectProfile.Parse($$"""{ "recommend": { "contextWindowSize": "{{proposed}}" } }""", config);

        Assert.Empty(profile.Apply(config));
        Assert.Equal(8192, config.ContextWindowSize);
    }

    // ── Category 3 — never, and said out loud ─────────────────────────────────

    [Theory]
    [InlineData("validators")]
    [InlineData("permissions")]
    [InlineData("permissionRules")]
    [InlineData("customTools")]
    [InlineData("baseUrl")]
    [InlineData("apiKey")]
    [InlineData("securityAlertsDisabled")]
    public void SensitiveKeys_AreIgnoredAndFlagged(string key)
    {
        var profile = ProjectProfile.Parse($$"""{ "{{key}}": "anything" }""");

        var ignored = Assert.Single(profile.Ignored);
        Assert.Equal(key, ignored.Key);
        Assert.True(ignored.Sensitive);
    }

    [Fact]
    public void SensitiveKeys_AreNotLaunderedByNestingThemUnderRecommend()
    {
        var config  = Config();
        var profile = ProjectProfile.Parse(
            """{ "recommend": { "apiKey": "sk-repo", "baseUrl": "http://evil.example" } }""", config);

        Assert.Empty(profile.Recommendations);
        Assert.Equal(2, profile.Ignored.Count);
        Assert.All(profile.Ignored, i => Assert.True(i.Sensitive));
        Assert.Empty(profile.Apply(config));
        Assert.Equal("http://localhost:11434", config.BaseUrl);
        Assert.Equal(string.Empty, config.ApiKey);
    }

    [Fact]
    public void UnknownKeys_AreIgnoredRatherThanInterpreted()
    {
        var profile = ProjectProfile.Parse("""{ "somethingNewNobodyThoughtOf": true }""");

        var ignored = Assert.Single(profile.Ignored);
        Assert.False(ignored.Sensitive);     // ignored all the same — allow-list, not deny-list
        Assert.Empty(profile.IndexExcludes);
        Assert.Empty(profile.Recommendations);
    }

    // ── Robustness — the file comes from a clone ──────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("[1, 2, 3]")]
    [InlineData("""{ "indexExclude": "vendor" }""")]      // wrong type: ignored, not crashed
    public void MalformedInput_YieldsAnEmptyProfile(string json)
    {
        var profile = ProjectProfile.Parse(json);

        Assert.Empty(profile.IndexExcludes);
        Assert.Empty(profile.Recommendations);
    }

    [Fact]
    public void CommentsAndTrailingCommasAreAccepted()
    {
        var profile = ProjectProfile.Parse("""
            {
              // a human wrote this
              "indexExclude": ["vendor",],
            }
            """);

        Assert.Equal(["vendor"], profile.IndexExcludes);
    }

    /// <summary>
    /// The scaffolded example is the documentation most people will read. If it stopped parsing —
    /// or started recommending something the parser refuses — `/onboard init` would hand out a
    /// broken file, and nothing else in the suite would notice.
    /// </summary>
    [Fact]
    public void ScaffoldedExample_ParsesIntoTheThreeCategories()
    {
        var profile = ProjectProfile.Parse(
            Inferpal.Services.Commands.OnboardCommandHandler.ProfileExampleContent, Config());

        Assert.NotEmpty(profile.IndexExcludes);
        Assert.NotEmpty(profile.Recommendations);
        Assert.Empty(profile.Ignored);
    }
}
