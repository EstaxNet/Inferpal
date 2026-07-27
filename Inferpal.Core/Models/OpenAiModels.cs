using System.Text.Json;
using System.Text.Json.Serialization;

namespace Inferpal.Models;

// ── OpenAI-compatible /v1/chat/completions DTOs ──────────────────────────────
// Mirror the OpenAI Chat Completions wire format used by LM Studio, llama.cpp server,
// vLLM, Jan, LiteLLM, etc. Distinct from the Ollama DTOs in OllamaModels.cs:
//  - streaming is SSE ("data: {json}\n\n", terminated by "data: [DONE]"), not NDJSON;
//  - tool-call arguments travel as a JSON *string* (not an object) and stream in fragments;
//  - tool results must carry a tool_call_id correlating them to the assistant's tool_calls.

/// <summary>Body of POST <c>/v1/chat/completions</c>.</summary>
record OpenAiChatRequest(
    [property: JsonPropertyName("model")]         string Model,
    [property: JsonPropertyName("messages")]      List<OpenAiRequestMessage> Messages,
    [property: JsonPropertyName("tools")]         List<ToolDefinition>? Tools = null,
    [property: JsonPropertyName("stream")]        bool Stream = true,
    [property: JsonPropertyName("stream_options")] OpenAiStreamOptions? StreamOptions = null,
    [property: JsonPropertyName("tool_choice")]   string? ToolChoice = null);

/// <summary>Asks the server to emit a final <c>usage</c> chunk while streaming.</summary>
record OpenAiStreamOptions(
    [property: JsonPropertyName("include_usage")] bool IncludeUsage = true);

/// <summary>One outbound message. Shaped for the OpenAI API (ids on tool calls, arguments as string,
/// <c>tool_call_id</c> on tool results) — built from the internal <see cref="ChatMessageDto"/>.</summary>
record OpenAiRequestMessage(
    [property: JsonPropertyName("role")]         string Role,
    [property: JsonPropertyName("content")]      string? Content = null,
    [property: JsonPropertyName("tool_calls")]   List<OpenAiToolCall>? ToolCalls = null,
    [property: JsonPropertyName("tool_call_id")] string? ToolCallId = null);

/// <summary>An assistant tool call on the wire: carries an id and arguments serialized as a JSON string.</summary>
record OpenAiToolCall(
    [property: JsonPropertyName("id")]       string Id,
    [property: JsonPropertyName("function")] OpenAiFnCall Function,
    [property: JsonPropertyName("type")]     string Type = "function");

/// <summary>Name + JSON-string arguments of a tool call.</summary>
record OpenAiFnCall(
    [property: JsonPropertyName("name")]      string Name,
    [property: JsonPropertyName("arguments")] string Arguments);

// ── Streaming response DTOs ───────────────────────────────────────────────────

/// <summary>One SSE chunk from the <c>/v1/chat/completions</c> stream. <see cref="Error"/> is set
/// when the server injects a failure mid-stream after the 200 headers (LM Studio / llama.cpp do this
/// for context overflow); it is a raw element because servers disagree on whether it is a string or
/// an object — see <c>OpenAiCompatibleClient.TryExtractError</c>.</summary>
record OpenAiStreamChunk(
    [property: JsonPropertyName("choices")] List<OpenAiChoice>? Choices = null,
    [property: JsonPropertyName("usage")]   OpenAiUsage? Usage = null,
    [property: JsonPropertyName("error")]   JsonElement Error = default);

/// <summary>A streamed choice; <see cref="FinishReason"/> is non-null on the terminal chunk.</summary>
record OpenAiChoice(
    [property: JsonPropertyName("delta")]         OpenAiDelta? Delta = null,
    [property: JsonPropertyName("finish_reason")] string? FinishReason = null);

/// <summary>Incremental content for a choice. Tool-call argument fragments accumulate by index.</summary>
record OpenAiDelta(
    [property: JsonPropertyName("content")]           string? Content = null,
    /// <summary>Reasoning channel name used by vLLM / DeepSeek-style servers.</summary>
    [property: JsonPropertyName("reasoning_content")] string? ReasoningContent = null,
    /// <summary>Reasoning channel name used by Ollama's OpenAI-compatible endpoint (field is <c>"reasoning"</c>).</summary>
    [property: JsonPropertyName("reasoning")]         string? Reasoning = null,
    [property: JsonPropertyName("tool_calls")]        List<OpenAiToolCallDelta>? ToolCalls = null);

/// <summary>A tool-call fragment: identified by <see cref="Index"/>; name/arguments arrive piecewise.</summary>
record OpenAiToolCallDelta(
    [property: JsonPropertyName("index")]    int Index,
    [property: JsonPropertyName("id")]       string? Id = null,
    [property: JsonPropertyName("function")] OpenAiFnDelta? Function = null);

