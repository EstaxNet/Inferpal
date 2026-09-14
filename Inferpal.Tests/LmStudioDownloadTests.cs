using Inferpal.Config;
using Inferpal.Services.Inference;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// <c>/models pull</c> under LM Studio reports what the download job became, not that it started.
/// </summary>
/// <remarks>
/// <c>POST /api/v1/models/download</c> answers at once with a job (<c>"status":"downloading"</c>), and the
/// job is followed on <c>GET /api/v1/models/download/status/{job_id}</c> (LM Studio REST API reference).
/// The client read only the POST answer and reported a success: the model was announced as downloaded
/// while it was still downloading — or after the download had failed.
/// </remarks>
public class LmStudioDownloadTests
{
    private static LmStudioClient Client(LoopbackHttpServer server) =>
        new(new InferpalConfig { Provider = "lmstudio", BaseUrl = server.BaseUrl });

    [Fact]
    public async Task ADownloadJobThatFails_IsReportedAsAFailure()
    {
        using var server = new LoopbackHttpServer(p => p switch
        {
            "/api/v1/models/download"              => """{"job_id":"job_1","status":"downloading","total_size_bytes":100}""",
            "/api/v1/models/download/status/job_1" => """{"job_id":"job_1","status":"failed"}""",
            _                                      => null,
        });

        var ok = await Client(server).PullModelAsync("org/model", _ => { }, CancellationToken.None);

        Assert.False(ok);
        Assert.Contains("/api/v1/models/download/status/job_1", server.Paths);
    }

    [Fact]
    public async Task ADownloadJob_IsFollowedUntilItCompletes_WithItsProgress()
    {
        var polls = 0;
        using var server = new LoopbackHttpServer(p => p switch
        {
            "/api/v1/models/download" => """{"job_id":"job_2","status":"downloading","total_size_bytes":200}""",
            "/api/v1/models/download/status/job_2" => Interlocked.Increment(ref polls) == 1
                ? """{"job_id":"job_2","status":"downloading","total_size_bytes":200,"downloaded_bytes":100}"""
                : """{"job_id":"job_2","status":"completed","total_size_bytes":200,"downloaded_bytes":200}""",
            _ => null,
        });
        var statuses = new List<string>();

        var ok = await Client(server).PullModelAsync("org/model", s => { lock (statuses) statuses.Add(s); },
                                                     CancellationToken.None);

        Assert.True(ok);
        Assert.True(polls >= 2, $"the job was polled {polls} time(s)");
        Assert.Contains(statuses, s => s.Contains("50%", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AModelAlreadyDownloaded_IsASuccess_WithoutPolling()
    {
        using var server = new LoopbackHttpServer(p => p == "/api/v1/models/download"
            ? """{"status":"already_downloaded"}"""
            : null);

        var ok = await Client(server).PullModelAsync("org/model", _ => { }, CancellationToken.None);

        Assert.True(ok);
        Assert.DoesNotContain(server.Paths, p => p.Contains("/status", StringComparison.Ordinal));
    }

    /// <summary>Witness: a server that answers without a job keeps the 2xx as the signal.</summary>
    [Fact]
    public async Task AServerThatReturnsNoJob_KeepsTheSuccessOfItsAnswer()
    {
        using var server = new LoopbackHttpServer(p => p == "/api/v1/models/download" ? "{}" : null);

        Assert.True(await Client(server).PullModelAsync("org/model", _ => { }, CancellationToken.None));
    }
}
