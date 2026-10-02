using System.Diagnostics;
using System.IO;
using Inferpal.Services.Mcp;
using Xunit;

namespace Inferpal.Tests;

public class McpStdioClientTests
{
    // A "server" that exits immediately — cmd on Windows, /bin/sh elsewhere.
    private static McpServerConfig ExitsAtOnce() => OperatingSystem.IsWindows()
        ? new("test", "cmd.exe", ["/c", "exit"], new Dictionary<string, string>())
        : new("test", "/bin/sh", ["-c", "exit"], new Dictionary<string, string>());

    /// <summary>
    /// A server that dies AFTER the handshake is named by its exit code and what it wrote on stderr when the close is
    /// signalled — the start path did it for a failed start, a death mid-session left nothing to say.
    /// </summary>
    [Fact]
    public async Task AServerThatDiesMidSession_IsNamedWithItsExitCodeAndStderr()
    {
        if (!await PythonForTests.IsInstalledAsync()) return;   // UNDECIDED without Python on the machine — never read as a pass
        var script = Path.Combine(Path.GetTempPath(), $"inferpal-mcp-dies-{Guid.NewGuid():N}.py");
        File.WriteAllText(script, """
            import json, sys
            for line in sys.stdin:
                msg = json.loads(line)
                if msg.get("method") == "initialize":
                    print(json.dumps({"jsonrpc": "2.0", "id": msg["id"], "result": {"protocolVersion": "2024-11-05",
                          "capabilities": {"tools": {}}, "serverInfo": {"name": "dies", "version": "1"}}}), flush=True)
                elif msg.get("method") == "tools/list":
                    print(json.dumps({"jsonrpc": "2.0", "id": msg["id"], "result": {"tools": []}}), flush=True)
                elif msg.get("method") == "tools/call":
                    sys.stderr.write("dying on purpose\n"); sys.stderr.flush()
                    sys.exit(3)
            """);
        try
        {
            var client = new McpStdioClient(new("dies", OperatingSystem.IsWindows() ? "python" : "python3", [script],
                                                new Dictionary<string, string>()));
            var closed = new TaskCompletionSource();
            client.Closed += () => closed.TrySetResult();

            Assert.True(await client.StartAsync(CancellationToken.None), client.LastError);
            Assert.NotNull(await client.ListToolsAsync(CancellationToken.None));
            try { await client.CallToolAsync("x", System.Text.Json.JsonDocument.Parse("{}").RootElement, CancellationToken.None); }
            catch (Exception) { /* the call dies with the server — the point is what the close says */ }

            var fired = await Task.WhenAny(closed.Task, Task.Delay(TimeSpan.FromSeconds(30)));
            Assert.True(ReferenceEquals(fired, closed.Task), "Closed should fire when the server dies mid-session.");
            Assert.Contains("exited with code 3 mid-session", client.LastError);
            Assert.Contains("dying on purpose", client.LastError);
            await client.DisposeAsync();
        }
        finally { try { File.Delete(script); } catch { } }
    }

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
    public async Task ACommandThatDoesNotExist_ReturnsFalse_AndNamesIt_NeverThrows()
    {
        // StartAsync's contract: false and a cause, never an exception. Describing a failed start asked a process that
        // had never started for its exit — which throws — so a mistyped command escaped as an exception.
        var client = new McpStdioClient(new("typo", "inferpal-no-such-command-xyz", [], new Dictionary<string, string>()));

        Assert.False(await client.StartAsync(CancellationToken.None));
        Assert.Contains("inferpal-no-such-command-xyz", client.LastError);
        await client.DisposeAsync();
    }

    [Fact]
    public async Task AnNpxServer_Starts_OnEveryPlatform()
    {
        // `"command": "npx"` is the MCP configuration of most server READMEs, and of docs/mcp.md. On Windows npx is a
        // batch script (npx.cmd), which CreateProcess does not resolve: the server never started.
        if (!NpmTools.Installed()) return;   // UNDECIDED without Node on the machine — never read as a pass
        var client = new McpStdioClient(new("npx", "npx", ["--version"], new Dictionary<string, string>()));

        Assert.False(await client.StartAsync(CancellationToken.None));   // it is not an MCP server: the handshake fails
        Assert.Contains("exited", client.LastError);                      // …but it RAN
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
