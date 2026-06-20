using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using Inferpal.Localization;
using Inferpal.Models;
using Inferpal.Services;
using Xunit;

namespace Inferpal.Tests;

// Covers the pure wire-mapping helpers of the OpenAI-compatible provider (LM Studio et al.):
// URL normalization, internal-message → OpenAI-shape mapping (ids, string arguments,
// tool_call_id correlation), and streamed tool-call fragment assembly. No network.
public class OpenAiCompatibleClientTests
{
    // ── V1 URL normalization ──────────────────────────────────────────────────

    [Theory]
    [InlineData("http://localhost:1234",   "http://localhost:1234/v1")]
    [InlineData("http://localhost:1234/",  "http://localhost:1234/v1")]
    [InlineData("http://localhost:1234/v1", "http://localhost:1234/v1")]
    [InlineData("http://localhost:1234/v1/", "http://localhost:1234/v1")]
    [InlineData("HTTP://HOST/V1",          "HTTP://HOST/V1")]   // already a /v1 root (case-insensitive)
    [InlineData("",                        "")]                 // empty stays empty (no URL configured)
    public void V1_AppendsSuffixUnlessAlreadyPresent(string raw, string expected)
        => Assert.Equal(expected, OpenAiCompatibleClient.V1(raw));

    // ── Message mapping ───────────────────────────────────────────────────────

    [Fact]
    public void MapMessages_PassesThroughUserAndSystem()
    {
        var mapped = OpenAiCompatibleClient.MapMessages(
        [
            new("system", "sys"),
            new("user", "hello"),
        ]);

        Assert.Equal(2, mapped.Count);
        Assert.Equal("system", mapped[0].Role);
        Assert.Equal("sys",    mapped[0].Content);
        Assert.Null(mapped[0].ToolCalls);
        Assert.Equal("hello",  mapped[1].Content);
    }

    [Fact]
    public void MapMessages_AssistantToolCalls_GetIdsAndStringArguments()
    {
        var args = JsonDocument.Parse("""{"path":"a.cs"}""").RootElement.Clone();
        var mapped = OpenAiCompatibleClient.MapMessages(
        [
            new("assistant", string.Empty, [new ToolCallDto(new ToolCallFunction("read_file", args))]),
        ]);

        var asst = Assert.Single(mapped);
        Assert.Null(asst.Content);                       // empty content → null when tool_calls present
        var call = Assert.Single(asst.ToolCalls!);
        Assert.Equal("call_0",    call.Id);
        Assert.Equal("function",  call.Type);
        Assert.Equal("read_file", call.Function.Name);
        // Arguments must be a JSON *string* on the wire (not an object).
        Assert.Equal("""{"path":"a.cs"}""", call.Function.Arguments);
    }

    [Fact]
    public void MapMessages_ToolResults_CorrelateByPosition()
    {
        var a1 = JsonDocument.Parse("""{"x":1}""").RootElement.Clone();
        var a2 = JsonDocument.Parse("""{"x":2}""").RootElement.Clone();
        var mapped = OpenAiCompatibleClient.MapMessages(
        [
            new("user", "go"),
            new("assistant", null,
            [
                new ToolCallDto(new ToolCallFunction("t1", a1)),
                new ToolCallDto(new ToolCallFunction("t2", a2)),
            ]),
            new("tool", "result-1"),
            new("tool", "result-2"),
        ]);

        var asst   = mapped[1];
        var id1     = asst.ToolCalls![0].Id;
        var id2     = asst.ToolCalls![1].Id;
        Assert.Equal("tool", mapped[2].Role);
        Assert.Equal(id1, mapped[2].ToolCallId);   // first result → first call's id
        Assert.Equal(id2, mapped[3].ToolCallId);   // second result → second call's id
        Assert.Equal("result-1", mapped[2].Content);
    }

    // ── Consecutive same-role coalescing (regression: Mistral/Devstral strict template) ──
    // The agent emits two consecutive user turns (context+question, then plan prompt). Mistral's
    // tool-aware Jinja template rejects that with "roles must alternate user and assistant"; we
    // merge adjacent same-role plain turns so alternation holds.

