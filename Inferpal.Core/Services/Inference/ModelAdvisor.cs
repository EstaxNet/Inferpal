using System.Globalization;
using Inferpal.Localization;
using Inferpal.Models;

namespace Inferpal.Services.Inference;

/// <summary>One field of the settings form the suggestion fills, the value it proposes and why.</summary>
/// <param name="Key">The configuration key (<c>defaultModel</c>, <c>inlineCompletionModel</c>, <c>ragEmbeddingModel</c>).</param>
/// <param name="Value">The proposed value; empty means the field's automatic choice ("same as chat", "automatic").</param>
/// <param name="Changed">The value differs from what the form holds.</param>
internal sealed record ModelSuggestionField(string Key, string Value, string Reason, bool Changed);

/// <summary>What "Suggest the best models" proposes: the fields it fills, what it leaves and why, or why it cannot.</summary>
/// <param name="Refusal">Set when there is nothing to suggest from (the server lists nothing, or no chat model).</param>
internal sealed record ModelSuggestion(
    IReadOnlyList<ModelSuggestionField> Fields, IReadOnlyList<string> Notes, string? Refusal = null)
{
    /// <summary>The sentence under the button: what to do next.</summary>
    public string Summary => Refusal ?? (Fields.Any(f => f.Changed) ? Strings.SuggestReview : Strings.SuggestNoChange);
}

/// <summary>
/// The best models among those INSTALLED, for the fields of the settings form a user sets first — the chat model, the
/// autocomplete model, the code-search model — from what Inferpal's battery measured (<see cref="ModelProfiles"/>,
/// <c>docs/models.md</c>).
/// </summary>
/// <remarks>
/// <para>
/// ⚠ It proposes, it never saves: the values land in the form, which counts them as unsaved changes, and the user saves
/// them or not. And it never writes a field the form hides — the per-task models of the advanced settings are named in
/// a note, left as they are: a button that silently changes what nobody can see is the defect the fold was built against.
/// </para>
/// <para>
/// ⚠ Only models the server already has: nothing is downloaded. When no recommended agent model is installed, a note
/// names the ones measured for the card's memory — the card table of <c>docs/models.md</c> (24 GB measured at most:
/// nothing is recommended above it).
/// </para>
/// </remarks>
internal static class ModelAdvisor
{
    /// <summary>The fields the suggestion fills: the three a user sets first, all on the page's visible part.</summary>
    public static readonly IReadOnlyList<string> Keys = ["defaultModel", "inlineCompletionModel", "ragEmbeddingModel"];

    /// <summary>The per-task models of the advanced settings, which the suggestion names and never writes.</summary>
    private static readonly (string Key, Func<string> Role)[] Overrides =
    [
        ("agentModel",       () => Strings.ModelRoleAgent),
        ("codeActionsModel", () => Strings.ModelRoleCodeActions),
        ("inlineEditModel",  () => Strings.ModelRoleInlineEdit),
        ("utilityModel",     () => Strings.ModelRoleUtility),
    ];

    /// <summary>The agent models of the card table of <c>docs/models.md</c>, with the smallest card each was measured to
    /// fit (with the context, the autocomplete and the embedding model beside it).</summary>
    internal static readonly IReadOnlyList<(string Name, int CardGb)> RecommendedAgents =
    [
        ("Qwen3.8 27B", 24),
        ("Gemma 4 12B", 12),
        ("Bonsai 27B",  12),
    ];

    /// <summary>Asks <paramref name="client"/> what it has installed, then suggests — both editors' one call.</summary>
    /// <param name="client">A client for the server the FORM names (the user may not have saved a new address yet).</param>
    /// <param name="server">That server's settings: an empty list is said for what it is (stopped, refused, empty).</param>
    public static async Task<ModelSuggestion> SuggestAsync(
        IInferenceProvider client, Config.InferpalConfig server, double vramBudgetGb,
        IReadOnlyDictionary<string, string> current, CancellationToken ct)
    {
        var installed = await client.ListInstalledModelsAsync(ct, server.BaseUrl);
        var nothing = installed.Count == 0
            ? await ModelCatalog.EmptyListMeansAsync(client, server, Strings.SuggestNothingListed, ct)
            : Strings.SuggestNothingListed;
        return Suggest(installed, vramBudgetGb, current, nothing);
    }

