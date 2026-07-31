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
