using System.IO;
using System.Linq;
using Inferpal.Services;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// An ANSWER that talks about reasoning tags keeps what it says. The strip removed every
/// <c>&lt;think&gt;…&lt;/think&gt;</c> it found and cut an unclosed one to the end, wherever it
/// stood — so the regex a model writes to answer "how do I remove Qwen3's reasoning?" deleted half
/// the answer and left a broken code fence, and a sentence naming the opening tag in backticks
/// erased everything after it. Reasoning is what the model emits as TEXT; a tag inside code is
/// something the answer is showing.
/// </summary>
public class ReasoningTagMentionTests
{
    private const string RegexAnswer =
        "Qwen3 wraps its reasoning in `<think>` tags. Strip them with a regex:\n\n" +
        "```python\nimport re\nclean = re.sub(r\"<think>.*?</think>\", \"\", text, flags=re.DOTALL)\n```\n\n" +
        "This keeps only the final answer.";

    private const string SpanAnswer =
        "The model opens its reply with `<think>` and closes it later.\n\n" +
        "1. Read the stream.\n2. Drop everything until the closing tag.\n3. Show the rest.";

    [Fact]
    public void ARegexInACodeBlock_SurvivesWhole()
    {
        Assert.Equal(RegexAnswer, MarkdownParser.StripThinkTags(RegexAnswer));
    }

    [Fact]
    public void AnOpeningTagNamedInACodeSpan_DoesNotEraseTheRestOfTheAnswer()
    {
        Assert.Equal(SpanAnswer, MarkdownParser.StripThinkTags(SpanAnswer));
    }

    [Fact]
    public void TheRenderedCodeBlock_CarriesTheRegexTheModelWrote()
    {
        var code = MarkdownParser.Parse(RegexAnswer).Single(b => b.Type == "code_block");

        Assert.Contains("re.sub(r\"<think>.*?</think>\", \"\", text, flags=re.DOTALL)", code.Text);
    }

    [Fact]
    public void TheCopiedOrExportedAnswer_IsTheWholeAnswer()
    {
        Assert.Equal(SpanAnswer, MarkdownParser.ShownText("assistant", SpanAnswer));
    }

    // ── Reference arms: real reasoning still goes ────────────────────────────

    [Theory]
    // Reasoning ahead of the answer — the ordinary form.
    [InlineData("<think>\nThe user wants a greeting.\n</think>\n\nHello!", "Hello!")]
    // Stopped mid-reasoning: the unclosed tail is reasoning.
    [InlineData("<think>\nStill thinking about `x`", "")]
    // Agent iterations streamed into one message: reasoning in the middle of the text.
    [InlineData("First step.\n<think>next</think>\nSecond step.", "First step.\n\nSecond step.")]
    // Reasoning that itself writes code: its fences are the reasoning's, not the answer's.
    [InlineData("<think>\nI'll write:\n```python\nx = 1\n```\n</think>\n\nDone.", "Done.")]
    // Reasoning that OPENS a fence and never closes it must not turn the answer into code.
    [InlineData("<think>\n```\nhalf a thought\n</think>\nAnswer.\n<think>more</think>End.", "Answer.\nEnd.")]
    // A closed code span before the reasoning does not hide it.
    [InlineData("Use `x`.\n<think>secret</think>\nDone.", "Use `x`.\n\nDone.")]
    // A lone backtick is literal text, not a span that hides what follows.
    [InlineData("It costs 5`.\n<think>secret</think>\nDone.", "It costs 5`.\n\nDone.")]
    public void RealReasoning_IsStillRemoved(string content, string expected)
    {
        Assert.Equal(expected, MarkdownParser.StripThinkTags(content));
    }

    // ── The VS Code webview renders with its own copy of the rule ────────────
    // Not executable from this suite (it runs in the webview), so this is a source scan with its
    // witnesses. The same cases were run through Node against the transpiled module.

    private static string WebviewSource(string file)
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Inferpal.sln")))
            dir = Path.GetDirectoryName(dir);
        Assert.True(dir is not null, "Inferpal.sln not found above the test assembly.");
        var path = Path.Combine(dir!, "vscode", "src", "webview", file);
        Assert.True(File.Exists(path), $"{file} not found: the webview's reasoning rule moved, and this test reads nothing.");
        return SettingsSchemaDriftTests.NeutralizeTypeScriptComments(File.ReadAllText(path));
    }

    [Fact]
    public void TheWebview_SkipsFencesAndCodeSpans_BeforeLookingForATag()
    {
        var rule = WebviewSource("reasoning.ts");

        Assert.Contains("export function stripThinkTags(", rule);
        Assert.Contains("readFence(", rule);
        Assert.Contains("spanCloser(", rule);
    }

    [Fact]
    public void TheWebview_RendersThroughThatRule_AndNoTagRegexIsLeft()
    {
        var markdown = WebviewSource("markdown.ts");
        Assert.Contains("from './reasoning'", markdown);
        Assert.Contains("md.render(stripThinkTags(", markdown);

        // The pattern that removed a tag wherever it stood, code included.
        foreach (var file in new[] { "markdown.ts", "main.ts", "reasoning.ts" })
            Assert.DoesNotContain("<think>[\\s\\S]", WebviewSource(file));
    }
}
