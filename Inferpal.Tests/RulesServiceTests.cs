using System.IO;
using Inferpal.Services;
using Xunit;

namespace Inferpal.Tests;

public class RulesServiceTests : IDisposable
{
    private readonly string _dir;

    public RulesServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"rules_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private void WriteRule(string name, string content) =>
        File.WriteAllText(Path.Combine(_dir, name), content);

    // ── ParseFrontMatter ────────────────────────────────────────────────────────

    [Fact]
    public void ParseFrontMatter_NoFrontMatter_ReturnsWholeTextAsBody()
    {
        var (fm, body) = RulesService.ParseFrontMatter("Just a rule body.\nSecond line.");
        Assert.Empty(fm);
        Assert.Equal("Just a rule body.\nSecond line.", body);
    }

    [Fact]
    public void ParseFrontMatter_ReadsKeysAndPreservesBody()
    {
        var text = "---\ndescription: Naming\nglobs: **/*.cs, src/**\nalwaysApply: false\n---\nUse PascalCase.\n";
        var (fm, body) = RulesService.ParseFrontMatter(text);

        Assert.Equal("Naming", fm["description"]);
        Assert.Equal("**/*.cs, src/**", fm["globs"]);
        Assert.Equal("false", fm["alwaysApply"]);
        Assert.Equal("Use PascalCase.\n", body);
    }

    [Fact]
    public void ParseFrontMatter_HandlesCrlf()
    {
        var text = "---\r\ndescription: X\r\n---\r\nBody here.\r\n";
        var (fm, body) = RulesService.ParseFrontMatter(text);
        Assert.Equal("X", fm["description"]);
        Assert.Equal("Body here.\n", body);
    }

    // ── GlobMatch ────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("**/*.cs", "src/app/Foo.cs", true)]
    [InlineData("**/*.cs", "Foo.cs", true)]
    [InlineData("*.cs", "deep/nested/Bar.cs", true)]   // bare pattern matches file name at any depth
    [InlineData("*.cs", "Bar.cs", true)]
    [InlineData("*.cs", "Bar.ts", false)]
    [InlineData("src/**", "src/app/Foo.cs", true)]
    [InlineData("src/**", "lib/Foo.cs", false)]
    [InlineData("src/*.cs", "src/Foo.cs", true)]
    [InlineData("src/*.cs", "src/sub/Foo.cs", false)]  // * does not cross '/'
    [InlineData("file?.cs", "fileA.cs", true)]
    [InlineData("file?.cs", "fileAB.cs", false)]
    public void GlobMatch_Cases(string glob, string path, bool expected)
    {
        Assert.Equal(expected, RulesService.GlobMatch(glob, path));
    }

    [Fact]
    public void GlobMatch_NormalizesBackslashes()
    {
        Assert.True(RulesService.GlobMatch("src/**", "src\\app\\Foo.cs"));
    }

    // ── Matches ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Matches_AlwaysApply_IgnoresActiveFile()
    {
        var rule = new ProjectRule("R", "body", ["**/*.ts"], AlwaysApply: true);
        Assert.True(RulesService.Matches(rule, "Foo.cs"));
        Assert.True(RulesService.Matches(rule, null));
    }

    [Fact]
    public void Matches_NoGlobs_AppliesEverywhere()
    {
        var rule = new ProjectRule("R", "body", [], AlwaysApply: false);
        Assert.True(RulesService.Matches(rule, "anything.py"));
        Assert.True(RulesService.Matches(rule, null));
    }

    [Fact]
    public void Matches_GlobScoped_OnlyWhenActiveFileMatches()
    {
        var rule = new ProjectRule("R", "body", ["**/*.cs"], AlwaysApply: false);
        Assert.True(RulesService.Matches(rule, "src/Foo.cs"));
        Assert.False(RulesService.Matches(rule, "src/Foo.ts"));
        Assert.False(RulesService.Matches(rule, null));  // scoped rule needs an active file
    }

    // ── Load / Render ──────────────────────────────────────────────────────────

    [Fact]
    public void Load_MissingDirectory_ReturnsEmpty()
    {
        Assert.Empty(RulesService.Load(Path.Combine(_dir, "does-not-exist")));
    }

    [Fact]
    public void Load_ParsesNameGlobsAndAlwaysApply()
    {
        WriteRule("naming.md", "---\ndescription: Naming rules\nglobs: **/*.cs\nalwaysApply: true\n---\nUse PascalCase.");
        WriteRule("plain.md", "No frontmatter, just text.");

        var rules = RulesService.Load(_dir);

        Assert.Equal(2, rules.Count);
        var naming = rules.Single(r => r.Name == "Naming rules");
        Assert.True(naming.AlwaysApply);
        Assert.Equal(["**/*.cs"], naming.Globs);
        Assert.Equal("Use PascalCase.", naming.Body);

        var plain = rules.Single(r => r.Name == "plain");  // falls back to file name
        Assert.False(plain.AlwaysApply);
        Assert.Empty(plain.Globs);
    }

    [Fact]
    public void Load_SkipsEmptyBody()
    {
        WriteRule("empty.md", "---\ndescription: Nothing\n---\n   \n");
        Assert.Empty(RulesService.Load(_dir));
    }

    [Fact]
    public void Render_EmptyInput_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, RulesService.Render([]));
    }

    [Fact]
    public void Render_IncludesHeaderNameAndBody()
    {
        var rendered = RulesService.Render([new ProjectRule("My Rule", "Do the thing.", [], true)]);
        Assert.Contains("## Rules", rendered);
        Assert.Contains("### My Rule", rendered);
        Assert.Contains("Do the thing.", rendered);
    }
}
