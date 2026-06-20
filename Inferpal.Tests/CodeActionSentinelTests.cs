using Inferpal.Services;
using Xunit;

namespace Inferpal.Tests;

// Covers the pure "nothing to do" detection shared by the in-place code actions
// (/refactor, /fix, /doc) and the test generation pipeline (/test). The model is told to reply
// with the sentinel alone when the requested change would bring nothing.
public class CodeActionSentinelTests
{
    [Theory]
    [InlineData("INFERPAL_NO_CHANGE_NEEDED")]                 // bare token
    [InlineData("  INFERPAL_NO_CHANGE_NEEDED  ")]             // surrounding whitespace
    [InlineData("INFERPAL_NO_CHANGE_NEEDED.")]                // trailing punctuation
    [InlineData("`INFERPAL_NO_CHANGE_NEEDED`")]              // wrapped in backticks
    [InlineData("\"INFERPAL_NO_CHANGE_NEEDED\"")]            // wrapped in quotes
    [InlineData("inferpal_no_change_needed")]                 // case-insensitive
    [InlineData("\nINFERPAL_NO_CHANGE_NEEDED\n")]            // newlines
    public void IsNoChange_recognizes_the_sentinel(string reply)
    {
        Assert.True(CodeActionSentinel.IsNoChange(reply));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("public int Add(int a, int b) => a + b;")]    // real code
    [InlineData("// INFERPAL_NO_CHANGE_NEEDED means no-op\npublic void M() { }")] // token only in a comment
    [InlineData("INFERPAL_NO_CHANGE_NEEDED is the sentinel we return")]            // token inside a sentence
    public void IsNoChange_rejects_everything_else(string? reply)
    {
        Assert.False(CodeActionSentinel.IsNoChange(reply));
    }
}
