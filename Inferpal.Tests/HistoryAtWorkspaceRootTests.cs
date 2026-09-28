using System.IO;
using System.Text.Json;
using Inferpal.Localization;
using Inferpal.Models;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

// ──────────────────────────────────────────────────────────────────────────────────────────────
//  Outside a git repository, the file history lives at the workspace root — one .inferpal/history.
//
//  It lived in the folder of each file an edit touched (src/Shop/.inferpal/history, Components/
//  Pages/.inferpal/history…), while restore_file's description names one .inferpal/history/.
//  Measured on the tool battery: asked to restore Cart.cs "from Inferpal's file history", Devstral
//  looked at the workspace root, found nothing, and answered that no backup existed. The git root
//  still wins, a file outside the workspace keeps its own folder, and a snapshot an older version
//  wrote in the file's folder is still found by a restore.
// ──────────────────────────────────────────────────────────────────────────────────────────────
public sealed class HistoryAtWorkspaceRootTests : IDisposable
{
    private readonly string _ws = Directory.CreateTempSubdirectory("inferpal-histroot-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_ws, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private sealed class Yes : IApprovalService
    {
        public Task<bool> RequestApprovalAsync(string toolName, string details, CancellationToken ct,
                                               string? subject = null, DiffInfo? diff = null, bool forcePrompt = false) =>
            Task.FromResult(true);
    }

    private static JsonElement Args(object value) =>
        JsonDocument.Parse(JsonSerializer.Serialize(value)).RootElement.Clone();

    private string Cart(string text = "namespace Shop;\n\npublic sealed class Cart { }\n")
    {
        var dir = Directory.CreateDirectory(Path.Combine(_ws, "src", "Shop")).FullName;
        var path = Path.Combine(dir, "Cart.cs");
        File.WriteAllText(path, text);
        return path;
    }

    private FileHistoryService History() => new() { WorkspaceRoot = () => _ws };

    [Fact]
    public async Task AnEditOutsideGit_IsBackedUpAtTheWorkspaceRoot_AndRestoredFromThere()
    {
        var cart = Cart();
        var history = History();
        history.BeginRun();
        var diff = new ApplyDiffTool(new Yes(), history, () => _ws);
        Assert.DoesNotContain("not found", await diff.ExecuteAsync(
            Args(new { path = cart, old_content = "public sealed class Cart { }", new_content = "// Reviewed\npublic sealed class Cart { }" }), default));

        Assert.NotEmpty(Directory.EnumerateFiles(Path.Combine(_ws, ".inferpal", "history")));
        Assert.False(Directory.Exists(Path.Combine(_ws, "src", "Shop", ".inferpal")));   // no history folder per edited folder

        var restored = await new RestoreFileTool(new Yes(), history, () => _ws).ExecuteAsync(Args(new { path = cart }), default);
        Assert.DoesNotContain(Strings.RestoreNotFound(cart), restored);
        Assert.Equal("namespace Shop;\n\npublic sealed class Cart { }\n", File.ReadAllText(cart));
    }

    [Fact]
    public async Task ASnapshotAnOlderVersionWroteInTheFilesFolder_IsStillFound()
    {
        var cart = Cart("old content\n");
        var legacy = new FileHistoryService();                       // no workspace root: the file's own folder
        await legacy.SnapshotAsync(cart, default);
        Assert.True(Directory.Exists(Path.Combine(_ws, "src", "Shop", ".inferpal", "history")));
        File.WriteAllText(cart, "new content\n");

        Assert.NotNull(await History().FindRestoreCandidateAsync(cart, default));
        Assert.NotNull(History().FindMostRecentSnapshot(cart));
    }

    [Fact]
    public void TheGitRootStillWins_AndAFileOutsideTheWorkspaceKeepsItsFolder()
    {
        var repo = Directory.CreateDirectory(Path.Combine(_ws, "repo")).FullName;
        Directory.CreateDirectory(Path.Combine(repo, ".git"));
        var inRepo = Path.Combine(repo, "app", "src", "A.cs");
        Assert.Equal(Path.Combine(repo, ".inferpal", "history"),
                     FileHistoryService.GetHistoryDir(inRepo, workspaceRoot: Path.Combine(repo, "app")));

        var outside = Path.Combine(Path.GetTempPath(), "elsewhere-" + Guid.NewGuid().ToString("N"), "B.cs");
        Assert.Equal(Path.Combine(Path.GetDirectoryName(outside)!, ".inferpal", "history"),
                     FileHistoryService.GetHistoryDir(outside, workspaceRoot: Path.Combine(_ws, "ws")));
    }

    [Fact]
    public void TheToolRegistry_GivesTheHistoryItsWorkspaceRoot()
    {
        // The registry needs every service of a session to be built; read the wiring instead.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Inferpal.sln"))) dir = dir.Parent;
        var code = ConventionCoverageTests.CodeOnly(Path.Combine(dir!.FullName, "Inferpal.Core", "Services", "Execution", "ToolRegistry.cs"));

        Assert.Contains("_fileHistory = fileHistory ?? new();", code, StringComparison.Ordinal);   // witness
        Assert.Contains("_fileHistory.WorkspaceRoot ??= () => indexService.RootDir;", code, StringComparison.Ordinal);
    }
}
