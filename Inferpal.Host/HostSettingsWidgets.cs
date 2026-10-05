using Inferpal.Config;
using Inferpal.Services.Agent;
using Inferpal.Services.Presentation;
using Inferpal.Services.Prompting;
using StreamJsonRpc;

namespace Inferpal.Host;

/// <summary>
/// The live blocks of the settings pages (<see cref="SettingSection.Widget"/>): the code index, the @Docs sites, how
/// full the conversation is, the project's files, the pinned files' sizes. Built by the Core presenters the Visual
/// Studio window uses, so both editors say the same thing about the same index.
/// </summary>
internal sealed partial class HostServer
{
    private SettingsDocsActions? _settingsDocs;

    /// <summary>`settings/indexCard` — the code index as the Code search page shows it.</summary>
    [JsonRpcMethod("settings/indexCard")]
    public async Task<IndexCardModel> SettingsIndexCard(CancellationToken ct)
    {
        var s = Session();
        return SettingsWidgets.IndexCard(await s.Index.SnapshotAsync(ct), s.Config.RagEnabled,
                                         Services.Inference.EmbeddingModels.Configured(s.Config), DateTime.Now);
    }

    /// <summary>`settings/indexRebuild` — <c>/index rebuild</c>, from the page's button.</summary>
    [JsonRpcMethod("settings/indexRebuild")]
    public Task<IndexCardModel> SettingsIndexRebuild(CancellationToken ct)
    {
        var s = Session();
        if (!string.IsNullOrEmpty(s.RootDir) && !s.Index.IsIndexing) s.Index.StartIndexing(s.RootDir);
        return SettingsIndexCard(ct);
    }

    /// <summary>`settings/exclusions` — the patterns of <c>.inferpal/project.json</c> the index applies.</summary>
    [JsonRpcMethod("settings/exclusions")]
    public SettingsExclusionsDto SettingsExclusions()
    {
        var s = Session();
        var file = string.IsNullOrEmpty(s.RootDir) ? string.Empty : Path.Combine(s.RootDir, ".inferpal", "project.json");
        return new([.. s.Index.ProfileExcludes], file, file.Length > 0 && File.Exists(file));
    }

    /// <summary>`settings/docsSites` — the @Docs sites, with what each crawl did.</summary>
    [JsonRpcMethod("settings/docsSites")]
    public async Task<SettingsDocsDto> SettingsDocsSites(CancellationToken ct) =>
        new([.. await DocsActions().RowsAsync(ct)], Message: null);

    /// <summary>`settings/docsAction` — add, reindex or remove a site through <c>/docs</c>; the crawl runs on.</summary>
    [JsonRpcMethod("settings/docsAction", UseSingleObjectParameterDeserialization = true)]
    public async Task<SettingsDocsDto> SettingsDocsAction(SettingsDocsActionParams p, CancellationToken ct)
    {
        if (p.Verb is not ("add" or "reindex" or "remove"))
            throw new LocalRpcException($"Unknown docs action '{p.Verb}'. Use one of: add, reindex, remove.");
        var actions = DocsActions();
        var message = await actions.RunAsync(p.Verb, p.Arg, ct);
        return new([.. await actions.RowsAsync(ct)], message);
    }

    /// <summary>`settings/contextUsage` — how full the conversation's window is, from the X-Ray panel's counts.</summary>
    [JsonRpcMethod("settings/contextUsage")]
    public ContextUsageModel SettingsContextUsage()
    {
        var s = Session();
        return SettingsWidgets.ContextUsage(XRayPanelPresenter.Build(
            BuildPromptSections(s), s.XrayDisabledSections,
            AgentOrchestrator.EstimateConversationTokens(SnapshotHistory(s)), s.ContextWindowInUse,
            toolTokens: ContextManager.NextTurnToolTokens(s.Tools, s.ToolsEnabled, s.PlanMode)));
    }

    /// <summary>`settings/projectFiles` — the project's files the prompt reads, and whether each exists.</summary>
    [JsonRpcMethod("settings/projectFiles")]
    public IReadOnlyList<ProjectFileRow> SettingsProjectFiles() => SettingsWidgets.ProjectFiles(Session().RootDir);

