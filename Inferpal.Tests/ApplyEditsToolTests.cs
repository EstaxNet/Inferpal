using System.IO;
using System.Text;
using System.Text.Json;
using Inferpal.Services;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

// Atomic multi-file edits: all-or-nothing application, sequential edits per file, and cancellation.
public class ApplyEditsToolTests
{
    private sealed class StubApproval(bool approve) : IApprovalService
    {
        public Task<bool> RequestApprovalAsync(string toolName, string details, CancellationToken ct, string? subject = null, DiffInfo? diff = null, bool forcePrompt = false)
            => Task.FromResult(approve);
    }

    private static ApplyEditsTool Tool(string root, bool approve = true) =>
        new(new StubApproval(approve), new FileHistoryService(), () => root, smartFix: null);

    private static JsonElement Args(params (string path, string oldC, string newC)[] edits)
    {
        var sb = new StringBuilder("{\"edits\":[");
        for (int i = 0; i < edits.Length; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(JsonSerializer.Serialize(new { path = edits[i].path, old_content = edits[i].oldC, new_content = edits[i].newC }));
        }
        sb.Append("]}");
        return JsonDocument.Parse(sb.ToString()).RootElement.Clone();
    }

    [Fact]
    public async Task AppliesEditsAcrossMultipleFiles()
    {
        using var tmp = new TempDir();
        var a = tmp.File("A.cs", "int x = 1;");
        var b = tmp.File("B.cs", "int y = 2;");

        await Tool(tmp.Path).ExecuteAsync(Args((a, "x = 1", "x = 10"), (b, "y = 2", "y = 20")), CancellationToken.None);

        Assert.Equal("int x = 10;", await File.ReadAllTextAsync(a));
        Assert.Equal("int y = 20;", await File.ReadAllTextAsync(b));
    }

    [Fact]
    public async Task AnyFailedEdit_LeavesAllFilesUntouched()
    {
        using var tmp = new TempDir();
        var a = tmp.File("A.cs", "int x = 1;");
        var b = tmp.File("B.cs", "int y = 2;");

        // Second edit's old_content does not exist → whole batch must abort, nothing written.
        var result = await Tool(tmp.Path).ExecuteAsync(
            Args((a, "x = 1", "x = 10"), (b, "DOES NOT EXIST", "z")), CancellationToken.None);

        Assert.Equal("int x = 1;", await File.ReadAllTextAsync(a));   // untouched
        Assert.Equal("int y = 2;", await File.ReadAllTextAsync(b));   // untouched
        Assert.Contains("B.cs", result);                             // names the offending file
    }

    [Fact]
    public async Task MultipleEditsSameFile_AppliedInOrder()
    {
        using var tmp = new TempDir();
        var a = tmp.File("A.cs", "a\nb\nc");

        await Tool(tmp.Path).ExecuteAsync(Args((a, "a", "A"), (a, "c", "C")), CancellationToken.None);

        Assert.Equal("A\nb\nC", await File.ReadAllTextAsync(a));
    }

