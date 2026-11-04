using System.IO;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// <c>File.Replace</c> still holds a signal when the rename's event wakes its reader: the first read fails, and a reader
/// the watcher woke gets no other chance. That moment is read through, never taken for "no signal"; what is really
/// absent, or broken, waits for nothing, and neither does a thread that draws the editor.
/// </summary>
/// <remarks>
/// The file is let go from the reader's own wait, so no test depends on how fast anything runs. Reads run on a pool
/// thread, as a watcher's handler does: the suite's own threads have a synchronization context, under which nothing waits.
/// </remarks>
[Collection(SignalCollection.Name)]
public sealed class SignalHeldByItsWriterTests : IDisposable
{
    private sealed record Marker(string Id);

    private readonly SignalScratchDir _signals = new();
    private readonly List<TimeSpan> _waits = [];
    private FileStream? _held;

    public void Dispose()
    {
        _held?.Dispose();
        _signals.Dispose();
    }

    private string SignalPath(string name) => Path.Combine(_signals.Path, name);

    private void Hold(string path) => _held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);

    private void HoldUntilTheFirstWait(string path)
    {
        Hold(path);
        SignalFile._readWaitOverride = wait => { _waits.Add(wait); _held?.Dispose(); };
    }

    [Fact]
    public async Task AKeyPress_ReadWhileItsWriterHoldsTheFile_IsServed()
    {
        if (!OperatingSystem.IsWindows()) return;   // FileShare.None is advisory elsewhere
        CompletionAskSignal.Write(null, "nothing", "failed", "list", "no editor");
        HoldUntilTheFirstWait(CompletionAskSignal.RequestPath);

        var ask = await Task.Run(() => CompletionAskSignal.TryTake(null));

        Assert.Equal("nothing", ask?.Nothing);
        Assert.Single(_waits);
    }

    [Fact]
    public async Task ADrop_ReadWhileItsWriterHoldsTheFile_IsTaken()
    {
        if (!OperatingSystem.IsWindows()) return;   // FileShare.None is advisory elsewhere
        ChatDropSignal.Write([@"C:\src\a.cs"], []);
        HoldUntilTheFirstWait(ChatDropSignal.FilePath);

        var drop = await Task.Run(ChatDropSignal.Take);

        Assert.Equal([@"C:\src\a.cs"], drop?.Paths);
    }

    [Fact]
    public async Task ABuildSignal_ThatDoesNotGoThroughTryRead_IsReadThroughTheSameMoment()
    {
        if (!OperatingSystem.IsWindows()) return;   // FileShare.None is advisory elsewhere
        BuildSignalFile.Write(@"C:\src\Shop.sln", ["CS0103"], 1);
        HoldUntilTheFirstWait(BuildSignalFile.FilePath);

        // The monitor clears the file right after this read.
        var payload = await Task.Run(BuildSignalFile.TryRead);

        Assert.Equal(@"C:\src\Shop.sln", payload.SolutionPath);
    }

    [Fact]
    public async Task ARefusedOpen_TheOtherShapeOfAHeldFile_IsWaitedThrough()
    {
        if (!OperatingSystem.IsWindows()) return;   // the shape ERROR_ACCESS_DENIED takes in .NET
        var path = SignalPath("refused.json");
        Directory.CreateDirectory(path);   // opening a folder is refused, as a file on its way out is
        SignalFile._readWaitOverride = wait =>
        {
            _waits.Add(wait);
            Directory.Delete(path);
            File.WriteAllText(path, "text");
        };

        Assert.Equal("text", await Task.Run(() => SignalFile.ReadShared(path)));
        Assert.Single(_waits);
    }

    [Fact]
    public async Task AFileNeverLetGo_ReadsAsNoSignal_AfterAWaitThatIsBounded()
    {
        if (!OperatingSystem.IsWindows()) return;   // FileShare.None is advisory elsewhere
        var path = SignalPath("held.json");
        SignalFile.Write(path, new Marker("a"), "test");
        Hold(path);
        SignalFile._readWaitOverride = _waits.Add;

        Assert.Null(await Task.Run(() => SignalFile.TryRead<Marker>(path)));

        Assert.NotEmpty(_waits);
        Assert.True(_waits.Sum(w => w.TotalMilliseconds) <= 100, $"waited {_waits.Sum(w => w.TotalMilliseconds)} ms");
    }

    [Fact]
    public async Task AWatchersEvent_ArrivesWithoutTheContextOfTheThreadThatStartedIt()
    {
        // The retry only runs without a synchronization context: one that flowed here would switch it off without a word.
        var woken = new TaskCompletionSource<SynchronizationContext?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new SynchronizationContext());
        try
        {
            using var watcher = new FileSystemWatcher(_signals.Path, "woken.json") { EnableRaisingEvents = true };
            watcher.Created += (_, _) => woken.TrySetResult(SynchronizationContext.Current);
            File.WriteAllText(SignalPath("woken.json"), "{}");

            Assert.Null(await woken.Task.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false));
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }

    [Fact]
    public void OnAThreadThatDrawsTheEditor_AHeldFile_WaitsForNothing()
    {
        if (!OperatingSystem.IsWindows()) return;   // FileShare.None is advisory elsewhere
        var path = SignalPath("drawn.json");
        SignalFile.Write(path, new Marker("a"), "test");
        Hold(path);
        SignalFile._readWaitOverride = _waits.Add;
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new SynchronizationContext());
        try
        {
            Assert.Null(SignalFile.TryRead<Marker>(path));
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }

        Assert.Empty(_waits);
    }

    [Fact]
    public async Task AnAbsentSignal_WaitsForNothing()
    {
        SignalFile._readWaitOverride = _waits.Add;
        var path = SignalPath("absent.json");

        Assert.Null(await Task.Run(() => SignalFile.TryRead<Marker>(path)));
        // The file that vanishes between the check and the open, and the folder that does.
        await Assert.ThrowsAsync<FileNotFoundException>(() => Task.Run(() => SignalFile.ReadShared(path)));
        await Assert.ThrowsAsync<DirectoryNotFoundException>(
            () => Task.Run(() => SignalFile.ReadShared(SignalPath(Path.Combine("gone", "a.json")))));

        Assert.Empty(_waits);
    }

    [Fact]
    public async Task AMalformedSignal_WaitsForNothing()
    {
        SignalFile._readWaitOverride = _waits.Add;
        var path = SignalPath("broken.json");
        File.WriteAllText(path, "{ not json");

        Assert.Null(await Task.Run(() => SignalFile.TryRead<Marker>(path)));

        Assert.Empty(_waits);
    }

    [Fact]
    public async Task AFreeSignal_IsReadAtOnce()
    {
        SignalFile._readWaitOverride = _waits.Add;
        var path = SignalPath("free.json");
        SignalFile.Write(path, new Marker("a"), "test");

        Assert.Equal("a", (await Task.Run(() => SignalFile.TryRead<Marker>(path)))?.Id);

        Assert.Empty(_waits);
    }
}
