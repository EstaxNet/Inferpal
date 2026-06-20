namespace Inferpal.Services;

/// <summary>
/// Builds a Fill-in-the-Middle prompt by hand for servers that don't apply the model's FIM template
/// themselves (OpenAI-compatible <c>/v1/completions</c> has no <c>suffix</c> parameter, unlike Ollama's
/// <c>/api/generate</c>). The special-token format is model-family specific, detected from the model id.
/// </summary>
/// <remarks>
/// Token references: Qwen2.5-Coder / CodeGemma (<c>&lt;|fim_prefix|&gt;…&lt;|fim_suffix|&gt;…&lt;|fim_middle|&gt;</c>),
/// StarCoder/StarCoder2 (<c>&lt;fim_prefix&gt;…</c>), CodeLlama (<c>&lt;PRE&gt; … &lt;SUF&gt;… &lt;MID&gt;</c>),
/// DeepSeek-Coder (<c>&lt;｜fim▁begin｜&gt;…&lt;｜fim▁hole｜&gt;…&lt;｜fim▁end｜&gt;</c>). Unknown families fall back to
/// a prefix-only prompt (<see cref="IsFim"/> = <c>false</c>) so completion still works, just without the suffix.
/// </remarks>
internal static class FimTemplate
{
    /// <summary>A templated FIM prompt plus the stop tokens that bound the generated middle.</summary>
    internal readonly record struct FimSpec(string Prompt, string[] Stop, bool IsFim);

    // Generic stop for the prefix-only fallback: three blank lines (mirrors the Ollama FIM path).
    private static readonly string[] FallbackStop = ["\n\n\n"];

    public static FimSpec Build(string? modelId, string prefix, string suffix)
    {
        var id = (modelId ?? string.Empty).ToLowerInvariant();

        // DeepSeek-Coder — check before "coder"/generic matches.
        if (id.Contains("deepseek"))
            return new FimSpec(
                $"<｜fim▁begin｜>{prefix}<｜fim▁hole｜>{suffix}<｜fim▁end｜>",
                ["<｜end▁of▁sentence｜>"], IsFim: true);

        // StarCoder / StarCoder2 — bare <fim_*> tokens (no pipes).
        if (id.Contains("starcoder"))
            return new FimSpec(
                $"<fim_prefix>{prefix}<fim_suffix>{suffix}<fim_middle>",
                ["<|endoftext|>", "<file_sep>"], IsFim: true);

        // CodeLlama — <PRE>/<SUF>/<MID> with spaces.
        if (id.Contains("codellama") || id.Contains("code-llama"))
            return new FimSpec(
                $"<PRE> {prefix} <SUF>{suffix} <MID>",
                ["<EOT>", "</s>"], IsFim: true);

        // Qwen coder family + CodeGemma — <|fim_*|> tokens.
        if (id.Contains("qwen") || id.Contains("codegemma"))
            return new FimSpec(
                $"<|fim_prefix|>{prefix}<|fim_suffix|>{suffix}<|fim_middle|>",
                ["<|endoftext|>", "<|fim_pad|>", "<|file_sep|>"], IsFim: true);

        // Unknown family → prefix-only completion (suffix is dropped, but ghost text still works).
        return new FimSpec(prefix, FallbackStop, IsFim: false);
    }
}
