using Inferpal.Services.CodeActions;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// Smart Fix runs after every write and hands the model
/// <c>"🔨 Smart Fix: {0} compilation error(s) detected"</c>. Until 2026-09-10 that <c>{0}</c> was
/// the count of the <b>already truncated</b> list: a build with eighty errors introduced itself as
/// "20". Not a silence — a <b>wrong number</b>, in the tightest loop the product has. The model
/// fixes its twenty, rebuilds, finds sixty, and reads those as errors it has just introduced.
/// </summary>
public class SmartFixErrorCountTests
{
    private const int Cap = SmartFixValidator.MaxErrorLinesListed;

    private static List<string> Lines(int n) =>
        Enumerable.Range(1, n).Select(i => $"Foo.cs({i},1): error CS0103: line {i}").ToList();

    [Fact]
    public void ExtractErrorLines_DoesNotCap_TheCountIsTheCallersToReport()
    {
        // This is where the count was lost: the method already returned `.Take(25)`, so the caller
        // COULD NOT know the real total, whatever it did afterwards.
        var output = string.Join("\n", Lines(Cap + 40));

        var extracted = SmartFixValidator.ExtractErrorLines(output);

        Assert.Equal(Cap + 40, extracted.Count);
    }

    [Fact]
    public void Listed_CapsTheTextAndSaysHowManyItLeftOut()
    {
        var text = SmartFixValidator.Listed(Lines(Cap + 40));

        var rendered = text.Split('\n');
        Assert.Equal(Cap + 1, rendered.Length);          // the lines plus the marker
        Assert.Contains($"+{40} more", text);
        Assert.Contains("line 1", text);
        Assert.DoesNotContain($"line {Cap + 40}", text); // that one is genuinely out of the render
    }

    [Fact]
    public void Listed_UnderTheCap_RendersEverythingAndSaysNothing()
    {
        // WITNESS: without it, a `Listed` that always appended a marker — or always truncated —
        // would pass the previous test.
        var text = SmartFixValidator.Listed(Lines(3));

        Assert.Equal(3, text.Split('\n').Length);
        Assert.DoesNotContain("more", text);
    }

    [Fact]
    public void Listed_ExactlyAtTheCap_IsNotMarkedAsTruncated()
    {
        var text = SmartFixValidator.Listed(Lines(Cap));

        Assert.Equal(Cap, text.Split('\n').Length);
        Assert.DoesNotContain("more", text);
    }

    [Fact]
    public void ExtractErrorLines_PrefersErrorLines_ButFallsBackToEverything()
    {
        // Witness for the selection, untouched by the fix: Go does not write the word "error".
        Assert.Equal(
            ["main.go:10:5: undefined: foo"],
            SmartFixValidator.ExtractErrorLines("main.go:10:5: undefined: foo"));

        Assert.Equal(
            ["b.cs(1,1): error CS0103: x"],
            SmartFixValidator.ExtractErrorLines("noise\nb.cs(1,1): error CS0103: x"));
    }
}
