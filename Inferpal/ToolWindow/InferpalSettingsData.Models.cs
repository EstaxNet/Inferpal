using System.Collections.ObjectModel;
using System.Globalization;
using System.Runtime.Serialization;
using Inferpal.Config;
using Inferpal.Localization;
using Inferpal.Services.Inference;
using Inferpal.Services.Presentation;
using Microsoft.VisualStudio.Extensibility.UI;

namespace Inferpal.ToolWindow;

/// <summary>
/// The Server page's two model blocks: "Suggest the best models" (<see cref="ModelAdvisor"/>) and "Loaded now"
/// (<see cref="LoadedModelsCard"/>, <see cref="ModelUnloader"/>) — the Core's facts and sentences, the ones the VS Code
/// panel shows, bound here as primitives for Remote UI.
/// </summary>
internal partial class InferpalSettingsData
{
    // ── Suggest the best models ──────────────────────────────────────────────
    private string _suggestionSummary = "", _btnSuggestModels = "", _hintSuggestModels = "";
    private bool _hasSuggestion, _suggesting;

    [DataMember] public string BtnSuggestModels  { get => _btnSuggestModels;  set => SetProperty(ref _btnSuggestModels,  value); }
    [DataMember] public string HintSuggestModels { get => _hintSuggestModels; set => SetProperty(ref _hintSuggestModels, value); }
    /// <summary>One line per field: what was proposed for it, and why.</summary>
    [DataMember] public ObservableCollection<string> SuggestionLines { get; } = [];
    /// <summary>What the suggestion left as it is, and why it matters.</summary>
    [DataMember] public ObservableCollection<string> SuggestionNotes { get; } = [];
    [DataMember] public string SuggestionSummary { get => _suggestionSummary; set => SetProperty(ref _suggestionSummary, value); }
    [DataMember] public bool   HasSuggestion     { get => _hasSuggestion;     set => SetProperty(ref _hasSuggestion,     value); }
    [DataMember] public bool   NotSuggesting     { get => !_suggesting;       set => SetProperty(ref _suggesting,        !value); }
    [DataMember] public AsyncCommand SuggestModelsCommand { get; private set; } = null!;

    // ── Loaded now ───────────────────────────────────────────────────────────
    private string _loadedModelsSummary = "", _loadedModelsMessage = "", _labelLoadedModels = "", _noteLoadedModels = "",
                   _btnUnloadAll = "", _btnUnloadModel = "", _btnRefreshLoaded = "";
    private bool _canUnloadAll;

    [DataMember] public string LabelLoadedModels   { get => _labelLoadedModels;   set => SetProperty(ref _labelLoadedModels,   value); }
    [DataMember] public string NoteLoadedModels    { get => _noteLoadedModels;    set => SetProperty(ref _noteLoadedModels,    value); }
    [DataMember] public string BtnUnloadAll        { get => _btnUnloadAll;        set => SetProperty(ref _btnUnloadAll,        value); }
    [DataMember] public string BtnUnloadModel      { get => _btnUnloadModel;      set => SetProperty(ref _btnUnloadModel,      value); }
    [DataMember] public string BtnRefreshLoaded    { get => _btnRefreshLoaded;    set => SetProperty(ref _btnRefreshLoaded,    value); }
    [DataMember] public string LoadedModelsSummary { get => _loadedModelsSummary; set => SetProperty(ref _loadedModelsSummary, value); }
    [DataMember] public string LoadedModelsMessage { get => _loadedModelsMessage; set => SetProperty(ref _loadedModelsMessage, value); }
    [DataMember] public bool   CanUnloadAll        { get => _canUnloadAll;        set => SetProperty(ref _canUnloadAll,        value); }
    [DataMember] public ObservableCollection<LoadedModelItem> LoadedModelRows { get; } = [];
    [DataMember] public AsyncCommand UnloadAllCommand    { get; private set; } = null!;
    [DataMember] public AsyncCommand RefreshLoadedCommand { get; private set; } = null!;

    /// <summary>Commands of the two blocks; called from <see cref="InitWidgets"/>.</summary>
    private void InitModelBlocks()
    {
        SuggestModelsCommand = new AsyncCommand((_, ct) => SuggestModelsAsync(ct));
        UnloadAllCommand     = new AsyncCommand((_, ct) => UnloadAsync(null, ct));
        RefreshLoadedCommand = new AsyncCommand((_, ct) => RefreshLoadedModelsAsync(ct));
    }

    /// <summary>The words of the two blocks; part of <see cref="ApplyWidgetLabels"/>.</summary>
    private void ApplyModelBlockLabels()
    {
        BtnSuggestModels  = Strings.BtnSuggestModels;
        HintSuggestModels = Strings.HintSuggestModels;
        LabelLoadedModels = Strings.SettingsSectionLoadedModels;
        NoteLoadedModels  = Strings.NoteLoadedModels;
        BtnUnloadAll      = Strings.BtnUnloadAll;
        BtnUnloadModel    = Strings.BtnUnloadModel;
        BtnRefreshLoaded  = Strings.BtnRefreshLoaded;
    }

