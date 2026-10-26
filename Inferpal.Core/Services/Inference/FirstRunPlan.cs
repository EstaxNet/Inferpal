using Inferpal.Config;
using Inferpal.Localization;
using Inferpal.Models;

namespace Inferpal.Services.Inference;

/// <summary>A model the first run can download, measured for a card of <paramref name="CardGb"/>.</summary>
/// <param name="Name">The name the docs and the settings give it.</param>
/// <param name="Tag">The name to pull it by.</param>
/// <param name="DownloadGb">The size of the download.</param>
internal sealed record ModelDownload(string Name, string Tag, double DownloadGb, int CardGb);

/// <summary>What the first run proposes for a server that answers.</summary>
/// <param name="ChatModel">The chat and agent model proposed; <c>null</c> when the server has no chat model.</param>
/// <param name="CompletionModel">The autocomplete model; <c>null</c> = the chat model.</param>
/// <param name="SearchModel">The code-search model; <c>null</c> when the server has none, or only models that search code
/// worse than keywords alone (<paramref name="SearchPassedOver"/> tells the two apart) — search then uses keywords.</param>
/// <param name="BudgetGb">The card's memory; 0 = unknown.</param>
/// <param name="NeededGb">The estimated memory of the proposed models together; 0 when nothing is proposed.</param>
/// <param name="Downloads">Without a chat model, on a server Inferpal can download to: what fits the card.</param>
/// <param name="Listed">Without a chat model: the models the server has (for code search only, or nothing).</param>
/// <param name="InstalledCount">How many models the server has, of every kind.</param>
/// <param name="NoneFits">The server takes downloads, but none of the recommended models fits the card: said as that,
/// not as a server that cannot download.</param>
/// <param name="Window">The chat model's context window for the card (<see cref="ContextWindowChoice"/>), set by the
/// caller that knows which role models the card keeps; <c>null</c> when there is nothing to propose or say.</param>
/// <param name="Installed">What the server lists, as the plan read it (the sizes the window is sized from).</param>
/// <param name="SearchPassedOver">With no code-search model proposed: the installed embedding models passed over for
/// searching code worse than keywords alone (<see cref="EmbeddingModels.PassedOver"/>), said on the card.</param>
internal sealed record FirstRunPlan(
    string Provider, string Url, string? ChatModel, string? CompletionModel, string? SearchModel, int ChatCount,
    double BudgetGb, double NeededGb, IReadOnlyList<ModelDownload> Downloads, IReadOnlyList<string> Listed,
    int InstalledCount, bool NoneFits = false, WindowAdvice? Window = null, IReadOnlyList<InstalledModelInfo>? Installed = null,
    IReadOnlyList<string>? SearchPassedOver = null)
{
    /// <summary>The proposed models fit the card (or the card is unknown).</summary>
    public bool Fits => BudgetGb <= 0 || NeededGb <= BudgetGb;
}

/// <summary>
/// The first-run card's proposal for one server: the chat and autocomplete models the settings' "Suggest the best models"
/// would pick (<see cref="ModelAdvisor"/>, one logic for both), the code-search model Visual Studio's first run picks
/// (<see cref="EmbeddingModels.PreferredOf"/>), and, when the server has no chat model, what to download.
/// </summary>
internal static class FirstRunPlanner
{
    /// <param name="client">A client for that server.</param>
    /// <param name="server">That server's settings (its address and type).</param>
    /// <param name="budgetGb">The card's memory; 0 = unknown.</param>
    public static async Task<FirstRunPlan> PlanAsync(
        IInferenceProvider client, InferpalConfig server, double budgetGb, CancellationToken ct)
    {
        var installed = await client.ListInstalledModelsAsync(ct, server.BaseUrl).ConfigureAwait(false);
        return Plan(installed, server.Provider, server.BaseUrl, budgetGb, client.Capabilities.ModelManagement) with
        {
            Installed = installed,
        };
    }

    /// <summary>The pure decision (tested without a server).</summary>
    /// <param name="canDownload">The server takes downloads through its API (Ollama, LM Studio).</param>
    public static FirstRunPlan Plan(
        IReadOnlyList<InstalledModelInfo> installed, string provider, string url, double budgetGb, bool canDownload)
    {
        var byKind = ModelsByKind.Of(installed);
        if (byKind.Chat.Count == 0)
        {
            // Only Ollama's library names are known: another server is told to get a chat model its own way.
            var downloadable = canDownload && InferenceProviderFactory.Canonical(provider) == InferenceProviderFactory.Ollama;
            var downloads = downloadable ? ModelAdvisor.DownloadsFor(budgetGb) : [];
            return new(provider, url, null, null, null, 0, budgetGb, 0, downloads, [.. installed.Select(m => m.Name)],
                       installed.Count, NoneFits: downloadable && downloads.Count == 0);
        }

        var suggestion = ModelAdvisor.Suggest(installed, budgetGb, new Dictionary<string, string>(), Strings.SuggestNothingListed);
        string? Value(string key) => suggestion.Fields.FirstOrDefault(f => f.Key == key)?.Value is { Length: > 0 } v ? v : null;
        var chat       = Value("defaultModel")!;
        var completion = Value("inlineCompletionModel");
        var search     = EmbeddingModels.PreferredOf(byKind.Embedding);

        long SizeOf(string? name) =>
            name is null ? 0 : installed.FirstOrDefault(m => ModelCatalog.SameModelName(m.Name, name))?.SizeBytes ?? 0;
        ModelCatalog.TrioFitsBudget(budgetGb, [SizeOf(chat), SizeOf(completion), SizeOf(search)], out var needed);
        return new(provider, url, chat, completion, search, byKind.Chat.Count, budgetGb, needed, [], [], installed.Count,
                   SearchPassedOver: search is null ? EmbeddingModels.PassedOver(byKind.Embedding) : []);
    }
}
