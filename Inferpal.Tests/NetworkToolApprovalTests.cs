using System.Text.Json;
using Inferpal.Services;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

// fetch_url and web_search are outbound-network tools — covert exfiltration channels for a
// prompt-injected model. They must run through the approval gate; a denial must short-circuit
// BEFORE any network call. These tests would hang/throw if the tool ignored the denial and
// actually reached out, so passing also proves the early return.
public class NetworkToolApprovalTests
{
    private sealed class StubApproval(bool approve) : IApprovalService
    {
        public int Calls { get; private set; }
        public string? LastTool { get; private set; }
        public Task<bool> RequestApprovalAsync(string toolName, string details, CancellationToken ct, string? subject = null)
        {
            Calls++;
            LastTool = toolName;
            return Task.FromResult(approve);
        }
    }

    private static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public async Task FetchUrl_WhenDenied_ReturnsCancelled_WithoutFetching()
    {
        var approval = new StubApproval(approve: false);
        var tool     = new FetchUrlTool(approval);

        var result = await tool.ExecuteAsync(Args("""{"url":"https://example.com/"}"""), CancellationToken.None);

        Assert.Equal("Cancelled by user.", result);
        Assert.Equal(1, approval.Calls);
        Assert.Equal("fetch_url", approval.LastTool);
    }

    [Fact]
    public async Task WebSearch_WhenDenied_ReturnsCancelled_WithoutSearching()
    {
        var approval = new StubApproval(approve: false);
        var tool     = new WebSearchTool(approval);

        var result = await tool.ExecuteAsync(Args("""{"query":"secret data"}"""), CancellationToken.None);

        Assert.Equal("Cancelled by user.", result);
        Assert.Equal(1, approval.Calls);
        Assert.Equal("web_search", approval.LastTool);
    }
}
