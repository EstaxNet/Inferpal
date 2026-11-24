using System.Text;
using Inferpal.Services.Commands;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// What <c>/test coverage</c> cuts to send the model (a source too large, a runner's output) keeps whole characters, as every
/// other cut does (<c>SafeTruncate</c>): half an emoji is a lone surrogate no UTF-8 writer encodes.
/// </summary>
public class TestCoverageExcerptCutsTests
{
    private static readonly string Emoji = char.ConvertFromUtf32(0x1F600);

    private static void AssertWhole(string text) =>
        Assert.Equal(text, Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(text)));

    [Fact]
    public void AToolOutputsHeadAndTail_KeepWholeCharacters() =>
        AssertWhole(TestCoverageCommandHandler.Ends(new string('a', 9) + Emoji + new string('b', 40) + Emoji + "cccc", 10, 5));

    [Fact]
    public void ASourceTooLargeToSend_IsCutOnWholeCharacters() =>
        AssertWhole(TestCoverageCommandHandler.Bounded([.. Enumerable.Repeat(new string('x', 999) + Emoji + "tail", 400)], 100, 110));
}
