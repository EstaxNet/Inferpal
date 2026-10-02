using System.Diagnostics;
using System.IO;
using System.Net.Http;
using Inferpal.Services.Inference;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A backend that never answers the TCP handshake fails as "unreachable" within the connect budget — not after the
/// operating system's own retries (127 s on Linux, where a stopped Ollama under WSL's mirrored networking drops
/// instead of refusing) — and the expiry is never read as somebody's cancellation.
/// </summary>
public class BackendConnectTimeoutTests
{
    // A connection that never establishes: the handshake no one answers, without needing a black-holed network.
    private static async ValueTask<Stream> NeverConnects(SocketsHttpConnectionContext _, CancellationToken ct)
    {
        await Task.Delay(System.Threading.Timeout.Infinite, ct);
        throw new InvalidOperationException("unreachable");
    }

    [Fact]
    public async Task AConnectionThatNeverEstablishes_FailsAsUnreachable_NamingTheAddress()
    {
        using var http = new HttpClient(ConnectDeadline.Create(TimeSpan.FromSeconds(1), NeverConnects))
            { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
        var sw = Stopwatch.StartNew();

        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => http.GetAsync("http://backend.test:1234/v1/models", CancellationToken.None));

        // An HttpRequestException, not a cancellation: the chat clients map it to "cannot reach", never to
        // "stopped responding (inactivity timeout)" nor to a silent user cancel.
        Assert.Contains("backend.test:1234", ex.Message);
        Assert.Contains("no connection could be established", ex.Message);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(30), $"took {sw.Elapsed}");
    }

    [Fact]
    public async Task TheCallersCancellation_StaysACancellation()
    {
        // Reference arm: a cancel the caller asked for — the user's Stop, or a call's own deadline — is still a
        // cancellation; only the expiry nobody asked for becomes "unreachable".
        using var http = new HttpClient(ConnectDeadline.Create(TimeSpan.FromSeconds(30), NeverConnects))
            { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => http.GetAsync("http://backend.test:1234/v1/models", cts.Token));
        Assert.True(cts.IsCancellationRequested);
    }

    [Fact]
    public void TheBackendClient_BoundsItsConnections()
    {
        // The budget is seconds, well under any OS's own retries, and the shared client is built on it.
        Assert.InRange(ConnectDeadline.Timeout, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(20));
        var code = ConventionCoverageTests.CodeOnly(Path.Combine(RepoRoot(),
            "Inferpal.Core", "Services", "Inference", "InferenceProviderBase.cs"));
        Assert.Contains("_http = new(ConnectDeadline.Create(ConnectDeadline.Timeout))", code);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "README.md")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
