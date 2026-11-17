using Inferpal.Services.Shell;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A command measured several times (pari 103): the median and the spread after a warm-up, failed runs left out and said, and
/// against the previous measure of the same command, "within the noise" when the runs overlap — the sentence that made two models
/// revert the changes that gained nothing.
/// </summary>
public class RepeatedRunTests
{
    private static RunTiming[] Runs(params double[] seconds) => seconds.Select(s => new RunTiming(s, 0)).ToArray();

    [Fact]
    public void TheFigures_AreTheMedianAndTheSpread()
    {
        var s = RepeatedRun.Summarize(Runs(3.6, 3.4, 3.5, 3.7, 3.5))!;

        Assert.Equal(3.5, s.Median, 3);
        Assert.Equal(3.4, s.Min, 3);
        Assert.Equal(3.7, s.Max, 3);
        Assert.Equal(5, s.Runs);
    }

    [Fact]
    public void OverlappingRuns_AreWithinTheNoise_EvenWithALowerMedian()
    {
        var before = RepeatedRun.Summarize(Runs(3.63, 3.55, 3.70, 3.60, 3.58))!;
        var after = RepeatedRun.Summarize(Runs(3.49, 3.56, 3.61, 3.52, 3.54))!;

        var said = RepeatedRun.Compare(before, after);

        Assert.Contains("within the noise", said);
        Assert.Contains("-", said);
    }

    [Fact]
    public void ApartRuns_AreFaster_BeyondTheNoise()
    {
        var said = RepeatedRun.Compare(RepeatedRun.Summarize(Runs(3.49, 3.44, 3.59))!, RepeatedRun.Summarize(Runs(0.46, 0.45, 0.47))!);

        Assert.Contains("faster, beyond the noise", said);
        Assert.Contains("-86", said);
    }

    [Fact]
    public void AFailedRun_IsNoTime_AndIsSaid()
    {
        var said = RepeatedRun.Report([new(1.0, 0), new(0.1, 1), new(1.1, 0)], 2.0, null);

        Assert.Contains("warm-up run (not counted): 2.000 s", said);
        Assert.Contains("0.100 s (exit 1)", said);
        Assert.Contains("1 failed run(s) left out", said);
        Assert.Contains("median 1.050 s", said);
        Assert.Contains("first measure of this command", said);
    }

    [Fact]
    public void TooFewGoodRuns_GiveNoVerdictOnTheNoise()
    {
        var previous = RepeatedRun.Summarize(Runs(3.6, 3.5))!;

        var said = RepeatedRun.Report(Runs(0.5, 0.4, 0.5), null, previous);

        Assert.Contains("too few good runs to compare", said);
        Assert.DoesNotContain("beyond the noise", said);
    }

    [Fact]
    public void AllRunsFailed_GivesNoFigure()
    {
        var said = RepeatedRun.Report([new(0.1, 2), new(0.1, 2)], null, null);

        Assert.Contains("no time: all 2 runs failed", said);
        Assert.DoesNotContain("median", said);
    }
}
