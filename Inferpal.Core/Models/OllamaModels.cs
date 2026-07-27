using System.Text.Json;
using System.Text.Json.Serialization;

namespace Inferpal.Models;

// ── Ollama /api/chat request / response DTOs ─────────────────────────────────
// These records mirror the Ollama REST API wire format exactly.
// See https://github.com/ollama/ollama/blob/main/docs/api.md#generate-a-chat-completion

/// <summary>Body of POST <c>/api/chat</c>.</summary>
record ChatRequest(
    [property: JsonPropertyName("model")]      string Model,
    [property: JsonPropertyName("messages")]   List<ChatMessageDto> Messages,
    /// <summary>Tool definitions sent so the model can choose to call them.</summary>
    [property: JsonPropertyName("tools")]      List<ToolDefinition>? Tools = null,
    [property: JsonPropertyName("stream")]     bool    Stream     = false,
    /// <summary>
    /// Ollama keep-alive duration after the response (e.g. <c>"10m"</c>, <c>"0"</c> to unload immediately).
    /// <c>null</c> = use Ollama default (5 min).
    /// </summary>
    [property: JsonPropertyName("keep_alive")] string? KeepAlive = null,
    /// <summary>
    /// Per-request runtime options (e.g. <c>num_ctx</c>). <c>null</c> = let Ollama use the
    /// model's default context window (which can be very large, e.g. 256k, and spill to CPU).
    /// </summary>
    [property: JsonPropertyName("options")]    ChatOptions? Options = null,
    /// <summary>
    /// Constrains tool usage: <c>"required"</c> forces the model to emit at least one tool call,
    /// <c>"auto"</c> lets it decide, <c>"none"</c> disables tools. <c>null</c> = Ollama default
    /// (<c>"auto"</c>). Unknown to older Ollama builds, which ignore the field harmlessly.
    /// </summary>
    [property: JsonPropertyName("tool_choice")] string? ToolChoice = null);

/// <summary>
/// Runtime options for <c>/api/chat</c>. Fields left <c>null</c> are omitted from the wire
/// payload so Ollama falls back to the model's own defaults.
/// </summary>
record ChatOptions(
    /// <summary>Context window size in tokens. Caps the KV-cache footprint to keep the model in VRAM.</summary>
    [property: JsonPropertyName("num_ctx")] int? NumCtx = null);

/// <summary>
/// A single message in the conversation history.
/// <c>Role</c> is one of <c>"user"</c>, <c>"assistant"</c>, or <c>"tool"</c>.
/// </summary>
record ChatMessageDto(
    [property: JsonPropertyName("role")]       string Role,
    [property: JsonPropertyName("content")]    string? Content = null,
    /// <summary>Populated by the model when it wants to invoke one or more tools.</summary>
    [property: JsonPropertyName("tool_calls")] List<ToolCallDto>? ToolCalls = null,
    /// <summary>
    /// Separate reasoning channel emitted by thinking models (magistral, deepseek-r1, qwen3-thinking,
    /// gpt-oss…). Ollama returns the chain-of-thought here and the user-facing answer in
    /// <see cref="Content"/>. Declared last so existing positional <c>new ChatMessageDto(role, content,
    /// toolCalls)</c> call sites keep compiling; <c>WhenWritingNull</c> keeps it out of request bodies.
    /// </summary>
    [property: JsonPropertyName("thinking")]   string? Thinking = null);

/// <summary>
/// One NDJSON chunk from the <c>/api/chat</c> stream.
/// When <see cref="Done"/> is <c>true</c> the chunk carries token counts and the final message.
/// </summary>
record ChatResponse(
    [property: JsonPropertyName("done")]               bool Done,
    [property: JsonPropertyName("message")]            ChatMessageDto? Message          = null,
    [property: JsonPropertyName("eval_count")]         int?            EvalCount        = null,
    [property: JsonPropertyName("prompt_eval_count")]  int?            PromptEvalCount  = null);

/// <summary>A single tool invocation requested by the model.</summary>
record ToolCallDto(
    [property: JsonPropertyName("function")] ToolCallFunction Function);