    /// <summary>
    /// Proposes the best installed models into the form — from the server the FORM names, as the refresh button lists
    /// it — and saves nothing: the picks count as unsaved changes, and Save stays the user's.
    /// </summary>
    private async Task SuggestModelsAsync(CancellationToken ct)
    {
        try
        {
            string providerCode = _config.Provider, apiKey = _config.ApiKey, url = _config.BaseUrl, budgetText = "";
            Dictionary<string, string> current = [];
            await RunOnVMContextAsync(() =>
            {
                NotSuggesting = false;
                providerCode = ProviderOptions.FirstOrDefault(p => p.Name == SelectedProvider).Code ?? _config.Provider;
                apiKey       = ApiKey?.Trim() ?? string.Empty;
                url          = string.IsNullOrWhiteSpace(BaseUrl) ? _config.BaseUrl : BaseUrl.Trim();
                budgetText   = VramBudgetText.Trim();
                current      = new()
                {
                    ["defaultModel"]          = SelectedModel ?? "",
                    ["inlineCompletionModel"] = InlineCompletionModel ?? "",
                    ["ragEmbeddingModel"]     = RagEmbeddingModel ?? "",
                    ["agentModel"]            = AgentModel ?? "",
                    ["codeActionsModel"]      = CodeActionsModel ?? "",
                    ["inlineEditModel"]       = InlineEditModel ?? "",
                    ["utilityModel"]          = UtilityModel ?? "",
                };
            });
            var server = new InferpalConfig { Provider = providerCode, BaseUrl = url, ApiKey = apiKey };
            var budget = double.TryParse(budgetText, NumberStyles.Float, CultureInfo.CurrentCulture, out var gb) && gb > 0
                ? gb : _config.VramBudgetGb;
            var suggestion = await ModelAdvisor.SuggestAsync(InferenceProviderFactory.Create(server), server, budget, current, ct);

            // The lists first: a value the combo box does not hold is written back as null by the Selector.
            await RefreshModelsAsync(url, ct);
            await RunOnVMContextAsync(() =>
            {
                SuggestionLines.Clear();
                SuggestionNotes.Clear();
                if (suggestion.Refusal is null)
                {
                    foreach (var f in suggestion.Fields)
                    {
                        if (f.Changed)
                        {
                            switch (f.Key)
                            {
                                case "defaultModel":          SelectedModel         = f.Value; break;
                                case "inlineCompletionModel": InlineCompletionModel = f.Value; break;
                                case "ragEmbeddingModel":     RagEmbeddingModel     = f.Value; break;
                            }
                        }
                        SuggestionLines.Add(f.Reason);
                    }
                    foreach (var note in suggestion.Notes) SuggestionNotes.Add(note);
                }
                SuggestionSummary = suggestion.Summary;
                HasSuggestion     = true;
                NotSuggesting     = true;
                RefreshUnsaved();
            });
        }
        catch (OperationCanceledException) { await RunOnVMContextAsync(() => NotSuggesting = true); }
        catch (Exception ex)
        {
            Services.Diagnostics.Swallow("Settings.SuggestModels", ex);
            await RunOnVMContextAsync(() =>
            {
                SuggestionLines.Clear();
                SuggestionNotes.Clear();
                SuggestionSummary = Services.Diagnostics.RootMessage(ex);
                HasSuggestion     = true;
                NotSuggesting     = true;
            });
        }
    }

    /// <summary>Reads what the server holds in memory (the server Inferpal is connected to, the saved one).</summary>
    private Task RefreshLoadedModelsAsync(CancellationToken ct) => ShowLoadedModelsAsync(null, ct);

    private async Task UnloadAsync(IReadOnlyList<string>? names, CancellationToken ct)
    {
        try
        {
            await RunOnVMContextAsync(() => CanUnloadAll = false);
            var message = await ModelUnloader.UnloadAsync(_client, _config, names, ct);
            await ShowLoadedModelsAsync(message, ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Services.Diagnostics.Swallow("Settings.UnloadModels", ex);
            await ShowLoadedModelsAsync(Services.Diagnostics.RootMessage(ex), ct);
        }
    }

    private async Task ShowLoadedModelsAsync(string? message, CancellationToken ct)
    {
        try
        {
            var embedding = _live is { } live ? (await live.Index.SnapshotAsync(ct)).Model : null;
            var card = await LoadedModelsCard.ReadAsync(_client, _config, embedding, ct, message);
            await RunOnVMContextAsync(() =>
            {
                LoadedModelsSummary = card.Summary;
                LoadedModelsMessage = card.Message ?? string.Empty;
                CanUnloadAll        = card.CanUnload && card.Rows.Count > 0;
                LoadedModelRows.Clear();
                foreach (var row in card.Rows)
                    LoadedModelRows.Add(new LoadedModelItem(row, card.CanUnload,
                        new AsyncCommand((_, c) => UnloadAsync([row.Name], c))));
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Services.Diagnostics.Swallow("Settings.LoadedModels", ex); }
    }
}

/// <summary>One model the server holds in memory, as Remote UI carries it.</summary>
[DataContract]
internal sealed class LoadedModelItem : NotifyPropertyChangedObject
{
    public LoadedModelItem(LoadedModelRow row, bool canUnload, AsyncCommand unload)
    {
        Name          = row.Name;
        Uses          = row.Uses;
        Details       = row.Details;
        HasDetails    = row.Details.Length > 0;
        CanUnload     = canUnload;
        UnloadCommand = unload;
    }

    [DataMember] public string Name       { get; }
    [DataMember] public string Uses       { get; }
    [DataMember] public string Details    { get; }
    [DataMember] public bool   HasDetails { get; }
    [DataMember] public bool   CanUnload  { get; }
    [DataMember] public AsyncCommand UnloadCommand { get; }
}
