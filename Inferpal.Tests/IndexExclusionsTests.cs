using System.IO;
using Inferpal.Services.Rag;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// What the semantic index refuses to read (roadmap §19). The profile may lengthen the list; the
/// tests below exist to prove it can never shorten it.
/// </summary>
public class IndexExclusionsTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "idx_excl_root");

    private static string P(params string[] parts) =>
        Path.Combine(new[] { Root }.Concat(parts).ToArray());

    [Theory]
    [InlineData("obj")]
    [InlineData("bin")]
    [InlineData(".git")]
    [InlineData("node_modules")]
    [InlineData(".inferpal")]
    public void BuiltInDirectories_AreAlwaysExcluded(string dir)
    {
        Assert.True(IndexExclusions.IsExcluded(P("src", dir, "File.cs"), Root));
    }

    [Fact]
    public void NothingIsExcludedByDefault()
    {
        Assert.False(IndexExclusions.IsExcluded(P("src", "File.cs"), Root));
    }

    [Fact]
    public void APlainName_MatchesThatDirectoryAtAnyDepth()
    {
        string[] extra = ["vendor"];

        Assert.True(IndexExclusions.IsExcluded(P("vendor", "lib.cs"), Root, extra));
        Assert.True(IndexExclusions.IsExcluded(P("src", "vendor", "lib.cs"), Root, extra));
        Assert.False(IndexExclusions.IsExcluded(P("src", "vendored", "lib.cs"), Root, extra));
    }

    [Fact]
    public void GlobsMatchAtAnyDepth()
    {
        string[] extra = ["**/*.generated.cs"];

        Assert.True(IndexExclusions.IsExcluded(P("a", "b", "Model.generated.cs"), Root, extra));
        Assert.False(IndexExclusions.IsExcluded(P("a", "b", "Model.cs"), Root, extra));
    }

    [Fact]
    public void DirectoryGlobs_CoverEveryDepthBelowThem()
    {
        string[] extra = ["docs/generated/**"];

        Assert.True(IndexExclusions.IsExcluded(P("docs", "generated", "api.md"), Root, extra));
        Assert.True(IndexExclusions.IsExcluded(P("docs", "generated", "deep", "api.md"), Root, extra));
        Assert.False(IndexExclusions.IsExcluded(P("docs", "hand-written.md"), Root, extra));
    }

    /// <summary>
    /// The profile is additive by construction: there is no syntax for "index this after all", and
    /// a repository writing one anyway gets an inert pattern rather than a re-included build output.
    /// </summary>
    [Theory]
    [InlineData("!bin")]
    [InlineData("-obj")]
    [InlineData("**")]
    public void NoPatternCanUnExcludeABuiltInDirectory(string pattern)
    {
        Assert.True(IndexExclusions.IsExcluded(P("bin", "Debug", "App.dll.cs"), Root, [pattern]));
    }

    /// <summary>
    /// The patterns come from a clone and run once per candidate file. A glob that translates to a
    /// backtracking regex must cost a bounded amount and let the pass continue — the indexing loop
    /// is background work, not a place to hang. Same reflex as the permission overlay's regexes.
    /// </summary>
    [Fact]
    public void APathologicalGlob_DoesNotHangTheIndexingPass()
    {
        var evil = string.Concat(Enumerable.Repeat("**a", 20));
        var path = P("src", new string('a', 180) + "b.cs");

        Inferpal.Services.Diagnostics.Clear();
        var start = System.Diagnostics.Stopwatch.StartNew();
        var excluded = IndexExclusions.IsExcluded(path, Root, [evil]);
        start.Stop();

        Assert.False(excluded);                                  // timed out ⇒ indexed, never hidden
        Assert.True(start.Elapsed < TimeSpan.FromSeconds(2), $"took {start.Elapsed}");

        // Proof the timeout is what stopped it — without the bound this line never runs at all.
        Assert.Contains(Inferpal.Services.Diagnostics.Snapshot(),
            e => e.Context == "IndexExclusions" && e.Detail.Contains("timed out"));
    }

    [Fact]
    public void APathOutsideTheRoot_IsNotMatchedByRelativePatterns()
    {
        var outside = Path.Combine(Path.GetTempPath(), "elsewhere", "vendor", "lib.cs");

        Assert.False(IndexExclusions.IsExcluded(outside, Root, ["vendor"]));
    }
}
