using System.Collections.Generic;
using System.IO;
using System.Linq;
using Inferpal.Services;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// When a scan is CAPPED, "which N files out of M" must be a property of the product, not of the
/// file system.
/// </summary>
/// <remarks>
/// <para>
/// ⚠ <b>The cap already declares itself — it is the SUBSET that was decided nowhere.</b>
/// <see cref="ScanCoverage"/> says "400 of 652"; what it does not say, and cannot say, is that those
/// 400 are the ones the volume happened to yield first. <c>Directory.GetFiles</c> and
/// <c>FileSystemEnumerable</c> return their entries in file-system order: by name on NTFS,
/// <b>arbitrary</b> on POSIX. So on a repository larger than the cap, <c>analyze_impact</c> (500),
/// <c>trace_dependency</c> (400) and the nexus (500) can answer two different things to two
/// identical calls, and neither answer is reproducible.
/// </para>
/// <para>
/// ⚠ <b>The discipline existed in ONE place</b>: <c>RenameSymbolTool</c> sorts
/// (<c>hits.OrderBy(h => h.FilePath)</c>) before capping what it shows in the approval prompt. Six
/// other sites cap without sorting. Same shape as the <c>@folder</c> fix — instance closed, class
/// left alive — and the same reason as RRF's tie-break: context that wobbles between two identical
/// gestures churns the KV-cache prefix.
/// </para>
/// <para>
/// ⚠ <b>And it is measurable HERE because the funnel is pure.</b> Measuring on the walk itself
/// would be impossible from a Windows box: NTFS already returns its entries by name, so an ordering
/// assertion would be green there by accident and would only go red on CI's POSIX legs — exactly the
/// trap this round closes.
/// </para>
/// </remarks>
public class ScanSubsetDeterminismTests
{
    /// <summary>The order a POSIX file system may return: the inode's.</summary>
    private static readonly List<string> Shuffled =
    [
        "/w/Zeta.cs", "/w/alpha.cs", "/w/Mid.cs", "/w/beta.cs", "/w/Aaa.cs",
    ];

    [Fact]
    public void ACappedScan_KeepsTheSameSubsetWhateverTheFileSystemOrder()
    {
        var (first,  _) = ScanCoverage.Take(Shuffled, cap: 3);
        var (second, _) = ScanCoverage.Take(Enumerable.Reverse(Shuffled).ToList(), cap: 3);

        // WITNESS: the cap really bites — without it the two lists would be equal for the wrong
        // reason (they would both fit whole).
        Assert.Equal(3, first.Count);
        Assert.True(Shuffled.Count > 3, "the cap does not bite: this test measures nothing.");

        Assert.Equal(first, second);
    }

    [Fact]
    public void TheSubsetIsTheOneAnyoneCanPredict()
    {
        // Ordinal, not the culture: that is what this repository requires wherever an order decides
        // (rule 19), and case must not depend on the machine.
        var (files, _) = ScanCoverage.Take(Shuffled, cap: 3);

        Assert.Equal(["/w/Aaa.cs", "/w/Mid.cs", "/w/Zeta.cs"], files);
    }

    [Fact]
    public void TheTotalCountedIsStillTheWholeSet()
    {
        // Reference arm: sorting does not change what the report announces — "3 of 5" stays "3 of
        // 5", and that count is what triggers the coverage warning.
        var (_, coverage) = ScanCoverage.Take(Shuffled, cap: 3);

        Assert.Equal(5, coverage.Total);
        Assert.Equal(3, coverage.Scanned);
        Assert.True(coverage.IsIncomplete);
    }

    [Fact]
    public void AScanThatFitsEntirely_IsUntouchedInContentAndComplete()
    {
        // Reference arm for the opposite failure: under the cap, nothing is dropped.
        var (files, coverage) = ScanCoverage.Take(Shuffled, cap: 50);

        Assert.Equal(5, files.Count);
        Assert.False(coverage.IsIncomplete);
        Assert.Equal([.. Shuffled.OrderBy(f => f, StringComparer.Ordinal)], files);
    }

    /// <summary>
    /// <c>list_files</c> does not go through the funnel — it caps the walk itself, and its message
    /// says "showing <b>first</b> {limit} files".
    /// </summary>
    /// <remarks>
    /// ⚠ A SOURCE assertion, and deliberately so: on NTFS the walk already returns its entries by
    /// name, so an assertion on the output would be green here <b>before</b> the fix and would only
    /// discriminate on CI's POSIX legs. The first round of sabotages showed it — removing the sort
    /// from this site reddened no test. A sabotage that reddens nothing does not say the code is
    /// good, it says the test is missing.
    /// </remarks>
    [Fact]
    public void ListFiles_OrdersBeforeItCaps_NotAfter()
    {
        var code = ConventionCoverageTests.CodeOnly(Path.Combine(
            RepoRoot(), "Inferpal.Core", "Services", "Tools", "ListFilesTool.cs"));

        // WITNESS: this really is the site that caps the walk.
        Assert.Contains("Take(limit + 1)", code, StringComparison.Ordinal);

        var order = code.IndexOf("OrderBy(f => f, StringComparer.Ordinal)", StringComparison.Ordinal);
        var take  = code.IndexOf("Take(limit + 1)", StringComparison.Ordinal);
        Assert.True(order >= 0, "the listing caps with no defined order: which files the model sees "
                              + "then depends on the volume, and \"first\" means nothing.");
        Assert.True(order < take, "the order is fixed AFTER the cap: the listing looks tidy, and the "
                                + "subset is still the one the file system chose.");
    }

    [Fact]
    public void EveryCappedScanGoesThroughTheFunnel()
    {
        // The sites that cap a prefix of the walk must go through ScanCoverage.Take: that is where
        // the order is decided, once, for all of them.
        var root = RepoRoot();
        foreach (var rel in new[]
                 {
                     Path.Combine("Inferpal.Core", "Services", "Tools", "AnalyzeImpactTool.cs"),
                     Path.Combine("Inferpal.Core", "Services", "Tools", "TraceDependencyTool.cs"),
                     Path.Combine("Inferpal.Core", "Services", "Tools", "NexusIntelligenceTool.cs"),
                     Path.Combine("Inferpal.Core", "Services", "Bench", "WorkspaceSymbolScanner.cs"),
                 })
        {
            var code = ConventionCoverageTests.CodeOnly(Path.Combine(root, rel));
            Assert.Contains("ScanCoverage.Take", code, StringComparison.Ordinal);
        }
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "README.md")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
