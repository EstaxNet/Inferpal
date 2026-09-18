using System.IO;
using System.Linq;
using System.Text.Json;
using Inferpal.Services;
using Inferpal.Services.Execution;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// Ten tools read a keyword the model wrote; eight name a value they do not recognise. The two that
/// stayed silent are exactly the two that <b>write</b>.
/// </summary>
/// <remarks>
/// <para>
/// ⚠ <b>Rule 22 documents its own blind spot</b>, and that is where the defect lived:
/// <i>"the comparison must be in the SAME method as the read. Two of the nine sites compare
/// elsewhere — apply_diff/apply_edits pass their occurrence to ApplyDiffMatcher"</i>. The scan lets
/// them out of its counter; nobody had gone to see what they did with it. The answer:
/// <c>default:</c>, that is <c>unique</c>, without a word.
/// </para>
/// <para>
/// ⚠ <b>The cost is not a refusal, it is a WRONG diagnosis.</b> <c>occurrence: "every"</c> on a
/// file with three matches became "require exactly one": the model asked to replace all three and
/// was told its <c>old_content</c> is <b>ambiguous</b>. It then rewrites a more precise
/// <c>old_content</c> — which was already right — and applies ONE edit where it wanted three, in a
/// file reported as changed. On the <c>apply_edits</c> side the whole batch aborts on
/// <i>"ambiguous (3 matches) for old_content"</i>, which sends the reader to the same
/// mauvais endroit.
/// </para>
/// <para>
/// The vocabulary and its refusal sentence live in <see cref="ApplyDiffMatcher"/> — one reader for
/// both tools, like the matcher itself: copying them on each side is the drift this repository pays
/// for elsewhere.
/// </para>
/// </remarks>
public class UnknownOccurrenceTests
{
    private sealed class StubApproval : IApprovalService
    {
        public int Calls { get; private set; }

        public Task<bool> RequestApprovalAsync(string toolName, string details, CancellationToken ct,
                                               string? subject = null, DiffInfo? diff = null,
                                               bool forcePrompt = false)
        {
            Calls++;
            return Task.FromResult(true);
        }
    }

    private static JsonElement Raw(string json) => JsonDocument.Parse(json).RootElement.Clone();
    private static string Json(string s) => JsonSerializer.Serialize(s);

    // ── The vocabulary, at the one place it is written ───────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("unique")]
    [InlineData("first")]
    [InlineData("all")]
    public void TheThreeModes_AndTheAbsentOne_AreAccepted(string? occurrence) =>
        Assert.Null(ApplyDiffMatcher.RejectOccurrence(occurrence));

    [Theory]
    [InlineData("every")]     // the synonym the model invents for 'all'
    [InlineData("1")]
    [InlineData("once")]
    public void AnythingElse_IsNamed(string occurrence)
    {
        var message = ApplyDiffMatcher.RejectOccurrence(occurrence);

        Assert.NotNull(message);
        // The refused value AND the vocabulary: a refusal that does not say what was expected
        // sends the model back to guessing.
        Assert.Contains(occurrence, message!, StringComparison.Ordinal);
        Assert.Contains("unique", message!, StringComparison.Ordinal);
        Assert.Contains("first",  message!, StringComparison.Ordinal);
        Assert.Contains("all",    message!, StringComparison.Ordinal);
    }

    // ── apply_diff ────────────────────────────────────────────────────────────

