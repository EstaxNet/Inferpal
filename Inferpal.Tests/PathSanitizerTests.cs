using System.IO;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// Workspace confinement — the boundary every file-writing tool relies on. The interesting case
/// is not the textual one (<c>..\..\etc</c>, which <c>Path.GetFullPath</c> already collapses) but
/// the link one: a junction planted inside the workspace whose target is outside it.
/// </summary>
public class PathSanitizerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "inferpal-paths-" + Guid.NewGuid().ToString("N"));
    private readonly string _outside = Path.Combine(Path.GetTempPath(), "inferpal-out-" + Guid.NewGuid().ToString("N"));

    public PathSanitizerTests()
    {
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_outside);
    }

    public void Dispose()
    {
        foreach (var dir in new[] { _root, _outside })
            try { Directory.Delete(dir, recursive: true); } catch { }
    }

    // ── Textual confinement ────────────────────────────────────────────────────

    [Fact]
    public void PathInsideTheRoot_IsAccepted()
        => PathSanitizer.AssertUnderRoot(Path.Combine(_root, "src", "a.cs"), _root);

    [Fact]
    public void TheRootItself_IsAccepted()
        => PathSanitizer.AssertUnderRoot(_root, _root);

    [Fact]
    public void PathOutsideTheRoot_IsRejected()
        => Assert.Throws<ArgumentException>(
            () => PathSanitizer.AssertUnderRoot(Path.Combine(_outside, "a.cs"), _root));

    [Fact]
    public void SiblingSharingThePrefix_IsRejected()
    {
        // "C:\proj\src_other" must not pass because it starts with "C:\proj\src".
        var sibling = _root + "_other";
        Assert.Throws<ArgumentException>(() => PathSanitizer.AssertUnderRoot(Path.Combine(sibling, "a.cs"), _root));
    }

    [Fact]
    public void TraversalSegments_AreCollapsedBeforeTheCheck()
    {
        var escaping = PathSanitizer.Sanitize(Path.Combine(_root, "..", "elsewhere", "a.cs"));
        Assert.Throws<ArgumentException>(() => PathSanitizer.AssertUnderRoot(escaping, _root));
    }

    [Fact]
    public void NoWorkspaceRoot_DisablesTheCheck()
        => PathSanitizer.AssertUnderRoot(Path.Combine(_outside, "a.cs"), null);

    // ── Link confinement ───────────────────────────────────────────────────────

    [Fact]
    public void DirectoryLinkPointingOutside_IsRejected()
    {
        // Path.GetFullPath does not follow links, so a textual prefix check would happily
        // accept <root>\escape\secret.txt while the write lands outside the workspace.
        var link = Path.Combine(_root, "escape");
        if (!TryCreateDirectoryLink(link, _outside)) return;   // needs privileges/dev mode

        var through = Path.Combine(link, "secret.txt");

        Assert.Throws<ArgumentException>(() => PathSanitizer.AssertUnderRoot(through, _root));
    }

    [Fact]
    public void DirectoryLinkPointingInside_IsStillAccepted()
    {
        var target = Path.Combine(_root, "real");
        Directory.CreateDirectory(target);
        var link = Path.Combine(_root, "alias");
        if (!TryCreateDirectoryLink(link, target)) return;

        PathSanitizer.AssertUnderRoot(Path.Combine(link, "a.cs"), _root);
    }

    [Fact]
    public void ALinkedANCESTOR_StillResolvesToTheSameRoot()
    {
        // ⚠ The exact shape of the defect measured on the macOS CI leg (2026-09-10): there
        // `/var` is a link to `/private/var`, so the link is a DISTANT ANCESTOR of the workspace,
        // never its last component. ResolveLinkTarget only answers about the component you hand
        // it: on `<link>/ws` it returns null, and the old code concluded "not a link" and left the
        // path as it was — while the same folder reached by its real form compared under the other
        // name. The two no longer shared a prefix and a perfectly legitimate write was refused,
        // with the very message a user reported for an unrelated reason (issue #9).
        var realBase = Path.Combine(_outside, "real");
        var ws       = Path.Combine(realBase, "ws");
        Directory.CreateDirectory(ws);

        var linkBase = Path.Combine(_outside, "linked");
        if (!TryCreateDirectoryLink(linkBase, realBase)) return;   // needs privileges/dev mode

        // The root is given in its UNRESOLVED form (through the link), the target in its real
        // form — exactly what a current directory the kernel hands back already resolved produces.
        var rootThroughLink = Path.Combine(linkBase, "ws");
        var targetReal      = Path.Combine(ws, ".inferpal", "memory.md");

        PathSanitizer.AssertUnderRoot(targetReal, rootThroughLink);

        // And the other way round, which is the case of a user whose workspace IS under the link.
        PathSanitizer.AssertUnderRoot(Path.Combine(rootThroughLink, "a.cs"), ws);
    }

    [Fact]
    public void ALinkedAncestorDoesNotOpenTheSandbox()
    {
        // WITNESS: resolving more links must not widen what the sandbox accepts. A path that
        // genuinely leaves the workspace is still refused, ancestor link or not.
        var realBase = Path.Combine(_outside, "real2");
        var ws       = Path.Combine(realBase, "ws");
        Directory.CreateDirectory(ws);
        Directory.CreateDirectory(Path.Combine(realBase, "sibling"));

        var linkBase = Path.Combine(_outside, "linked2");
        if (!TryCreateDirectoryLink(linkBase, realBase)) return;

        Assert.Throws<ArgumentException>(() => PathSanitizer.AssertUnderRoot(
            Path.Combine(realBase, "sibling", "x.cs"), Path.Combine(linkBase, "ws")));
    }

    /// <summary>Creating a link needs Developer Mode or elevation on Windows; when it is not
    /// available the test degrades to a no-op rather than failing on the environment.</summary>
    private static bool TryCreateDirectoryLink(string link, string target)
    {
        try { Directory.CreateSymbolicLink(link, target); return true; }
        catch (Exception) { return false; }
    }

    // ── Sanitize ───────────────────────────────────────────────────────────────

    [Fact]
    public void Sanitize_StripsControlCharactersModelsInject()
        => Assert.EndsWith("a.cs", PathSanitizer.Sanitize(Path.Combine(_root, "a.cs") + "\0"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Sanitize_EmptyInput_Throws(string? raw)
        => Assert.Throws<ArgumentException>(() => PathSanitizer.Sanitize(raw));
}