    [Fact]
    public void MapMessages_MergesConsecutiveUserTurns()
    {
        var mapped = OpenAiCompatibleClient.MapMessages(
        [
            new("system", "sys"),
            new("user", "context + question"),
            new("user", "plan prompt"),
        ]);

        Assert.Equal(2, mapped.Count);                              // the two users collapse to one
        Assert.Equal("system", mapped[0].Role);
        Assert.Equal("user",   mapped[1].Role);
        Assert.Equal("context + question\n\nplan prompt", mapped[1].Content);
    }

    [Fact]
    public void MapMessages_FoldsUserNudgeAfterToolResult_StrictTemplateAlternation()
    {
        // Strict Mistral/Devstral templates reject tool→user; the OBSERVER nudge must fold into the
        // tool result so the wire history ends …assistant(tool_calls) → tool(result + nudge) → [generate].
        var args = JsonDocument.Parse("""{"path":"a.cs"}""").RootElement.Clone();
        var mapped = OpenAiCompatibleClient.MapMessages(
        [
            new("user", "go"),
            new("assistant", null, [new ToolCallDto(new ToolCallFunction("read_file", args))]),
            new("tool", "file body"),
            new("user", "observe"),     // user after a tool result → folded into the tool turn
        ]);

        Assert.Equal(3, mapped.Count);
        Assert.Equal("user",      mapped[0].Role);
        Assert.Equal("assistant", mapped[1].Role);
        Assert.Equal("tool",      mapped[2].Role);
        Assert.Equal("file body\n\nobserve", mapped[2].Content);
    }

    [Fact]
    public void CoalesceConsecutiveRoles_SkipsBlankContentWhenJoining()
    {
        // Helper now lives on the shared base (used by both the OpenAI-compat and Ollama paths).
        var merged = InferenceProviderBase.CoalesceConsecutiveRoles(
        [
            new("user", "first"),
            new("user", ""),        // empty body must not produce a dangling separator
            new("user", "third"),
        ]);

        var single = Assert.Single(merged);
        Assert.Equal("first\n\nthird", single.Content);
    }

    // ── Streamed tool-call assembly ───────────────────────────────────────────

    [Fact]
    public void BuildToolCalls_AssemblesFragmentedArguments()
    {
        // Arguments stream in pieces across SSE deltas and must be concatenated then parsed.
        var acc = new SortedDictionary<int, (string Name, StringBuilder Args)>
        {
            [0] = ("search", new StringBuilder("""{"q":""").Append("\"hi\"}")),
        };

        var calls = OpenAiCompatibleClient.BuildToolCalls(acc);
        var call  = Assert.Single(calls!);
        Assert.Equal("search", call.Function.Name);
        Assert.Equal("hi", call.Function.Arguments.GetProperty("q").GetString());
    }

    [Fact]
    public void BuildToolCalls_EmptyArguments_DefaultToEmptyObject()
    {
        var acc = new SortedDictionary<int, (string Name, StringBuilder Args)>
        {
            [0] = ("now", new StringBuilder()),
        };

        var call = Assert.Single(OpenAiCompatibleClient.BuildToolCalls(acc)!);
        Assert.Equal(JsonValueKind.Object, call.Function.Arguments.ValueKind);
        Assert.Empty(call.Function.Arguments.EnumerateObject());
    }

    [Fact]
    public void BuildToolCalls_NoFragments_ReturnsNull()
        => Assert.Null(OpenAiCompatibleClient.BuildToolCalls([]));

    // ── Capabilities ──────────────────────────────────────────────────────────

    // ── Reasoning field names (regression: Ollama's /v1 uses "reasoning", vLLM "reasoning_content") ──

