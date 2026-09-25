using System.IO;
using System.Text.Json;
using Inferpal.Models;
using Inferpal.Services.Agent;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// The writing tools given the path of a DIRECTORY. Measured: <c>write_file</c> had the human approve a write "to a
/// folder", then answered "Access … is denied. The arguments are not the cause, so the same call will fail the same
/// way: … continue without this tool" — false (the path IS the cause), and the one advice that makes the model give up
/// writing; <c>apply_diff</c> and <c>delete_file</c> answered "file not found" for a path that exists. Refused, named,
/// and BEFORE the approval prompt: having someone approve what cannot happen is worse than refusing.
/// </summary>
public sealed class WriteToDirectoryTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("inferpal-wdir-").FullName;
    private readonly string _dir;

    public WriteToDirectoryTests()
    {
        _dir = Path.Combine(_root, "sub");
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "keep.txt"), "x");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private sealed class CountingApproval : IApprovalService
    {
        public int Calls;
        public Task<bool> RequestApprovalAsync(string toolName, string details, CancellationToken ct,
                                               string? subject = null, DiffInfo? diff = null, bool forcePrompt = false)
        {
            Calls++;
            return Task.FromResult(true);
        }
    }

    private sealed class One(ITool tool) : IToolRegistry
    {
        public IReadOnlyList<ToolDefinition> Definitions => [];
        public DiffInfo? ConsumeDiff() => null;
        public Task<string> ExecuteAsync(string name, JsonElement args, CancellationToken ct) => tool.ExecuteAsync(args, ct);
    }

    // [InlineData], not [MemberData]: DocCountersTests counts the cases of a theory from its InlineData.
    [Theory]
    [InlineData("write_file")]
    [InlineData("apply_diff")]
    [InlineData("apply_edits")]
    [InlineData("delete_file")]
    [InlineData("restore_file")]
    public async Task ADirectory_IsRefusedByName_BeforeAnyoneIsAskedToApprove(string name)
    {
        var approval = new CountingApproval();
        var history  = new FileHistoryService();
        (ITool tool, object args) = name switch
        {
            "write_file"  => ((ITool)new WriteFileTool(approval, history, () => _root), (object)new { path = _dir, content = "hello" }),
            "apply_diff"  => (new ApplyDiffTool(approval, history, () => _root), new { path = _dir, old_content = "a", new_content = "b" }),
            "apply_edits" => (new ApplyEditsTool(approval, history, () => _root),
                              new { edits = new[] { new { path = _dir, old_content = "a", new_content = "b" } } }),
            "delete_file" => (new DeleteFileTool(approval, history, () => _root), new { path = _dir }),
            _             => (new RestoreFileTool(approval, history, () => _root), new { path = _dir }),
        };

        var result = await AgentOrchestrator.ExecuteToolSafeAsync(
            new One(tool), name, JsonDocument.Parse(JsonSerializer.Serialize(args)).RootElement, CancellationToken.None);

        Assert.Contains("is a directory", result);
        Assert.DoesNotContain("continue without this tool", result);
        Assert.Equal(0, approval.Calls);
        Assert.True(File.Exists(Path.Combine(_dir, "keep.txt")));                                   // untouched
    }
}
