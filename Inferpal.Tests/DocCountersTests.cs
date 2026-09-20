using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Inferpal.Config;
using Inferpal.Services;
using Inferpal.Services.Docs;
using Inferpal.Services.Lsp;
using Inferpal.Services.Mcp;
using Inferpal.Services.Rag;
using Xunit;

namespace Inferpal.Tests;

// Hand-maintained counters in the README rot silently — these tests turn drift into a red
// build. The tool count is read from a real ToolRegistry (the same graph the host builds),
// the test count from this very assembly (xUnit discovery = [Fact]s + one case per
// [InlineData]; the suite uses no MemberData/ClassData, which this test also enforces —
// adding one would silently break the count).
//
// Fixing a drift is one command, not arithmetic — the tests rewrite the README themselves when
// INFERPAL_UPDATE_DOCS is set (snapshot-test convention):
//     $env:INFERPAL_UPDATE_DOCS=1; dotnet test --filter DocCounters
public class DocCountersTests
{
    private static bool UpdateMode =>
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("INFERPAL_UPDATE_DOCS"));

    /// <summary>
    /// Compares a counter against the README. In update mode the README is rewritten through
    /// <paramref name="rewrite"/> and the test passes; otherwise a drift fails with the exact
    /// command that fixes it.
    /// </summary>
    private static void AssertCounter(string docPath, int actual, int documented,
                                      Func<string, string> rewrite, string what)
    {
        if (actual == documented) return;

        if (UpdateMode)
        {
            // Preserve the BOM: every doc in this repo is UTF-8 *with* one, and File.WriteAllText
            // would drop it — a whole-file diff on a one-digit fix, and a header line that PS 5.1
            // then reads as "ï»¿# Inferpal".
            var bytes  = File.ReadAllBytes(docPath);
            var hasBom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
            File.WriteAllText(docPath, rewrite(File.ReadAllText(docPath)), new UTF8Encoding(hasBom));
            return;
        }

        Assert.Fail($"{Path.GetFileName(docPath)} states {documented} {what} but the code has {actual}. " +
                    "Fix it with: $env:INFERPAL_UPDATE_DOCS=1; dotnet test --filter DocCounters");
    }
    /// <summary>Repo root = first ancestor of the test bin folder containing README.md.</summary>
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "README.md")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    [Fact]
    public void Readme_BuiltInToolCount_MatchesTheRegistry()
    {
        // Same graph the host builds, with MCP/custom tools left at their empty defaults,
        // so Definitions is exactly the built-in set.
        var config   = new InferpalConfig();
        var client   = new FakeInferenceProvider();
        var editor   = new NullEditorSurface();
        var approval = new NoopApproval();
        var index    = new ProjectIndexService(client, config, new LspSemanticProvider());
        var registry = new ToolRegistry(editor, approval, config, index, client,
                                        new ProjectMapService(editor), new McpToolService(config, approval),
                                        new DocsIndexService(client, config), new OpenDocumentOverlay(),
                                        new NullDebugSession());

        var actual = registry.Definitions.Count;

        // ⚠ The claim is NOT written once. It was guarded in one file, then two, then three —
        // and each time the copy left outside the guard was the one that went wrong: the
        // Marketplace description had drifted to 26 while the product grew to 28 (revue
        // pre-1.6.0 §3.4), then the listing body, then the Quick facts table
        // of docs/README.md, still at 26 two releases later. Naming the files one by one is what
        // produced that series, so this test no longer names them: it SCANS every living doc and
        // checks EVERY occurrence. A fourth copy added tomorrow is guarded the day it is written.
        var claimed = 0;
        foreach (var doc in LivingDocs())
        {
            var text = File.ReadAllText(doc);
            foreach (Match m in Regex.Matches(text, ToolClaim))   // "N built-in tools" / "agent tools" / "ITool implementations"
            {
                claimed++;
                AssertCounter(doc, actual, int.Parse(m.Groups[1].Value),
                              t => Regex.Replace(t, ToolClaim, $"{actual} built-in"),
                              "built-in tools");
                if (UpdateMode) break;   // the rewrite above fixed every occurrence of this file at once
            }
        }

        // A guard that scans can also pass by finding nothing. The three copies a release depends
        // on — the README, the VSIX description, the Marketplace listing — must actually be there.
        foreach (var required in new[] { "README.md", Path.Combine("Inferpal", "source.extension.vsixmanifest"),
                                         "MARKETPLACE.md" })   // MARKETPLACE.md: private repo only
        {
            var path = Path.Combine(RepoRoot(), required);
            if (!File.Exists(path)) continue;
            Assert.True(Regex.IsMatch(File.ReadAllText(path), ToolClaim),
                        $"{required} no longer states 'N built-in tools' — the counter lost its home.");
        }

        Assert.True(claimed >= 3, $"Only {claimed} copies of the tool count were found; the scan is not looking where the claim lives.");
    }

    /// <summary>"28 built-in tools", "28 built-in agent tools", "28 built-in ITool implementations".</summary>
    private const string ToolClaim = @"(\d+) built-in";

    /// <summary>
    /// Documents that describe the product AS IT IS, and must therefore carry today's counters.
    /// Deliberately excluded: <c>CHANGELOG.md</c> and <c>ROADMAP.md</c>, which state what was true
    /// in a past release (25, then 26) and must keep saying so, and <c>docs/probes</c> /
    /// <c>docs/revues</c> / <c>docs/reflexions</c>, which are dated measurements — rewriting a
    /// record of what was measured would be the exact opposite of this test's purpose.
    /// </summary>
    /// <remarks>
    /// Shared with <see cref="PublicDocsCoverageTests"/>: "which pages describe the product as it
    /// is" has one answer. A second enumeration would drift, and the page it stopped naming is the
    /// one the drift would hide in — the reason the counters are swept rather than listed.
    /// </remarks>
    internal static IEnumerable<string> LivingDocs()
    {
        var root = RepoRoot();

        // site/index.html is the fifth copy, and the only one a stranger reads before installing
        // anything: the landing page states the tool count in its hero. It is private to this repo
        // (denylisted in tools/inferpal-guard.ps1), hence the File.Exists guard below — the public
        // clone simply has no site/ and skips it, like MARKETPLACE.md.
        // vscode/README.md is the sixth copy, and it is not a nicety: vsce packages it INTO the
        // VSIX, and the Marketplace renders it as the listing page — the one document a stranger
        // reads before installing. It came into scope the day the VS Code extension went public.
        foreach (var relative in new[] { "README.md", "MARKETPLACE.md", "CONTRIBUTING.md",
                                         Path.Combine("site", "index.html"),
                                         Path.Combine("vscode", "README.md"),
                                         Path.Combine("Inferpal", "source.extension.vsixmanifest") })
        {
            var path = Path.Combine(root, relative);
            if (File.Exists(path)) yield return path;
        }

        // docs/*.md only — TopDirectoryOnly leaves probes/revues/reflexions out by construction.
        var docs = Path.Combine(root, "docs");
        if (!Directory.Exists(docs)) yield break;
        foreach (var path in Directory.EnumerateFiles(docs, "*.md", SearchOption.TopDirectoryOnly))
            yield return path;
    }

    [Fact]
    public void Readme_TestsBadge_MatchesThisAssembly()
    {
        // §23: the net8.0 leg deliberately omits the VSIX-dependent test files, so its own count
        // can never match the badge — the badge is owned by the full net8.0-windows suite.
#if WINDOWS
        var total = 0;
        foreach (var type in typeof(DocCountersTests).Assembly.GetTypes())
        {
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
            {
                if (method.GetCustomAttribute<Xunit.TheoryAttribute>() is not null)
                {
                    var inline = method.GetCustomAttributes<Xunit.InlineDataAttribute>().Count();
                    Assert.True(inline > 0,
                        $"{type.Name}.{method.Name} is a [Theory] without [InlineData] — " +
                        "this counter only understands InlineData-driven theories.");
                    total += inline;
                }
                else if (method.GetCustomAttribute<Xunit.FactAttribute>() is not null)
                {
                    total++;
                }
            }
        }

        var path  = Path.Combine(RepoRoot(), "README.md");
        var badge = Regex.Match(File.ReadAllText(path), @"tests-(\d+)%20passing");
        Assert.True(badge.Success, "README.md no longer carries the tests-N%20passing badge.");

        AssertCounter(path, total, int.Parse(badge.Groups[1].Value),
                      text => Regex.Replace(text, @"tests-\d+%20passing", $"tests-{total}%20passing"),
                      "passing tests");
#endif
    }
}