    [Fact]
    public void StreamChunk_Deserializes_BothReasoningFieldNames()
    {
        // Ollama's OpenAI endpoint streams chain-of-thought under "reasoning" (confirmed live).
        var ollama = JsonSerializer.Deserialize<OpenAiStreamChunk>(
            """{"choices":[{"delta":{"role":"assistant","content":"","reasoning":"thinking"}}]}""");
        Assert.Equal("thinking", ollama!.Choices![0].Delta!.Reasoning);

        // vLLM / DeepSeek-style servers use "reasoning_content".
        var vllm = JsonSerializer.Deserialize<OpenAiStreamChunk>(
            """{"choices":[{"delta":{"reasoning_content":"pondering"}}]}""");
        Assert.Equal("pondering", vllm!.Choices![0].Delta!.ReasoningContent);
    }

    // ── In-stream server-error surfacing (regression: LM Studio context overflow) ──
    // LM Studio sends HTTP 200 then injects {"error":...} into the SSE body when the request
    // (inflated by the agent's tool definitions) exceeds the model's loaded context window.

    [Fact]
    public void StreamChunk_Deserializes_ErrorElement()
    {
        var chunk = JsonSerializer.Deserialize<OpenAiStreamChunk>(
            """{"error":{"message":"request (9233 tokens) exceeds the available context size (8192 tokens)"}}""");
        Assert.Equal(JsonValueKind.Object, chunk!.Error.ValueKind);
    }

    [Fact]
    public void TryExtractError_NoError_ReturnsNull()
    {
        // A normal streaming chunk has no "error" property → default JsonElement (Undefined).
        var chunk = JsonSerializer.Deserialize<OpenAiStreamChunk>(
            """{"choices":[{"delta":{"content":"hi"}}]}""");
        Assert.Null(OpenAiCompatibleClient.TryExtractError(chunk!.Error));
    }

    [Fact]
    public void ParseErrorElement_BareErrorLine_ExtractsErrorMember()
    {
        // A server can abort with a bare {"error":{...}} line (no "data:" prefix) after the 200 headers.
        var err = OpenAiCompatibleClient.ParseErrorElement("""{"error":{"message":"boom"}}""");
        Assert.Equal("boom", OpenAiCompatibleClient.TryExtractError(err));
    }

    [Fact]
    public void ParseErrorElement_NormalLine_ReturnsUndefined()
    {
        var err = OpenAiCompatibleClient.ParseErrorElement("""{"choices":[]}""");
        Assert.Equal(JsonValueKind.Undefined, err.ValueKind);
        Assert.Null(OpenAiCompatibleClient.TryExtractError(err));
    }

    [Fact]
    public void TryExtractError_ObjectWithMessage_ReturnsMessage()
    {
        var err = JsonDocument.Parse("""{"message":"boom","code":400}""").RootElement;
        Assert.Equal("boom", OpenAiCompatibleClient.TryExtractError(err));
    }

    [Fact]
    public void TryExtractError_BareString_ReturnsString()
    {
        var err = JsonDocument.Parse("\"plain error\"").RootElement;
        Assert.Equal("plain error", OpenAiCompatibleClient.TryExtractError(err));
    }

    [Theory]
    [InlineData("request (9233 tokens) exceeds the available context size (8192 tokens)")]
    [InlineData("The number of tokens to keep (n_keep: 8933) is greater than the context length (n_ctx: 8192)")]
    [InlineData("Try to load the model with a larger context length")]
    public void MapServerError_ContextOverflow_GetsActionableHint(string serverError)
    {
        var msg = OpenAiCompatibleClient.MapServerError(serverError, "http://localhost:1234/v1");
        // The actionable hint mentions raising the context length; the raw server text is preserved.
        Assert.Equal(Strings.MsgContextOverflow(serverError), msg);
        Assert.Contains(serverError, msg);
    }

    [Fact]
    public void MapServerError_GenericError_SurfacedVerbatimWithUrl()
    {
        var msg = OpenAiCompatibleClient.MapServerError("model not found", "http://host/v1");
        Assert.Equal(Strings.MsgServerError("http://host/v1", "model not found"), msg);
        Assert.Contains("model not found", msg);
        Assert.Contains("http://host/v1", msg);
    }

    // ── Proactive context-fit guard (kills the doomed round-trip before it's sent) ──

