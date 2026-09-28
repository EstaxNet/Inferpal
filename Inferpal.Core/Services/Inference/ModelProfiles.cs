namespace Inferpal.Services.Inference;

/// <summary>How a model family writes a tool call when the server leaves it as text.</summary>
internal enum ToolCallForm
{
    /// <summary><c>&lt;tool_call&gt;{"name": …, "arguments": …}&lt;/tool_call&gt;</c> (Qwen3, Hermes).</summary>
    JsonTag,
    /// <summary><c>&lt;tool_call&gt;&lt;function=name&gt;&lt;parameter=key&gt;…</c> (Qwen3.5 / 3.6 / 3.8).</summary>
    QwenXml,
    /// <summary><c>&lt;tool_call&gt;name&lt;arg_key&gt;…&lt;/arg_key&gt;&lt;arg_value&gt;…</c> (GLM).</summary>
    GlmArgs,
    /// <summary><c>[TOOL_CALLS]name[ARGS]{…}</c> (Mistral, Devstral).</summary>
    MistralTokens,
    /// <summary><c>&lt;|tool_call&gt;call:name{…}&lt;tool_call|&gt;</c> (Gemma 4).</summary>
    GemmaNative,
    /// <summary><c>&lt;atem:function_calls&gt;…</c> (Muse Glimmer).</summary>
    Atem,
}

/// <summary>How a model family writes its reasoning when the server leaves it in the answer.</summary>
internal enum ReasoningForm
{
    /// <summary>The family does not reason.</summary>
    None,
    /// <summary>A lone <c>&lt;/think&gt;</c>: the chat template writes the opening tag (Qwen3, GLM).</summary>
    TemplateOpenedThink,
    /// <summary>Addressed messages or a thought channel (Muse Glimmer's <c>to=self</c>, Gemma 4's <c>&lt;|channel&gt;thought</c>).</summary>
    ChannelEnvelope,
}

/// <summary>The fill-in-the-middle tokens a family was trained with.</summary>
internal enum FimForm { None, QwenCoder, StarCoder, CodeLlama, DeepSeekCoder }

/// <summary>What Inferpal's battery measured for a family as the agent.</summary>
internal enum AgentFit
{
    /// <summary>Measured: completes the battery's tasks.</summary>
    Recommended,
    /// <summary>Measured: completes part of them.</summary>
    Usable,
    /// <summary>Not measured yet.</summary>
    Unmeasured,
    /// <summary>Measured: fails most of them, or is not trained for tool calling.</summary>
    NotRecommended,
}

/// <summary>
/// The sampling settings a family's vendor publishes for chat and agent use — only those it publishes: a value left
/// <c>null</c> is not sent, and the server's own applies.
/// </summary>
internal sealed record ModelSampling(
    double? Temperature   = null,
    double? TopP          = null,
    int?    TopK          = null,
    double? MinP          = null,
    double? RepeatPenalty = null);

/// <param name="Anchor">The section of <c>docs/models.md</c> that documents the family (<c>&lt;a id="…"&gt;</c>).</param>
/// <param name="Ids">Lower-case fragments of a model id that name the family.</param>
/// <param name="Rank">Order among families of the same fit, when choosing a default model (lower first).</param>
/// <param name="ToolCalls">The form the family writes its calls in as text; <c>null</c> when it is not trained for tools.</param>
/// <param name="Sampling">What the vendor recommends for tool use and coding; <c>null</c> when it publishes nothing.</param>
internal sealed record ModelProfile(
    string Anchor,
    string Name,
    string[] Ids,
    AgentFit Agent,
    int Rank,
    ToolCallForm? ToolCalls,
    ReasoningForm Reasoning,
    FimForm Fim,
    ModelSampling? Sampling = null);

