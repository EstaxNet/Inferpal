using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// fetch_url and @Docs turn a page into text, and collapsed its whitespace everywhere — inside <c>&lt;pre&gt;</c> too:
/// every run of spaces became one, every indent was trimmed. A documentation page's code sample is its most useful
/// part, and in Python the indent IS the syntax: the model read a sample whose structure was gone.
/// </summary>
public class HtmlToTextCodeBlockTests
{
    private const string Page =
        "<html><body><h1>Retries</h1><p>Use   the    helper:</p>" +
        "<pre><code>def retry(fn, times=3):\n    for attempt in range(times):\n        try:\n            return fn()\n" +
        "        except IOError:\n            continue\n</code></pre><p>That   is all.</p></body></html>";

    [Fact]
    public void ACodeBlock_KeepsItsIndentation()
    {
        var text = FetchUrlTool.HtmlToText(Page);

        Assert.Contains("def retry(fn, times=3):\n    for attempt in range(times):\n        try:\n            return fn()", text);
        Assert.Contains("        except IOError:\n            continue", text);
    }

    [Fact]
    public void ProseWhitespace_IsStillCollapsed()
    {
        // Reference arm: outside a code block, a run of spaces is layout, not content.
        var text = FetchUrlTool.HtmlToText(Page);

        Assert.Contains("Use the helper:", text);
        Assert.Contains("That is all.", text);
    }

    [Fact]
    public void ADocChunkThatStartsInsideACodeBlock_KeepsItsFirstIndent()
    {
        // @Docs chunks that text with overlap: a chunk after the first starts wherever the budget falls, often inside
        // a code sample — and trimming the chunk dedented its first line only.
        var code = string.Join('\n', Enumerable.Range(0, 400).Select(i => $"        value_{i} = compute({i})  # step"));

        var chunks = Inferpal.Services.Docs.DocChunker.Chunk("site", "https://docs.example.com/a", "A", "def run():\n" + code);

        Assert.True(chunks.Count > 1, "the text must span several chunks for this to measure anything");
        Assert.All(chunks.Skip(1), c => Assert.StartsWith("        value_", c.Content));
    }

    [Fact]
    public void EntitiesInsideACodeBlock_AreDecoded()
    {
        var text = FetchUrlTool.HtmlToText("<pre>if a &lt; b &amp;&amp; c:\n    pass</pre>");

        Assert.Contains("if a < b && c:\n    pass", text);
    }
}