    /// <param name="installed">What the server lists, with the size of each on disk (0 when it does not say).</param>
    /// <param name="vramBudgetGb">The card's memory set in the settings; 0 = unknown.</param>
    /// <param name="current">The form's values, by configuration key (unsaved edits included).</param>
    /// <param name="nothingListed">The sentence for an empty list — the caller knows whether the server answered.</param>
    public static ModelSuggestion Suggest(
        IReadOnlyList<InstalledModelInfo> installed, double vramBudgetGb, IReadOnlyDictionary<string, string> current,
        string nothingListed)
    {
        if (installed.Count == 0) return new([], [], nothingListed);

        var names      = installed.Select(m => m.Name).ToList();
        var chatModels = names.Where(n => !ModelCatalog.IsEmbeddingModel(n)).ToList();
        var embedding  = names.Where(ModelCatalog.IsEmbeddingModel).ToList();
        if (chatModels.Count == 0) return new([], [], Strings.SuggestOnlyEmbedding);

        string Current(string key) => current.TryGetValue(key, out var v) ? v?.Trim() ?? "" : "";
        long SizeOf(string? name) =>
            name is null ? 0 : installed.FirstOrDefault(m => ModelCatalog.SameModelName(m.Name, name))?.SizeBytes ?? 0;
        ModelSuggestionField Field(string key, string value, string reason) =>
            new(key, value, reason, !string.Equals(Current(key), value, StringComparison.Ordinal));

        var fields = new List<ModelSuggestionField>();
        var notes  = new List<string>();

        // ── Chat (and agent, unless the advanced settings route it elsewhere) ──
        var chat    = ModelCatalog.PickBestChatModel(chatModels);
        var profile = ModelProfiles.For(chat);
        fields.Add(Field("defaultModel", chat, profile?.Agent switch
        {
            AgentFit.Recommended    => Strings.SuggestChatRecommended(chat),
            AgentFit.Usable         => Strings.SuggestChatUsable(chat),
            AgentFit.NotRecommended => Strings.SuggestChatNotRecommended(chat),
            _                       => Strings.SuggestChatUnmeasured(chat),
        }));
        if (profile?.Agent is not AgentFit.Recommended && RecommendedFor(vramBudgetGb) is { Length: > 0 } recommended)
            notes.Add(Strings.SuggestInstallRecommended(recommended));

        // ── Code search: automatic, the best embedding model installed ──
        var bestEmbedding = EmbeddingModels.PreferredOf(embedding);
        var configuredEmbedding = Current("ragEmbeddingModel");
        // A choice that already IS the best stays as written: rewriting it as "automatic" would count as a change.
        var embeddingValue = bestEmbedding is not null && ModelCatalog.SameModelName(configuredEmbedding, bestEmbedding)
            ? configuredEmbedding : "";
        var embeddingField = Field("ragEmbeddingModel", embeddingValue, bestEmbedding is null
            ? Strings.SuggestEmbeddingNone
            : Strings.SuggestEmbeddingAuto(bestEmbedding));
        fields.Add(embeddingField);
        if (embeddingField.Changed) notes.Add(Strings.SuggestEmbeddingReindex);

        // ── Autocomplete: the model measured best, when it fits beside the others ──
        var completion = chatModels.FirstOrDefault(n => ModelProfiles.For(n)?.Anchor == "mellum");
        var neededGb   = 0.0;
        var fits       = completion is not null && ModelCatalog.TrioFitsBudget(
            vramBudgetGb, [SizeOf(chat), SizeOf(completion), SizeOf(bestEmbedding)], out neededGb)
            // An unknown card "fits" anything: a second model is then proposed only beside a chat model that does not
            // autocomplete well itself.
            && (vramBudgetGb > 0 || profile?.Anchor != "qwen35");
        fields.Add(
            fits                          ? Field("inlineCompletionModel", completion!, Strings.SuggestFimMeasured(completion!))
            // A chat model measured to autocomplete well needs no second model beside it.
            : profile?.Anchor == "qwen35"  ? Field("inlineCompletionModel", "", Strings.SuggestFimChatCompletes)
            : completion is not null      ? Field("inlineCompletionModel", "", Strings.SuggestFimDoesNotFit(completion,
                                                neededGb.ToString("0.#", CultureInfo.CurrentCulture),
                                                vramBudgetGb.ToString("0.#", CultureInfo.CurrentCulture)))
            :                               Field("inlineCompletionModel", "", Strings.SuggestFimSameAsChat));

        // ── What the advanced settings route elsewhere: named, never written ──
        var routed = Overrides
            .Where(o => Current(o.Key).Length > 0)
            .Select(o => $"{o.Role()} → {Current(o.Key)}")
            .ToList();
        if (routed.Count > 0) notes.Add(Strings.SuggestOverrides(string.Join(", ", routed)));

        // The form's order: chat, autocomplete, code search.
        return new([.. Keys.Select(k => fields.First(f => f.Key == k))], notes);
    }

    /// <summary>The recommended agents measured on a card of <paramref name="vramBudgetGb"/>; all of them, with their card,
    /// when the card is unknown. Empty when the card is smaller than every measured setup.</summary>
    internal static string RecommendedFor(double vramBudgetGb) =>
        string.Join(" · ", RecommendedAgents
            .Where(a => vramBudgetGb <= 0 || a.CardGb <= vramBudgetGb + 0.5)
            .OrderByDescending(a => a.CardGb)
            .Select(a => vramBudgetGb > 0 ? a.Name : $"{a.Name} ({a.CardGb} GB)"));
}
