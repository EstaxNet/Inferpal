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
