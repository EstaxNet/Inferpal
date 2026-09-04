using System.IO;
using Inferpal.Services;
using Xunit;

namespace Inferpal.Tests;

// Run grouping behind /undo-run: first-change-per-file tracking (pure) plus the restore/delete
// integration over real temp files.
public class FileHistoryRunTests
{
    // ── HistoryRun (pure) ──────────────────────────────────────────────────────

    [Fact]
    public void RecordFirst_KeepsFirstSnapshotPerFile()
    {
        var run = new HistoryRun("r1");
        run.RecordFirst(@"C:\a.cs", "snap-1");
        run.RecordFirst(@"C:\a.cs", "snap-2");   // later edit in same run — ignored
        run.RecordFirst(@"C:\b.cs", null);       // created this run

        Assert.Equal(2, run.FileCount);
        Assert.Equal("snap-1", run.Changes.First(c => c.OriginalPath == @"C:\a.cs").SnapshotPath);
        Assert.Null(run.Changes.First(c => c.OriginalPath == @"C:\b.cs").SnapshotPath);
    }

    [Fact]
    public void Runs_AreMostRecentFirst()
    {
        var svc = new FileHistoryService();
        var first  = svc.BeginRun();
        var second = svc.BeginRun();

        Assert.Equal(second, svc.Runs[0].Id);
        Assert.Equal(first,  svc.Runs[1].Id);
    }

    /// <summary>
    /// What is written <b>after</b> a run belongs to no run.
    /// </summary>
    /// <remarks>
    /// ⚠ <c>BeginRun</c> had no counterpart, so <c>_currentRun</c> stayed the last open run
    /// forever. The contract written on <c>RecordToolCall</c> — "No-op when no run is active" —
    /// was therefore only true before the session's first run. Consequence: a <c>/restore</c> or a
    /// tool launched by a slash command after the turn attached to it, <c>/undo-run</c> reverted
    /// it along with the turn, and <c>/replay</c> listed it in the journal of a run where it never
    /// happened.
    /// </remarks>
    [Fact]
    public async Task AfterEndRun_ALaterWriteBelongsToNoRun()
    {
        using var tmp = new TempDir();
        var during = tmp.File("during.txt", "before-run");
        var after  = tmp.File("after.txt",  "after-run");

        var svc = new FileHistoryService();
        svc.BeginRun();
        await svc.SnapshotAsync(during, CancellationToken.None);
        svc.EndRun();

        // The next gesture: outside any run.
        await svc.SnapshotAsync(after, CancellationToken.None);
        svc.NoteCreated(Path.Combine(tmp.Path, "ghost.txt"));

        var run = svc.Runs.Single();
        Assert.Equal(1, run.FileCount);
        Assert.Equal(during, run.Changes.Single().OriginalPath);
    }

    /// <summary>The scope closes the run on every exit path, exception included.</summary>
    [Fact]
    public async Task BeginRunScope_ClosesTheRun_EvenOnAnEarlyExit()
    {
        using var tmp = new TempDir();
        var inside  = tmp.File("inside.txt",  "x");
        var outside = tmp.File("outside.txt", "y");

        var svc = new FileHistoryService();
        try
        {
            using var scope = svc.BeginRunScope();
            await svc.SnapshotAsync(inside, CancellationToken.None);
            throw new InvalidOperationException("abrupt exit, like a cancelled /tdd");
        }
        catch (InvalidOperationException) { }

        await svc.SnapshotAsync(outside, CancellationToken.None);

        var run = svc.Runs.Single();
        Assert.Equal(1, run.FileCount);
        Assert.Equal(inside, run.Changes.Single().OriginalPath);
    }

    // ── Undo integration (real temp files) ──────────────────────────────────────

    [Fact]
    public async Task UndoRun_RestoresModifiedFile()
    {
        using var tmp = new TempDir();
        var file = tmp.File("a.txt", "original");

        var svc = new FileHistoryService();
        svc.BeginRun();
        await svc.SnapshotAsync(file, CancellationToken.None);   // captures "original"
        await File.WriteAllTextAsync(file, "modified by agent");

        var run    = svc.Runs.First(r => r.FileCount > 0);
        var result = await svc.UndoRunAsync(run, CancellationToken.None);

        Assert.Equal("original", await File.ReadAllTextAsync(file));
        Assert.Contains(file, result.Restored);
        Assert.Empty(result.Failed);
    }