/// <summary>Streamed name/arguments fragments of a tool call.</summary>
record OpenAiFnDelta(
    [property: JsonPropertyName("name")]      string? Name = null,
    [property: JsonPropertyName("arguments")] string? Arguments = null);

/// <summary>Token accounting, present on the final chunk when <c>stream_options.include_usage</c> is set.</summary>
record OpenAiUsage(
    [property: JsonPropertyName("prompt_tokens")]     int? PromptTokens = null,
    [property: JsonPropertyName("completion_tokens")] int? CompletionTokens = null,
    [property: JsonPropertyName("total_tokens")]      int? TotalTokens = null);

// ── /v1/completions DTOs (legacy text completion, used for client-side FIM) ──────

/// <summary>Body of POST <c>/v1/completions</c> (no <c>suffix</c>: the FIM prompt is templated client-side).</summary>
record OpenAiCompletionRequest(
    [property: JsonPropertyName("model")]       string Model,
    [property: JsonPropertyName("prompt")]      string Prompt,
    [property: JsonPropertyName("max_tokens")]  int MaxTokens,
    [property: JsonPropertyName("temperature")] double Temperature,
    [property: JsonPropertyName("stream")]      bool Stream = true,
    [property: JsonPropertyName("stop")]        string[]? Stop = null);

/// <summary>One SSE chunk from the <c>/v1/completions</c> stream (text is in <c>choices[].text</c>).</summary>
record OpenAiCompletionChunk(
    [property: JsonPropertyName("choices")] List<OpenAiCompletionChoice>? Choices = null);

/// <summary>A streamed completion choice.</summary>
record OpenAiCompletionChoice(
    [property: JsonPropertyName("text")]          string? Text = null,
    [property: JsonPropertyName("finish_reason")] string? FinishReason = null);

// ── LM Studio native models DTOs ─────────────────────────────────────────────────
// LM Studio ships two incompatible native shapes; LmStudioClient probes v1 first, then v0.

/// <summary>Response from <c>GET /api/v1/models</c> (LM Studio 0.4.0+ native REST API):
/// the list is under <c>"models"</c> and each entry is identified by <c>"key"</c>.</summary>
record LmStudioV1ModelsResponse(
    [property: JsonPropertyName("models")] List<LmStudioV1Model>? Models = null);

/// <summary>One entry from <c>GET /api/v1/models</c>. The model id is <c>key</c>; a model is loaded
/// when <c>loaded_instances</c> is non-empty; <c>size_bytes</c> is the on-disk size.</summary>
record LmStudioV1Model(
    [property: JsonPropertyName("key")]                string? Key = null,
    [property: JsonPropertyName("loaded_instances")]   List<JsonElement>? LoadedInstances = null,
    [property: JsonPropertyName("max_context_length")] int? MaxContextLength = null,
    [property: JsonPropertyName("architecture")]       string? Architecture = null,
    [property: JsonPropertyName("size_bytes")]         long? SizeBytes = null);

/// <summary>Response from the legacy <c>GET /api/v0/models</c> (pre-0.4.0): list under <c>"data"</c>.</summary>
record LmStudioModelsResponse(
    [property: JsonPropertyName("data")] List<LmStudioModel>? Data = null);

/// <summary>One model entry from LM Studio's legacy v0 API. <c>state</c> is <c>"loaded"</c> or
/// <c>"not-loaded"</c>; on-disk size / VRAM bytes are not exposed. <c>max_context_length</c> is the
/// model's <em>capability</em>; <c>loaded_context_length</c> (present only while loaded) is the n_ctx
/// the running instance was actually loaded with — the figure a request must fit into.</summary>
record LmStudioModel(
    [property: JsonPropertyName("id")]                    string Id,
    [property: JsonPropertyName("state")]                 string? State = null,
    [property: JsonPropertyName("max_context_length")]    int? MaxContextLength = null,
    [property: JsonPropertyName("loaded_context_length")] int? LoadedContextLength = null,
    [property: JsonPropertyName("arch")]                  string? Arch = null);

// ── /v1/embeddings DTOs ───────────────────────────────────────────────────────

/// <summary>Body of POST <c>/v1/embeddings</c>.</summary>
record OpenAiEmbeddingRequest(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("input")] string Input);

/// <summary>Response from <c>/v1/embeddings</c>.</summary>
record OpenAiEmbeddingResponse(
    [property: JsonPropertyName("data")] List<OpenAiEmbeddingData>? Data = null);

/// <summary>One embedding vector entry.</summary>
record OpenAiEmbeddingData(
    [property: JsonPropertyName("embedding")] float[]? Embedding = null);

// ── /v1/models DTOs ───────────────────────────────────────────────────────────

/// <summary>Response from <c>GET /v1/models</c>.</summary>
record OpenAiModelsResponse(
    [property: JsonPropertyName("data")] List<OpenAiModelEntry>? Data = null);

/// <summary>One model entry (only the id is reliably present; sizes are unknown).</summary>
record OpenAiModelEntry(
    [property: JsonPropertyName("id")] string Id);
