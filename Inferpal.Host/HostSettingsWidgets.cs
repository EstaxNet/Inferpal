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
