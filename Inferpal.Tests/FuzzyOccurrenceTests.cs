using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

// ──────────────────────────────────────────────────────────────────────────────────────────────
//  The whitespace-tolerant pass of apply_diff / apply_edits honours `occurrence` like the exact one.
//
//  Two blocks matching only once indentation is set aside: "first" and "all" were ignored there, and
//  the edit came back "found 2 times — make the match unique" — the one remedy the model had just
//  declined by asking for every match.
// ──────────────────────────────────────────────────────────────────────────────────────────────
public class FuzzyOccurrenceTests
{
    // Tabs in the file, spaces in old_content: no exact match, two tolerant ones.
    private const string File =
        "void F()\n{\n\tif (a)\n\t\tb();\n}\nvoid G()\n{\n\tif (a)\n\t\tb();\n}\n";
    private const string Old = "if (a)\n    b();";
    private const string New = "\tif (a)\n\t\tc();";

    [Fact]
    public void All_ReplacesEveryBlock()
    {
        var r = ApplyDiffMatcher.Resolve(File, Old, New, "all");

        Assert.True(r.Fuzzy);   // witness: the exact pass found nothing — this is the tolerant one
        Assert.Equal(2, r.Count);
        Assert.Equal(File.Replace("\t\tb();", "\t\tc();"), r.Modified);
    }

    [Fact]
    public void First_ReplacesTheFirstBlockOnly()
    {
        var r = ApplyDiffMatcher.Resolve(File, Old, New, "first");

        Assert.Equal(1, r.Count);
        var firstAt = File.IndexOf("\t\tb();", System.StringComparison.Ordinal);
        var expected = File[..firstAt] + "\t\tc();" + File[(firstAt + "\t\tb();".Length)..];
        Assert.Equal(expected, r.Modified);
    }

    [Fact]
    public void Unique_StillRefusesTwoBlocks()
    {
        // Reference arm: the default keeps refusing an ambiguous tolerant match.
        var r = ApplyDiffMatcher.Resolve(File, Old, New, null);

        Assert.Null(r.Modified);
        Assert.Equal(2, r.Count);
    }
}