    [Fact]
    public async Task ApplyDiff_NamesTheUnknownOccurrence_InsteadOfCallingItAmbiguous()
    {
        var dir  = Directory.CreateTempSubdirectory("inferpal-occ").FullName;
        var file = Path.Combine(dir, "A.cs");
        const string content = "x();\ny();\nx();\nz();\nx();\n";
        await File.WriteAllTextAsync(file, content);
        try
        {
            var approval = new StubApproval();
            var tool     = new ApplyDiffTool(approval, new FileHistoryService(), () => dir);

            var answer = await tool.ExecuteAsync(
                Raw($$"""{"path":{{Json(file)}},"old_content":"x();","new_content":"w();","occurrence":"every"}"""),
                CancellationToken.None);

            Assert.Equal(ApplyDiffMatcher.RejectOccurrence("every"), answer);
            // ⚠ The diagnosis being replaced: "ambiguous" sent the model to rewrite a fine old_content.
            Assert.DoesNotContain("ambiguous", answer, StringComparison.OrdinalIgnoreCase);
            // Refused BEFORE the prompt, like update_memory: having a write approved and then doing
            // another one is worse than refusing.
            Assert.Equal(0, approval.Calls);
            Assert.Equal(content, await File.ReadAllTextAsync(file));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task ApplyDiff_StillHonoursTheThreeItKnows()
    {
        // Reference arm: 'all' really does replace all three.
        var dir  = Directory.CreateTempSubdirectory("inferpal-occ").FullName;
        var file = Path.Combine(dir, "A.cs");
        await File.WriteAllTextAsync(file, "x();\ny();\nx();\nz();\nx();\n");
        try
        {
            var tool = new ApplyDiffTool(new StubApproval(), new FileHistoryService(), () => dir);

            await tool.ExecuteAsync(
                Raw($$"""{"path":{{Json(file)}},"old_content":"x();","new_content":"w();","occurrence":"all"}"""),
                CancellationToken.None);

            Assert.Equal("w();\ny();\nw();\nz();\nw();\n", await File.ReadAllTextAsync(file));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // ── apply_edits : tout ou rien, et par son nom ───────────────────────────

    [Fact]
    public async Task ApplyEdits_AbortsTheWholeBatch_NamingTheOccurrence()
    {
        var dir = Directory.CreateTempSubdirectory("inferpal-occ").FullName;
        var a   = Path.Combine(dir, "A.cs");
        var b   = Path.Combine(dir, "B.cs");
        await File.WriteAllTextAsync(a, "int a = 1;\n");
        await File.WriteAllTextAsync(b, "x();\nx();\n");
        try
        {
            var approval = new StubApproval();
            var tool     = new ApplyEditsTool(approval, new FileHistoryService(), () => dir);

            var answer = await tool.ExecuteAsync(
                Raw($$"""
                {"edits":[
                  {"path":{{Json(a)}},"old_content":"int a = 1;","new_content":"int a = 2;"},
                  {"path":{{Json(b)}},"old_content":"x();","new_content":"w();","occurrence":"every"}
                ]}
                """),
                CancellationToken.None);

            // The exact cause, and the offending edit named — the shape this batch already uses.
            Assert.Contains("every", answer, StringComparison.Ordinal);
            Assert.Contains("#2",    answer, StringComparison.Ordinal);
            Assert.DoesNotContain("ambiguous", answer, StringComparison.OrdinalIgnoreCase);

            // ALL or nothing: the first edit, perfectly valid, is not applied.
            Assert.Equal("int a = 1;\n", await File.ReadAllTextAsync(a));
            Assert.Equal("x();\nx();\n", await File.ReadAllTextAsync(b));
            Assert.Equal(0, approval.Calls);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // ── The class witness: the eight others already name it ──────────────────

    [Fact]
    public void EveryToolThatReadsAModelKeyword_NamesAValueItDoesNotKnow()
    {
        // WITNESS of the shape, not of the count: if an eleventh site reads a keyword tomorrow
        // without ever naming the unknown, this test does not see it — that is rule 22's business,
        // and its documented blind spot is precisely what this file repairs. What is locked here is
        // that the two REPAIRED sites keep going through the shared vocabulary.
        var root = RepoRoot();
        foreach (var name in new[] { "ApplyDiffTool", "ApplyEditsTool" })
        {
            var code = ConventionCoverageTests.CodeOnly(
                Path.Combine(root, "Inferpal.Core", "Services", "Tools", $"{name}.cs"));
            Assert.Contains("RejectOccurrence", code, StringComparison.Ordinal);
        }

        // And the vocabulary is written in ONE place only: two copies drift.
        var tools = Directory.EnumerateFiles(Path.Combine(root, "Inferpal.Core", "Services", "Tools"), "*.cs");
        var declaring = tools.Where(f => ConventionCoverageTests.CodeOnly(f)
                                         .Contains("\"unique\"", StringComparison.Ordinal))
                             .Select(Path.GetFileName)
                             .ToList();
        Assert.Equal(["ApplyDiffMatcher.cs"], declaring);
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
