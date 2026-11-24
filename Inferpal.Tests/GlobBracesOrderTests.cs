using System.Text.RegularExpressions;
using Inferpal.Services.Governance;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A brace set <c>{a,b}</c> is a set only when each one is closed after it opens: braces that only add up (<c>a}{b</c>) are
/// literal characters, and the glob still compiles — one bad glob of a rule must not take every rule out of the prompt.
/// </summary>
public class GlobBracesOrderTests
{
    [Theory]
    [InlineData("a}{b")]
    [InlineData("}{")]
    [InlineData("x}{y,z}{")]
    public void BracesThatOnlyAddUp_AreLiteral_AndTheGlobCompiles(string glob)
    {
        var pattern = RulesService.GlobToRegex(glob);
        _ = new Regex(pattern);   // throws when the braces were read as a set they are not
        Assert.True(RulesService.GlobMatch(glob, glob));
    }

    [Fact]
    public void ASetInItsOrder_IsStillASet()
    {
        Assert.True(RulesService.GlobMatch("*.{md,mdx}", "docs/a.mdx"));
        Assert.False(RulesService.GlobMatch("*.{md,mdx}", "docs/a.txt"));
    }
}
