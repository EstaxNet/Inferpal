using System.IO;
using System.Text.Json;
using Inferpal.Services.Execution;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

// ──────────────────────────────────────────────────────────────────────────────────────────────
//  A refused edit names the line of the file that differs.
//
//  On the real-condition battery a model copied a method into old_content without the `$` of its
//  interpolated string. The refusal said "check spaces, line breaks and indentation" — what the
//  tolerant pass already ignores — so the model re-read the file, saw the line it believed it had
//  copied, sent the same call again, and the run was stopped for repeating.
// ──────────────────────────────────────────────────────────────────────────────────────────────
[Collection(CultureSerialCollection.Name)]
public sealed class ClosestLineOnRefusedEditTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "closest-line-" + Guid.NewGuid().ToString("N"));

    public ClosestLineOnRefusedEditTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private const string Inventory =
        "namespace Shop;\n\npublic sealed class Inventory\n{\n"
        + "    private readonly Dictionary<string, int> _stock = new();\n\n"
        + "    public int Stock(string sku) => _stock.TryGetValue(sku, out var n) ? n : 0;\n\n"
        + "    public void Reserve(string sku, int quantity)\n    {\n"
        + "        if (Stock(sku) < quantity)\n"
        + "            throw new InvalidOperationException($\"Not enough stock for '{sku}'.\");\n"
        + "        _stock[sku] = Stock(sku) - quantity;\n    }\n}\n";

    // The old_content the model sent: the same method, the `$` dropped.
    private const string DroppedDollar =
        "    public void Reserve(string sku, int quantity)\n    {\n"
        + "        if (Stock(sku) < quantity)\n"
        + "            throw new InvalidOperationException(\"Not enough stock for '{sku}'.\");\n"
        + "        _stock[sku] = Stock(sku) - quantity;\n    }";

    private const string FileLine = "throw new InvalidOperationException($\"Not enough stock for '{sku}'.\");";

    private sealed class Approve : IApprovalService
    {
        public Task<bool> RequestApprovalAsync(string toolName, string details, CancellationToken ct,
            string? subject = null, DiffInfo? diff = null, bool forcePrompt = false) => Task.FromResult(true);
    }

    private static JsonElement Json(object o) => JsonSerializer.SerializeToElement(o);

    [Fact]
    public void ANearCopy_IsAnsweredWithTheLineFromTheFile_AndWhereItDiffers()
    {
        Assert.Equal(0, ApplyDiffMatcher.Resolve(Inventory, DroppedDollar, "x", null).Count);   // WITNESS: refused

        var note = ApplyDiffMatcher.Closest(Inventory, DroppedDollar);

        Assert.NotNull(note);
        Assert.Contains("lines 9–14", note);
        Assert.Contains("Line 12 differs", note);
        Assert.Contains("file:        " + FileLine, note);
        Assert.Contains("after \"…new InvalidOperationException(\"", note);
    }

    [Fact]
    public void AnOldContentUnlikeAnythingInTheFile_GetsNoNote()
    {
        // Reference arms: nothing close, and a single unrelated line — a guess here would point at the wrong code.
        Assert.Null(ApplyDiffMatcher.Closest(Inventory, "public decimal Total(Cart cart)\n{\n    return cart.Sum();\n}"));
        Assert.Null(ApplyDiffMatcher.Closest(Inventory, "Console.WriteLine(42);"));
        Assert.Null(ApplyDiffMatcher.Closest(Inventory, "   \n  "));
    }

    [Fact]
    public async Task ApplyDiff_AndApplyEdits_BothSayIt()
    {
        var path = Path.Combine(_root, "Inventory.cs");
        File.WriteAllText(path, Inventory);

        var diff = await new ApplyDiffTool(new Approve(), new FileHistoryService(), () => _root).ExecuteAsync(
            Json(new { path, old_content = DroppedDollar, new_content = "    public bool Reserve() => true;" }), default);
        var edits = await new ApplyEditsTool(new Approve(), new FileHistoryService(), () => _root).ExecuteAsync(
            Json(new { edits = new[] { new { path, old_content = DroppedDollar, new_content = "    public bool Reserve() => true;" } } }),
            default);

        Assert.Contains(FileLine, diff);
        Assert.Contains(FileLine, edits);
        Assert.Equal(Inventory, File.ReadAllText(path));   // nothing was written
    }
}
