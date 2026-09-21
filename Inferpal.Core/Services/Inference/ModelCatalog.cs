using System.Globalization;
using System.Text;
using System.Text.Json;
using Inferpal.Localization;
using Inferpal.Models;

namespace Inferpal.Services.Inference;

/// <summary>
/// Shared model-name classification and first-run model choice. Previously duplicated
/// between the tool-window VM (first-run discovery) and the settings VM (embedding
/// model dropdown), with subtly diverging keyword lists — this is now the single
/// source of truth for both.
/// </summary>
internal static class ModelCatalog
{
    /// <summary>
    /// What an EMPTY model list actually means: nothing installed, or nobody to ask.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>An unreachable backend never throws</b> — all three providers catch, trace and
    /// <c>return []</c> — so every reader of an empty list is one sentence away from telling the
    /// user their models are gone when the server is simply stopped. On a product whose
    /// prerequisite is `ollama serve`, that is the most likely state of all.
    /// ⚠ And the ordinary sentences make it worse by naming a remedy that cannot work in the state
    /// they describe: <c>/bench</c> says "install one (/models pull …)", which needs the very
    /// backend that is down.
    /// <para>
    /// One reader for every caller, and the round-trip happens only on the EMPTY branch, so the
    /// ordinary answer costs nothing. Best effort: a check that fails on its own must not replace
    /// an answer with an exception.
    /// </para>
    /// </remarks>
    public static async Task<string> EmptyListMeansAsync(
        IInferenceProvider client, Config.InferpalConfig config, string nothingInstalled, CancellationToken ct)
    {
        var url = config.BaseUrl;
        if (string.IsNullOrWhiteSpace(url)) return nothingInstalled;

        try
        {
            return await client.CheckConnectionAsync(url, ct) ? nothingInstalled : Strings.MsgUnreachable(url);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Diagnostics.Swallow("ModelCatalog.EmptyListMeans", ex);
            return nothingInstalled;
        }
    }

