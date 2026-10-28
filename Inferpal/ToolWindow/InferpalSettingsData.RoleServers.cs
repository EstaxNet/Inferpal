using System.Collections.ObjectModel;
using System.Runtime.Serialization;
using Inferpal.Config;
using Inferpal.Localization;
using Inferpal.Services;
using Inferpal.Services.Inference;
using Inferpal.Services.Presentation;
using Microsoft.VisualStudio.Extensibility.UI;

namespace Inferpal.ToolWindow;

/// <summary>
/// Autocomplete and code search on a server of their own (<see cref="RoleServers"/>): on the Server page, where each
/// runs, its model, and its address with its own Test button; in the advanced Connection section, the kind and the key
/// of each server. The form of the VS Code panel (<see cref="SettingsSchema"/>), field for field.
/// </summary>
/// <remarks>
/// ⚠ Flat properties, one set per role, not an object per role: rules 16 and 26 of <c>ConventionCoverageTests</c> derive
/// the bound lists and properties from <c>{Binding Name}</c>, and a dotted path would hide them from both.
/// </remarks>
internal partial class InferpalSettingsData
{
    [DataMember] public ObservableCollection<string> AvailableCompletionPlaces { get; } = [];
    [DataMember] public ObservableCollection<string> AvailableSearchPlaces     { get; } = [];
    /// <summary>The completion model box's list: the chat server's models, or its own server's when it runs elsewhere.</summary>
    [DataMember] public ObservableCollection<string> AvailableCompletionModels { get; } = [];

    private string? _completionRunsOn, _searchRunsOn, _completionProvider, _searchProvider;
    private string _completionBaseUrl = string.Empty, _searchBaseUrl = string.Empty;
    private string _completionApiKey = string.Empty, _searchApiKey = string.Empty;
    private string _completionStatus = string.Empty, _searchStatus = string.Empty, _completionEmptyChoice = string.Empty;
    private bool _completionElsewhere, _searchElsewhere;

    [DataMember] public string? CompletionRunsOn
    {
        get => _completionRunsOn;
        set { if (SetProperty(ref _completionRunsOn, value)) OnRolePlaceChanged(); }
    }
    [DataMember] public string? SearchRunsOn
    {
        get => _searchRunsOn;
        set { if (SetProperty(ref _searchRunsOn, value)) OnRolePlaceChanged(); }
    }
    [DataMember] public string CompletionBaseUrl
    {
        get => _completionBaseUrl;
        set { if (SetProperty(ref _completionBaseUrl, value)) OnRoleServerEdited(); }
    }
    [DataMember] public string SearchBaseUrl
    {
        get => _searchBaseUrl;
        set { if (SetProperty(ref _searchBaseUrl, value)) OnRoleServerEdited(); }
    }
    [DataMember] public string? CompletionProvider { get => _completionProvider; set { if (SetProperty(ref _completionProvider, value)) OnRoleServerEdited(); } }
    [DataMember] public string? SearchProvider     { get => _searchProvider;     set { if (SetProperty(ref _searchProvider,     value)) OnRoleServerEdited(); } }
    [DataMember] public string  CompletionApiKey   { get => _completionApiKey;   set { if (SetProperty(ref _completionApiKey,   value)) OnRoleServerEdited(); } }
    [DataMember] public string  SearchApiKey       { get => _searchApiKey;       set { if (SetProperty(ref _searchApiKey,       value)) OnRoleServerEdited(); } }
    /// <summary>What the completion server's Test button found.</summary>
    [DataMember] public string  CompletionStatus   { get => _completionStatus;   set => SetProperty(ref _completionStatus,   value); }
    [DataMember] public string  SearchStatus       { get => _searchStatus;       set => SetProperty(ref _searchStatus,       value); }
    /// <summary>"Another server" is chosen: its address, kind and key are shown.</summary>
    [DataMember] public bool    CompletionElsewhere { get => _completionElsewhere; set => SetProperty(ref _completionElsewhere, value); }
    [DataMember] public bool    SearchElsewhere     { get => _searchElsewhere;     set => SetProperty(ref _searchElsewhere,     value); }
    /// <summary>What the completion model box shows for its empty value: the chat's model on the chat's server, none on
    /// a server of its own (<see cref="RoleServers.CompletionModel"/>).</summary>
    [DataMember] public string  CompletionEmptyChoice { get => _completionEmptyChoice; set => SetProperty(ref _completionEmptyChoice, value); }

