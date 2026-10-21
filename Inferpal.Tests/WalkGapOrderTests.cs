using System.IO;
using Inferpal.Services;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// The folder a walk names as its gap is the first in ordinal order, never the first the file system lists.
/// </summary>
/// <remarks>
/// ⚠ <see cref="WorkspaceScan.FirstWalkGap"/> names ONE folder, above a search, a listing or the index's coverage.
/// Under POSIX a directory's listing order is arbitrary (ext4 orders by hash), so with two gaps side by side the one
/// named changed from one run to the next on the same tree. NTFS lists names case-insensitively — <c>a</c> before
/// <c>B</c> — where ordinal order puts <c>B</c> first: that difference is what lets this test see the file system's
/// order on Windows as well.
/// </remarks>
public sealed class WalkGapOrderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "inferpal-tests", $"gap-order-{Guid.NewGuid():N}");
    private readonly string _target = Path.Combine(Path.GetTempPath(), "inferpal-tests", $"gap-target-{Guid.NewGuid():N}");
    private readonly string _lower;
    private readonly string _upper;

    public WalkGapOrderTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        Directory.CreateDirectory(_target);
        _lower = Path.Combine(_root, "src", "a");
        _upper = Path.Combine(_root, "src", "B");
        try { Directory.CreateSymbolicLink(_lower, _target); } catch { }
        try { Directory.CreateSymbolicLink(_upper, _target); } catch { }
    }

    public void Dispose()
    {
        // The links first: a recursive delete that walks through them would take their target with it.
        foreach (var l in new[] { _lower, _upper })
            try { if (Directory.Exists(l)) Directory.Delete(l); } catch { }
        try { Directory.Delete(_root, recursive: true); } catch { }
        try { Directory.Delete(_target, recursive: true); } catch { }
    }

    [Fact]
    public void TwoLinkedFolders_TheOneNamed_IsTheFirstInOrdinalOrder()
    {
        Assert.True(Directory.Exists(_lower) && Directory.Exists(_upper),
            "The links could not be created (symbolic-link creation privilege): this test is UNDECIDED, not green.");

        var gap = WorkspaceScan.FirstWalkGap(_root, _root);

        Assert.NotNull(gap);
        Assert.Equal(WorkspaceScan.WalkGapKind.NotFollowed, gap!.Value.Kind);
        Assert.Equal(Path.Combine("src", "B"), gap.Value.Folder);
    }
}
