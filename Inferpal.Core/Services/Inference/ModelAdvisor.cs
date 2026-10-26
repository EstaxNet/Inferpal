using System.Globalization;
using Inferpal.Localization;
using Inferpal.Models;

namespace Inferpal.Services.Inference;

/// <summary>One field of the settings form the suggestion fills, the value it proposes and why.</summary>
/// <param name="Key">The configuration key (<c>defaultModel</c>, <c>inlineCompletionModel</c>, <c>ragEmbeddingModel</c>,
/// and <c>contextWindowSize</c> when a size is decided for the card).</param>
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
        ("Qwen3.8 27B",    24),
        ("Gemma 4 12B",    12),
        ("Bonsai 27B",     12),
        ("Granite 4.2 8B", 8),
    ];

    /// <summary>The recommended agents Ollama's library serves, with the name to pull and the size of the download as its
    /// registry gives it. ⚠ Only names of <see cref="RecommendedAgents"/>:
    /// one list of what is recommended. Bonsai 27B is on Hugging Face only, so it is not offered for download.</summary>
    internal static readonly IReadOnlyList<(string Name, string OllamaTag, double DownloadGb)> OllamaDownloads =
    [
        ("Qwen3.8 27B",    "qwen3.8:27b",   17.7),
        ("Gemma 4 12B",    "gemma4:12b",    8.0),
        ("Granite 4.2 8B", "granite4.2:8b", 5.3),
    ];

    /// <summary>The downloads measured to fit a card of <paramref name="vramBudgetGb"/>, largest card first; all of them
    /// when the card is unknown.</summary>
    internal static IReadOnlyList<ModelDownload> DownloadsFor(double vramBudgetGb) =>
    [
        .. OllamaDownloads
            .Select(d => new ModelDownload(d.Name, d.OllamaTag, d.DownloadGb,
                                           RecommendedAgents.First(a => a.Name == d.Name).CardGb))
            .Where(d => vramBudgetGb <= 0 || d.CardGb <= vramBudgetGb + 0.5)
            .OrderByDescending(d => d.CardGb),
    ];

    /// <summary>The measured model to name for a card of <paramref name="vramBudgetGb"/>: the largest that fits, on a
    /// server that pulls Ollama's library (the tags are its library's); <c>null</c> elsewhere, and when the card is
    /// unknown — a guess would download a model that does not fit.</summary>
    internal static ModelDownload? DownloadToName(string provider, double vramBudgetGb) =>
        InferenceProviderFactory.Canonical(provider) == InferenceProviderFactory.Ollama && vramBudgetGb > 0
            ? DownloadsFor(vramBudgetGb).FirstOrDefault()
            : null;

    /// <summary>Asks <paramref name="client"/> what it has installed, then suggests — both editors' one call.</summary>
    /// <param name="client">A client for the server the FORM names (the user may not have saved a new address yet).</param>
    /// <param name="server">That server's settings: an empty list is said for what it is (stopped, refused, empty).</param>
    /// <param name="saved">The saved settings: their switches (the index, completions) and the window the product last
    /// sized. <c>null</c>: the factory switches, and no window of the product's.</param>
    public static async Task<ModelSuggestion> SuggestAsync(
        IInferenceProvider client, Config.InferpalConfig server, double vramBudgetGb,
        IReadOnlyDictionary<string, string> current, CancellationToken ct, Config.InferpalConfig? saved = null)
    {
        var installed = await client.ListInstalledModelsAsync(ct, server.BaseUrl);
        var nothing = installed.Count == 0
            ? await ModelCatalog.EmptyListMeansAsync(client, server, Strings.SuggestNothingListed, ct)
            : Strings.SuggestNothingListed;
        var suggestion = Suggest(installed, vramBudgetGb, current, nothing);
        if (suggestion.Fields.FirstOrDefault(f => f.Key == "defaultModel")?.Value is not { Length: > 0 } chat) return suggestion;

        // ⚠ Only over an AUTOMATIC window: a window the person set is theirs, and a Save to keep the proposed models would
        // otherwise change it with them. What the product last wrote is read in the FILE: the other editor's /setup counts.
        var held = current.TryGetValue(WindowKey, out var h) ? h?.Trim() ?? "" : "";
        if (held.Length > 0 && !(int.TryParse(held, NumberStyles.Integer, CultureInfo.InvariantCulture, out var heldTokens)
                                 && ContextWindowChoice.IsAutomatic(heldTokens, saved is null ? 0 : ContextWindowChoice.WrittenAuto(saved))))
            return suggestion;

        // The context window for that model and card, beside the search and completion models it proposes. A role on a
        // server of its own has no field here, so nothing beside the chat; an empty value is the field's automatic choice
        // (the best embedding model, the chat's own model). Only when a size is decided: an unknown card is asked by the
        // form's own memory field.
        string? Proposed(string key) => suggestion.Fields.FirstOrDefault(f => f.Key == key)?.Value;
        var search = Proposed("ragEmbeddingModel") is { } s
            ? s.Length > 0 ? s : EmbeddingModels.PreferredOf(ModelsByKind.Of(installed).Embedding)
            : null;
        var switches = new Config.InferpalConfig
        {
            RagEnabled = saved?.RagEnabled ?? true, InlineCompletionEnabled = saved?.InlineCompletionEnabled ?? true,
        };
        var window = await ContextWindowChoice.AdviseAsync(client, server.Provider, chat,
            ContextWindowChoice.Beside(switches, chat, search, Proposed("inlineCompletionModel")), installed, vramBudgetGb, ct);
        if (window.Tokens is not { } tokens) return suggestion;
        var value   = tokens.ToString(CultureInfo.InvariantCulture);
        // A form that sends no window holds the factory one.
        var changed = !string.Equals(held.Length > 0 ? held : ContextWindowChoice.Factory.ToString(CultureInfo.InvariantCulture),
                                     value, StringComparison.Ordinal);
        // The factory window is proposed only to bring a larger automatic one back down (another card, another model).
        if (window.Sentence is null && !changed) return suggestion;
        var reason = window.Sentence ?? Strings.WindowChosen(tokens, vramBudgetGb.ToString("0.#", CultureInfo.CurrentCulture));
        var fields = suggestion.Fields.ToList();
        fields.Insert(fields.FindIndex(f => f.Key == "defaultModel") + 1, new ModelSuggestionField(WindowKey, value, reason, changed));
        return suggestion with { Fields = fields };
    }

    /// <summary>The context window's field, proposed beside the chat model when a size is decided for the card.</summary>
    public const string WindowKey = "contextWindowSize";

    /// <param name="installed">What the server lists, with the size of each on disk (0 when it does not say).</param>
    /// <param name="vramBudgetGb">The card's memory set in the settings; 0 = unknown.</param>
    /// <param name="current">The form's values, by configuration key (unsaved edits included).</param>
    /// <param name="nothingListed">The sentence for an empty list — the caller knows whether the server answered.</param>
    public static ModelSuggestion Suggest(
        IReadOnlyList<InstalledModelInfo> installed, double vramBudgetGb, IReadOnlyDictionary<string, string> current,
        string nothingListed)
    {
        if (installed.Count == 0) return new([], [], nothingListed);

        var byKind     = ModelsByKind.Of(installed);
        var chatModels = byKind.Chat;
        var embedding  = byKind.Embedding;
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

        // ── A role on a server of its own: the models listed here are not there, so its model is left as it is ──
        bool OwnServer(string role) => Current(role + "RunsOn") switch
        {
            RoleServers.OnOther        => true,
            RoleServers.OnAutocomplete => OwnServer("inlineCompletion"),
            _                          => false,
        };
        foreach (var (role, key, name) in new[]
                 {
                     ("inlineCompletion", "inlineCompletionModel", Strings.SettingsPageAutocomplete),
                     ("ragEmbedding",     "ragEmbeddingModel",     Strings.SettingsPageSearch),
                 })
        {
            if (!OwnServer(role)) continue;
            fields.RemoveAll(f => f.Key == key);
            if (key == "ragEmbeddingModel") notes.Remove(Strings.SuggestEmbeddingReindex);   // nothing changes to reindex
            notes.Add(Strings.SuggestRoleOwnServer(name));
        }

        // The form's order: chat, autocomplete, code search.
        return new([.. Keys.SelectMany(k => fields.Where(f => f.Key == k))], notes);
    }

    /// <summary>The recommended agents measured on a card of <paramref name="vramBudgetGb"/>; all of them, with their card,
    /// when the card is unknown. Empty when the card is smaller than every measured setup.</summary>
    internal static string RecommendedFor(double vramBudgetGb) =>
        string.Join(" · ", RecommendedAgents
            .Where(a => vramBudgetGb <= 0 || a.CardGb <= vramBudgetGb + 0.5)
            .OrderByDescending(a => a.CardGb)
            .Select(a => vramBudgetGb > 0 ? a.Name : $"{a.Name} ({a.CardGb} GB)"));
}