    [DataMember] public AsyncCommand TestCompletionServerCommand { get; private set; } = null!;
    [DataMember] public AsyncCommand TestSearchServerCommand     { get; private set; } = null!;

    private string _labelSectionAutocomplete = string.Empty, _labelSectionSearch = string.Empty, _labelRunsOn = string.Empty,
                   _hintCompletionRunsOn = string.Empty, _hintSearchRunsOn = string.Empty, _labelRoleServerUrl = string.Empty,
                   _hintRoleServerUrl = string.Empty, _labelRoleModel = string.Empty, _labelCompletionProvider = string.Empty,
                   _labelCompletionApiKey = string.Empty, _labelSearchProvider = string.Empty, _labelSearchApiKey = string.Empty,
                   _hintRoleProvider = string.Empty;
    [DataMember] public string LabelSectionAutocomplete { get => _labelSectionAutocomplete; set => SetProperty(ref _labelSectionAutocomplete, value); }
    [DataMember] public string LabelSectionSearch       { get => _labelSectionSearch;       set => SetProperty(ref _labelSectionSearch,       value); }
    [DataMember] public string LabelRunsOn              { get => _labelRunsOn;              set => SetProperty(ref _labelRunsOn,              value); }
    [DataMember] public string HintCompletionRunsOn     { get => _hintCompletionRunsOn;     set => SetProperty(ref _hintCompletionRunsOn,     value); }
    [DataMember] public string HintSearchRunsOn         { get => _hintSearchRunsOn;         set => SetProperty(ref _hintSearchRunsOn,         value); }
    [DataMember] public string LabelRoleServerUrl       { get => _labelRoleServerUrl;       set => SetProperty(ref _labelRoleServerUrl,       value); }
    [DataMember] public string HintRoleServerUrl        { get => _hintRoleServerUrl;        set => SetProperty(ref _hintRoleServerUrl,        value); }
    [DataMember] public string LabelRoleModel           { get => _labelRoleModel;           set => SetProperty(ref _labelRoleModel,           value); }
    [DataMember] public string LabelCompletionProvider  { get => _labelCompletionProvider;  set => SetProperty(ref _labelCompletionProvider,  value); }
    [DataMember] public string LabelCompletionApiKey    { get => _labelCompletionApiKey;    set => SetProperty(ref _labelCompletionApiKey,    value); }
    [DataMember] public string LabelSearchProvider      { get => _labelSearchProvider;      set => SetProperty(ref _labelSearchProvider,      value); }
    [DataMember] public string LabelSearchApiKey        { get => _labelSearchApiKey;        set => SetProperty(ref _labelSearchApiKey,        value); }
    [DataMember] public string HintRoleProvider         { get => _hintRoleProvider;         set => SetProperty(ref _hintRoleProvider,         value); }

    // What a screen reader names each field: the role, then the field — two "Runs on" and three "Test" on one page.
    private string _automationCompletionRunsOn = string.Empty, _automationCompletionUrl = string.Empty,
                   _automationCompletionTest = string.Empty, _automationSearchRunsOn = string.Empty,
                   _automationSearchUrl = string.Empty, _automationSearchTest = string.Empty;
    [DataMember] public string AutomationCompletionRunsOn { get => _automationCompletionRunsOn; set => SetProperty(ref _automationCompletionRunsOn, value); }
    [DataMember] public string AutomationCompletionUrl    { get => _automationCompletionUrl;    set => SetProperty(ref _automationCompletionUrl,    value); }
    [DataMember] public string AutomationCompletionTest   { get => _automationCompletionTest;   set => SetProperty(ref _automationCompletionTest,   value); }
    [DataMember] public string AutomationSearchRunsOn     { get => _automationSearchRunsOn;     set => SetProperty(ref _automationSearchRunsOn,     value); }
    [DataMember] public string AutomationSearchUrl        { get => _automationSearchUrl;        set => SetProperty(ref _automationSearchUrl,        value); }
    [DataMember] public string AutomationSearchTest       { get => _automationSearchTest;       set => SetProperty(ref _automationSearchTest,       value); }