    [Fact]
    public async Task UndoRun_DeletesFileCreatedDuringRun()
    {
        using var tmp = new TempDir();
        var created = Path.Combine(tmp.Path, "created.txt");

        var svc = new FileHistoryService();
        svc.BeginRun();
        svc.NoteCreated(created);                       // tool records a creation
        await File.WriteAllTextAsync(created, "new");   // ...then writes it

        var run    = svc.Runs.First(r => r.FileCount > 0);
        var result = await svc.UndoRunAsync(run, CancellationToken.None);

        Assert.False(File.Exists(created));
        Assert.Contains(created, result.Deleted);
    }

    [Fact]
    public async Task Restore_NeverConfusesHomonymsFromDifferentFolders()
    {
        // Snapshot names used to carry the bare file name only: restore_file on A\Config.cs
        // picked the most recent snapshot NAMED Config.cs — B's — and wrote B's content into A
        // with a plausible-looking approval diff (pre-1.6.0 architecture review, §1.4). The shared history dir
        // requires a common git root, hence the fake .git below.
        using var tmp = new TempDir();
        Directory.CreateDirectory(Path.Combine(tmp.Path, ".git"));
        Directory.CreateDirectory(Path.Combine(tmp.Path, "A"));
        Directory.CreateDirectory(Path.Combine(tmp.Path, "B"));
        var fileA = Path.Combine(tmp.Path, "A", "Config.cs"); await File.WriteAllTextAsync(fileA, "contentA");
        var fileB = Path.Combine(tmp.Path, "B", "Config.cs"); await File.WriteAllTextAsync(fileB, "contentB");

        var svc = new FileHistoryService();
        await svc.SnapshotAsync(fileA, CancellationToken.None);
        await Task.Delay(25);                                     // B strictly newer
        await svc.SnapshotAsync(fileB, CancellationToken.None);

        var snapForA = svc.FindMostRecentSnapshot(fileA);
        Assert.NotNull(snapForA);
        Assert.Equal("contentA", await File.ReadAllTextAsync(snapForA!));
    }

    [Fact]
    public async Task SnapshotFailure_IsReportedByUndoRun_AndNeverDeletesTheFile()
    {
        // A locked file (antivirus, another process, full disk…) used to fail the snapshot in a
        // bare catch: the write went ahead with no net and the file silently vanished from the
        // /undo-run perimeter (pre-1.6.0 architecture review, §1.3). It must surface as Failed — and never be
        // treated as "created this run" (which undo would DELETE).
        using var tmp = new TempDir();
        var file = tmp.File("locked.txt", "precious");

        var svc = new FileHistoryService();
        svc.BeginRun();
        using (new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.Equal(string.Empty, await svc.SnapshotAsync(file, CancellationToken.None));

        var run    = svc.Runs.First(r => r.FileCount > 0);
        var result = await svc.UndoRunAsync(run, CancellationToken.None);

        Assert.Contains(file, result.Failed);
        Assert.True(File.Exists(file));
        Assert.Equal("precious", await File.ReadAllTextAsync(file));
    }

    [Fact]
    public async Task SnapshotWithoutActiveRun_DoesNotThrow_AndTracksNothing()
    {
        using var tmp = new TempDir();
        var file = tmp.File("a.txt", "x");

        var svc = new FileHistoryService();                 // no BeginRun
        await svc.SnapshotAsync(file, CancellationToken.None);

        Assert.Empty(svc.Runs);
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } =
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "inferpal_hist_" + Guid.NewGuid().ToString("N"));

        public TempDir() => Directory.CreateDirectory(Path);

        public string File(string name, string content)
        {
            var p = System.IO.Path.Combine(Path, name);
            System.IO.File.WriteAllText(p, content);
            return p;
        }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { }
        }
    }
}
