using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Inferpal.Services;
using Inferpal.Services.Editor;
using Inferpal.Services.Execution;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// <c>update_memory</c> took a snapshot before writing, then <b>threw its answer away</b> — so it
/// erased the project memory even when nothing could be saved.
/// </summary>
/// <remarks>
/// <para>
/// ⚠ <b>The rule is written in the funnel, and seven of its nine readers honour it.</b>
/// <c>FileHistoryService.BackUpBeforeChangeAsync</c> porte en remarque : <i>« ⚠ The change must then
/// NOT happen: every editing tool promises that restore_file and /undo-run bring the previous
/// version back, and a failed snapshot leaves nothing to bring back — a deletion is then
/// permanent »</i>. <c>apply_diff</c>, <c>apply_edits</c>, <c>write_file</c>, <c>delete_file</c>,
/// <c>rename_symbol</c>, <c>restore_file</c> and <c>BackedUpFileWriter</c> all abort on
/// <c>!saved</c>. <c>update_memory</c> called the method underneath — <c>SnapshotAsync</c> — and
/// ignored what it returned.
/// </para>
/// <para>
/// ⚠ <b>And the line just above the call PROMISED that net</b>: <i>"Before the write, so
/// /undo-run can put back a memory that "clear" or "replace" removed"</i>. Under
/// <c>mode: "clear"</c>, the project memory — re-injected into the system prompt of every later
/// session — was emptied with no net and the turn answered success.
/// </para>
/// <para>
/// The other reader of <c>SnapshotAsync</c>, <c>EditorWriteGate</c>, is best-effort <b>on purpose</b>
/// and says so: it writes the <i>buffer</i>, the file on disk does not move, and the editor's own
/// undo stack is the real net. A reasoned difference, not an oversight — hence a named assertion
/// rather than a rule, which would live off that exemption.
/// </para>
/// </remarks>
public class MemoryWithoutANetTests
{
    private sealed class NoEditor : IEditorSurface
    {
        public bool IsAvailable => false;
        public string? ActiveDocumentPath => null;
        public IReadOnlyList<string> GetOpenDocumentPaths() => [];
        public Task<ActiveDocument?> GetActiveDocumentAsync(CancellationToken ct) => Task.FromResult<ActiveDocument?>(null);
        public Task<string?> InsertAtCursorAsync(string text, CancellationToken ct) => Task.FromResult<string?>(null);
        public Task<EditorEditResult?> ReplaceSelectionAsync(string text, CancellationToken ct) => Task.FromResult<EditorEditResult?>(null);
        public Task<string?> GetEditorDiagnosticsAsync(CancellationToken ct) => Task.FromResult<string?>(null);
    }

    private sealed class YesApproval : IApprovalService
    {
        public Task<bool> RequestApprovalAsync(string toolName, string details, CancellationToken ct,
                                               string? subject = null, DiffInfo? diff = null,
                                               bool forcePrompt = false) => Task.FromResult(true);
    }

