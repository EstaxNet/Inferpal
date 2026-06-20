using System.Text.Json;
using Inferpal.Services;
using Xunit;

namespace Inferpal.Tests;

// Covers CapForContext: large tool results must be capped before entering the agent context so a
// single oversized result (e.g. fetch_url, up to 50 000 chars) cannot blow past num_ctx and make
// Ollama silently truncate the request, dropping the system prompt + task + plan.
public class AgentOrchestratorCapTests
{
    private const int Max = AgentOrchestrator.MaxToolResultCharsInContext;

    [Fact]
    public void ShortResult_IsReturnedUnchanged()
    {
        var result = "small tool result";
        Assert.Equal(result, AgentOrchestrator.CapForContext(result));
    }

    [Fact]
    public void ResultAtExactlyMax_IsNotTruncated()
    {
        var result = new string('x', Max);
        Assert.Equal(result, AgentOrchestrator.CapForContext(result));
    }

    [Fact]
    public void OversizedResult_IsTruncatedWithNote()
    {
        var result = new string('x', Max + 5000);

        var capped = AgentOrchestrator.CapForContext(result);

        Assert.NotEqual(result, capped);
        Assert.StartsWith(new string('x', Max), capped);   // first Max chars preserved verbatim
        Assert.Contains("truncated", capped);              // truncation note appended
        Assert.Contains((Max + 5000).ToString(), capped);  // original length reported
        // The kept payload never exceeds Max; only the short note is added on top.
        Assert.True(capped.Length < result.Length);
        Assert.True(capped.Length <= Max + 200);
    }

    // ── ToolCallKey: intra-run dedup identity ──────────────────────────────────

    [Fact]
    public void IdenticalToolCall_ProducesSameKey()
    {
        using var a = JsonDocument.Parse("""{"url":"https://x.test/page"}""");
        using var b = JsonDocument.Parse("""{"url":"https://x.test/page"}""");

        Assert.Equal(
            AgentOrchestrator.ToolCallKey("fetch_url", a.RootElement),
            AgentOrchestrator.ToolCallKey("fetch_url", b.RootElement));
    }

    [Fact]
    public void DifferentArguments_ProduceDifferentKeys()
    {
        using var a = JsonDocument.Parse("""{"url":"https://x.test/a"}""");
        using var b = JsonDocument.Parse("""{"url":"https://x.test/b"}""");

        Assert.NotEqual(
            AgentOrchestrator.ToolCallKey("fetch_url", a.RootElement),
            AgentOrchestrator.ToolCallKey("fetch_url", b.RootElement));
    }

    [Fact]
    public void SameArguments_DifferentTool_ProduceDifferentKeys()
    {
        using var a = JsonDocument.Parse("""{"query":"leek pie"}""");
        using var b = JsonDocument.Parse("""{"query":"leek pie"}""");

        Assert.NotEqual(
            AgentOrchestrator.ToolCallKey("web_search", a.RootElement),
            AgentOrchestrator.ToolCallKey("search_codebase", b.RootElement));
    }
}
