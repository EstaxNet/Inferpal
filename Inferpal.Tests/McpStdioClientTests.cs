using System.Diagnostics;
using Inferpal.Services.Mcp;
using Xunit;

namespace Inferpal.Tests;

public class McpStdioClientTests
{
    private static McpServerConfig Cmd(params string[] args) =>
        new("test", "cmd.exe", args, new Dictionary<string, string>());

    [Fact]
    public async Task Closed_FiresWhenProcessExitsUnexpectedly()
    {
        // A process that exits straight away: the handshake fails (StartAsync returns false) and the
        // read loop ends — which is exactly the "server died" signal the reconnect logic listens for.
        var client = new McpStdioClient(Cmd("/c", "exit"));
        var closed = new TaskCompletionSource();
        client.Closed += () => closed.TrySetResult();

        var ok = await client.StartAsync(CancellationToken.None);

        Assert.False(ok);
        var fired = await Task.WhenAny(closed.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.True(ReferenceEquals(fired, closed.Task), "Closed should fire when the process exits on its own.");

        await client.DisposeAsync();
    }

    [Fact]
    public async Task Dispose_DoesNotRaiseASecondClose()
    {
        // After an unexpected exit (one Closed), disposing the already-dead client must not report
        // a further close — the read-loop guard keys off _disposed, set at the top of DisposeAsync.
        var client = new McpStdioClient(Cmd("/c", "exit"));
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
