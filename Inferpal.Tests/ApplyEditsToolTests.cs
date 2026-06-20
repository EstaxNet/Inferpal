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
        public Task<bool> RequestApprovalAsync(string toolName, string details, CancellationToken ct, string? subject = null)
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
