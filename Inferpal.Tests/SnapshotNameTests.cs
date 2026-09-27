using System.IO;
using Inferpal.Services.Execution;
using Xunit;

namespace Inferpal.Tests;

// ──────────────────────────────────────────────────────────────────────────────────────────────
//  A snapshot does not look like the file it backs up.
//
//  The history folder sits inside the project tree, and test runners find tests by their name. Measured
//  with a passing test and a stale backup of it in .inferpal/history/: jest, vitest and node --test
//  (Node 20) ran "<timestamp>_<hash>_sum.test.js" too — 2 suites, the backup red; named "….test.js.bak",
//  1 suite, green. In a real project the backup's relative imports no longer resolve, so every test file
//  the agent had edited was a failing suite in npm test, run_tests and /tdd. Snapshots written before the
//  extension are renamed the next time the folder is written to, and stay restorable.
// ──────────────────────────────────────────────────────────────────────────────────────────────
public class SnapshotNameTests
{
    private static string NewRoot() => Directory.CreateTempSubdirectory("inferpal-snapname").FullName;

    [Fact]
    public async Task ASnapshotOfATestFile_DoesNotEndLikeATestFile_AndIsStillFound()
    {
        var root = NewRoot();
        try
        {
            var file = Path.Combine(root, "sum.test.js");
            await File.WriteAllTextAsync(file, "test('adds', () => {});");
            var history = new FileHistoryService();

            var snap = await history.SnapshotAsync(file, CancellationToken.None);

            Assert.NotEqual(string.Empty, snap);   // witness: a snapshot was taken
            Assert.Equal(".bak", Path.GetExtension(snap));
            Assert.EndsWith("_sum.test.js" + FileHistoryService.SnapshotExtension, snap);
            Assert.Equal(snap, history.FindMostRecentSnapshot(file));
            Assert.True(FileHistoryService.IsSnapshotOf(snap, file));   // what restore_file accepts
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }

    [Fact]
    public async Task ASnapshotFromBeforeTheExtension_IsRenamedByTheNextWrite_AndStaysRestorable()
    {
        var root = NewRoot();
        try
        {
            var test = Path.Combine(root, "sum.test.js");
            await File.WriteAllTextAsync(test, "current");
            var historyDir = FileHistoryService.GetHistoryDir(test);
            Directory.CreateDirectory(historyDir);
            var older = Path.Combine(historyDir, $"2026-09-01_10-00-00-000_{FileHistoryService.PathHash(test)}_sum.test.js");
            await File.WriteAllTextAsync(older, "stale");

            // Before any write the older form is still read.
            Assert.Equal(older, new FileHistoryService().FindMostRecentSnapshot(test));

            // A write of ANOTHER file in the same folder renames it.
            var other = Path.Combine(root, "other.js");
            await File.WriteAllTextAsync(other, "x");
            Assert.NotEqual(string.Empty, await new FileHistoryService().SnapshotAsync(other, CancellationToken.None));

            Assert.False(File.Exists(older));
            var renamed = older + FileHistoryService.SnapshotExtension;
            Assert.Equal(renamed, new FileHistoryService().FindMostRecentSnapshot(test));
            Assert.Equal("stale", await File.ReadAllTextAsync(renamed));
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }

    [Fact]
    public async Task TheRename_LeavesCurrentSnapshotsAndOtherFilesAlone()
    {
        // Reference arms: a name already in the current form is not given a second extension, and a file that
        // is not a snapshot (no date prefix) is not a snapshot to rename.
        var root = NewRoot();
        try
        {
            var file = Path.Combine(root, "a.cs");
            await File.WriteAllTextAsync(file, "class A {}");
            var historyDir = FileHistoryService.GetHistoryDir(file);
            Directory.CreateDirectory(historyDir);
            var current = Path.Combine(historyDir, $"2026-09-01_10-00-00-000_{FileHistoryService.PathHash(file)}_a.cs.bak");
            var notes   = Path.Combine(historyDir, "README.txt");
            await File.WriteAllTextAsync(current, "kept");
            await File.WriteAllTextAsync(notes, "mine");

            Assert.NotEqual(string.Empty, await new FileHistoryService().SnapshotAsync(file, CancellationToken.None));

            Assert.True(File.Exists(current));
            Assert.False(File.Exists(current + FileHistoryService.SnapshotExtension));
            Assert.True(File.Exists(notes));
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }
}
