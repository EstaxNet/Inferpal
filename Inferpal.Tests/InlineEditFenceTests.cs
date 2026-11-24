using Inferpal.Services.CodeActions;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A reply to an in-place edit wrapped in a fence of tildes is unwrapped like one of backticks: Markdown has both, and the
/// chat's parser reads both — left, the fence lines were written into the file as code.
/// </summary>
public class InlineEditFenceTests
{
    [Theory]
    [InlineData("~~~python\nx = 1\n~~~")]
    [InlineData("Here is the fix:\n~~~\nx = 1\n~~~\nThis sets x.")]
    [InlineData("```python\nx = 1\n```")]   // reference arm
    public void AFencedReply_IsItsCode_WhateverTheFenceCharacter(string reply) =>
        Assert.Equal("x = 1", InlineEditResponse.Clean(reply));

    [Fact]
    public void AMarkdownFileWithTildeFences_KeepsItsOwnBlocks_OnlyTheWrappingPairGoes()
    {
        var reply = "~~~markdown\n# Title\n~~~bash\nls\n~~~\n~~~";
        Assert.Equal("# Title\n~~~bash\nls\n~~~", InlineEditResponse.Clean(reply, ownFences: true));
    }
}
