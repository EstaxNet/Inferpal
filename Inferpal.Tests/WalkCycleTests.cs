using System.IO;
using System.Linq;
using Inferpal.Services;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A directory link pointing back into the tree used to kill the product's only walk funnel.
/// </summary>
/// <remarks>
/// <para>
/// ⚠ <b>Measured, and it was fatal, not noisy.</b> With a junction at <c>src/deep/loop</c> pointing
/// at the workspace root, <c>WorkspaceScan.EnumerateFiles</c> threw
/// <see cref="OutOfMemoryException"/> — and on another run had not returned after more than ten
/// minutes. <c>IgnoreInaccessible</c> is what makes it fatal: at the bottom of the cycle the
/// per-directory error is swallowed and the enumeration keeps queueing directories. This funnel
/// serves the index, <c>search_in_files</c>, <c>list_files</c>, the mentions and every analysis
/// tool, so the blast radius was everything.
/// </para>
/// <para>
/// ⚠ <b>Bounding the depth was measured and rejected.</b> <c>MaxRecursionDepth = 64</c> stops the
/// crash but walks the cycle over and over: <b>43 paths for 2 real files</b>, i.e. the same file
/// indexed twenty-one times under twenty-one paths. A crash traded for a poisoned index.
/// </para>
/// <para>
/// ⚠ <b>And the fix has a cost that must be said.</b> Not following links also means a legitimately
/// linked source folder is not read (measured: 2 files instead of 3). That is why
/// <see cref="WorkspaceScan.FirstWalkGap"/> reports two distinct reasons rather than one: "cannot
/// be listed" and "is a link, not followed" send the reader to two different places.
/// </para>
/// <para>
/// The real shapes: a <c>latest</c> symlink, a Docker bind mount inside the repository, a junction
/// into a Windows profile.
/// </para>
/// </remarks>
public sealed class WalkCycleTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "inferpal-tests", $"cycle-{Guid.NewGuid():N}");
    private readonly string _link;
    private readonly string _shared;
    private readonly string _legitLink;
    private readonly string _fileLink;

    public WalkCycleTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src", "deep"));
        File.WriteAllText(Path.Combine(_root, "src", "A.cs"), "class A { }");
        File.WriteAllText(Path.Combine(_root, "src", "deep", "B.cs"), "class B { }");

        // Le cycle : un lien de dossier qui renvoie vers la racine.
        _link = Path.Combine(_root, "src", "deep", "loop");
        try { Directory.CreateSymbolicLink(_link, _root); } catch { }

        // A LEGITIMATE link, outside the tree: that is the fix's cost, and it gets measured too.
        _shared = Path.Combine(Path.GetTempPath(), "inferpal-tests", $"shared-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_shared);
        File.WriteAllText(Path.Combine(_shared, "Shared.cs"), "class Shared { }");
        _legitLink = Path.Combine(_root, "src", "linked");
        try { Directory.CreateSymbolicLink(_legitLink, _shared); } catch { }

        // A linked FILE: it carries the same attribute as a linked folder, and it does not loop.
        _fileLink = Path.Combine(_root, "src", "Linked.cs");
        try { File.CreateSymbolicLink(_fileLink, Path.Combine(_shared, "Shared.cs")); } catch { }
    }

    public void Dispose()
    {
        // ⚠ The LINKS first, always: a recursive delete that walks through them takes their targets
        // with it — here the working root itself, and the shared folder.
        try { if (File.Exists(_fileLink)) File.Delete(_fileLink); } catch { }
        foreach (var l in new[] { _link, _legitLink })
            try { if (Directory.Exists(l)) Directory.Delete(l); } catch { }
        try { Directory.Delete(_root, recursive: true); } catch { }
        try { Directory.Delete(_shared, recursive: true); } catch { }
    }

    /// <summary>
    /// The cycle really exists. Without this witness, everything below would be green on a machine
    /// where creating links is refused — and would measure nothing at all.
    /// </summary>
    private void AssertTheCycleExists()
    {
        Assert.True(Directory.Exists(_link),
            "The cyclic link could not be created (symbolic-link creation privilege): this test is "
            + "UNDECIDED, not green.");
        Assert.True(File.Exists(Path.Combine(_link, "src", "A.cs")),
            "The link exists but does not loop back to the root: the witness does not discriminate.");
    }

    [Fact]
    public void TheWalk_OnACycle_TerminatesAndCountsEachFileOnce()
    {
        AssertTheCycleExists();

        // A POSITIVE assertion on the count: "it does not crash" would also pass on a walk that
        // returns 43 paths for 2 files, which is the fix that was rejected.
        var files = WorkspaceScan.EnumerateFiles(_root, "*.cs", _root).ToList();

        Assert.Equal(1, files.Count(f => Path.GetFileName(f) == "A.cs"));
        Assert.Equal(1, files.Count(f => Path.GetFileName(f) == "B.cs"));
        Assert.DoesNotContain(files, f => f.Contains("loop", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// ⚠ The fix's cost must stop at FOLDERS. A linked file carries exactly the same reparse-point
    /// attribute as a linked folder, and the obvious way of writing the fix —
    /// <c>AttributesToSkip = ReparsePoint</c> — threw it away too: measured, <c>Linked.cs</c> had
    /// vanished from the walk with no gap returned, since the detector only looks at folders. A
    /// linked file cannot loop: that was data lost for nothing.
    /// </summary>
    [Fact]
    public void TheWalk_ReadsALinkedFILE_BecauseOnlyADirectoryCanLoop()
    {
        Assert.True(File.Exists(_fileLink), "file link not created: test UNDECIDED");

        var files = WorkspaceScan.EnumerateFiles(_root, "*.cs", _root).ToList();

        Assert.Contains(files, f => Path.GetFileName(f) == "Linked.cs");
        // The sandbox's full count: A.cs, B.cs and the linked file — and NOTHING else, so neither
        // the cycle nor the contents of the linked folder.
        Assert.Equal(3, files.Count);
    }

    [Fact]
    public void TheDetector_NamesTheLink_WithItsOwnReason()
    {
        AssertTheCycleExists();

        var gap = WorkspaceScan.FirstWalkGap(_root, _root);

        Assert.NotNull(gap);
        Assert.Equal(WorkspaceScan.WalkGapKind.NotFollowed, gap!.Value.Kind);
        // And the path stays READABLE: before the fix, the detector descended until Windows refused
        // the path, then returned its 895 characters of repeated `src\deep\loop`.
        Assert.True(gap.Value.Folder.Length < 60, $"chemin invraisemblable : {gap.Value.Folder}");
        Assert.Contains("loop", gap.Value.Folder);
    }

    [Fact]
    public void TheTwoReasons_RenderTwoDifferentSentences()
    {
        // Without this, a fix returning the same sentence for both causes would pass every other
        // test — and send the user to repair a permission on a link.
        var notFollowed = new WorkspaceScan.WalkGap("linked", WorkspaceScan.WalkGapKind.NotFollowed);
        var unlistable  = new WorkspaceScan.WalkGap("pgdata", WorkspaceScan.WalkGapKind.Unlistable);

        Assert.NotEqual(notFollowed.Sentence(), unlistable.Sentence());
        Assert.Equal(Inferpal.Localization.Strings.ScanFolderNotFollowed("linked"), notFollowed.Sentence());
        Assert.Equal(Inferpal.Localization.Strings.ScanFolderSkipped("pgdata"), unlistable.Sentence());
    }

    [Fact]
    public void TheWalk_DoesNotReadALegitimatelyLinkedFolder_AndTheDetectorSaysSo()
    {
        // ⚠ The fix's COST, asserted rather than discovered later: the contents of a linked folder
        // are not read. The product has to say so, and that is all it can do — following the link
        // means the cycle and the death of the walk.
        Assert.True(Directory.Exists(_legitLink), "legitimate link not created: test UNDECIDED");
        Assert.True(File.Exists(Path.Combine(_legitLink, "Shared.cs")), "the legitimate link does not point at the share");

        var files = WorkspaceScan.EnumerateFiles(_root, "*.cs", _root).ToList();

        Assert.DoesNotContain(files, f => Path.GetFileName(f) == "Shared.cs");

        // ⚠ Not a plain `NotNull`: the sabotage showed that a detector blind to links descends into
        // the cycle until the system refuses the path, then returns an "unlistable" gap — non-null,
        // so the test passed for the wrong reason. What is required is that a link be named AS a
        // link.
        var gap = WorkspaceScan.FirstWalkGap(_root, _root);
        Assert.NotNull(gap);
        Assert.Equal(WorkspaceScan.WalkGapKind.NotFollowed, gap!.Value.Kind);
    }
}
