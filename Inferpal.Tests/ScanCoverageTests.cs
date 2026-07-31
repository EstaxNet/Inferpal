using Inferpal.Localization;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// The analysis tools cap how many files they read. Capping is fine; hiding it is not — an agent
/// reading "Direct dependants (0)" cannot tell "nothing depends on this file" from "nothing among
/// the arbitrary first 500 files the filesystem enumerated".
/// </summary>
public class ScanCoverageTests
{
    private static IEnumerable<string> Files(int count) =>
        Enumerable.Range(0, count).Select(i => $"f{i}.cs");

    [Fact]
    public void UnderTheCap_EverythingIsScannedAndNothingIsWarned()
    {
        var (files, coverage) = ScanCoverage.Take(Files(10), cap: 500);

        Assert.Equal(10, files.Count);
        Assert.Equal(10, coverage.Total);
        Assert.Equal(10, coverage.Scanned);
        Assert.False(coverage.IsPartial);
        Assert.Empty(coverage.Warning());
    }

    [Fact]
    public void OverTheCap_ReportsBothNumbers()
    {
        var (files, coverage) = ScanCoverage.Take(Files(1200), cap: 500);

        Assert.Equal(500, files.Count);
        Assert.Equal(1200, coverage.Total);
        Assert.True(coverage.IsPartial);
        Assert.Equal(Strings.ScanPartial(500, 1200), coverage.Warning());
        Assert.Contains("500", coverage.Warning());
        Assert.Contains("1200", coverage.Warning());
    }

    [Fact]
    public void ExactlyAtTheCap_IsNotPartial()
    {
        var (_, coverage) = ScanCoverage.Take(Files(500), cap: 500);

        Assert.False(coverage.IsPartial);
    }

    [Fact]
    public void EmptyInput_IsNotPartial()
    {
        var (files, coverage) = ScanCoverage.Take([], cap: 500);

        Assert.Empty(files);
        Assert.False(coverage.IsPartial);
    }

    [Fact]
    public void Take_EnumeratesTheSourceOnlyOnce()
    {
        // The source is a directory walk: enumerating it twice would double the I/O of every
        // analysis call.
        var enumerations = 0;
        IEnumerable<string> Counted()
        {
            enumerations++;
            foreach (var f in Files(600)) yield return f;
        }

        ScanCoverage.Take(Counted(), cap: 500);

        Assert.Equal(1, enumerations);
    }
}
