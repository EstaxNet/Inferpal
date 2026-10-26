using Inferpal.Config;

namespace Inferpal.Services.Inference;

/// <summary>
/// Which embedding model the product uses, and the exact text it sends that model — the one reader for every embedding
/// call of the code index.
/// </summary>
/// <remarks>
/// <para>
/// ⚠ <b>No embedding model is required</b>, like every other model role: with the field empty, an index embeds with
/// the best embedding model the backend has installed (<see cref="ChooseAsync"/>), and with none installed it runs on
/// its keyword half — a choice, reported as such, never as a failure. Queries are embedded with the model the index's
/// vectors came from, never re-chosen: a query vector from another model scores noise against them.
/// </para>
/// <para>
/// ⚠ An embedding model is trained with a prompt format of its own, and the text sent without it lands elsewhere in its
/// vector space: Qwen3 Embedding without its instruction on queries ranks the code so badly that the hybrid search
/// returns less than its keyword half alone. The format is a fact about the model (its card), read by family name —
/// a model this table does not know is sent the bare text, as before.
/// </para>
/// <para>
/// ⚠ The formats below apply to the <b>code</b> index only: they are what the code search was compared with. The
/// documentation index (<c>@Docs</c>) sends bare text — its queries are prose about prose, and no format was chosen
/// for that.
/// </para>
/// <para>
/// ⚠ A stored vector is a <b>document</b> embedding: an index built with another document format is not reusable, the
/// same way a vector from another model is not. <see cref="CodeIndexIdentity"/> is what an index records and compares
/// — the model name, plus the document format when there is one. A query-only format (Qwen3) changes nothing stored.
/// </para>
/// </remarks>
internal static class EmbeddingModels
{
    /// <summary>
    /// The family preferred among installed embedding models — its Ollama name (<c>ollama pull embeddinggemma</c>; LM
    /// Studio lists it as <c>text-embedding-embeddinggemma-300m</c>).
    /// </summary>
    public const string Preferred = "embeddinggemma";

    /// <summary>The embedding model the user set, or <c>null</c> when the field is empty.</summary>
    public static string? Configured(InferpalConfig config) =>
        string.IsNullOrWhiteSpace(config.RagEmbeddingModel) ? null : config.RagEmbeddingModel.Trim();

    /// <summary>
    /// The embedding model an index embeds with: the configured one; else the best one installed; else <c>null</c>
    /// (keyword search only).
    /// </summary>
    /// <param name="previous">The model the index's stored vectors came from, if any.</param>
    /// <remarks>
    /// ⚠ An EMPTY model list is a backend that did not answer — a reachable one lists at least its chat model — not a
    /// backend without an embedding model. It keeps <paramref name="previous"/>: read as "none installed", an outage at
    /// start-up would discard every stored vector, to be recomputed at the next start.
    /// </remarks>
    public static async Task<string?> ChooseAsync(InferpalConfig config, IInferenceProvider client, string? previous,
                                                  CancellationToken ct) =>
        (await ChooseCoreAsync(config, client, previous, forCode: false, ct).ConfigureAwait(false)).Model;

    /// <summary>
    /// <see cref="ChooseAsync"/> for the CODE index, with the installed models the automatic choice passed over because
    /// they search code worse than its keyword half (<see cref="SearchesCodeBelowKeywords"/>) — what the index's status
    /// names when it runs on keywords.
    /// </summary>
    public static Task<(string? Model, IReadOnlyList<string> PassedOver)> ChooseForCodeAsync(
        InferpalConfig config, IInferenceProvider client, string? previous, CancellationToken ct) =>
        ChooseCoreAsync(config, client, previous, forCode: true, ct);

    private static async Task<(string? Model, IReadOnlyList<string> PassedOver)> ChooseCoreAsync(
        InferpalConfig config, IInferenceProvider client, string? previous, bool forCode, CancellationToken ct)
    {
        if (Configured(config) is { } configured) return (configured, []);
        var models = await client.ListModelsByKindAsync(ct);
        if (models.Listed.Count == 0) return (previous, []);
        var chosen = PreferredOf(models.Embedding, forCode);
        return (chosen, chosen is null && forCode ? PassedOver(models.Embedding) : []);
    }

    /// <summary>
    /// The embedding model to pick among those installed: the preferred family if it is there, else the first listed —
    /// for the CODE index (<paramref name="forCode"/>), never one that searches code worse than its keyword half alone
    /// (<see cref="SearchesCodeBelowKeywords"/>). <c>null</c> when none is left: keyword search.
    /// </summary>
    /// <param name="forCode">The code index (the default): the one the embedding bench measured. The documentation index
    /// was not measured, and keeps the first one listed.</param>
    public static string? PreferredOf(IReadOnlyList<string> installedEmbeddingModels, bool forCode = true) =>
        installedEmbeddingModels.FirstOrDefault(m => FormatOf(m)?.Tag == FormatOf(Preferred)!.Tag)
        ?? installedEmbeddingModels.FirstOrDefault(m => !forCode || !SearchesCodeBelowKeywords(m));

