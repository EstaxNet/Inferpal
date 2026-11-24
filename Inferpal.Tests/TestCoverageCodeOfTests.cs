using Inferpal.Services.Commands;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// The test file <c>/test coverage</c> writes is read from the reply as every reply turned into a file is
/// (<c>MarkdownParser.WithoutLeadingReasoning</c>): reasoning at its head goes, a tag the code itself holds stays.
/// </summary>
public class TestCoverageCodeOfTests
{
    [Fact]
    public void ATagInsideBareCode_IsKept()
    {
        const string reply = "def test_strip():\n    assert strip(\"<think>x</think>y\") == \"y\"\n";
        Assert.Equal(reply, TestCoverageCommandHandler.CodeOf(reply));
    }

    [Theory]
    [InlineData("~~~python\ndef test_a():\n    assert True\n~~~")]
    [InlineData("````python\ndef test_a():\n    assert True\n````")]
    [InlineData("Here it is:\n```python\ndef test_a():\n    assert True\n```\nIt covers a.")]   // reference arm
    public void AFenceOfTildesOrOfFourBackticks_IsUnwrappedAsTheChatReadsIt(string reply) =>
        // Left whole, the fence lines were written into the test file.
        Assert.Equal("def test_a():\n    assert True\n", TestCoverageCommandHandler.CodeOf(reply));

    [Fact]
    public void ReasoningAtTheHead_Goes()
    {
        const string reply = "<think>I will test strip.</think>\ndef test_strip():\n    assert strip(\"a\") == \"a\"\n";
        Assert.Equal("def test_strip():\n    assert strip(\"a\") == \"a\"\n", TestCoverageCommandHandler.CodeOf(reply));
    }
}
