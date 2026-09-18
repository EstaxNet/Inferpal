using System.Linq;
using Inferpal.Services.Docs;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A documentation page with no line breaks became <b>one chunk</b>, whatever its size.
/// </summary>
/// <remarks>
/// <para>
/// ⚠ <c>DocChunker</c>'s own summary says <i>"Prose has no syntactic boundaries"</i> — and then slides
/// a <b>line-based</b> window over it. An ordinary page survives because <c>HtmlToText</c> inserts a
/// newline after every block-level close, but three real shapes do not: a page that is one long
/// <c>&lt;p&gt;</c>, a <c>&lt;pre&gt;</c> dump, and — guaranteed — any page that took the
/// regex-timeout fallback, which strips tags and inserts <b>no</b> newlines at all.
/// </para>
/// <para>
/// ⚠ <b>Measured</b>: 128 231 characters on a single line produced exactly <b>1</b> chunk. It is then
/// embedded whole — far past the embedding model's context, so the vector describes its opening and
/// nothing else — and <c>search_docs</c> shows the model its first 900 characters. The page is in the
/// index, counted, and unfindable.
/// </para>
/// <para>
/// ⚠ Same shape as the lesson already written on the code chunker: <i>a long symbol is CUT, not
/// shrunk</i>. This is its fourth tier, with the opposite failure — nothing cuts at all.
/// </para>
/// </remarks>
public sealed class DocChunkerLongLineTests
{
    /// <summary>~500 tokens is the target; a chunk is allowed to overshoot it, not to ignore it.</summary>
    private const int GenerousChunkCharCeiling = 8_000;

    private static string OneLine(int words) =>
        string.Join(" ", Enumerable.Range(0, words).Select(i => $"w{i:D5}"));

    [Fact]
    public void APageOnASingleLine_IsCutIntoWindows_NotServedWhole()
    {
        var text = OneLine(20_000);
        Assert.DoesNotContain('\n', text);            // witness: the fixture really has no line breaks
        Assert.True(text.Length > 100_000, $"the fixture is only {text.Length} characters long.");

        var chunks = DocChunker.Chunk("site", "https://docs.example.com/a", "A", text);

        Assert.True(chunks.Count > 1, $"still one chunk of {text.Length} characters.");
        Assert.All(chunks, c => Assert.True(c.Content.Length <= GenerousChunkCharCeiling,
            $"a chunk of {c.Content.Length} characters is not a window."));
    }

    [Fact]
    public void CuttingAPage_LosesNoWord()
    {
        // ⚠ The half that matters: a cut that drops the tail is the defect the code chunker already
        // paid for. Every word must be findable in some chunk.
        var text   = OneLine(20_000);
        var chunks = DocChunker.Chunk("site", "https://docs.example.com/a", "A", text);
        var joined = string.Join(" ", chunks.Select(c => c.Content));

        foreach (var probe in new[] { "w00000", "w09999", "w19999" })
            Assert.Contains(probe, joined, StringComparison.Ordinal);
    }

    [Fact]
    public void CuttingAPage_NeverSplitsAWordInTwo()
    {
        var chunks = DocChunker.Chunk("site", "https://docs.example.com/a", "A", OneLine(20_000));

        // Every token of the fixture is exactly six characters; a cut mid-word would leave a shorter
        // fragment at a chunk boundary. Split on every separator the chunker may introduce — the
        // pieces of a cut line are rejoined with a newline.
        foreach (var c in chunks)
            Assert.All(c.Content.Split([' ', '\n'], StringSplitOptions.RemoveEmptyEntries),
                       w => Assert.Equal(6, w.Length));
    }

    [Fact]
    public void ASingleWordLongerThanTheBudget_StillTerminates_AndIsKept()
    {
        // No space to cut on: the window must fall back to a hard cut rather than loop forever or
        // give up on the line.
        var blob   = new string('x', 40_000);
        var chunks = DocChunker.Chunk("site", "https://docs.example.com/a", "A", blob);

        Assert.True(chunks.Count > 1, "a 40 000-character token was served whole.");
        Assert.Equal(blob.Length, chunks.Sum(c => c.Content.Length));
    }

    [Fact]
    public void AnOrdinaryPage_ChunksAsItAlwaysDid()
    {
        // REFERENCE ARM: HtmlToText gives one line per block, and those lines are short. The window
        // must keep grouping them — a fix that cut per line would explode an ordinary page into
        // hundreds of one-paragraph chunks.
        var text = string.Join("\n", Enumerable.Range(0, 20).Select(i =>
            $"Paragraph {i}: " + string.Join(" ", Enumerable.Range(0, 20).Select(j => $"word{j}"))));

        var chunks = DocChunker.Chunk("site", "https://docs.example.com/a", "A", text);

        Assert.InRange(chunks.Count, 1, 6);
        Assert.Contains(chunks, c => c.Content.Contains("Paragraph 0:", StringComparison.Ordinal));
        Assert.Contains(chunks, c => c.Content.Contains("Paragraph 19:", StringComparison.Ordinal));
    }
}
