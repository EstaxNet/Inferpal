using System.IO;
using System.Text.Json;
using Inferpal.Services.CodeActions;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// ⚠ <c>delete_file</c> and <c>restore_file</c> hand their approval card the change as a <see cref="DiffInfo"/>, like
/// <c>write_file</c>: rendered into the details instead, Visual Studio offered no "Open diff", the card had no line count,
/// and a deletion proposed by a background task reached its report as a sentence without the lines it removes.
/// </summary>
public sealed class FileToolCardDiffTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ip-cardiff-" + Guid.NewGuid().ToString("N"));

    public FileToolCardDiffTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static JsonElement Args(object o) => JsonSerializer.SerializeToElement(o);

    private sealed class Capturing(bool approve) : IApprovalService
    {
        public DiffInfo? Diff;
        public string Details = string.Empty;
        public Task<bool> RequestApprovalAsync(string toolName, string details, CancellationToken ct,
                                               string? subject = null, DiffInfo? diff = null, bool forcePrompt = false)
        {
            Diff = diff;
            Details = details;
            return Task.FromResult(approve);
        }
    }

    [Fact]
    public async Task ADeletion_ShowsTheFileItRemoves_AsItsChange()
    {
        var path = Path.Combine(_root, "Old.cs");
        File.WriteAllText(path, "class Old {}\n");
        var approval = new Capturing(approve: false);

        await new DeleteFileTool(approval, new FileHistoryService(), () => _root)
            .ExecuteAsync(Args(new { path }), CancellationToken.None);

        Assert.Equal("class Old {}\n", approval.Diff?.OldText);
        Assert.Equal(string.Empty, approval.Diff?.NewText);
        Assert.Equal(path, approval.Diff?.FilePath);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task ABinaryFile_HasNoTextToShow()
    {
        var path = Path.Combine(_root, "logo.png");
        File.WriteAllBytes(path, [0x89, 0x50, 0x4E, 0x47, 0x00, 0x00, 0x01, 0x02]);
        var approval = new Capturing(approve: false);

        await new DeleteFileTool(approval, new FileHistoryService(), () => _root)
            .ExecuteAsync(Args(new { path }), CancellationToken.None);

        Assert.NotEmpty(approval.Details);   // witness: the card was asked
        Assert.Null(approval.Diff);
    }

    [Fact]
    public async Task ARestore_ShowsTheFileThenTheBackup_AsItsChange()
    {
        var path    = Path.Combine(_root, "a.txt");
        var history = new FileHistoryService();
        File.WriteAllText(path, "v1\n");
        var snapshot = await history.SnapshotAsync(path, CancellationToken.None);
        File.WriteAllText(path, "v2\n");
        var approval = new Capturing(approve: false);

        await new RestoreFileTool(approval, history, () => _root)
            .ExecuteAsync(Args(new { path, snapshot }), CancellationToken.None);

        Assert.Equal("v2\n", approval.Diff?.OldText);
        Assert.Equal("v1\n", approval.Diff?.NewText);
        Assert.Equal("v2\n", File.ReadAllText(path));
    }
}
