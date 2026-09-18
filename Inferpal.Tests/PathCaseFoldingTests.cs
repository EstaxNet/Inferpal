using System.Collections.Generic;
using System.IO;
using System.Linq;
using Inferpal.Services;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// "Do these two paths mean the same file?" — one question, and the repository answered it TWICE,
/// differently.
/// </summary>
/// <remarks>
/// <para>
/// ⚠ <c>OpenDocumentOverlay</c> said <c>IsLinux() ? Ordinal : OrdinalIgnoreCase</c>, with the right
/// reason written down: Windows and macOS (default APFS, and HFS+ before it) fold case, Linux does
/// not. Four other sites — <c>PathSanitizer</c>, <c>SessionManager</c>, <c>TaskProposal</c>,
/// <c>BackgroundTaskToolRegistry</c> — each carried their own private copy of
/// <c>IsWindows() ? OrdinalIgnoreCase : Ordinal</c>, which says the opposite <b>about macOS</b>.
/// Both cannot be true, and the divergence is <b>invisible on Windows and on Linux</b>: it shows
/// only on the leg that sees what the other two cannot.
/// </para>
/// <para>
/// ⚠ <b>And six path-keyed collections held the property in no way at all</b>, two of which decide
/// a write: <c>apply_edits</c> (its <c>current</c>/<c>original</c> tables) and
/// <c>FileHistoryService</c> (the change set <c>/undo-run</c> replays). Under Linux, where
/// <c>A.cs</c> and <c>a.cs</c> are two files, the first applied the edit to one file's content and
/// wrote it under the other's name; the second never recorded the second file, so undo could no
/// longer give it back.
/// </para>
/// <para>
/// ⚠ <b>The shape of the assertion is the point.</b> Pinning one answer outright — "these two paths
/// are equal" — would measure the platform: green on Windows, red on Linux, for a product that is
/// correct on both. What is checked here is the <b>contract</b>: the comparer folds case if and only
/// if this platform's file system folds it. That is checkable on all four legs.
/// </para>
/// </remarks>
public class PathCaseFoldingTests
{
    /// <summary>True where the platform's default file system folds case.</summary>
    private static bool FoldsCase => !OperatingSystem.IsLinux();

    [Fact]
    public void TheComparerFoldsCaseExactlyWhereTheFileSystemDoes()
    {
        Assert.Equal(FoldsCase, PathComparer.Default.Equals(@"/w/A.cs", @"/w/a.cs"));
        Assert.Equal(FoldsCase, string.Equals(@"/w/A.cs", @"/w/a.cs", PathComparer.Comparison));
    }

    [Fact]
    public void TwoPathsThatDifferForReal_AreNeverTheSame()
    {
        // Reference arm: folding case must not fold anything else.
        Assert.False(PathComparer.Default.Equals(@"/w/A.cs", @"/w/B.cs"));
        Assert.False(PathComparer.Default.Equals(@"/w/A.cs", @"/w/sub/A.cs"));
    }

    [Fact]
    public void ACollectionKeyedByPath_AgreesWithTheComparer()
    {
        // The defect's real shape: a table whose key is a path.
        var byPath = new Dictionary<string, string>(PathComparer.Default) { [@"/w/A.cs"] = "first" };
        byPath[@"/w/a.cs"] = "second";

        Assert.Equal(FoldsCase ? 1 : 2, byPath.Count);
    }

    /// <summary>
    /// The DECISION about macOS, pinned where it is readable from any platform.
    /// </summary>
    /// <remarks>
    /// ⚠ A SOURCE assertion, and its absence was the hole: flipping the funnel's answer about macOS
    /// reddens <b>no</b> test from a Windows box, since both formulas give the same result there.
    /// That is precisely what let the two answers coexist for months. The predicate must therefore
    /// be readable, and readable as <c>IsLinux()</c>: the shape that puts macOS on the side of the
    /// systems that fold case.
    /// </remarks>
    [Fact]
    public void TheDecisionAboutMacOS_IsWrittenDownAndReadable()
    {
        var code = ConventionCoverageTests.CodeOnly(Path.Combine(
            RepoRoot(), "Inferpal.Core", "Services", "PathComparer.cs"));

        // WITNESS: this really is the file that decides.
        Assert.Contains("StringComparer", code, StringComparison.Ordinal);

        Assert.Contains("IsLinux()", code, StringComparison.Ordinal);
        Assert.DoesNotContain("IsWindows()", code, StringComparison.Ordinal);
    }

    // ── Une question, un lecteur ─────────────────────────────────────────────

