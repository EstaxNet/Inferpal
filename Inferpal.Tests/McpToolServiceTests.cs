using System.Diagnostics;
using System.Text.Json;
using Inferpal.Config;
using Inferpal.Services;
using Inferpal.Services.Mcp;
using Xunit;

namespace Inferpal.Tests;

public class McpToolServiceTests
{
    // ── Test doubles ──────────────────────────────────────────────────────────

    private sealed class AutoApprove : IApprovalService
    {
        public Task<bool> RequestApprovalAsync(string toolName, string details, CancellationToken ct, string? subject = null)
            => Task.FromResult(true);
    }

    private sealed class FakeMcpClient(string name) : IMcpClient
    {
        public string ServerName => name;
        public string? LastError { get; set; }
        public bool NeedsAuthorization { get; set; }
        public bool StartResult { get; set; } = true;
        public List<McpToolInfo> ToolList { get; set; } = [];
        public int StartCount;
        public bool Disposed { get; private set; }

        public event Action? ToolsChanged;
        public event Action? Closed;

        public Task<bool> StartAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref StartCount);
            return Task.FromResult(StartResult);
        }

        public Task<IReadOnlyList<McpToolInfo>> ListToolsAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<McpToolInfo>>(ToolList.ToList());

        public Task<string> CallToolAsync(string toolName, JsonElement arguments, CancellationToken ct)
            => Task.FromResult("ok");

        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }

        public void RaiseToolsChanged() => ToolsChanged?.Invoke();
        public void RaiseClosed()       => Closed?.Invoke();
    }

    private static McpToolInfo Tool(string name) =>
        new(name, "desc", JsonDocument.Parse("{}").RootElement.Clone());

    private static string OneServer(string name) => $$"""{ "{{name}}": { "command": "x" } }""";

    private static readonly IReadOnlyList<TimeSpan> FastBackoff = [TimeSpan.FromMilliseconds(5)];

    /// <summary>Builds a disabled-by-default service so the constructor doesn't auto-start; the caller
    /// flips McpEnabled and awaits RefreshAsync for deterministic, single-pass discovery.</summary>
    private static McpToolService NewService(InferpalConfig config, Func<McpServerConfig, IMcpClient> factory,
                                             IReadOnlyList<TimeSpan>? backoff = null)
        => new(config, new AutoApprove(), factory, backoff ?? FastBackoff);

    private static async Task WaitUntil(Func<bool> cond, string because, int timeoutMs = 2000)
    {
        var sw = Stopwatch.StartNew();
        while (!cond() && sw.ElapsedMilliseconds < timeoutMs)
            await Task.Delay(10);
        Assert.True(cond(), because);
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RefreshAsync_DiscoversAndNamespacesTools()
    {
        var config = new InferpalConfig { McpServersJson = OneServer("srv") };
        var client = new FakeMcpClient("srv") { ToolList = [Tool("read"), Tool("write")] };
        await using var svc = NewService(config, _ => client);

        config.McpEnabled = true;
        await svc.RefreshAsync();

        Assert.Equal(["mcp__srv__read", "mcp__srv__write"], svc.Tools.Select(t => t.Name));
        var status = Assert.Single(svc.Status);
        Assert.True(status.Connected);
        Assert.Equal(2, status.ToolCount);
    }

    [Fact]
    public async Task RefreshAsync_SkipsDisabledServers()
    {
        var config = new InferpalConfig
        {
            McpServersJson = """{ "on": { "command": "x" }, "off": { "command": "y", "disabled": true } }""",
        };
        var started = new FakeMcpClient("on") { ToolList = [Tool("t")] };
        await using var svc = NewService(config, cfg => cfg.Name == "on" ? started : new FakeMcpClient(cfg.Name));

        config.McpEnabled = true;
        await svc.RefreshAsync();

        var status = Assert.Single(svc.Status);
        Assert.Equal("on", status.Name);
    }

    [Fact]
    public async Task RefreshAsync_FailedStart_ReportedAsDisconnectedWithError()
    {
        var config = new InferpalConfig { McpServersJson = OneServer("srv") };
        var client = new FakeMcpClient("srv") { StartResult = false, LastError = "boom" };
        await using var svc = NewService(config, _ => client);

        config.McpEnabled = true;
        await svc.RefreshAsync();

        Assert.Empty(svc.Tools);
        var status = Assert.Single(svc.Status);
        Assert.False(status.Connected);
        Assert.Equal("boom", status.Error);
        Assert.True(client.Disposed);   // a server that won't start is disposed, not retained
    }

    [Fact]
    public async Task FailedStart_NeedingAuth_SurfacesAuthRequiredStatus()
    {
        var config = new InferpalConfig { McpServersJson = OneServer("srv") };
        var client = new FakeMcpClient("srv") { StartResult = false, NeedsAuthorization = true, LastError = "401" };
        await using var svc = NewService(config, _ => client);

        config.McpEnabled = true;
        await svc.RefreshAsync();

        var status = Assert.Single(svc.Status);
        Assert.False(status.Connected);
        Assert.True(status.AuthRequired);
    }

    [Fact]
    public async Task ToolsChanged_RerunsDiscoveryLive()
    {
        var config = new InferpalConfig { McpServersJson = OneServer("srv") };
        var client = new FakeMcpClient("srv") { ToolList = [Tool("a")] };
        await using var svc = NewService(config, _ => client);

        config.McpEnabled = true;
        await svc.RefreshAsync();
        Assert.Single(svc.Tools);

        client.ToolList = [Tool("a"), Tool("b")];
        client.RaiseToolsChanged();

        await WaitUntil(() => svc.Tools.Count == 2, "live re-discovery should pick up the new tool");
        Assert.Equal(["mcp__srv__a", "mcp__srv__b"], svc.Tools.Select(t => t.Name));
    }

    [Fact]
    public async Task Closed_DropsToolsThenReconnects()
    {
        var config = new InferpalConfig { McpServersJson = OneServer("srv") };
        var first  = new FakeMcpClient("srv") { ToolList = [Tool("a")] };
        var second = new FakeMcpClient("srv") { ToolList = [Tool("a"), Tool("b")] };
        var queue  = new Queue<FakeMcpClient>([first, second]);
        await using var svc = NewService(config, _ => queue.Dequeue());

        config.McpEnabled = true;
        await svc.RefreshAsync();
        Assert.Single(svc.Tools);

        first.RaiseClosed();

        await WaitUntil(() => svc.Tools.Count == 2, "tools should reappear after a successful reconnect");
        Assert.True(first.Disposed, "the dead client should be disposed");
        Assert.Equal(["mcp__srv__a", "mcp__srv__b"], svc.Tools.Select(t => t.Name));
        Assert.True(svc.Status.Single().Connected);
    }

    [Fact]
    public async Task Closed_ReconnectFails_LeavesServerDisconnected()
    {
        var config = new InferpalConfig { McpServersJson = OneServer("srv") };
        var first   = new FakeMcpClient("srv") { ToolList = [Tool("a")] };
        var dead1   = new FakeMcpClient("srv") { StartResult = false };
        var dead2   = new FakeMcpClient("srv") { StartResult = false };
        var queue   = new Queue<FakeMcpClient>([first, dead1, dead2]);
        // Two backoff slots ⇒ two reconnect attempts, both failing.
        await using var svc = NewService(config, _ => queue.Dequeue(),
                                         [TimeSpan.FromMilliseconds(5), TimeSpan.FromMilliseconds(5)]);

        config.McpEnabled = true;
        await svc.RefreshAsync();

        first.RaiseClosed();

        await WaitUntil(() => svc.Status.Single().Error == "server exited — reconnect failed",
                        "after exhausting backoff the server stays disconnected");
        Assert.Empty(svc.Tools);
        Assert.False(svc.Status.Single().Connected);
        Assert.Empty(queue);   // both reconnect clients were consumed
    }

    [Fact]
    public async Task DisableMcp_RefreshTearsDownServers()
    {
        var config = new InferpalConfig { McpServersJson = OneServer("srv") };
        var client = new FakeMcpClient("srv") { ToolList = [Tool("a")] };
        await using var svc = NewService(config, _ => client);

        config.McpEnabled = true;
        await svc.RefreshAsync();
        Assert.Single(svc.Tools);

        config.McpEnabled = false;
        await svc.RefreshAsync();

        Assert.Empty(svc.Tools);
        Assert.Empty(svc.Status);
        Assert.True(client.Disposed);
    }
}