    [Fact]
    public async Task Edit_PreservesBomAndEncoding_OfTheExistingFile()
    {
        // File.WriteAllTextAsync always emits UTF-8 without BOM: a one-line edit used to strip
        // the BOM VS puts on .cs files and to transcode UTF-16 files outright (revue §1.9).
        using var tmp = new TempDir();
        var bomFile  = Path.Combine(tmp.Path, "Bom.cs");
        var utf16    = Path.Combine(tmp.Path, "Wide.cs");
        await File.WriteAllTextAsync(bomFile, "int x = 1;", new UTF8Encoding(true));
        await File.WriteAllTextAsync(utf16,   "int y = 2;", Encoding.Unicode);

        await Tool(tmp.Path).ExecuteAsync(Args((bomFile, "x = 1", "x = 10"), (utf16, "y = 2", "y = 20")), CancellationToken.None);

        var bomBytes  = await File.ReadAllBytesAsync(bomFile);
        var wideBytes = await File.ReadAllBytesAsync(utf16);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bomBytes.Take(3).ToArray());   // UTF-8 BOM kept
        Assert.Equal(new byte[] { 0xFF, 0xFE },       wideBytes.Take(2).ToArray());  // UTF-16 LE BOM kept
        Assert.Equal("int x = 10;", await File.ReadAllTextAsync(bomFile));
        Assert.Equal("int y = 20;", await File.ReadAllTextAsync(utf16));     // still readable, not mojibake
    }

    [Fact]
    public async Task NoEdits_ReturnsEmptyNotice_AndWritesNothing()
    {
        using var tmp = new TempDir();
        var doc = JsonDocument.Parse("{\"edits\":[]}").RootElement.Clone();
        var result = await Tool(tmp.Path).ExecuteAsync(doc, CancellationToken.None);
        Assert.False(string.IsNullOrWhiteSpace(result));
    }

    [Fact]
    public async Task Cancelled_WritesNothing()
    {
        using var tmp = new TempDir();
        var a = tmp.File("A.cs", "int x = 1;");

        await Tool(tmp.Path, approve: false).ExecuteAsync(Args((a, "x = 1", "x = 10")), CancellationToken.None);

        Assert.Equal("int x = 1;", await File.ReadAllTextAsync(a));   // denial → no write
    }

    // ── A malformed edit is a failed batch, not a dropped line ────────────────
    //
    // The description the model reads promises, word for word: "If ANY edit cannot be applied, NO
    // file is changed." A malformed entry used to be skipped, and the answer then reported success
    // for the survivors — "Applied 2 edit(s)" on a batch of 3. The model has no way to see the one
    // that vanished, so it carries on believing the change is in the file.

    private static JsonElement Raw(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Theory]
    // new_content missing — used to be dropped; "" stays valid and means "delete the block".
    [InlineData("""{"edits":[{"path":"@A","old_content":"x = 1"},{"path":"@B","old_content":"y = 2","new_content":"y = 20"}]}""",
                "new_content")]
    // old_content missing.
    [InlineData("""{"edits":[{"path":"@A","new_content":"x = 10"},{"path":"@B","old_content":"y = 2","new_content":"y = 20"}]}""",
                "old_content")]
    // a wrong-typed value is the same event as a missing one: it is not a string.
    [InlineData("""{"edits":[{"path":"@A","old_content":"x = 1","new_content":42},{"path":"@B","old_content":"y = 2","new_content":"y = 20"}]}""",
                "new_content")]
    // not an object at all.
    [InlineData("""{"edits":["oops",{"path":"@B","old_content":"y = 2","new_content":"y = 20"}]}""",
                "not an object")]
    public async Task AMalformedEdit_AbortsTheWholeBatchAndNamesIt(string json, string named)
    {
        using var tmp = new TempDir();
        var a = tmp.File("A.cs", "int x = 1;");
        var b = tmp.File("B.cs", "int y = 2;");

        var args   = Raw(json.Replace("@A", a.Replace("\\", "\\\\")).Replace("@B", b.Replace("\\", "\\\\")));
        var result = await Tool(tmp.Path).ExecuteAsync(args, CancellationToken.None);

        Assert.Contains(named, result, StringComparison.Ordinal);
        // The well-formed edit of the same batch must NOT have been applied.
        Assert.Equal("int x = 1;", await File.ReadAllTextAsync(a));
        Assert.Equal("int y = 2;", await File.ReadAllTextAsync(b));
    }

    [Fact]
    public async Task AnEmptyNewContent_StaysAValidDeletion()
    {
        // The counterpart of the rule above: "" is how a block is deleted, and it must keep working.
        using var tmp = new TempDir();
        var a = tmp.File("A.cs", "int x = 1;");

        await Tool(tmp.Path).ExecuteAsync(Args((a, "int x = 1;", "")), CancellationToken.None);

        Assert.Equal("", await File.ReadAllTextAsync(a));
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } =
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "inferpal_edits_" + Guid.NewGuid().ToString("N"));

        public TempDir() => Directory.CreateDirectory(Path);

        public string File(string name, string content)
        {
            var p = System.IO.Path.Combine(Path, name);
            System.IO.File.WriteAllText(p, content);
            return p;
        }

        public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
    }
}
