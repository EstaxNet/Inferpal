using System.IO;
using System.Linq;
using Inferpal.Services;
using Inferpal.Services.Tools;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A choice among files a directory listing or a walk returns — "the" solution, "the" project, "the" marker — is made in
/// a fixed order, never in the order the file system hands them out.
/// </summary>
/// <remarks>
/// That order is arbitrary under POSIX: with two solutions, <c>get_diagnostics</c> built one or the other depending on
/// the machine, and Smart Fix, <c>run_tests</c>, the test-file resolver, the project map and the contract scan of
/// <c>/doc</c> each picked their first file the same way. ⚠ Measurable on Windows too: NTFS lists names case-INsensitively
/// (<c>alpha</c> before <c>Zeta</c>), the ordinal order puts <c>Zeta</c> first.
/// </remarks>
public sealed class FileSystemOrderChoiceTests : IDisposable
{
    private readonly string _root =
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"inferpal-fsorder-{Guid.NewGuid():N}")).FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void ShallowestFirst_ThenOrdinal()
    {
        var sep = Path.DirectorySeparatorChar;
        Assert.Equal([$"r{sep}Zeta.sln", $"r{sep}alpha.sln", $"r{sep}a{sep}Aaa.sln"],
                     WorkspaceScan.ShallowestFirst([$"r{sep}a{sep}Aaa.sln", $"r{sep}alpha.sln", $"r{sep}Zeta.sln"]));
    }

    [Fact]
    public void GetDiagnostics_BuildsTheSameSolution_OnEveryMachine()
    {
        File.WriteAllText(Path.Combine(_root, "alpha.sln"), "");
        File.WriteAllText(Path.Combine(_root, "Zeta.sln"), "");
        Directory.CreateDirectory(Path.Combine(_root, "a"));
        File.WriteAllText(Path.Combine(_root, "a", "Aaa.sln"), "");

        Assert.Equal(Path.Combine(_root, "Zeta.sln"), GetDiagnosticsTool.FindProjectFile(_root));
    }

    // ── The rule: no First / FirstOrDefault / Take straight off a directory listing or a walk ─────────────────────

    private static readonly string[] Choosers = ["First", "FirstOrDefault", "Single", "SingleOrDefault", "Last", "LastOrDefault", "Take"];
    private static readonly string[] Orderings = ["Order", "OrderBy", "OrderByDescending", "OrderDescending", "ShallowestFirst"];
    private static readonly string[] Listings = ["GetFiles", "EnumerateFiles", "GetDirectories", "EnumerateDirectories",
                                                 "GetFileSystemEntries", "EnumerateFileSystemEntries"];

    /// <summary>
    /// Exempt by name, with the reason. A heuristic that every entry satisfies alike: is this folder a NuGet cache
    /// (the first fifty packages hold a <c>.nupkg</c>)? Sorting thousands of package folders to decide it is all cost.
    /// </summary>
    private static readonly string[] Exempt = ["WorkspaceScan.cs:Take"];

    private static string Name(ExpressionSyntax e) => e switch
    {
        MemberAccessExpressionSyntax m => m.Name.Identifier.Text,
        IdentifierNameSyntax i         => i.Identifier.Text,
        GenericNameSyntax g            => g.Identifier.Text,
        _                              => "",
    };

    /// <summary>Whether the receiver chain of a chooser reaches a listing before any ordering.</summary>
    private static bool ChoosesInListingOrder(InvocationExpressionSyntax chooser)
    {
        ExpressionSyntax? e = (chooser.Expression as MemberAccessExpressionSyntax)?.Expression;
        while (e is InvocationExpressionSyntax inv)
        {
            var name = Name(inv.Expression);
            if (Orderings.Contains(name)) return false;
            if (Listings.Contains(name)
                && inv.Expression is MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax { Identifier.Text: "Directory" or "WorkspaceScan" } })
                return true;
            e = (inv.Expression as MemberAccessExpressionSyntax)?.Expression;
        }
        return false;
    }

    [Fact]
    public void NoChoiceIsMade_InTheOrderTheFileSystemLists()
    {
        var repo  = ConversationPersistenceSilenceTests.RepoRoot();
        var files = new[] { "Inferpal.Core", "Inferpal", "Inferpal.Host" }
            .SelectMany(p => Directory.EnumerateFiles(Path.Combine(repo, p), "*.cs", SearchOption.AllDirectories))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .ToList();
        // WITNESS: the scan reads the product's sources — a moved folder would leave it judging nothing, green.
        Assert.True(files.Count > 300, $"Only {files.Count} source file(s) read: the scan no longer sees the product.");

        var ordered = 0;
        var offenders = new List<string>();
        foreach (var path in files)
        {
            var root = CSharpSyntaxTree.ParseText(File.ReadAllText(path), path: path).GetRoot();
            foreach (var inv in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                var name = Name(inv.Expression);
                if (!Choosers.Contains(name)) continue;
                if (ChoosesInListingOrder(inv))
                {
                    if (!Exempt.Contains($"{Path.GetFileName(path)}:{name}"))
                        offenders.Add($"{Path.GetRelativePath(repo, path)}:{inv.GetLocation().GetLineSpan().StartLinePosition.Line + 1} {inv}");
                }
                else if (inv.ToString().Contains("Order(StringComparer.Ordinal)") || inv.ToString().Contains("ShallowestFirst"))
                {
                    ordered++;
                }
            }
        }

        // WITNESS: the compliant shape is recognised — the sites this rule was written for read as ordered.
        Assert.True(ordered >= 4, $"Only {ordered} ordered choice(s) recognised: the rule no longer reads the shape it checks.");
        Assert.True(offenders.Count == 0,
            "A choice made in the order the file system lists (sort it, or WorkspaceScan.ShallowestFirst):\n" + string.Join("\n", offenders));
    }
}