    [Fact]
    public void NoSiteAnswersTheQuestionOnItsOwn()
    {
        // ⚠ Five sites re-derived the expression privately, and two of them did not say the same
        // thing. This repository's failure mode is ENUMERATION: what gets copied drifts.
        var offenders = new List<string>();
        var scanned   = 0;

        foreach (var file in ConventionCoverageTests.CoreSources("Services")
                     .Concat(ConventionCoverageTests.ViewModelSources()))
        {
            var code = ConventionCoverageTests.CodeOnly(file);
            scanned++;
            // The funnel itself, and ONE named exemption with its reason: the case of ENVIRONMENT
            // VARIABLE NAMES is not that of paths. `PATH` and `path` are two distinct variables
            // under Linux whatever the volume — a property of the system, not of the FILE system.
            // Same shape, different question.
            if (file.EndsWith("PathComparer.cs", StringComparison.Ordinal)
             || file.EndsWith("ShellStateProtocol.cs", StringComparison.Ordinal)) continue;

            // The two shapes of the question, written by hand.
            if (code.Contains("IsWindows() ? StringComparer.OrdinalIgnoreCase", StringComparison.Ordinal)
             || code.Contains("IsWindows() ? StringComparison.OrdinalIgnoreCase", StringComparison.Ordinal)
             || code.Contains("IsLinux() ? StringComparer.Ordinal", StringComparison.Ordinal)
             || code.Contains("IsLinux() ? StringComparison.Ordinal", StringComparison.Ordinal))
                offenders.Add(Path.GetFileName(file));
        }

        // WITNESS: the rule is worth nothing unless it really read some sources.
        Assert.True(scanned >= 100, $"Only {scanned} source(s) read: the rule measures nothing any more.");

        Assert.True(offenders.Count == 0,
            "Path case folding is re-derived on the spot instead of going through PathComparer — "
            + "that is how two sites came to say the opposite of each other about macOS:"
            + Environment.NewLine + "  " + string.Join(Environment.NewLine + "  ", offenders));
    }

    /// <summary>
    /// The comparisons that decide a path IDENTITY, as opposed to those that are tolerant on
    /// purpose.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>Named assertions, not a rule</b>: the discriminator is semantic. Four sites fold case
    /// <b>deliberately</b> and must go on doing so — excluded folder names (<c>bin</c>, <c>obj</c>,
    /// <c>node_modules</c>: a <c>BIN</c> must stay excluded), <c>@Docs</c> URLs (a host is
    /// case-insensitive), the diff anchor suffix, and the <c>/tests/</c> heuristic of
    /// <c>analyze_impact</c>. A scan rule would live off those exemptions, which this repository
    /// forbids itself.
    /// ⚠ The site that stings is <c>analyze_impact</c>: skipping the target file in its own scan
    /// for dependants is an identity, and folding it dropped a <b>real</b> dependant on Linux —
    /// with no cap and no read failure, hence invisible to the whole coverage machinery.
    /// </remarks>
    [Theory]
    [InlineData("Inferpal.Core", "Services", "Tools", "AnalyzeImpactTool.cs", "file.Equals(targetFile")]
    [InlineData("Inferpal.Core", "Services", "Lsp", "CSharpSemanticIndex.cs", "path.StartsWith(root")]
    [InlineData("Inferpal.Core", "Services", "WorkspaceScan.cs", "path.StartsWith(r,")]
    [InlineData("Inferpal.Core", "Services", "Tools", "GetOpenEditorsTool.cs", "string.Equals(path, activePath")]
    public void APathIDENTITY_IsDecidedByTheSharedRule(string a, string b, string c, string d, string? site = null)
    {
        // The third InlineData carries only three path segments: the fourth then holds the site.
        var parts = site is null ? new[] { a, b, c } : new[] { a, b, c, d };
        var probe = site ?? d;

        var code = ConventionCoverageTests.CodeOnly(Path.Combine(RepoRoot(), Path.Combine(parts)));

        // WITNESS: the comparison this case targets still exists, in this shape.
        var at = code.IndexOf(probe, StringComparison.Ordinal);
        Assert.True(at >= 0, $"\"{probe}\" is nowhere to be found: this case would have measured nothing.");

        var line = code[at..(code.IndexOf('\n', at) is var e && e > at ? e : code.Length)];
        Assert.Contains("PathComparer.Comparison", line, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Inferpal.Core", "Services", "Tools", "ApplyEditsTool.cs")]
    [InlineData("Inferpal.Core", "Services", "Execution", "FileHistoryService.cs")]
    [InlineData("Inferpal.Core", "Services", "Lsp", "CSharpSemanticIndex.cs")]
    [InlineData("Inferpal.Core", "Services", "Rag", "ProjectIndexService.cs")]
    [InlineData("Inferpal.Core", "Services", "ProjectMapService.cs")]
    [InlineData("Inferpal.Core", "Services", "Tools", "AnalyzeImpactTool.cs")]
    public void ACollectionKeyedByAPath_UsesTheSharedComparer(params string[] parts)
    {
        var code = ConventionCoverageTests.CodeOnly(Path.Combine(RepoRoot(), Path.Combine(parts)));

        Assert.Contains("PathComparer", code, StringComparison.Ordinal);
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
