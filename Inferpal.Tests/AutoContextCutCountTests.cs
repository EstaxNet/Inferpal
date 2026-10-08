using Inferpal.Services.Rag;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// The auto-context note "N of M retrieved snippets — the rest did not fit" counts in M only what did not fit: a snippet
/// of an attached file is in the prompt already, one of a file changed since it was indexed is left out for its own
/// reason, and says it.
/// </summary>
/// <remarks>
/// When the budget stopped the loop, the results after it were never looked at: an attached file's snippets and stale
/// ones among them were counted as "did not fit" (3 of 7 for 3 of 4), and the stale ones went unsaid.
/// </remarks>
public sealed class AutoContextCutCountTests
{
    private static RagHit Hit(string rel, string body) =>
        new(new RagChunk { FilePath = @"C:\ws\" + rel, RelPath = rel, StartLine = 1, EndLine = 20, Content = body },
            0.8f, IsCosine: true);

    private static string Big =>
        string.Join("\n", Enumerable.Range(1, 40).Select(i => $"    var line{i} = Compute({i});"));

    [Fact]
    public void WhatFollowsTheCut_IsCountedByWhatHappensToIt()
    {
        List<RagHit> results =
        [
            Hit("A.cs", "class A { }"), Hit("B.cs", "class B { }"), Hit("C.cs", "class C { }"),
            Hit("Big.cs", Big),                                       // does not fit: the cut
            Hit("Attached.cs", "class Attached { }"), Hit("Attached.cs", "class Attached2 { }"),
            Hit("Changed.cs", "class Changed { }"),
        ];

        var block = RagAutoContext.Build(results, new HashSet<string> { @"C:\ws\Attached.cs" }, budget: 300,
                                         notYetReindexed: new HashSet<string> { @"C:\ws\Changed.cs" });

        Assert.Contains("_3 of 4 retrieved snippets — the rest did not fit. ", block, StringComparison.Ordinal);
        Assert.Contains("1 snippet(s) of files changed since they were indexed were left out", block, StringComparison.Ordinal);
    }

    [Fact]
    public void ACutWithNothingElseBehindIt_CountsEveryRemainingSnippet()
    {
        // Reference arm: every result after the cut did not fit.
        List<RagHit> results = [Hit("A.cs", "class A { }"), Hit("Big.cs", Big), Hit("Big2.cs", Big)];

        var block = RagAutoContext.Build(results, new HashSet<string>(), budget: 300);

        Assert.Contains("_1 of 3 retrieved snippets — the rest did not fit. ", block, StringComparison.Ordinal);
        Assert.DoesNotContain("changed since", block, StringComparison.Ordinal);
    }
}
