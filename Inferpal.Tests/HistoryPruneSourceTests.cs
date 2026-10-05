using System.IO;
using System.Linq;
using System.Text.Json;
using Inferpal.Services.Execution;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

// ──────────────────────────────────────────────────────────────────────────────────────────────
//  A restore never deletes the version it is about to put back, and never overwrites without a backup.
//
//  The backup of the current state, taken before a restore_file or an /undo-run, went through the
//  20-per-file cap: when the source was the oldest kept, taking the backup deleted it, and the restore
//  failed with the approved version gone. A run writing one file twenty times lost its pre-run backup
//  to its own snapshots. And /undo-run deleted or overwrote a file it could not back up.
// ──────────────────────────────────────────────────────────────────────────────────────────────
public sealed class HistoryPruneSourceTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("inferpal-prunesource-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort cleanup */ }
    }

    /// <summary>A run that writes <paramref name="writes"/> versions of a file that started as "original".</summary>
    private async Task<(FileHistoryService History, HistoryRun Run, string Path)> RunWriting(int writes)
    {
        var path = Path.Combine(_dir, "a.txt");
        File.WriteAllText(path, "original");
        var stamp = new DateTime(2026, 10, 5, 12, 0, 0, DateTimeKind.Utc);
        var tick  = 0;
        var history = new FileHistoryService { UtcNow = () => stamp.AddSeconds(tick++) };
        history.BeginRun();
        for (var i = 0; i < writes; i++)
        {
            var (saved, snap) = await history.BackUpBeforeChangeAsync(path, CancellationToken.None);
            Assert.True(saved);
            File.SetLastWriteTimeUtc(snap, stamp.AddSeconds(tick));   // the prune orders by write time
            File.WriteAllText(path, $"v{i}");
        }
        history.EndRun();
        return (history, history.Runs[^1], path);
    }

    [Theory]
    [InlineData(20)]   // the undo's own backup was the 21st: it pruned the run's source
    [InlineData(25)]   // the run's own snapshots pruned it
    public async Task UndoingARun_PutsBackTheVersionFromBeforeIt(int writes)
    {
        var (history, run, path) = await RunWriting(writes);

        var undo = await history.UndoRunAsync(run, CancellationToken.None);

        Assert.Empty(undo.Failed);
        Assert.Equal("original", File.ReadAllText(path));
    }

    private sealed class Approve : IApprovalService
    {
        public Task<bool> RequestApprovalAsync(string toolName, string details, CancellationToken ct, string? subject = null,
                                               Services.CodeActions.DiffInfo? diff = null, bool forcePrompt = false)
            => Task.FromResult(true);
    }

    [Fact]
    public async Task RestoringTheOldestBackup_RestoresIt()
    {
        var path = Path.Combine(_dir, "b.txt");
        File.WriteAllText(path, "v0");
        var stamp = new DateTime(2026, 10, 5, 12, 0, 0, DateTimeKind.Utc);
        var tick  = 0;
        var history = new FileHistoryService { UtcNow = () => stamp.AddSeconds(tick++) };
        string? oldest = null;
        for (var i = 1; i <= 20; i++)
        {
            var (_, snap) = await history.BackUpBeforeChangeAsync(path, CancellationToken.None);
            File.SetLastWriteTimeUtc(snap, stamp.AddSeconds(tick));
            oldest ??= snap;
            File.WriteAllText(path, $"v{i}");
        }

        var said = await new RestoreFileTool(new Approve(), history, () => _dir).ExecuteAsync(
            JsonSerializer.SerializeToElement(new { path, snapshot = oldest }), CancellationToken.None);

        Assert.Equal("v0", File.ReadAllText(path));
        Assert.DoesNotContain("Error", said);
    }

    [Fact]
    public async Task UndoingACreation_ThatCannotBeBackedUp_DeletesNothing()
    {
        var path = Path.Combine(_dir, "created.txt");
        var history = new FileHistoryService();
        history.BeginRun();
        Assert.True((await history.BackUpBeforeChangeAsync(path, CancellationToken.None)).Saved);   // a creation
        File.WriteAllText(path, "user work");
        history.EndRun();
        File.WriteAllText(Path.Combine(_dir, ".inferpal"), "a file where the history folder would go");   // no backup possible

        var undo = await history.UndoRunAsync(history.Runs[^1], CancellationToken.None);

        Assert.True(File.Exists(path), "The file was deleted without a backup.");
        Assert.Equal("user work", File.ReadAllText(path));
        Assert.Contains(path, undo.Failed);
        Assert.Empty(undo.Deleted);
    }

    [Fact]
    public async Task UndoingACreation_WithABackup_StillDeletesIt()
    {
        // Reference arm: a file the run created is removed once its current state is saved.
        var path = Path.Combine(_dir, "created.txt");
        var history = new FileHistoryService();
        history.BeginRun();
        await history.BackUpBeforeChangeAsync(path, CancellationToken.None);
        File.WriteAllText(path, "made by the run");
        history.EndRun();

        var undo = await history.UndoRunAsync(history.Runs[^1], CancellationToken.None);

        Assert.False(File.Exists(path));
        Assert.Contains(path, undo.Deleted);
    }
}