    /// <summary>The installed embedding models the code index's automatic choice passes over (<see cref="SearchesCodeBelowKeywords"/>).</summary>
    public static IReadOnlyList<string> PassedOver(IReadOnlyList<string> installedEmbeddingModels) =>
        [.. installedEmbeddingModels.Where(SearchesCodeBelowKeywords)];

    // With each of these models, the product's hybrid search finds fewer of the right files than its keyword half alone:
    // bge-m3, nomic-embed-text (v1.5) and mxbai-embed-large as Ollama serves them, Qwen3 Embedding 0.6B even with its
    // instruction. Only these exact models: a sibling (nomic-embed-code, nomic-embed-text v2, Qwen3 Embedding 4B or 8B,
    // mxbai-embed-xsmall) is another model, never measured. Matched on the name without separators (Flat).
    private static readonly (string Has, string? Not)[] BelowKeywords =
    [
        ("bgem3", null), ("nomicembedtext", "v2"), ("mxbaiembedlarge", null), ("qwen3embedding06b", null),
    ];

    /// <summary>A model that makes the CODE search worse than its keyword half alone: never picked automatically for it
    /// (a model the person set stays theirs).</summary>
    internal static bool SearchesCodeBelowKeywords(string model)
    {
        var flat = Flat(model);
        return BelowKeywords.Any(b => flat.Contains(b.Has, StringComparison.Ordinal)
                                      && (b.Not is null || !flat.Contains(b.Not, StringComparison.Ordinal)));
    }

    /// <summary>The name without separators, lower case: every spelling a backend uses lands on the same family
    /// (<c>embeddinggemma:300m</c>, <c>text-embedding-embeddinggemma-300m</c>, <c>Qwen3_Embedding-0.6B</c>).</summary>
    private static string Flat(string model) =>
        new(model.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    /// <summary>A family's documented format for code retrieval.</summary>
    /// <param name="Tag">Names the document format in an index's identity; changes when <paramref name="Document"/> does.</param>
    internal sealed record Format(string Tag, string Query, string Document);

    // Matched on the name with separators removed, so every spelling a backend uses lands on the same family:
    // "embeddinggemma:300m", "text-embedding-embeddinggemma-300m", "qwen3-embedding:0.6b", "Qwen3_Embedding-0.6B".
    // ⚠ Nomic is absent on purpose: its "search_query: " / "search_document: " prefixes do not help this search.
    private static readonly (string Family, Format Format)[] Formats =
    [
        ("embeddinggemma", new("gemma-code", "task: code retrieval | query: ", "title: none | text: ")),
        ("qwen3embedding", new("qwen3-code",
            "Instruct: Given a question about a codebase, retrieve the code that answers it\nQuery:", "")),
    ];

    /// <summary>The documented format of <paramref name="model"/>'s family, or <c>null</c> for bare text.</summary>
    internal static Format? FormatOf(string model)
    {
        var flat = Flat(model);
        foreach (var (family, format) in Formats)
            if (flat.Contains(family, StringComparison.Ordinal)) return format;
        return null;
    }

    /// <summary>The text a code-search query is embedded as.</summary>
    internal static string CodeQueryText(string model, string query) => (FormatOf(model)?.Query ?? "") + query;

    /// <summary>The text an indexed code chunk is embedded as.</summary>
    internal static string CodeDocumentText(string model, string content) => (FormatOf(model)?.Document ?? "") + content;

    /// <summary>
    /// What a code index records next to its vectors, and compares before reusing them; empty for an index built with
    /// no embedding model.
    /// </summary>
    internal static string CodeIndexIdentity(string? model) =>
        model is null ? "" : FormatOf(model) is { Document.Length: > 0 } f ? $"{model}#{f.Tag}" : model;

    /// <summary>The model an index identity (<see cref="CodeIndexIdentity"/>) names — <c>null</c> for none.</summary>
    internal static string? ModelOfIdentity(string? identity) =>
        string.IsNullOrEmpty(identity) ? null : identity.Split('#')[0];

    /// <summary>Embeds a code-search query in its model's format.</summary>
    internal static Task<float[]?> EmbedCodeQueryAsync(IInferenceProvider client, string model, string query,
                                                        CancellationToken ct) =>
        client.GetEmbeddingAsync(CodeQueryText(model, query), model, ct);

    /// <summary>Embeds an indexed code chunk in its model's format.</summary>
    internal static Task<float[]?> EmbedCodeDocumentAsync(IInferenceProvider client, string model, string content,
                                                           CancellationToken ct) =>
        client.GetEmbeddingAsync(CodeDocumentText(model, content), model, ct);
}
