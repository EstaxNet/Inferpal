using System.Text.RegularExpressions;
using Inferpal.Services;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// Locks the decision contract that drives the typed @-mention popup. The popup's
/// UI (RemoteUI + Ollama + RAG) can't be exercised headlessly, but the routing —
/// "committed query category" vs "still choosing a category" — is pure regex work,
/// now owned by <see cref="MentionController"/> (see also MentionControllerTests for
/// the higher-level Parse API).
///
/// Regression target: selecting <c>@code</c> commits "@code " (trailing space) with an
/// empty query. That must be recognised as a committed code mention so the empty-query
/// branch can show a hint instead of silently closing the popup ("@code ne fait rien").
/// </summary>
public class MentionRegexTests
{
    private static readonly Regex Mention      = MentionController.MentionRegex;
    private static readonly Regex MentionQuery = MentionController.MentionQueryRegex;

    [Theory]
    [InlineData("@code ",     "")]      // just committed from the menu — empty query → hint branch
    [InlineData("@code auth", "auth")]  // typed a query → 🔮 action branch
    [InlineData("@file Foo",  "Foo")]
    [InlineData("@folder bar","bar")]
    public void CommittedQueryCategory_IsRecognised(string prompt, string expectedTrimmedQuery)
    {
        var m = MentionQuery.Match(prompt);
        Assert.True(m.Success, $"'{prompt}' should match the committed-query regex");
        Assert.Equal(expectedTrimmedQuery, m.Groups["q"].Value.Trim());
    }

    [Fact]
    public void CommittedCode_PreservesCategory()
    {
        var m = MentionQuery.Match("@code something");
        Assert.True(m.Success);
        Assert.Equal("code", m.Groups["cat"].Value.ToLowerInvariant());
    }

    [Theory]
    [InlineData("@code")]       // no space yet → still choosing the category, not committed
    [InlineData("@")]
    [InlineData("@clipboard")]  // instant providers are never query-committed
    public void NotYetCommitted_DoesNotMatchQueryRegex(string prompt) =>
        Assert.False(MentionQuery.Match(prompt).Success);

    [Theory]
    [InlineData("@",          "")]
    [InlineData("@c",         "c")]
    [InlineData("@code",      "code")]
    [InlineData("@clipboard", "clipboard")]
    public void CategoryChoosing_CapturesTypedToken(string prompt, string expectedToken)
    {
        var m = Mention.Match(prompt);
        Assert.True(m.Success, $"'{prompt}' should match the category regex");
        Assert.Equal(expectedToken, m.Groups[1].Value);
    }

    [Fact]
    public void CommittedQuery_TakesPrecedenceOverCategoryMatch()
    {
        // "@code auth" matches both regexes; production checks the committed one first so the
        // search action wins over re-listing categories.
        Assert.True(MentionQuery.Match("@code auth").Success);
        Assert.False(Mention.Match("@code auth").Success); // trailing word breaks the @\w+$ anchor
    }
}
