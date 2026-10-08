using System.IO;
using Inferpal.Services.Commands;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// The repository brief of <c>/onboard context</c> cuts its lists in ORDINAL order, and a sampled folder says how many
/// children it did not list.
/// </summary>
/// <remarks>
/// The brief caps the layout at 40 entries, the sample at 12 folders and each folder at 8 children — in the order the
/// file system handed them out, arbitrary under POSIX: which entries survived the cut depended on the machine that wrote
/// <c>.inferpal/context.md</c>, a file committed with the repository and read as the system prompt of every session. And
/// a folder of 200 files showed 8 names with nothing after them, read as the whole folder.
/// ⚠ Measurable on Windows too: NTFS hands names out in case-INsensitive order (<c>alpha</c> before <c>Zeta</c>), the
/// ordinal order puts <c>Zeta</c> first — a fixture mixing the two cases tells the sorted list from the raw one.
/// </remarks>
public sealed class RepoBriefSortedSampleTests : IDisposable
{
    private readonly string _root =
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"inferpal-brief-sort-{Guid.NewGuid():N}")).FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static Task<(string, int)> Git(string args, CancellationToken _) => Task.FromResult(("feat: x", 0));

    private Task<string> Brief() => OnboardCommandHandler.BuildRepoBriefAsync(_root, Git, CancellationToken.None);

    [Fact]
    public async Task ASampledFolder_ListsItsFirstChildrenInOrdinalOrder_AndCountsTheRest()
    {
        var src = Directory.CreateDirectory(Path.Combine(_root, "src")).FullName;
        foreach (var name in new[] { "alpha.cs", "beta.cs", "gamma.cs", "delta.cs", "epsilon.cs", "zeta.cs", "eta.cs", "theta.cs", "iota.cs" })
            File.WriteAllText(Path.Combine(src, name), "// x");
        File.WriteAllText(Path.Combine(src, "Zeta.md"), "x");   // upper case: ordinal sorts it first, NTFS does not

        var brief = await Brief();

        Assert.Contains("- `src/` → Zeta.md, alpha.cs, beta.cs, delta.cs, epsilon.cs, eta.cs, gamma.cs, iota.cs, … +2\n",
                        brief, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFolderThatFits_HasNoCount()
    {
        // Reference arm: a folder listed whole says nothing more.
        var src = Directory.CreateDirectory(Path.Combine(_root, "src")).FullName;
        File.WriteAllText(Path.Combine(src, "b.cs"), "// x");
        File.WriteAllText(Path.Combine(src, "A.cs"), "// x");

        var brief = await Brief();

        Assert.Contains("- `src/` → A.cs, b.cs\n", brief, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheLayoutAndTheSampledFolders_AreCutInOrdinalOrder()
    {
        // 13 folders: the sample stops at 12. Ordinal order puts the upper-case "Zoo" first and "zz" last.
        foreach (var name in new[] { "aa", "bb", "cc", "dd", "ee", "ff", "gg", "hh", "ii", "jj", "kk", "zz", "Zoo" })
            File.WriteAllText(Path.Combine(Directory.CreateDirectory(Path.Combine(_root, name)).FullName, "f.cs"), "// x");

        var brief = await Brief();

        Assert.Contains("- `Zoo/` → f.cs", brief, StringComparison.Ordinal);
        Assert.Contains("(the sample stops at 12 folders): zz\n", brief, StringComparison.Ordinal);
        Assert.True(brief.IndexOf("- Zoo/", StringComparison.Ordinal) < brief.IndexOf("- aa/", StringComparison.Ordinal),
                    "the layout is not in ordinal order");
    }
}