    /// <summary>The chat server's last listing: what a role on the chat's server lists.</summary>
    private ModelsByKind _chatListing = ModelsByKind.None;

    /// <summary>Each role server's last listing, by server (its kind and address): a listing that comes back late files
    /// itself under its own server and never stands for another.</summary>
    private readonly Dictionary<(string Kind, string Url), ModelsByKind> _roleListing = [];

    /// <summary>While the fields are put back all at once: the state follows once, after.</summary>
    private bool _loadingRoleServers;

    /// <summary>The roles' Test buttons and fields from the saved settings; called by the constructor, after the chat's
    /// server and models (the place lists are filled with the labels, in the interface language).</summary>
    private void InitRoleServers(InferpalConfig saved)
    {
        TestCompletionServerCommand = new AsyncCommand((_, ct) => TestRoleServerAsync(ServerRole.Completion, ct));
        TestSearchServerCommand     = new AsyncCommand((_, ct) => TestRoleServerAsync(ServerRole.Search, ct));
        LoadRoleServerFields(saved);
    }

    /// <summary>Puts the roles' fields back to what <paramref name="c"/> holds — the window opening, or Cancel.</summary>
    private void LoadRoleServerFields(InferpalConfig c)
    {
        static string Place((string Code, string Name)[] options, string? code) =>
            options.FirstOrDefault(o => string.Equals(o.Code, code?.Trim(), StringComparison.OrdinalIgnoreCase)).Name ?? options[0].Name;
        static string? Kind(string? code) =>
            string.IsNullOrWhiteSpace(code) ? null : ProviderOptions.FirstOrDefault(p => p.Code == code.Trim()).Name;

        _loadingRoleServers = true;
        try
        {
            // ⚠ A key written as null in the file reads as null: every field of the form is a string.
            CompletionRunsOn   = Place(RoleServerSettings.CompletionPlaces, c.InlineCompletionRunsOn);
            CompletionBaseUrl  = c.InlineCompletionBaseUrl ?? string.Empty;
            CompletionProvider = Kind(c.InlineCompletionProvider);
            CompletionApiKey   = c.InlineCompletionApiKey ?? string.Empty;
            SearchRunsOn       = Place(RoleServerSettings.SearchPlaces, c.RagEmbeddingRunsOn);
            SearchBaseUrl      = c.RagEmbeddingBaseUrl ?? string.Empty;
            SearchProvider     = Kind(c.RagEmbeddingProvider);
            SearchApiKey       = c.RagEmbeddingApiKey ?? string.Empty;
            CompletionStatus   = SearchStatus = string.Empty;
        }
        finally { _loadingRoleServers = false; }
        UpdateRoleServerState();
    }

