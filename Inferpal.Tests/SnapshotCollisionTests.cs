using System.IO;
using Inferpal.Services.Execution;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A <see cref="FileHistoryService"/> snapshot never overwrites another one.
/// </summary>
/// <remarks>
/// A snapshot's name is its millisecond timestamp followed by the file's identity, and the write
/// overwrote without looking. Two snapshots of the same file within the same millisecond therefore
/// shared one file: the second replaced the first. That is exactly what <c>/undo-run</c> does — it
/// snapshots the current state before restoring — and on a fast runner the restore read back the
/// state it was meant to undo (Ubuntu CI red on
/// <c>UndoRun_RestoresTheFileAndReportsItRelativeToTheRoot</c>). The clock is frozen here so the test
/// does not depend on the machine's speed.
/// </remarks>
public sealed class SnapshotCollisionTests : IDisposable
{
    private static readonly DateTime Frozen = new(2026, 9, 13, 1, 2, 3, 456, DateTimeKind.Utc);

    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("inferpal-snapcollide-");

    public void Dispose()
    {
        try { _dir.Delete(recursive: true); } catch { /* best-effort cleanup */ }
    }

    [Fact]
    public async Task UndoRun_RestoresThePreRunContent_WhenItsOwnSnapshotLandsInTheSameMillisecond()
    {
        var history = new FileHistoryService { UtcNow = () => Frozen };
        var file    = Path.Combine(_dir.FullName, "a.txt");
        await File.WriteAllTextAsync(file, "before");

        history.BeginRun();
        await history.SnapshotAsync(file, CancellationToken.None);
        await File.WriteAllTextAsync(file, "after");

        await history.UndoRunAsync(history.Runs[0], CancellationToken.None);

        Assert.Equal("before", await File.ReadAllTextAsync(file));
    }

    [Fact]
    public async Task TwoSnapshotsInTheSameMillisecond_AreBothKept_AndTheLaterOneIsTheMostRecent()
    {
        var history = new FileHistoryService { UtcNow = () => Frozen };
        var file    = Path.Combine(_dir.FullName, "b.txt");

        await File.WriteAllTextAsync(file, "v1");
        var first = await history.SnapshotAsync(file, CancellationToken.None);
        await File.WriteAllTextAsync(file, "v2");
        var second = await history.SnapshotAsync(file, CancellationToken.None);

        Assert.NotEqual(first, second);
        Assert.Equal("v1", await File.ReadAllTextAsync(first));
        Assert.Equal("v2", await File.ReadAllTextAsync(second));

        // Witness: the later snapshot is still the one restore_file picks.
        Assert.Equal(second, history.FindMostRecentSnapshot(file));
    }
}