    [Fact]
    public void EstimateRequestTokens_CountsMessageContentAndToolDefs()
    {
        var messages = new List<ChatMessageDto> { new("user", new string('x', 400)) }; // 400 chars → ~100 tk
        var contentOnly = OpenAiCompatibleClient.EstimateRequestTokens(messages, null);
        Assert.Equal(100, contentOnly);

        // Adding tool definitions must raise the estimate — the schemas are real tokens on the wire,
        // and they are exactly what tips an agent request over a modest loaded context.
        var defs = new List<ToolDefinition>
        {
            new("function", new ToolFunction("read_file", new string('d', 800), new { })),
        };
        Assert.True(OpenAiCompatibleClient.EstimateRequestTokens(messages, defs) > contentOnly);
    }

    [Fact]
    public void EstimateRequestTokens_IncludesAssistantToolCallPayloads()
    {
        var args = JsonDocument.Parse("""{"path":"some/long/path/to/a/file.cs"}""").RootElement.Clone();
        var withCall = new List<ChatMessageDto>
        {
            new("assistant", null, [new ToolCallDto(new ToolCallFunction("read_file", args))]),
        };
        Assert.True(OpenAiCompatibleClient.EstimateRequestTokens(withCall, null) > 0);
    }

    [Fact]
    public void CheckContextFit_OverBudget_ReturnsActionableMessage()
    {
        // Prompt estimate alone exceeds the loaded window → fail fast with the concrete numbers.
        var msg = OpenAiCompatibleClient.CheckContextFit(estimateTokens: 9233, loadedContext: 8192);
        Assert.Equal(Strings.MsgContextWontFit(9233, 8192), msg);
        Assert.Contains("9233", msg);
        Assert.Contains("8192", msg);
    }

    [Fact]
    public void CheckContextFit_WithinBudget_ReturnsNull()
        => Assert.Null(OpenAiCompatibleClient.CheckContextFit(estimateTokens: 4000, loadedContext: 8192));

    [Theory]
    [InlineData(null)]  // unknown loaded context (generic OpenAI server) → never blocks
    [InlineData(0)]     // a zero/garbage figure must not block either
    public void CheckContextFit_UnknownOrZeroContext_NeverBlocks(int? loadedContext)
        => Assert.Null(OpenAiCompatibleClient.CheckContextFit(estimateTokens: 999_999, loadedContext));

    [Fact]
    public void Capabilities_OpenAi_DisablesOllamaOnlyFeatures()
    {
        var caps = ProviderCapabilities.OpenAiCompatible;
        Assert.False(caps.ModelManagement);
        Assert.False(caps.VramMonitoring);
        Assert.False(caps.Fim);
        Assert.False(caps.KeepAlive);
        Assert.True(ProviderCapabilities.Ollama.VramMonitoring);
    }

    [Fact]
    public void Capabilities_LmStudio_HasManagementAndFimButNoKeepAlive()
    {
        // LM Studio manages models + does client-side FIM, but its OpenAI chat wire carries no
        // per-request keep_alive — so the auto-unload setting must stay hidden for it.
        var caps = ProviderCapabilities.LmStudio;
        Assert.True(caps.ModelManagement);
        Assert.True(caps.VramMonitoring);
        Assert.True(caps.Fim);
        Assert.False(caps.KeepAlive);
    }

    [Theory]
    [InlineData("ollama",            true,  true)]   // Ollama: keep_alive + FIM
    [InlineData("lmstudio",          false, true)]   // LM Studio: no keep_alive, has FIM
    [InlineData("openai-compatible", false, false)]  // generic OpenAI: neither
    [InlineData(null,                true,  true)]   // unknown/blank falls back to Ollama
    [InlineData("LMSTUDIO",          false, true)]   // case-insensitive
    public void CapabilitiesFor_MapsProviderCodeToGating(string? code, bool keepAlive, bool fim)
    {
        var caps = InferenceProviderFactory.CapabilitiesFor(code);
        Assert.Equal(keepAlive, caps.KeepAlive);
        Assert.Equal(fim, caps.Fim);
    }
}
