using System.IO;
using Inferpal.Services.Presentation;
using Xunit;

namespace Inferpal.Tests;

// ──────────────────────────────────────────────────────────────────────────────────────────────
//  Reasoning closed by a lone </think> — the template opened the block — is reasoning.
//
//  Qwen3, Qwen3.6/3.8, Bonsai and GLM have the <think> written by their chat template, so their output holds only the
//  closing tag. A server that does not separate reasoning (vLLM without a reasoning parser, llama-server with
//  reasoning-format none, LM Studio for GLM with its separation setting off) sends it in the answer: on screen, in
//  copies and exports, and in every artifact made from a reply.
// ──────────────────────────────────────────────────────────────────────────────────────────────
public class OrphanThinkCloseTests
{
    [Theory]
    [InlineData("Let me look at the file.\nIt applies a discount.\n</think>\n\nThe answer is 60.", "The answer is 60.")]    // Qwen3
    [InlineData("the user wants the sum</think>The answer is 60.", "The answer is 60.")]                                      // GLM, inline
    public void AReplyThatClosesReasoningItNeverOpened_ShowsTheAnswerOnly(string content, string shown) =>
        Assert.Equal(shown, MarkdownParser.StripThinkTags(content));

    [Theory]
    [InlineData("Close it with `</think>`, as Qwen3 expects.")]
    [InlineData("Split on the tag:\n\n```python\nanswer = text.split(\"</think>\")[-1]\n```\n\nThat keeps the answer.")]
    [InlineData("<think>short</think>The tag </think> can stray later.")]
    public void ALoneTagTheAnswerShows_IsKept(string content)
    {
        // Reference arms: a tag in code is what the answer shows; after a paired block, a stray one is text.
        var expected = content.StartsWith("<think>short</think>", StringComparison.Ordinal)
            ? "The tag </think> can stray later."
            : content;
        Assert.Equal(expected, MarkdownParser.StripThinkTags(content));
    }

    [Fact]
    public void AnArtifact_LosesTheReasoningBeforeALoneCloseLine()
    {
        var reply = "I need to add a Reset method.\n</think>\n\npublic void Reset() => count = 0;";

        Assert.Equal("public void Reset() => count = 0;", MarkdownParser.WithoutLeadingReasoning(reply).Trim());
    }

    [Theory]
    [InlineData("var answer = text.Split(\"</think>\")[1];")]
    [InlineData("the user wants the sum</think>public int Sum() => 1;")]   // inline: not certain enough in bare code
    public void AnArtifact_KeepsCodeThatHoldsTheTag(string reply) =>
        Assert.Equal(reply, MarkdownParser.WithoutLeadingReasoning(reply));

    [Fact]
    public void TheWebview_HasTheSameRule()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Inferpal.sln"))) dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        var path = Path.Combine(dir!, "vscode", "src", "webview", "reasoning.ts");
        Assert.True(File.Exists(path), "reasoning.ts moved: this test reads nothing.");
        var rule = SettingsSchemaDriftTests.NeutralizeTypeScriptComments(File.ReadAllText(path));

        Assert.Contains("indexOfTag(text, THINK_CLOSE, 0) < 0", rule);          // a lone close is looked for
        Assert.Contains("!sawTag && tagAt(text, THINK_CLOSE, i)", rule);        // and read as the end of reasoning
    }
}