    /// <summary>
    /// Same model name, case aside, allowing Ollama's implicit <c>:latest</c> tag (<c>llama3.1</c> is
    /// <c>llama3.1:latest</c>). Any other tag names another model: <c>qwen3:8b</c> and <c>qwen3:32b</c> are
    /// two downloads, two sizes, two VRAM footprints.
    /// </summary>
    internal static bool SameModelName(string a, string b) =>
        string.Equals(ModelKey(a), ModelKey(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>The name without Ollama's implicit <c>:latest</c> tag: the key under which a model counts once.</summary>
    internal static string ModelKey(string name) =>
        name.EndsWith(":latest", StringComparison.OrdinalIgnoreCase) ? name[..^":latest".Length] : name;

    // Priority: specialized code models first, then popular general models.
    private static readonly string[] ChatPriority =
    [
        "qwen2.5-coder", "qwen2-coder", "deepseek-coder", "codellama",
        "starcoder2",    "starcoder",   "codegemma",
        "llama3.1",      "llama3.2",    "llama3",
        "mistral-nemo",  "mistral",
        "phi4",          "phi3",
        "gemma3",        "gemma2",      "gemma",
        "llava",
    ];

    private static readonly string[] EmbeddingKeywords = ["embed", "bge", "minilm", "e5"];

    /// <summary>Bytes per binary gigabyte (matches the header badge's divisor).</summary>
    public const double BytesPerGb = 1_073_741_824.0;

    /// <summary>
    /// Flat multiplier applied to a model's on-disk size to approximate its loaded VRAM
    /// footprint: the quantized weights map roughly 1:1 to VRAM, plus ~20 % for the KV-cache
    /// and runtime buffers. Deliberately rough — enough to answer "does it fit / does it spill".
    /// </summary>
    public const double VramOverheadFactor = 1.2;

    /// <summary>Estimated loaded VRAM footprint (bytes) for a model of the given on-disk size.</summary>
    public static long EstimateVramBytes(long diskSizeBytes) =>
        diskSizeBytes <= 0 ? 0 : (long)(diskSizeBytes * VramOverheadFactor);

    /// <summary>Summed estimated footprint (GB) of the given models.</summary>
    public static double EstimateTotalGb(IEnumerable<long> diskSizes) =>
        diskSizes.Where(s => s > 0).Sum(EstimateVramBytes) / BytesPerGb;

    /// <summary>
    /// True when the combined estimated footprint of the given models fits in the budget.
    /// <paramref name="neededGb"/> reports the estimated total either way. Always true when
    /// the budget is unknown (<paramref name="budgetGb"/> &lt;= 0) — we never warn without data.
    /// </summary>
    public static bool TrioFitsBudget(double budgetGb, IEnumerable<long> diskSizes, out double neededGb)
    {
        neededGb = EstimateTotalGb(diskSizes);
        return budgetGb <= 0 || neededGb <= budgetGb;
    }

    /// <summary>True when a single model's estimated footprint already exceeds a known budget.</summary>
    public static bool SingleModelExceeds(double budgetGb, long diskSizeBytes) =>
        budgetGb > 0 && EstimateVramBytes(diskSizeBytes) / BytesPerGb > budgetGb;

    // ── num_ctx VRAM bound (KV-cache sizing) ───────────────────────────────────

    /// <summary>
    /// Parses the architecture fields needed to size the KV-cache from <c>/api/show</c>'s
    /// <c>model_info</c> dict. Keys are prefixed by the architecture name (e.g. <c>llama.</c>).
    /// Returns <c>null</c> when the essential fields are missing (older/quantization-only metadata).
    /// </summary>
    public static ModelArchInfo? ParseArch(IReadOnlyDictionary<string, JsonElement> info)
    {
        var arch   = GetString(info, "general.architecture");
        var prefix = string.IsNullOrEmpty(arch) ? "" : arch + ".";

        var blockCount  = GetInt(info, prefix + "block_count");
        var headCount   = GetInt(info, prefix + "attention.head_count");
        var headCountKv = GetInt(info, prefix + "attention.head_count_kv");
        var embedding   = GetInt(info, prefix + "embedding_length");
        var contextLen  = GetInt(info, prefix + "context_length");
        var keyLength   = GetInt(info, prefix + "attention.key_length");
        var valueLength = GetInt(info, prefix + "attention.value_length");

        if (blockCount <= 0 || headCount <= 0 || embedding <= 0) return null;
        if (headCountKv <= 0) headCountKv = headCount; // no GQA → MHA

        return new ModelArchInfo(blockCount, headCount, headCountKv, embedding, contextLen, keyLength, valueLength);
    }

    /// <summary>
    /// KV-cache bytes consumed per token. Assumes an fp16 cache (2 bytes/element, Ollama's
    /// default): <c>block_count × head_count_kv × (key_length + value_length)</c>.
    /// </summary>
    /// <remarks>⚠ The head sizes come from <c>attention.key_length</c>/<c>value_length</c> when the
    /// model declares them, and only fall back to <c>embedding_length / head_count</c>: the two
    /// differ on real models (Gemma 2 9B: 256 declared, 224 derived), and the derived figure
    /// under-sizes the cache, so the recommended num_ctx spills onto the CPU.</remarks>
    public static long KvCacheBytesPerToken(ModelArchInfo arch, int bytesPerElement = 2)
    {
        var derived = arch.EmbeddingLength / arch.HeadCount;
        var key     = arch.KeyLength   > 0 ? arch.KeyLength   : derived;
        var value   = arch.ValueLength > 0 ? arch.ValueLength : derived;
        return (long)arch.BlockCount * arch.HeadCountKv * (key + value) * bytesPerElement;
    }

    /// <summary>
    /// Largest <c>num_ctx</c> whose KV-cache fits the VRAM left after the model's weights, capped
    /// by the model's trained context length and floored to a 1024-token step. Returns 0 when the
    /// budget is unknown or the weights alone already exceed it.
    /// </summary>
    public static int MaxSafeNumCtx(double budgetGb, long weightsBytes, ModelArchInfo arch)
    {
        if (budgetGb <= 0) return 0;

        var headroom = budgetGb * BytesPerGb - weightsBytes;
        if (headroom <= 0) return 0;

        var perToken = KvCacheBytesPerToken(arch);
        if (perToken <= 0) return 0;

        var maxTokens = (long)(headroom / perToken);
        if (arch.ContextLength > 0) maxTokens = Math.Min(maxTokens, arch.ContextLength);

        return (int)(maxTokens / 1024 * 1024); // floor to a clean 1024-token step
    }

    private static string GetString(IReadOnlyDictionary<string, JsonElement> info, string key) =>
        info.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    /// <summary>Reads an integer metadata value; if the value is an array (per-layer counts),
    /// takes the maximum element. Tolerant of missing keys and non-numeric values (→ 0).</summary>
    private static int GetInt(IReadOnlyDictionary<string, JsonElement> info, string key)
    {
        if (!info.TryGetValue(key, out var v)) return 0;
        switch (v.ValueKind)
        {
            case JsonValueKind.Number:
                return v.TryGetInt32(out var n) ? n : 0;
            case JsonValueKind.Array:
                var max = 0;
                foreach (var e in v.EnumerateArray())
                    if (e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out var m) && m > max) max = m;
                return max;
            default:
                return 0;
        }
    }

    /// <summary>
    /// Heuristic: the name contains an embedding-family keyword. Drives both the
    /// settings dropdown filter and the first-run chat/embedding split.
    /// </summary>
    public static bool IsEmbeddingModel(string name) =>
        EmbeddingKeywords.Any(kw => name.Contains(kw, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// First-run default-model choice: the first priority entry found as a substring
    /// (code models first), the first available model otherwise. The list must be
    /// non-empty — callers guard the "no chat models" case with their own message.
    /// </summary>
    public static string PickBestChatModel(IReadOnlyList<string> models)
    {
        foreach (var pref in ChatPriority)
        {
            var match = models.FirstOrDefault(m =>
                m.Contains(pref, StringComparison.OrdinalIgnoreCase));
            if (match is not null) return match;
        }
        return models[0]; // fallback: first available
    }

    /// <summary>
    /// Compact one-line VRAM badge for the window header: <c>name · X.X GB │ name2 · Y.Y GB</c>.
    /// The tag suffix is dropped (<c>llama3.1:8b</c> → <c>llama3.1</c>); a model loaded on CPU
    /// (<see cref="RunningModelInfo.SizeVram"/> == 0) shows just its name; an empty list → "".
    /// </summary>
    public static string FormatVramBadge(IReadOnlyList<RunningModelInfo> running) =>
        string.Join(" │ ", running.Select(m =>
        {
            var colon     = m.Name.IndexOf(':', StringComparison.Ordinal);
            var shortName = colon > 0 ? m.Name[..colon] : m.Name;
            // Invariant: the badge pairs the number with an English "GB" unit, so a localized
            // decimal comma ("2,0 GB") would read inconsistently — and keeps the format deterministic.
            return m.SizeVram > 0
                ? $"{shortName} · {(m.SizeVram / BytesPerGb).ToString("F1", CultureInfo.InvariantCulture)} GB"
                : shortName;
        }));

    /// <summary>The <c>/models running</c> table (VRAM in MB).</summary>
    public static string FormatRunningModels(IReadOnlyList<RunningModelInfo> running)
    {
        var sb = new StringBuilder($"{Strings.ModelsRunningHeader}\n\n| {Strings.ModelsTableModel} | {Strings.ModelsTableVram} |\n|---|---|\n");
        foreach (var m in running)
            sb.AppendLine($"| `{m.Name}` | {m.SizeVram / 1024 / 1024:N0} MB |");
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// The <c>/models</c> table: every installed model, with its VRAM footprint and a
    /// 🟢 marker when currently loaded, then the sub-command hints.
    /// </summary>
    public static string FormatInstalledModels(
        IReadOnlyList<string> models, IReadOnlyList<RunningModelInfo> running)
    {
        var vramMap = running.ToDictionary(m => m.Name, m => m.SizeVram / 1024 / 1024);
        var sb = new StringBuilder($"{Strings.ModelsInstalledHeader}\n\n| {Strings.ModelsTableModel} | {Strings.ModelsTableVram} |\n|---|---|\n");
        foreach (var m in models)
        {
            var vram = vramMap.TryGetValue(m, out var v) ? $"{v:N0} MB 🟢" : "—";
            sb.AppendLine($"| `{m}` | {vram} |");
        }
        sb.AppendLine($"\n`/models pull <name>` · `/models delete <name>` · `/models running`");
        return sb.ToString().TrimEnd();
    }
}
