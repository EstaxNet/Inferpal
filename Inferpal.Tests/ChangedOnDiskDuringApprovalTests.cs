using System.IO;
using System.Text.Json;
using Inferpal.Services;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A change is computed from the file read before its approval card; what the user SAVES while the card waits — Ctrl+S,
/// VS Code's auto-save, another program — is not an unsaved change, and writing the approved change put the older text
/// back over it. Every writer refuses instead, and the file keeps what was saved.
/// </summary>
public sealed class ChangedOnDiskDuringApprovalTests : IDisposable
{
    private readonly string _ws = Path.Combine(Path.GetTempPath(), "inferpal_changed_" + Guid.NewGuid().ToString("N"));

    public ChangedOnDiskDuringApprovalTests() => Directory.CreateDirectory(_ws);

    public void Dispose() { try { Directory.Delete(_ws, recursive: true); } catch { } }

    /// <summary>Approves, after <paramref name="meanwhile"/> ran — what the user does while the card waits.</summary>
    private sealed class ApprovalThatWaits(Action meanwhile) : IApprovalService
    {
        public int Prompts { get; private set; }

        public Task<bool> RequestApprovalAsync(string toolName, string details, CancellationToken ct, string? subject = null,
                                               DiffInfo? diff = null, bool forcePrompt = false)
        {
            Prompts++;
            meanwhile();
            return Task.FromResult(true);
        }
    }

    private string File(string name, string content)
    {
        var path = Path.Combine(_ws, name);
        System.IO.File.WriteAllText(path, content);
        return path;
    }

    private static JsonElement Json(object o) => JsonSerializer.SerializeToElement(o);

    [Fact]
    public async Task ApplyDiff_KeepsWhatWasSavedDuringTheApproval()
    {
        var path     = File("A.cs", "int x = 1;\nint y = 2;\n");
        var approval = new ApprovalThatWaits(() => System.IO.File.WriteAllText(path, "int x = 1;\nint y = 2;\n// saved\n"));

        var said = await new ApplyDiffTool(approval, new FileHistoryService(), () => _ws)
            .ExecuteAsync(Json(new { path, old_content = "int x = 1;", new_content = "int x = 10;" }), CancellationToken.None);

        Assert.Equal(1, approval.Prompts);   // witness: the card was shown and accepted
        Assert.Contains("changed on disk while the approval was pending", said);
        Assert.Equal("int x = 1;\nint y = 2;\n// saved\n", System.IO.File.ReadAllText(path));
    }

    [Fact]
    public async Task ApplyDiff_WritesWhenNothingChanged()
    {
        // Reference arm: the same call, nobody touching the file, writes.
        var path = File("A.cs", "int x = 1;\n");

        await new ApplyDiffTool(new ApprovalThatWaits(() => { }), new FileHistoryService(), () => _ws)
            .ExecuteAsync(Json(new { path, old_content = "int x = 1;", new_content = "int x = 10;" }), CancellationToken.None);

        Assert.Equal("int x = 10;\n", System.IO.File.ReadAllText(path));
    }

    [Fact]
    public async Task WriteFile_KeepsWhatWasSavedDuringTheApproval()
    {
        var path     = File("B.cs", "class B {}\n");
        var approval = new ApprovalThatWaits(() => System.IO.File.WriteAllText(path, "class B { int saved; }\n"));

        var said = await new WriteFileTool(approval, new FileHistoryService(), () => _ws)
            .ExecuteAsync(Json(new { path, content = "class B { int model; }\n" }), CancellationToken.None);

        Assert.Contains("changed on disk while the approval was pending", said);
        Assert.Equal("class B { int saved; }\n", System.IO.File.ReadAllText(path));
    }

    [Fact]
    public async Task WriteFile_DoesNotReplaceAFileCreatedDuringTheApproval()
    {
        var path     = Path.Combine(_ws, "New.cs");
        var approval = new ApprovalThatWaits(() => System.IO.File.WriteAllText(path, "// written by someone else\n"));

        var said = await new WriteFileTool(approval, new FileHistoryService(), () => _ws)
            .ExecuteAsync(Json(new { path, content = "class New {}\n" }), CancellationToken.None);

        Assert.Contains("was created by something else while the approval was pending", said);
        Assert.Equal("// written by someone else\n", System.IO.File.ReadAllText(path));
    }

    [Fact]
    public async Task ApplyEdits_KeepsEveryFile_WhenOneWasSavedDuringTheApproval()
    {
        var a        = File("A.cs", "int a = 1;\n");
        var b        = File("B.cs", "int b = 2;\n");
        var approval = new ApprovalThatWaits(() => System.IO.File.WriteAllText(b, "int b = 2;\n// saved\n"));
        var args     = Json(new { edits = new[]
        {
            new { path = a, old_content = "a = 1", new_content = "a = 10" },
            new { path = b, old_content = "b = 2", new_content = "b = 20" },
        } });

        var said = await new ApplyEditsTool(approval, new FileHistoryService(), () => _ws, smartFix: null)
            .ExecuteAsync(args, CancellationToken.None);

        Assert.Contains("changed on disk while the approval was pending", said);
        Assert.Equal("int a = 1;\n", System.IO.File.ReadAllText(a));   // all or nothing: the batch is not half applied
        Assert.Equal("int b = 2;\n// saved\n", System.IO.File.ReadAllText(b));
    }

    [Fact]
    public async Task DeleteFile_KeepsAFileSavedDuringTheApproval()
    {
        var path     = File("C.cs", "class C {}\n");
        var approval = new ApprovalThatWaits(() => System.IO.File.WriteAllText(path, "class C { int saved; }\n"));

        var said = await new DeleteFileTool(approval, new FileHistoryService(), () => _ws)
            .ExecuteAsync(Json(new { path }), CancellationToken.None);

        Assert.Contains("changed on disk while the approval was pending", said);
        Assert.True(System.IO.File.Exists(path));
    }
}