/// <summary>
/// What Inferpal knows about each model family it is measured with — one place, documented in <c>docs/models.md</c>.
/// </summary>
/// <remarks>
/// <para>
/// ⚠ A profile never gates how a response is read: every tool-call form and every reasoning form is read for every
/// model (<c>InlineToolCallParser</c>, <c>ChannelEnvelope</c>, <c>MarkdownParser</c>), because a model id is a name a
/// user or a packager chose — a renamed GGUF or a fine-tune keeps its format and loses its name. The profile says what
/// the family writes, so that the documentation, the default model choice and the fill-in-the-middle template agree,
/// and the tests hold them together.
/// </para>
/// <para>
/// Order matters: the first profile one of whose ids the model id contains wins, so the more specific come first
/// (<c>qwen2.5-coder</c> before <c>qwen3</c>…).
/// </para>
/// </remarks>
internal static class ModelProfiles
{
    public static readonly IReadOnlyList<ModelProfile> All =
    [
        // ── Code models: fill in the middle ──────────────────────────────────
        new("qwen3-coder",    "Qwen3 Coder",              ["qwen3-coder"],
            AgentFit.Usable,         2, ToolCallForm.QwenXml,       ReasoningForm.None,                FimForm.QwenCoder,
            new(Temperature: 0.7, TopP: 0.8, TopK: 20, RepeatPenalty: 1.05)),
        new("qwen-coder",     "Qwen2.5 Coder",            ["qwen2.5-coder", "qwen2-coder", "qwen-coder"],
            AgentFit.NotRecommended, 0, ToolCallForm.JsonTag,       ReasoningForm.None,                FimForm.QwenCoder,
            new(Temperature: 0.7, TopP: 0.8, TopK: 20, RepeatPenalty: 1.05)),
        new("codegemma",      "CodeGemma",                ["codegemma"],
            AgentFit.NotRecommended, 0, null,                       ReasoningForm.None,                FimForm.QwenCoder),
        new("deepseek-coder", "DeepSeek Coder",           ["deepseek-coder"],
            AgentFit.NotRecommended, 0, null,                       ReasoningForm.None,                FimForm.DeepSeekCoder),
        new("starcoder",      "StarCoder",                ["starcoder"],
            AgentFit.NotRecommended, 0, null,                       ReasoningForm.None,                FimForm.StarCoder),
        new("codellama",      "Code Llama",               ["codellama", "code-llama"],
            AgentFit.NotRecommended, 0, null,                       ReasoningForm.None,                FimForm.CodeLlama),

        // ── Agent models ─────────────────────────────────────────────────────
        // Fill in the middle: measured on what reaches the editor, 27 right completions in 36 with the tokens, 12 from
        // the prefix alone — while Qwen3 4B Thinking (3 vs 15) and Bonsai, its 1-bit sibling (0 vs 8), do worse with them.
        new("qwen35",         "Qwen3.8 / Qwen3.6",        ["qwen3.8", "qwen3.6", "qwen3.5"],
            AgentFit.Recommended,    1, ToolCallForm.QwenXml,       ReasoningForm.TemplateOpenedThink, FimForm.QwenCoder,
            new(Temperature: 1.0, TopP: 0.95, TopK: 20, MinP: 0.0)),
        new("devstral",       "Devstral Small 2",         ["devstral"],
            AgentFit.Recommended,    2, ToolCallForm.MistralTokens, ReasoningForm.None,                FimForm.None,
            new(Temperature: 0.15)),
        new("bonsai",         "Bonsai 27B",               ["bonsai"],
            AgentFit.Recommended,    4, ToolCallForm.QwenXml,       ReasoningForm.TemplateOpenedThink, FimForm.None,
            new(Temperature: 0.7, TopP: 0.95, TopK: 20)),
        new("muse-glimmer",   "Muse Glimmer",             ["muse-glimmer"],
            AgentFit.Recommended,    3, ToolCallForm.Atem,          ReasoningForm.ChannelEnvelope,     FimForm.None,
            new(Temperature: 1.0, TopP: 0.95, TopK: 64)),
        new("gemma4",         "Gemma 4",                  ["gemma-4", "gemma4"],
            AgentFit.Unmeasured,     3, ToolCallForm.GemmaNative,   ReasoningForm.ChannelEnvelope,     FimForm.None,
            new(Temperature: 1.0, TopP: 0.95, TopK: 64)),
        new("qwen3",          "Qwen3 4B Thinking 2507",   ["qwen3"],
            AgentFit.Usable,         3, ToolCallForm.JsonTag,       ReasoningForm.TemplateOpenedThink, FimForm.None,
            new(Temperature: 0.6, TopP: 0.95, TopK: 20, MinP: 0.0)),
        // Z.ai's settings for tool calling and agentic coding, not its general ones (1.0 / 0.95); Unsloth adds min_p
        // 0.01 and no repeat penalty — llama.cpp's defaults are 0.05 and 1.1. Usable with a GGUF made after the
        // llama.cpp scoring fix (Unsloth's); an older one loops whatever Inferpal sends.
        new("glm47",          "GLM 4.7 Flash",            ["glm-4.7", "glm4.7"],
            AgentFit.Usable,         1, ToolCallForm.GlmArgs,       ReasoningForm.TemplateOpenedThink, FimForm.None,
            new(Temperature: 0.7, TopP: 1.0, MinP: 0.01, RepeatPenalty: 1.0)),
    ];

    /// <summary>
    /// The sampling to send with a chat or agent request to <paramref name="modelId"/>: its vendor's, when the user
    /// leaves <see cref="Config.InferpalConfig.UseRecommendedSampling"/> on and the family is known; <c>null</c> otherwise.
    /// </summary>
    public static ModelSampling? SamplingFor(string? modelId, Config.InferpalConfig config) =>
        config.UseRecommendedSampling ? For(modelId)?.Sampling : null;

    /// <summary>The profile of the family <paramref name="modelId"/> belongs to, or <c>null</c> for an unknown family
    /// and for an embedding model — <c>text-embedding-qwen3-embedding-0.6b</c> is not a Qwen3 chat model.</summary>
    public static ModelProfile? For(string? modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId) || ModelCatalog.IsEmbeddingModel(modelId)) return null;
        var id = modelId.ToLowerInvariant();
        return All.FirstOrDefault(p => p.Ids.Any(fragment => id.Contains(fragment, StringComparison.Ordinal)));
    }
}