    private static JsonElement Raw(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private const string Remembered = "IMPORTANT: this project uses tabs.\n";

    /// <summary>Workspace with a memory file, and the path where its snapshots would go.</summary>
    private static async Task<(string Root, string MemPath, string HistoryDir)> WorkspaceAsync()
    {
        var root = Directory.CreateTempSubdirectory("inferpal-mem").FullName;
        var dir  = Path.Combine(root, ".inferpal");
        Directory.CreateDirectory(dir);
        var memPath = Path.Combine(dir, "memory.md");
        await File.WriteAllTextAsync(memPath, Remembered);
        return (root, memPath, FileHistoryService.GetHistoryDir(memPath));
    }

    [Fact]
    public async Task ClearingTheMemory_IsRefused_WhenNoSnapshotCouldBeSaved()
    {
        var (root, memPath, historyDir) = await WorkspaceAsync();
        try
        {
            // A FILE where the history folder should be born: Directory.CreateDirectory throws on
            // it, on every platform and whatever the account.
            Directory.CreateDirectory(Path.GetDirectoryName(historyDir)!);
            await File.WriteAllTextAsync(historyDir, "not a directory");

            // WITNESS: the fixture REALLY does break the snapshot. Without it, this test would
            // measure a world where the defect does not exist and stay green on the old code.
            var probe = await new FileHistoryService().SnapshotAsync(memPath, CancellationToken.None);
            Assert.Equal(string.Empty, probe);

            var tool = new UpdateMemoryTool(new NoEditor(), new YesApproval(), new FileHistoryService(), () => root);

            var answer = await tool.ExecuteAsync(
                Raw("""{"mode":"clear"}"""), CancellationToken.None);

            // The project memory is untouched — it is what goes back into the system prompt of
            // toutes les sessions suivantes.
            Assert.Equal(Remembered, await File.ReadAllTextAsync(memPath));
            Assert.Equal(FileHistoryService.BackupFailedMessage(memPath), answer);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task ReplacingTheMemory_IsRefusedToo_WhenNoSnapshotCouldBeSaved()
    {
        // Same door: `replace` overwrites just as much as `clear` erases.
        var (root, memPath, historyDir) = await WorkspaceAsync();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(historyDir)!);
            await File.WriteAllTextAsync(historyDir, "not a directory");

            var tool = new UpdateMemoryTool(new NoEditor(), new YesApproval(), new FileHistoryService(), () => root);

            await tool.ExecuteAsync(
                Raw("""{"mode":"replace","content":"nothing matters"}"""), CancellationToken.None);

            Assert.Equal(Remembered, await File.ReadAllTextAsync(memPath));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task WithAWorkingHistory_ClearStillClears()
    {
        // Reference arm: the nominal path does not move, and the net really is there.
        var (root, memPath, historyDir) = await WorkspaceAsync();
        try
        {
            var tool = new UpdateMemoryTool(new NoEditor(), new YesApproval(), new FileHistoryService(), () => root);

            var answer = await tool.ExecuteAsync(Raw("""{"mode":"clear"}"""), CancellationToken.None);

            Assert.Equal(string.Empty, await File.ReadAllTextAsync(memPath));
            Assert.NotEqual(FileHistoryService.BackupFailedMessage(memPath), answer);
            // And the snapshot really is there: otherwise the refusal above would guard a fiction.
            Assert.True(Directory.Exists(historyDir) && Directory.GetFiles(historyDir).Length > 0,
                        "no snapshot written: the reference arm proves nothing.");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task AppendingToAMemoryThatDoesNotExistYet_IsNotBlocked()
    {
        // ⚠ Reference arm for the GUARD itself: an absent file has nothing to back up, and
        // BackUpBeforeChangeAsync returns (true, "") for that. Confusing the two would forbid the
        // very first write of a project's memory.
        var root = Directory.CreateTempSubdirectory("inferpal-mem").FullName;
        try
        {
            var tool = new UpdateMemoryTool(new NoEditor(), new YesApproval(), new FileHistoryService(), () => root);

            await tool.ExecuteAsync(
                Raw("""{"mode":"append","content":"first note"}"""), CancellationToken.None);

            var memPath = Path.Combine(root, ".inferpal", "memory.md");
            Assert.Contains("first note", await File.ReadAllTextAsync(memPath), StringComparison.Ordinal);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void EveryToolThatWritesAFile_HonoursTheBackupItAsksFor()
    {
        // NAMED assertion: the only other caller of SnapshotAsync is EditorWriteGate, whose
        // best-effort is reasoned in its own comment (it writes the buffer, not the disk). A scan
        // rule would therefore live off that single exemption.
        var code = ConventionCoverageTests.CodeOnly(Path.Combine(
            RepoRoot(), "Inferpal.Core", "Services", "Tools", "UpdateMemoryTool.cs"));

        Assert.Contains("BackUpBeforeChangeAsync", code, StringComparison.Ordinal);
        Assert.Contains("BackupFailedMessage", code, StringComparison.Ordinal);
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