    /// <summary>The roles' words, and the place lists, in the interface language; part of <see cref="ApplyLabels"/>.</summary>
    /// <remarks>⚠ The places are translated: each list is relabelled IN PLACE, by index, and the selection follows its
    /// index — never cleared (rule 16), never matched by its old text.</remarks>
    private void ApplyRoleServerLabels()
    {
        LabelSectionAutocomplete = Strings.SettingsPageAutocomplete;
        LabelSectionSearch       = Strings.SettingsPageSearch;
        LabelRunsOn              = Strings.LabelRunsOn;
        HintCompletionRunsOn     = Strings.HintInlineCompletionRunsOn;
        HintSearchRunsOn         = Strings.HintRagEmbeddingRunsOn;
        LabelRoleServerUrl       = Strings.LabelRoleServerUrl;
        HintRoleServerUrl        = Strings.HintRoleServerUrl;
        LabelRoleModel           = Strings.LabelRoleModel;
        LabelCompletionProvider  = Strings.LabelCompletionProvider;
        LabelCompletionApiKey    = Strings.LabelCompletionApiKey;
        LabelSearchProvider      = Strings.LabelSearchProvider;
        LabelSearchApiKey        = Strings.LabelSearchApiKey;
        HintRoleProvider         = Strings.HintRoleProvider;

        AutomationCompletionRunsOn = $"{Strings.SettingsPageAutocomplete} — {Strings.LabelRunsOn}";
        AutomationCompletionUrl    = $"{Strings.SettingsPageAutocomplete} — {Strings.LabelRoleServerUrl}";
        AutomationCompletionTest   = $"{Strings.SettingsPageAutocomplete} — {Strings.BtnTest}";
        AutomationSearchRunsOn     = $"{Strings.SettingsPageSearch} — {Strings.LabelRunsOn}";
        AutomationSearchUrl        = $"{Strings.SettingsPageSearch} — {Strings.LabelRoleServerUrl}";
        AutomationSearchTest       = $"{Strings.SettingsPageSearch} — {Strings.BtnTest}";

        var completion = RoleServerSettings.Relabel(AvailableCompletionPlaces, RoleServerSettings.CompletionPlaces, _completionRunsOn,
                                 _config.InlineCompletionRunsOn);
        var search     = RoleServerSettings.Relabel(AvailableSearchPlaces,     RoleServerSettings.SearchPlaces,     _searchRunsOn,
                                 _config.RagEmbeddingRunsOn);
        _loadingRoleServers = true;
        try
        {
            CompletionRunsOn = completion;
            SearchRunsOn     = search;
        }
        finally { _loadingRoleServers = false; }
        UpdateRoleServerState();
    }

    /// <summary>A kind of server's code from its name in the list; <paramref name="current"/> when none is selected.</summary>
    private static string KindCode(string? name, string current) =>
        name is null ? current : ProviderOptions.FirstOrDefault(p => p.Name == name).Code ?? current;

    /// <summary>The settings the form shows, for the roles' resolution (<see cref="RoleServers"/>): the chat's server
    /// and each role's, as typed — the same answer the saved settings will give.</summary>
    private InferpalConfig RoleDraft() => new()
    {
        Provider                 = KindCode(SelectedProvider, _config.Provider),
        BaseUrl                  = (BaseUrl ?? string.Empty).Trim(),
        ApiKey                   = (ApiKey ?? string.Empty).Trim(),
        InlineCompletionRunsOn   = RoleServerSettings.PlaceCode(AvailableCompletionPlaces, RoleServerSettings.CompletionPlaces, CompletionRunsOn,
                                             _config.InlineCompletionRunsOn, out _),
        InlineCompletionBaseUrl  = CompletionBaseUrl.Trim(),
        InlineCompletionProvider = KindCode(CompletionProvider, string.Empty),
        InlineCompletionApiKey   = CompletionApiKey.Trim(),
        RagEmbeddingRunsOn       = RoleServerSettings.PlaceCode(AvailableSearchPlaces, RoleServerSettings.SearchPlaces, SearchRunsOn,
                                             _config.RagEmbeddingRunsOn, out _),
        RagEmbeddingBaseUrl      = SearchBaseUrl.Trim(),
        RagEmbeddingProvider     = KindCode(SearchProvider, string.Empty),
        RagEmbeddingApiKey       = SearchApiKey.Trim(),
    };

    /// <summary>The server a role's listing belongs to: its kind and its address (a key typed later is re-listed by the
    /// Test button, not at every keystroke).</summary>
    private static (string Kind, string Url) ListedServer(InferpalConfig draft, ServerRole role)
    {
        var (provider, url, _) = RoleServers.Of(draft, role);
        return (InferenceProviderFactory.Canonical(provider), url);
    }

    /// <summary>A place chosen: the state follows, and a server not listed yet is listed.</summary>
    private void OnRolePlaceChanged()
    {
        if (_loadingRoleServers) return;
        UpdateRoleServerState();
        _ = ListRoleModelsAsync(force: false, CancellationToken.None);
    }

    /// <summary>An address, a kind or a key edited: the state follows (listing waits for the Test button).</summary>
    private void OnRoleServerEdited()
    {
        if (!_loadingRoleServers) UpdateRoleServerState();
    }

