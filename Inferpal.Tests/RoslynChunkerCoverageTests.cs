using System.IO;
using System.Linq;
using Inferpal.Services.Lsp;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// What the C# chunker leaves OUT of the index — code that <c>search_codebase</c> then can never
/// find, with nothing saying so.
/// </summary>
public class RoslynChunkerCoverageTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "ws");

    /// <summary>
    /// As soon as a type existed in the file, top-level statements were ignored: a minimal-API
    /// <c>Program.cs</c> with a model at the bottom indexed only the model — not a single endpoint.
    /// </summary>
    [Fact]
    public void TopLevelStatements_NextToATypeDeclaration_AreIndexed()
    {
        var src = string.Join('\n',
            "var app = WebApplication.Create();",
            "app.MapGet(\"/orders\", () => Orders.All());",
            "app.MapPost(\"/orders\", (Order o) => Orders.Add(o));",
            "app.Run();",
            "",
            "public class Order",
            "{",
            "    public int Id { get; set; }",
            "    public string Name { get; set; } = \"\";",
            "}");

        var chunks = RoslynChunker.Chunk(Path.Combine(Root, "Program.cs"), src, Root);

        Assert.Contains(chunks, c => c.Content.Contains("MapGet", StringComparison.Ordinal));
    }

    /// <summary>
    /// A member past a chunk's budget was SHORTENED until it fit: its tail was indexed nowhere.
    /// </summary>
    [Fact]
    public void AMemberLongerThanTheChunkBudget_HasItsTailIndexedToo()
    {
        var body = Enumerable.Range(0, 400)
            .Select(i => $"        var value{i} = Compute({i}); // filler line number {i}");
        var src = string.Join('\n',
            new[] { "public class Big", "{", "    public void Run()", "    {" }
                .Concat(body)
                .Concat(new[] { "        FinalMarkerCall();", "    }", "}" }));

        var chunks = RoslynChunker.Chunk(Path.Combine(Root, "Big.cs"), src, Root);

        Assert.Contains(chunks, c => c.Content.Contains("FinalMarkerCall", StringComparison.Ordinal));
    }

    /// <summary>Witness: an ordinary file still gets one chunk per member, with its type.</summary>
    [Fact]
    public void AnOrdinaryType_StillGetsOneChunkPerMember()
    {
        var src = string.Join('\n',
            "public class Calc",
            "{",
            "    public int Add(int a, int b)",
            "    {",
            "        return a + b;",
            "    }",
            "}");

        var chunks = RoslynChunker.Chunk(Path.Combine(Root, "Calc.cs"), src, Root);

        Assert.Contains(chunks, c => c.TypeName == "Calc.Add");
    }

    /// <summary>
    /// Every fallback from a chunker tier to the regex tier <b>says so</b>, and only once.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both semantic tiers fell back to <c>CodeChunker</c> in silence. It is the most expensive
    /// degradation in the repository: Roslyn is shipped in the VSIX by a dedicated target
    /// (<c>RestoreRoslynToVsix</c>), so a package where it does not load makes ALL the C# fall back
    /// to the regex — the index loses <c>type_name</c> and its symbol boundaries — and an
    /// <b>external</b> probe had to be written (<c>docs/probes/rag-coverage</c>) because nothing
    /// said so. The LSP provider had already taken the lesson for an absent or dead server; what
    /// remained was the server that answers with unusable ranges.
    /// </para>
    /// <para>
    /// ⚠ <b>A rule, not a named assertion, because the subject is no longer unique</b>: two tiers
    /// today, and a third tomorrow would inherit the rule instead of rediscovering it. It reads a
    /// syntax tree — "this <c>catch</c> falls back to the regex chunker" is a question with an exact
    /// answer — and requires <c>RecordOnce</c>, not <c>Record</c>: these chunkers run <b>per
    /// file</b>, so one message per call would drown the 200-entry ring.
    /// </para>
    /// <para>
    /// ⚠ It targets only the fallbacks from an <b>exception</b>. The fallback on "zero chunks" is
    /// normal and per file (a file with no declared type, a file of imports only): it has nothing to
    /// report, and requiring it would have made the channel unusable.
    /// </para>
    /// </remarks>
    [Fact]
    public void EverySemanticChunker_SaysWhenItFallsBackToRegex_OncePerCause()
    {
        var files = new[]
        {
            Path.Combine(RepoRoot(), "Inferpal.Core", "Services", "Lsp", "RoslynChunker.cs"),
            Path.Combine(RepoRoot(), "Inferpal.Core", "Services", "Lsp", "LspChunker.cs"),
        };

        var offenders = new List<string>();
        var catches   = 0;

        foreach (var file in files)
        {
            var root = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree
                .ParseText(File.ReadAllText(file), path: file).GetRoot();

            // ⚠ The subject is the METHOD that falls back to the regex tier, not the catch that
            // contains the call: the two tiers have two shapes — one returns `CodeChunker.Chunk`
            // INSIDE its catch, the other returns it after the `try`. A predicate on the catch body
            // would have judged one site out of two, and it is the witness below that said so.
            foreach (var method in root.DescendantNodes()
                         .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax>())
            {
                var whole = method.ToString();
                if (!whole.Contains("CodeChunker.Chunk", StringComparison.Ordinal)) continue;

                foreach (var clause in method.DescendantNodes()
                             .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.CatchClauseSyntax>())
                {
                    var body = clause.Block?.ToString() ?? string.Empty;
                    // Relaying cancellation is not a degradation: it returns nothing.
                    if (body.Contains("throw;", StringComparison.Ordinal)) continue;
                    catches++;

                    if (!body.Contains("Diagnostics.RecordOnce(", StringComparison.Ordinal))
                        offenders.Add($"{Path.GetFileName(file)}"
                                    + $"({clause.GetLocation().GetLineSpan().StartLinePosition.Line + 1})");
                }
            }
        }

        // Witness: with no catch found, "no offenders" judges nothing — a renamed file, a moved
        // fallback, and the convention stops being enforced without going red.
        Assert.True(catches >= 2,
                    $"Only {catches} fallback(s) to the regex chunker found: the rule judges nothing any more.");

        Assert.True(offenders.Count == 0,
            "This chunker tier falls back to the regex WITHOUT saying so: the index loses its "
            + "symbol boundaries for a whole language and nothing announces it — the degradation "
            + "that cost this repository an external probe. Use Diagnostics.RecordOnce (per file, "
            + "so it is said once). Sites: " + string.Join(", ", offenders));
    }

    /// <summary>
    /// None of the chunker's tiers loses the <b>tail</b> of an over-budget symbol.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The same shape was found in <b>all three</b> tiers: the Roslyn tier had repaired it by
    /// splitting ("it used to be shrunk until it fit, and its tail was indexed nowhere"), the LSP
    /// tier shrank in 25% steps, and the regex tier truncated `mEnd` line by line. A fix that closes
    /// the instance you saw leaves alive the class you did not look for — here three times for one
    /// lesson.
    /// </para>
    /// <para>
    /// ⚠ This test is the guard of the <b>class</b>, and not three separate assertions: it is a
    /// property — "everything within the symbol's range ends up in a chunk" — and a fourth tier
    /// would inherit it. The witness is that the budget really bit (more than one piece): without
    /// it, a tier returning a single huge chunk would pass for correct.
    /// </para>
    /// </remarks>
    [Fact]
    public void NoChunkerTier_LosesTheTailOfAnOverBudgetSymbol()
    {
        // 600 lines of a single method: far beyond any tier's budget.
        var body = Enumerable.Range(0, 600)
            .Select(i => $"        var value{i:D3} = Compute(alpha, beta, gamma, delta);")
            .ToList();
        var src = string.Join('\n',
            new[] { "public class Big", "{", "    public void M()", "    {" }
                .Concat(body)
                .Concat(["    }", "}"]));

        var path = Path.Combine(Root, "Big.cs");

        foreach (var (tier, chunks) in new (string, List<Inferpal.Services.Rag.RagChunk>)[]
        {
            ("Roslyn", RoslynChunker.Chunk(path, src, Root)),
            ("regex",  Inferpal.Services.Rag.CodeChunker.Chunk(path, src, Root)),
        })
        {
            // Witness: the budget bit, so the symbol came back in several pieces.
            Assert.True(chunks.Count > 1, $"Tier {tier}: a single chunk for {body.Count} lines — the budget did not bite, nothing is judged.");

            // The body's last line must be found in a chunk: that is the one that was lost.
            var last = body[^1].Trim();
            Assert.True(chunks.Any(c => c.Content.Contains(last, StringComparison.Ordinal)),
                        $"Tier {tier}: the symbol's last line is in no chunk — "
                      + "search_codebase cannot reach it.");
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
