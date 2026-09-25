using System.Diagnostics;
using Inferpal.Services.Mcp;
using Xunit;

namespace Inferpal.Tests;

public class McpStdioClientTests
{
    // A "server" that exits immediately — cmd on Windows, /bin/sh elsewhere.
    private static McpServerConfig ExitsAtOnce() => OperatingSystem.IsWindows()
        ? new("test", "cmd.exe", ["/c", "exit"], new Dictionary<string, string>())
        : new("test", "/bin/sh", ["-c", "exit"], new Dictionary<string, string>());

    [Fact]
    public async Task Closed_FiresWhenProcessExitsUnexpectedly()
    {
        // A process that exits straight away: the handshake fails (StartAsync returns false) and the
        // read loop ends — which is exactly the "server died" signal the reconnect logic listens for.
        var client = new McpStdioClient(ExitsAtOnce());
        var closed = new TaskCompletionSource();
        client.Closed += () => closed.TrySetResult();

        var ok = await client.StartAsync(CancellationToken.None);

        Assert.False(ok);
        // 30 s: we wait for the event to ARRIVE; the delay is only a deadlock guard.
        var fired = await Task.WhenAny(closed.Task, Task.Delay(TimeSpan.FromSeconds(30)));
        Assert.True(ReferenceEquals(fired, closed.Task), "Closed should fire when the process exits on its own.");

        await client.DisposeAsync();
    }

    // A server that fails the way real ones do at startup: its reason on stderr, then a non-zero exit.
    private static McpServerConfig DiesWithAReason() => OperatingSystem.IsWindows()
        ? new("gh", "cmd.exe", ["/c", "echo Error: GITHUB_TOKEN is not set 1>&2 & exit 3"], new Dictionary<string, string>())
        : new("gh", "/bin/sh", ["-c", "echo 'Error: GITHUB_TOKEN is not set' >&2; exit 3"], new Dictionary<string, string>());

    [Fact]
    public async Task AServerThatDiesAtStartup_IsReportedWithWhatItWroteAndItsExitCode()
    {
        // stderr is where a server writes WHY it cannot start (a missing token, a bad path, an npm 404) — and the
        // only place. Drained and discarded, the failure read "connection closed": the cause, lost.
        var client = new McpStdioClient(DiesWithAReason());

        Assert.False(await client.StartAsync(CancellationToken.None));

        Assert.Contains("GITHUB_TOKEN is not set", client.LastError);
        Assert.Contains("3", client.LastError);
        await client.DisposeAsync();
    }

    [Fact]
    public async Task AServerThatDiesSilently_StillSaysItExited()
    {
        // Reference arm: nothing on stderr — the exit is still named, and nothing is made up.
        var client = new McpStdioClient(ExitsAtOnce());

        Assert.False(await client.StartAsync(CancellationToken.None));

        Assert.Contains("exited", client.LastError);
        Assert.DoesNotContain("stderr:", client.LastError);
        await client.DisposeAsync();
    }

    [Fact]
    public async Task Dispose_DoesNotRaiseASecondClose()
    {
        // After an unexpected exit (one Closed), disposing the already-dead client must not report
        // a further close — the read-loop guard keys off _disposed, set at the top of DisposeAsync.
        var client = new McpStdioClient(ExitsAtOnce());
        var count = 0;
        client.Closed += () => Interlocked.Increment(ref count);

        await client.StartAsync(CancellationToken.None);
        // Let the unexpected-exit close land.
        for (var i = 0; i < 50 && Volatile.Read(ref count) == 0; i++)
            await Task.Delay(20);

        Assert.Equal(1, Volatile.Read(ref count));

        await client.DisposeAsync();
        await Task.Delay(100);

        Assert.Equal(1, Volatile.Read(ref count));   // no extra Closed from Dispose
    }
}