    /// <summary>`settings/pinSizes` — what each pinned file the panel holds costs the prompt (unsaved edits included),
    /// switched off or past the cap too: the list shows every line, as Visual Studio's does.</summary>
    [JsonRpcMethod("settings/pinSizes", UseSingleObjectParameterDeserialization = true)]
    public IReadOnlyList<PinnedFileSize> SettingsPinSizes(SettingsPinSizesParams p) =>
        SettingsWidgets.PinnedSizes((p.Pins ?? string.Empty).Split('\n')
            .Select(l => l.Trim().TrimStart('#').Trim()).Where(l => l.Length > 0));

    /// <summary>`settings/loadedModels` — what the server holds in memory, and what Inferpal uses each model for.</summary>
    [JsonRpcMethod("settings/loadedModels")]
    public async Task<LoadedModelsModel> SettingsLoadedModels(CancellationToken ct)
    {
        var s = Session();
        return await LoadedModelsCard.ReadAsync(s.Client, s.Config, (await s.Index.SnapshotAsync(ct)).Model, ct);
    }

    /// <summary>`settings/unloadModels` — unloads the named models (every loaded one without names), then the block as
    /// the server now answers it, with what the unload did.</summary>
    [JsonRpcMethod("settings/unloadModels", UseSingleObjectParameterDeserialization = true)]
    public async Task<LoadedModelsModel> SettingsUnloadModels(SettingsUnloadParams p, CancellationToken ct)
    {
        var s = Session();
        var message = await ModelUnloader.UnloadAsync(s.Client, s.Config, p.Names is { Count: > 0 } ? p.Names : null, ct);
        return await LoadedModelsCard.ReadAsync(s.Client, s.Config, (await s.Index.SnapshotAsync(ct)).Model, ct, message);
    }

    /// <summary>
    /// `settings/suggestModels` — the best installed models for the form, from the server the FORM names (as
    /// <c>models/list</c>); proposed, never saved.
    /// </summary>
    [JsonRpcMethod("settings/suggestModels", UseSingleObjectParameterDeserialization = true)]
    public async Task<ModelSuggestion> SettingsSuggestModels(SettingsSuggestParams p, CancellationToken ct)
    {
        var s = Session();
        var draft = new InferpalConfig
        {
            Provider = string.IsNullOrWhiteSpace(p.Provider) ? s.Config.Provider : p.Provider.Trim(),
            BaseUrl  = string.IsNullOrWhiteSpace(p.BaseUrl) ? s.Config.BaseUrl : p.BaseUrl.Trim(),
            ApiKey   = p.ApiKey ?? s.Config.ApiKey,
        };
        var budget = double.TryParse(p.VramBudgetGb, System.Globalization.NumberStyles.Float,
                                     System.Globalization.CultureInfo.InvariantCulture, out var gb) && gb > 0
            ? gb : s.Config.VramBudgetGb;
        return await ModelAdvisor.SuggestAsync(_providerFactory(draft), draft, budget,
                                               p.Current ?? new Dictionary<string, string>(), ct);
    }

    private SettingsDocsActions DocsActions()
    {
        var s = Session();
        return _settingsDocs ??= new SettingsDocsActions(s.Config, s.Docs);
    }
}

/// <summary>`settings/exclusions` answer. <c>File</c>: the profile's path (empty without a workspace).</summary>
internal sealed record SettingsExclusionsDto(List<string> Patterns, string File, bool Exists);

/// <summary>`settings/docsSites` and `settings/docsAction` answer. <c>Message</c>: what the command said, if anything.</summary>
internal sealed record SettingsDocsDto(List<DocsSiteRow> Sites, string? Message);

/// <summary><c>Verb</c>: add | reindex | remove; <c>Arg</c>: the URL to add, or the site's id.</summary>
internal sealed record SettingsDocsActionParams(string Verb, string Arg);

internal sealed record SettingsPinSizesParams(string? Pins);

/// <summary><c>Names</c>: the models to unload; <c>null</c> or empty = every loaded one.</summary>
internal sealed record SettingsUnloadParams(List<string>? Names);

/// <summary>The form's server (as <c>models/list</c>), its card's memory as typed (invariant number; empty = the saved
/// one), and the form's model fields by configuration key.</summary>
internal sealed record SettingsSuggestParams(
    string? BaseUrl = null, string? Provider = null, string? ApiKey = null, string? VramBudgetGb = null,
    Dictionary<string, string>? Current = null);
