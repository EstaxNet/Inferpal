using Inferpal.Localization;

namespace Inferpal.Services.Rag;

/// <summary>Whether the semantic half of a hybrid search actually ran, and why not when it didn't.</summary>
internal enum SemanticSide
{
    /// <summary>The query was embedded: cosine and lexical both contributed.</summary>
    Ran,

    /// <summary>The user turned semantic search off — the keyword-only answer is what they asked for.</summary>
    DisabledByUser,

    /// <summary>The embedding never came back: model not pulled, backend down, breaker open.</summary>
    EmbeddingUnavailable,
}

/// <summary>
/// Decides what a hybrid search has to <b>say</b> when it returns nothing.
/// </summary>
/// <remarks>
/// ⚠ "No results" and "the semantic half never ran" came out as the same words. <c>SearchAsync</c>
/// skips its vector side when the embedding is null — embedding model not pulled, backend down,
/// embedding breaker open — and the tool then answered "No relevant code found for …", a flat
/// negative. A model reading that concludes the code does not exist and stops looking; the user
/// sees an assistant that cannot find anything in their own repository. That is the 1.6.8 class:
/// an absent capability rendered as a result.
///
/// Three states kept apart, the way <c>SettingsFallback</c> does for a numeric box: the full search
/// ran (nothing to say), the user turned semantic search off (that is what they asked for, but it
/// bears repeating before they conclude the code is absent), or the embedding did not answer (a
/// failure, with somewhere to look).
///
/// ⚠ This is <b>not</b> an error message: the keyword search did run, and its empty result stays
/// the main information. The sentence is added, it does not replace.
/// </remarks>
internal static class SearchDegradation
{
    /// <param name="semanticRequested">
    /// False only when the user turned semantic search off. An open breaker is not a user choice:
    /// it is a failure, and it is reported as one.
    /// </param>
    /// <param name="embedding">What the provider returned for the query.</param>
    internal static SemanticSide Classify(bool semanticRequested, float[]? embedding) =>
        embedding is { Length: > 0 } ? SemanticSide.Ran
        : !semanticRequested          ? SemanticSide.DisabledByUser
        : SemanticSide.EmbeddingUnavailable;

    /// <summary>
    /// <paramref name="noResults"/> as-is when the full search ran; otherwise the same sentence
    /// followed by what was not looked for.
    /// </summary>
    internal static string Explain(string noResults, SemanticSide side, string embeddingModel) =>
        side switch
        {
            SemanticSide.DisabledByUser       => noResults + "\n\n" + Strings.SearchKeywordOnlySemanticOff,
            SemanticSide.EmbeddingUnavailable => noResults + "\n\n" + Strings.SearchKeywordOnlyEmbeddingUnavailable(embeddingModel),
            _                                 => noResults,
        };
}