/// <summary>Name and JSON arguments of a tool call.</summary>
record ToolCallFunction(
    [property: JsonPropertyName("name")]      string Name,
    [property: JsonPropertyName("arguments")] JsonElement Arguments);

// ── Tool schema DTOs ─────────────────────────────────────────────────────────

/// <summary>Top-level wrapper that Ollama expects for each tool definition.</summary>
record ToolDefinition(
    /// <summary>Always <c>"function"</c> per the Ollama API.</summary>
    [property: JsonPropertyName("type")]     string Type,
    [property: JsonPropertyName("function")] ToolFunction Function);

/// <summary>Name, description, and JSON Schema parameters for one tool.</summary>
record ToolFunction(
    [property: JsonPropertyName("name")]        string Name,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("parameters")]  object Parameters);

// ── Model list DTOs ──────────────────────────────────────────────────────────

/// <summary>Single model entry from <c>GET /api/tags</c>.</summary>
record ModelInfo(
    [property: JsonPropertyName("name")] string Name,
    /// <summary>On-disk size of the model in bytes (a proxy for its VRAM weight footprint).</summary>
    [property: JsonPropertyName("size")] long   Size = 0);

/// <summary>Response from <c>GET /api/tags</c>.</summary>
record ModelListResponse(
    [property: JsonPropertyName("models")] List<ModelInfo> Models);

/// <summary>An installed model with its on-disk size — internal projection of <see cref="ModelInfo"/>,
/// decoupled from the Ollama wire format. Used by the hardware-aware VRAM estimation.</summary>
record InstalledModelInfo(string Name, long SizeBytes);

/// <summary>Response from <c>POST /api/show</c>. <c>model_info</c> is an open dict whose keys are
/// prefixed by the model architecture (e.g. <c>llama.block_count</c>); see <see cref="ModelArchInfo"/>.</summary>
record ShowModelResponse(
    [property: JsonPropertyName("model_info")] Dictionary<string, JsonElement>? ModelInfo);

/// <summary>The architecture fields needed to size the KV-cache, parsed from <c>/api/show</c>'s
/// <c>model_info</c>. All counts are per the GGUF metadata.</summary>
record ModelArchInfo(int BlockCount, int HeadCount, int HeadCountKv, int EmbeddingLength, int ContextLength);

// ── Ollama /api/generate DTOs (FIM inline completions) ───────────────────────

/// <summary>Body of POST <c>/api/generate</c> used for Fill-in-the-Middle completions.</summary>
record GenerateRequest(
    [property: JsonPropertyName("model")]      string          Model,
    [property: JsonPropertyName("prompt")]     string          Prompt,
    [property: JsonPropertyName("suffix")]     string?         Suffix,
    [property: JsonPropertyName("stream")]     bool            Stream,
    [property: JsonPropertyName("options")]    GenerateOptions Options,
    [property: JsonPropertyName("keep_alive")] string?         KeepAlive = null);

/// <summary>Sampling options for <c>/api/generate</c>.</summary>
record GenerateOptions(
    [property: JsonPropertyName("temperature")] double    Temperature,
    [property: JsonPropertyName("num_predict")] int       NumPredict,
    [property: JsonPropertyName("stop")]        string[]? Stop = null);

/// <summary>One NDJSON chunk from the <c>/api/generate</c> stream.</summary>
record GenerateResponse(
    [property: JsonPropertyName("response")] string? Response,
    [property: JsonPropertyName("done")]     bool    Done);

// ── Ollama /api/ps DTOs (running models / VRAM monitoring) ──────────────────

/// <summary>One entry from <c>GET /api/ps</c> — a currently loaded model.</summary>
record RunningModelInfo(
    [property: JsonPropertyName("name")]       string Name,
    /// <summary>VRAM used by this model in bytes (0 if running on CPU).</summary>
    [property: JsonPropertyName("size_vram")]  long   SizeVram,
    /// <summary>ISO-8601 UTC timestamp when Ollama will unload this model.</summary>
    [property: JsonPropertyName("expires_at")] string ExpiresAt);

/// <summary>Response from <c>GET /api/ps</c>.</summary>
record RunningModelsResponse(
    [property: JsonPropertyName("models")] List<RunningModelInfo> Models);

