using System.IO;
using Inferpal.Services.Governance;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// ⚠ A rule's <c>globs</c> was split on every comma, and the front matter read flat <c>key: value</c> lines only. The two
/// YAML ways of writing a list then silently changed the rule's SCOPE: a block list (<c>- "**/*.cs"</c> lines) left
/// <c>globs</c> empty — and a rule without globs applies to EVERY file — while an inline array produced
/// <c>["**/*.cs"</c> and <c>"**/*.ts"]</c>, which match nothing: the rule was never applied. And a brace set
/// (<c>*.{md,mdx}</c>, the form editors write) was cut in two and escaped as literals. <c>/rules</c> showed none of it.
/// </summary>
public sealed class RuleGlobListFormsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "inferpal-tests", $"rules-{Guid.NewGuid():N}");

    public RuleGlobListFormsTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private ProjectRule Rule(string frontMatter)
    {
        File.WriteAllText(Path.Combine(_dir, "r.md"), $"---\n{frontMatter}\n---\nUse the house style.\n");
        return Assert.Single(RulesService.Load(_dir, out _));
    }

    [Fact]
    public void ABlockList_ScopesTheRule_InsteadOfApplyingItEverywhere()
    {
        var rule = Rule("description: C# style\nglobs:\n  - \"**/*.cs\"\n  - \"**/*.csx\"");

        Assert.Equal(["**/*.cs", "**/*.csx"], rule.Globs);
        Assert.True(RulesService.Matches(rule, "src/App/Program.cs"));
        Assert.False(RulesService.Matches(rule, "README.md"));
    }

    [Fact]
    public void AnInlineArray_ScopesTheRule_InsteadOfNeverApplyingIt()
    {
        var rule = Rule("globs: [\"**/*.cs\", \"**/*.ts\"]");

        Assert.Equal(["**/*.cs", "**/*.ts"], rule.Globs);
        Assert.True(RulesService.Matches(rule, "web/app.ts"));
    }

    [Fact]
    public void ABraceSet_IsOnePatternWithAlternatives()
    {
        var rule = Rule("globs: docs/**/*.{md,mdx}");

        Assert.Equal(["docs/**/*.{md,mdx}"], rule.Globs);
        Assert.True(RulesService.Matches(rule, "docs/guide/intro.mdx"));
        Assert.True(RulesService.Matches(rule, "docs/readme.md"));
        Assert.False(RulesService.Matches(rule, "docs/readme.txt"));
    }

    [Fact]
    public void TheCommaSeparatedForm_IsUnchanged()   // reference arm: the documented form keeps working
    {
        var rule = Rule("globs: **/*.cs, **/*.ts");

        Assert.Equal(["**/*.cs", "**/*.ts"], rule.Globs);
        Assert.Matches(RulesService.GlobToRegex("a{b"), "a{b");   // an unbalanced brace stays a literal character
    }
}