    /// <summary>What follows where the roles run: the address, kind and key shown for "another server", the empty
    /// completion model's words, the Test buttons, the model lists, the "model used" lines.</summary>
    private void UpdateRoleServerState()
    {
        if (TestCompletionServerCommand is null) return;   // the constructor has not built the commands yet
        var draft = RoleDraft();
        CompletionElsewhere   = draft.InlineCompletionRunsOn == RoleServers.OnOther;
        SearchElsewhere       = draft.RagEmbeddingRunsOn == RoleServers.OnOther;
        CompletionEmptyChoice = RoleServers.OnChatServer(draft, ServerRole.Completion) ? EmptySameAsChat : Strings.SettingsNoModelChosen;
        // No address yet: the probe would test the chat's server and name it as this one.
        TestCompletionServerCommand.CanExecute = draft.InlineCompletionBaseUrl.Length > 0;
        TestSearchServerCommand.CanExecute     = draft.RagEmbeddingBaseUrl.Length > 0;
        SyncRoleModelLists(draft);
        UpdateModelLines();
    }

    /// <summary>Each role's model box lists its server's models: the chat's listing on the chat's server, its own
    /// server's otherwise — never the chat's models for a server that does not hold them.</summary>
    private void SyncRoleModelLists(InferpalConfig? draft = null)
    {
        draft ??= RoleDraft();
        ModelsByKind Listing(ServerRole role) =>
            RoleServers.OnChatServer(draft, role) ? _chatListing
            : _roleListing.TryGetValue(ListedServer(draft, role), out var own) ? own : ModelsByKind.None;
        SelectionPreservingList.Sync(AvailableCompletionModels, Listing(ServerRole.Completion).Chat,
                                     [InlineCompletionModel], leadingEmpty: true);
        // Only the models recognised as embedding models, plus the configured one, after an empty entry: left empty,
        // the best one installed is used at run time. Nothing is picked on the user's behalf.
        SelectionPreservingList.Sync(AvailableEmbeddingModels, Listing(ServerRole.Search).Embedding,
                                     [RagEmbeddingModel], leadingEmpty: true);
    }

    /// <summary>Lists the models of each role that runs on a server of its own — again when <paramref name="force"/>
    /// (the refresh button: a model pulled there since), else only a server not listed yet.</summary>
    private async Task ListRoleModelsAsync(bool force, CancellationToken ct)
    {
        foreach (var role in new[] { ServerRole.Completion, ServerRole.Search })
        {
            InferpalConfig draft = null!;
            var skip = true;
            await RunOnVMContextAsync(() =>
            {
                draft = RoleDraft();
                skip  = RoleServers.OnChatServer(draft, role) || (!force && _roleListing.ContainsKey(ListedServer(draft, role)));
                // Listed again: what the last listing said no longer stands; a failure says itself again.
                if (!skip && force)
                {
                    if (role == ServerRole.Completion) CompletionStatus = string.Empty; else SearchStatus = string.Empty;
                }
            });
            if (!skip) await ListRoleModelsAsync(role, draft, ct);
        }
    }

