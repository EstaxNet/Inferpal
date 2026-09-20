using Inferpal.Services.Rag;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// The block injected into every turn says what it is: a sample of the semantic index, not a search.
/// </summary>
/// <remarks>
/// <para>
/// ⚠ It is auto-injected — nobody asked for it — under a heading that reads
/// <c>## Relevant code (auto-retrieved for this question)</c>, and the retrieval itself is capped at
/// three chunks. Read as an answer, it makes a model conclude "this symbol is only used in X" about
/// a repository it was shown three snippets of. <c>search_codebase</c>, the explicit path over the
/// same index, prints its mode label and its index footer; this one printed nothing.
/// </para>
/// <para>
/// ⚠ Measured before the note existed: a chunk cut at 600 characters ended on a bare <c>…</c>
/// <b>inside the code fence</b> — <c>var line21 = C</c> then <c>…</c>, which reads as a syntax
/// error, not a truncation — and a chunk dropped by the character budget left no trace at all.
/// </para>
/// </remarks>
public sealed class RagAutoContextNoticeTests
{
    private static RagHit Hit(string rel, string body) =>
        new(new RagChunk
        {
            FilePath  = @"C:\ws\" + rel,
            RelPath   = rel,
            StartLine = 1,
            EndLine   = 20,
            Content   = body,
        }, 0.8f, IsCosine: true);

    private static string Big =>
        string.Join("\n", Enumerable.Range(1, 40).Select(i => $"    var line{i} = Compute({i});"));

    private static List<RagHit> Three() =>
    [
        Hit("Services/Alpha.cs", Big),
        Hit("Services/Beta.cs",  "class Beta { }"),
        Hit("Services/Gamma.cs", Big),
    ];

    [Fact]
    public void TheBlock_SaysItIsASample_AndNamesTheToolThatSearches()
    {
        var block = RagAutoContext.Build(Three(), new HashSet<string>());

        // WITNESS: the block really was produced, and really carries the snippets.
        Assert.Contains("Services/Alpha.cs", block);

        Assert.Contains("sample, not a search", block);
        Assert.Contains("search_codebase", block);
    }

    [Fact]
    public void ARetrievedSnippetThatDidNotFit_IsCounted()
    {
        var capped = RagAutoContext.Build(Three(), new HashSet<string>(), budget: 800);

        Assert.Equal(2, capped.Split("### ").Length - 1);      // witness: one really was dropped
        Assert.Contains("2 of 3 retrieved snippets", capped);
    }

    /// <summary>
    /// ⚠ A snippet skipped because its file is already attached is NOT a loss: its content is in
    /// the prompt, one section above. Counting it as "did not fit" would send the model looking for
    /// something it already has.
    /// </summary>
    [Fact]
    public void ASnippetSkippedBecauseItsFileIsAttached_IsNotReportedAsMissing()
    {
        var block = RagAutoContext.Build(Three(), new HashSet<string> { @"C:\ws\Services/Alpha.cs" });

        Assert.Equal(2, block.Split("### ").Length - 1);
        Assert.DoesNotContain("did not fit", block);
        Assert.Contains("Top 2 match", block);
    }

    [Fact]
    public void ASnippetCutMidLine_SaysSo_InsideTheFence()
    {
        var block = RagAutoContext.Build([Hit("Services/Alpha.cs", Big)], new HashSet<string>());

        Assert.Contains("…(truncated)", block);
        Assert.DoesNotContain("\n…\n", block);   // the bare ellipsis reads as a syntax error
    }

    /// <summary>Reference arm: nothing to say when nothing was retrieved.</summary>
    [Fact]
    public void WithNoResults_TheBlockIsEmpty()
    {
        Assert.Equal(string.Empty, RagAutoContext.Build([], new HashSet<string>()));
    }
}
