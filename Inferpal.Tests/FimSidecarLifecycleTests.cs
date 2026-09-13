#if WINDOWS
using System.IO;
using Inferpal.GhostText;
using Inferpal.Services.Signals;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// The FIM sidecar's lifecycle against a real child process that dies the moment it starts — the
/// shape of a sidecar missing an assembly, which ghost text must neither hammer nor hide.
/// </summary>
/// <remarks>
/// The stand-in is <c>whoami.exe</c> copied under the sidecar's name: it rejects the
/// <c>--vs-pid</c> argument and exits at once, without ever speaking the protocol.
/// </remarks>
[Collection(SignalCollection.Name)]
public sealed class FimSidecarLifecycleTests : IDisposable
{
    // A new configuration stamp per test: the sidecar's back-off is process-wide, and a new
    // configuration is exactly what clears it.
    private static long _stamp = Environment.TickCount64;

    private readonly SignalScratchDir _signals = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"Inferpal_fim_{Guid.NewGuid():N}");

    public FimSidecarLifecycleTests()
    {
        Directory.CreateDirectory(_dir);
        File.Copy(Path.Combine(Environment.SystemDirectory, "whoami.exe"), Path.Combine(_dir, "Inferpal.Fim.exe"));
        FimSidecar.DirectoryOverride = _dir;

        // Production always opens a door before the sidecar is asked anything.
        SignalScope.DeclareVsInstance(SignalFile.CurrentPid);
        InProcAliveSignal.Record(InProcAliveSignal.ComponentMef);
    }

    public void Dispose()
    {
        FimSidecar.Shutdown();
        FimSidecar.DirectoryOverride = null;
        _signals.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { /* the exe may still be mapped for a moment */ }
    }

    private static async Task<string?> AskAsync(long stamp)
    {
        using var belt = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        return await FimSidecar.CompleteAsync("int x = ", ";", 8, 0.2, null, stamp, belt.Token);
    }

    [Fact]
    public async Task ASidecarThatDiesAtStart_IsNotRelaunchedOnEveryCompletion()
    {
        var stamp  = Interlocked.Increment(ref _stamp);
        var before = FimSidecar.LaunchCount;

        for (var i = 0; i < 3; i++)
            Assert.Null(await AskAsync(stamp));

        // One launch, then the door is held for the cooldown: before, every pause in typing started
        // the dead sidecar again.
        Assert.Equal(1, FimSidecar.LaunchCount - before);
    }

    [Fact]
    public async Task ASidecarThatDiesWithoutAnswering_IsNotReportedAsAnswering_AndSaysWhy()
    {
        Assert.Null(await AskAsync(Interlocked.Increment(ref _stamp)));

        var state = InProcAliveSignal.TryRead();
        Assert.NotNull(state);
        Assert.False(state!.HasFim, "A sidecar that never answered was recorded as answering.");
        Assert.False(string.IsNullOrWhiteSpace(state.FimReason), "The sidecar died and nothing says why.");
    }
}
#endif