    /// <summary>Lists one role's server, files the listing under that server, and — still that role's server — says why a
    /// listing came back empty (stopped, refused, nothing installed): an empty box says nothing.</summary>
    private async Task ListRoleModelsAsync(ServerRole role, InferpalConfig draft, CancellationToken ct)
    {
        try
        {
            var server = ListedServer(draft, role);
            var client = InferenceProviderFactory.Create(draft, role);
            var models = await client.ListModelsByKindAsync(ct);
            var why = models.Listed.Count > 0 ? null
                : await ModelCatalog.EmptyListMeansAsync(client, new InferpalConfig { BaseUrl = server.Url },
                                                         Strings.SuggestNothingListed, ct);
            await RunOnVMContextAsync(() =>
            {
                _roleListing[server] = models;
                var shown = role == ServerRole.Completion ? CompletionElsewhere : SearchElsewhere;
                if (why is not null && shown && ListedServer(RoleDraft(), role) == server)
                {
                    if (role == ServerRole.Completion) CompletionStatus = why; else SearchStatus = why;
                }
                SyncRoleModelLists();
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Diagnostics.Swallow("Settings.RoleModels", ex); }
    }

    /// <summary>A role's Test button: probes ITS address with ITS key, names the kind of server that answers and selects
    /// it, then lists that server's models into the role's box.</summary>
    private async Task TestRoleServerAsync(ServerRole role, CancellationToken ct)
    {
        var completion = role == ServerRole.Completion;
        string url = string.Empty, key = string.Empty;
        await RunOnVMContextAsync(() =>
        {
            url = (completion ? CompletionBaseUrl : SearchBaseUrl).Trim();
            key = (completion ? CompletionApiKey : SearchApiKey).Trim();
            if (completion) CompletionStatus = Strings.StatusConnecting; else SearchStatus = Strings.StatusConnecting;
        });
        if (url.Length == 0) return;

        var (detected, refusal) = await ProviderProbe.DetectWithRefusalAsync(url, key, ct);
        var name = detected is null ? null : ProviderOptions.FirstOrDefault(p => p.Code == detected).Name;
        // A server that refused the probe (a wrong key) is up — the very thing this button checks. Resolved here, off
        // the view-model's context: a system proxy's PAC lookup can block.
        var notReached = name is null ? Strings.StatusNotReached(url, refusal) : null;
        InferpalConfig draft = null!;
        await RunOnVMContextAsync(() =>
        {
            var status = notReached ?? $"{Strings.StatusConnected} — {name}";
            if (completion) { CompletionStatus = status; if (name is not null) CompletionProvider = name; }
            else            { SearchStatus     = status; if (name is not null) SearchProvider     = name; }
            draft = RoleDraft();
        });
        if (name is not null && !RoleServers.OnChatServer(draft, role))
            await ListRoleModelsAsync(role, draft, ct);
    }

    /// <summary>The roles' fields as Save reads them (<see cref="RoleServerSettings.Write"/>), captured on the
    /// view-model's context.</summary>
    private RoleServerForm CaptureRoleServers() => new(
        CompletionRunsOn is null ? -1 : AvailableCompletionPlaces.IndexOf(CompletionRunsOn), CompletionRunsOn,
        CompletionBaseUrl.Trim(), CompletionProvider, CompletionApiKey.Trim(),
        SearchRunsOn is null ? -1 : AvailableSearchPlaces.IndexOf(SearchRunsOn), SearchRunsOn,
        SearchBaseUrl.Trim(), SearchProvider, SearchApiKey.Trim());

    /// <summary>The roles' fields against what is saved, for the unsaved-changes count.</summary>
    private IEnumerable<(string Form, string Saved)> RoleServerChanges(InferpalConfig saved)
    {
        var completionPlace = RoleServerSettings.PlaceMeaning(RoleServerSettings.CompletionPlaces, saved.InlineCompletionRunsOn);
        yield return (RoleServerSettings.PlaceCode(AvailableCompletionPlaces, RoleServerSettings.CompletionPlaces, CompletionRunsOn, completionPlace, out _),
                      completionPlace);
        yield return (CompletionBaseUrl.Trim(), saved.InlineCompletionBaseUrl);
        yield return (KindCode(CompletionProvider, saved.InlineCompletionProvider), saved.InlineCompletionProvider);
        yield return (CompletionApiKey.Trim(), saved.InlineCompletionApiKey);
        var searchPlace = RoleServerSettings.PlaceMeaning(RoleServerSettings.SearchPlaces, saved.RagEmbeddingRunsOn);
        yield return (RoleServerSettings.PlaceCode(AvailableSearchPlaces, RoleServerSettings.SearchPlaces, SearchRunsOn, searchPlace, out _),
                      searchPlace);
        yield return (SearchBaseUrl.Trim(), saved.RagEmbeddingBaseUrl);
        yield return (KindCode(SearchProvider, saved.RagEmbeddingProvider), saved.RagEmbeddingProvider);
        yield return (SearchApiKey.Trim(), saved.RagEmbeddingApiKey);
    }
}
