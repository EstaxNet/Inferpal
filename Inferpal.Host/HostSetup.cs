using Inferpal.Config;
using Inferpal.Localization;
using Inferpal.Models;
using Inferpal.Services.Hardware;
using Inferpal.Services.Inference;
using StreamJsonRpc;

namespace Inferpal.Host;

/// <summary>
/// The first run, for VS Code's welcome card and <c>/setup</c>: look for a model server, propose the models that fit,
/// save the choice, download a chat model — the Core's <see cref="ServerScan"/> (the search the ACP setup and Visual
/// Studio's first run make too) and <see cref="FirstRunPlanner"/>.
/// </summary>
internal sealed partial class HostServer
{
    /// <summary>
    /// `setup/needed` — why the first-run card is shown, or null: the configured server does not answer and nothing was
    /// chosen yet (<see cref="ServerScan.NothingChosen"/> — a server down later is the status line's to say, and a person
    /// who set one up is not sent looking for another), or it answers without any chat model.
    /// </summary>
    [JsonRpcMethod("setup/needed")]
    public async Task<string?> SetupNeededAsync(CancellationToken ct)
    {
        var s = Session();
        if (!await s.Client.CheckConnectionAsync(s.Config.BaseUrl, ct))
            return ServerScan.NothingChosen(s.Config) ? "unreachable" : null;
        return (await s.Client.ListModelsByKindAsync(ct)).Chat.Count == 0 ? "noChatModel" : null;
    }

    /// <summary>`setup/places` — where the scan will look, to show each place while it is checked.</summary>
    [JsonRpcMethod("setup/places")]
    public List<SetupPlaceDto> SetupPlaces() =>
        [.. ServerScan.Places(Session().Config.BaseUrl, Session().RemoteName).Select(p => new SetupPlaceDto(
            p.Url, p.Expected, "checking", null, null, 0, 0,
            p.Expected is { } code ? InferenceProviderFactory.DisplayName(code) : p.Url, Strings.SetupChecking, null))];

    /// <summary>`setup/scan` — every place a model server usually listens, and what each answered.</summary>
    [JsonRpcMethod("setup/scan")]
    public async Task<SetupScanResult> SetupScanAsync(CancellationToken ct)
    {
        var s = Session();
        var places = await ServerScan.ScanAsync(s.Config.BaseUrl, s.Config.ApiKey, _providerFactory, ct, s.RemoteName);
        return new([.. places.Select(SetupPlaceDto.Of)], ServerScan.Offered(places, s.Config.BaseUrl)?.Url);
    }

    /// <summary>`setup/examine` — the address the person typed, with the key they gave for it (none: no key is sent).</summary>
    [JsonRpcMethod("setup/examine", UseSingleObjectParameterDeserialization = true)]
    public async Task<SetupPlaceDto> SetupExamineAsync(SetupServerParams p, CancellationToken ct) =>
        SetupPlaceDto.Of(await ServerScan.ExamineAsync(
            ProviderProbe.RootOf(p.Url), null, KeyFor(Session(), p), _providerFactory, ct));

    /// <summary>`setup/plan` — the models proposed for a server that answers, or what to download when it has no chat
    /// model.</summary>
    [JsonRpcMethod("setup/plan", UseSingleObjectParameterDeserialization = true)]
    public async Task<SetupPlanDto> SetupPlanAsync(SetupServerParams p, CancellationToken ct)
    {
        var s = Session();
        var server = ServerConfig(s, p);
        var (budget, source) = await BudgetForAsync(s, server.BaseUrl, p.CardGb, ct);
        var client = _providerFactory(server);
        var plan = await FirstRunPlanner.PlanAsync(client, server, budget, ct);
        var kept = KeptRoleModels(s, server, plan.CompletionModel, plan.SearchModel);
        // A search model setup/apply keeps on this server stays in use: the card does not say search runs on keywords.
        if (!string.IsNullOrWhiteSpace(kept.Search)) plan = plan with { SearchPassedOver = [] };
        if (plan.ChatModel is { } chat)
            plan = plan with { Window = await WindowForAsync(s, server, client, plan.Installed ?? [], budget, chat, kept, ct) };
        return SetupPlanDto.Of(plan, source);
    }

    /// <summary>
    /// The context window <c>setup/apply</c> writes for <paramref name="chat"/>, beside the role models it KEEPS
    /// (<paramref name="kept"/>, <see cref="KeptRoleModels"/>) — the card shows the same one. <c>null</c> on another server
    /// type, and when the window is the person's: <c>setup/apply</c> leaves it, and the card does not announce another.
    /// </summary>
    private static async Task<WindowAdvice?> WindowForAsync(
        HostSession s, InferpalConfig server, IInferenceProvider client, IReadOnlyList<InstalledModelInfo> installed,
        double budget, string chat, (string? Completion, string? Search, InferpalConfig After) kept, CancellationToken ct)
    {
        if (!ContextWindowChoice.Applies(server.Provider) || !ContextWindowChoice.IsAutomatic(s.Config)) return null;
        // A search model left automatic is the ones that get loaded (the code index's pick, @Docs').
        var beside = ContextWindowChoice.Beside(kept.After, chat,
            ContextWindowChoice.SearchModelsBeside(kept.After, kept.Search, ModelsByKind.Of(installed).Embedding),
            kept.Completion);
        return await ContextWindowChoice.AdviseAsync(client, server.Provider, chat, beside, installed, budget, ct);
    }

    /// <summary>
    /// The completion and search models <c>setup/apply</c> leaves: on the chat's server, the card's — except while the
    /// server stays, where a model the card leaves out keeps the one chosen before (<c>/setup</c> run again to download a
    /// chat model wiped the completion and search models picked earlier, and the index was embedded again). A role on a
    /// server of its own keeps its model, which is not the card's to set: <c>null</c>.
    /// </summary>
    private static (string? Completion, string? Search, InferpalConfig After) KeptRoleModels(
        HostSession s, InferpalConfig server, string? cardCompletion, string? cardSearch)
    {
        // "The same server" is the chat's as the settings hold it now; "on the chat's server", the chat's as it will be:
        // a role on a server of its own at the old chat address is not on the new one.
        var sameServer = ServerScan.SameAddress(s.Config.BaseUrl, server.BaseUrl);
        var after = System.Text.Json.JsonSerializer.Deserialize<InferpalConfig>(System.Text.Json.JsonSerializer.Serialize(s.Config))!;
        after.Provider = server.Provider;
        after.BaseUrl  = server.BaseUrl;
        string? Kept(ServerRole role, string? card, string current) =>
            !RoleServers.OnChatServer(after, role) ? null
            : !sameServer || !string.IsNullOrWhiteSpace(card) ? card?.Trim() ?? ""
            : current;
        return (Kept(ServerRole.Completion, cardCompletion, s.Config.InlineCompletionModel),
                Kept(ServerRole.Search, cardSearch, s.Config.RagEmbeddingModel), after);
    }

    /// <summary>
    /// `setup/apply` — saves the server and the models chosen, and ends the first run. A server of another type than the
    /// one this host was built for needs a new host: the answer says so, and the editor restarts it.
    /// </summary>
    [JsonRpcMethod("setup/apply", UseSingleObjectParameterDeserialization = true)]
    public async Task<SetupApplyResult> SetupApplyAsync(SetupApplyParams p, CancellationToken ct)
    {
        var s = Session();
        var server = ServerConfig(s, new SetupServerParams(p.Url, p.Provider, p.ApiKey));
        var (budget, source) = await BudgetForAsync(s, server.BaseUrl, p.CardGb, ct);
        var chat = p.ChatModel.Trim();
        // Decided before the server changes: "the same server" is the one the settings hold now.
        var kept = KeptRoleModels(s, server, p.CompletionModel, p.SearchModel);
        var (completion, search, _) = kept;
        // The window the card showed (only Ollama is asked: another server would be listed for nothing).
        WindowAdvice? window = null;
        if (ContextWindowChoice.Applies(server.Provider))
        {
            var client = _providerFactory(server);
            window = await WindowForAsync(s, server, client, await client.ListInstalledModelsAsync(ct, server.BaseUrl),
                                          budget, chat, kept, ct);
        }
        string? windowText = null;

        await WithTurnSlotAsync("setup/apply", ct, async _ =>
        {
            var mcpBefore = (s.Config.McpEnabled, s.Config.McpServersJson);
            // ⚠ The budget is the card of the server it was measured or given for: another server takes its own, or none
            // — a figure from another machine would size the models and the context of this one.
            if (source != SetupBudgetSource.Configured)
            {
                // ⚠ Read from the file first: a figure another editor saved since this copy was read would otherwise
                // survive, a 0 over this copy's 0 being no change to save.
                s.Config.FillFromFile(nameof(InferpalConfig.VramBudgetGb));
                s.Config.VramBudgetGb = budget;
            }
            s.Config.Provider              = server.Provider;
            s.Config.BaseUrl               = server.BaseUrl;
            s.Config.ApiKey                = server.ApiKey;
            s.Config.DefaultModel          = chat;
            // One rule for what is kept and for what the window was sized beside (KeptRoleModels); on another server the
            // old choice may not be there, and the card's is taken.
            if (completion is not null) s.Config.InlineCompletionModel = completion;
            if (search is not null)     s.Config.RagEmbeddingModel     = search;
            // A size is said only once written; a card left unknown says why the window stays.
            if (window?.Tokens is { } tokens) windowText = ContextWindowChoice.Apply(s.Config, tokens) ? window.Sentence : null;
            else windowText = window?.Sentence;
            s.Config.IsFirstRun = false;
            s.Config.Save();
            s.FileSettings = (_fileSettings() ?? s.Config).SnapshotNow();
            await AfterSettingsChangedAsync(s, mcpBefore, ct);
            return true;
        });
        return new(InferenceProviderFactory.PendingSwitch(s.Client, s.Config.Provider) is not null, windowText);
    }

    /// <summary>`setup/pull` — downloads a model to the server the card shows, its progress in `setup/pullProgress`
    /// notifications.</summary>
    [JsonRpcMethod("setup/pull", UseSingleObjectParameterDeserialization = true)]
    public async Task<SetupPullResult> SetupPullAsync(SetupPullParams p, CancellationToken ct)
    {
        var s = Session();
        var client = _providerFactory(ServerConfig(s, new SetupServerParams(p.Url, p.Provider, p.ApiKey)));
        if (!client.Capabilities.ModelManagement) return new(false, Strings.ModelsBackendUnsupported);

        var tag = p.Tag.Trim();
        var ok = await client.PullModelAsync(tag, status => Notify("setup/pullProgress", new
        {
            tag,
            status,
            percent = PercentOf(status),
        }), ct);
        return ok ? new(true, null) : new(false, Strings.ModelsPullFailed(tag));
    }

    /// <summary>`setup/strings` — the card's fixed labels, in the editor's language (the sentences that carry a name or
    /// a number come composed with each answer).</summary>
    [JsonRpcMethod("setup/strings")]
    public Dictionary<string, string> SetupStrings()
    {
        var labels = FixedSetupStrings();
        // Connected to WSL, an SSH server or a container, "found here" means there: said under "no server found".
        if (Services.Inference.RemoteSide.SetupNote(Session().RemoteName) is { } note) labels["remoteNote"] = note;
        return labels;
    }

    private static Dictionary<string, string> FixedSetupStrings() => new()
    {
        ["lookingTitle"]   = Strings.SetupLookingTitle,
        ["lookingLead"]    = Strings.SetupLookingLead,
        ["checking"]       = Strings.SetupChecking,
        ["typeAddress"]    = Strings.SetupTypeAddress,
        ["foundTitle"]     = Strings.SetupFoundTitle,
        ["chat"]           = Strings.LabelChatModel,
        ["completion"]     = Strings.LabelInlineCompletionModel,
        ["search"]         = Strings.LabelRagEmbeddingModel,
        ["sameModel"]      = Strings.SetupSameModel,
        ["noSearchModel"]  = Strings.SetupNoSearchModel,
        ["start"]          = Strings.SetupStart,
        ["chooseOther"]    = Strings.SetupChooseOther,
        ["useInstead"]     = Strings.SetupUseInstead,
        ["nothingTitle"]   = Strings.SetupNothingTitle,
        ["nothingLead"]    = Strings.SetupNothingLead,
        ["otherMachine"]   = Strings.SetupOtherMachine,
        ["connect"]        = Strings.SetupConnect,
        ["serverKinds"]    = Strings.SetupServerKinds,
        ["noServerYet"]    = Strings.SetupNoServerYet,
        ["installHint"]    = Strings.SetupInstallHint,
        ["lookAgain"]      = Strings.SetupLookAgain,
        ["enterKey"]       = Strings.SetupEnterKey,
        ["apiKey"]         = Strings.LabelApiKey,
        ["noModelTitle"]   = Strings.SetupNoModelTitle,
        ["downloadGoesOn"] = Strings.SetupDownloadGoesOn,
    };

    /// <summary>The percentage a pull status ends with ("pulling … (37%)"); null when it carries none.</summary>
    internal static int? PercentOf(string? status)
    {
        var m = System.Text.RegularExpressions.Regex.Match(status ?? "", @"\((\d{1,3})%\)\s*$",
                                                           System.Text.RegularExpressions.RegexOptions.None,
                                                           TimeSpan.FromMilliseconds(100));
        return m.Success && int.TryParse(m.Groups[1].Value, out var n) ? Math.Clamp(n, 0, 100) : null;
    }

    /// <summary>A server's settings for the card's choice. ⚠ The saved key goes to the saved address only.</summary>
    private static InferpalConfig ServerConfig(HostSession s, SetupServerParams p) => new()
    {
        Provider = InferenceProviderFactory.Canonical(p.Provider),
        BaseUrl  = ProviderProbe.RootOf(p.Url),
        ApiKey   = KeyFor(s, p),
    };

    /// <summary>The key typed for that address; else the saved key, if that address is the saved one; else none.</summary>
    private static string KeyFor(HostSession s, SetupServerParams p) =>
        !string.IsNullOrWhiteSpace(p.ApiKey) ? p.ApiKey.Trim() : SavedKeyFor(s, p.Url);

    /// <summary>The saved key for <paramref name="url"/>: the chat server's, if that is the chat server; else none.</summary>
    /// <remarks>⚠ A key belongs to its server: a form or a role's Test that sends no key probes another address, and the
    /// chat server's key went with it.</remarks>
    internal static string SavedKeyFor(HostSession s, string? url) =>
        ServerScan.SameAddress(url, s.Config.BaseUrl) ? s.Config.ApiKey ?? "" : "";

    /// <summary>
    /// The graphics memory of the server the card shows: the card the person chose on it; else the budget set, for the
    /// address it was set with only; else this machine's card when the server runs on it (read, not saved —
    /// <c>setup/apply</c> saves it); else unknown (0), and the card asks.
    /// </summary>
    private static async Task<(double Gb, SetupBudgetSource Source)> BudgetForAsync(
        HostSession s, string url, double? chosenGb, CancellationToken ct)
    {
        if (chosenGb is > 0) return (chosenGb.Value, SetupBudgetSource.Chosen);
        // This copy's own figure goes with this copy's address; a figure taken from the file, with the file's address —
        // another editor may have set up another server since this copy was read.
        if (s.Config.VramBudgetGb > 0 && ServerScan.SameAddress(url, s.Config.BaseUrl))
            return (s.Config.VramBudgetGb, SetupBudgetSource.Configured);
        if (s.Config.VramBudgetGb <= 0 && ServerScan.SameAddress(url, s.Config.SharedBaseUrl)
            && s.Config.FillFromFile(nameof(InferpalConfig.VramBudgetGb)))
            return (s.Config.VramBudgetGb, SetupBudgetSource.Configured);
        var bytes = await HardwareProbe.TryDetectLocalVramBytesAsync(url, ct);
        return bytes is > 0
            ? (Math.Round(bytes.Value / ModelCatalog.BytesPerGb, 1), SetupBudgetSource.Detected)
            : (0, SetupBudgetSource.Unknown);
    }
}

/// <summary>One place the first run looked at. <c>Outcome</c>: answers | nothingListening | notAModelServer | needsKey,
/// or checking (<c>setup/places</c>, before the answer).
/// <c>Expected</c>: the server type that usually listens there; null for an address the person typed. <c>Name</c>: the
/// server's name to show; <c>Status</c>: what it answered, in the editor's language; <c>AlsoAnswering</c>: the line for a
/// second server that answers.</summary>
internal sealed record SetupPlaceDto(
    string Url, string? Expected, string Outcome, string? Provider, string? Detail, int ChatModels, int SearchModels,
    string Name, string Status, string? AlsoAnswering)
{
    public static SetupPlaceDto Of(ServerPlace p) => new(
        p.Url, p.Expected,
        p.Outcome switch
        {
            ServerPlaceOutcome.Answers          => "answers",
            ServerPlaceOutcome.NothingListening => "nothingListening",
            ServerPlaceOutcome.NotAModelServer  => "notAModelServer",
            _                                   => "needsKey",
        },
        p.Provider, p.Detail, p.ChatModels, p.SearchModels, p.Name, p.Status,
        p.Outcome == ServerPlaceOutcome.Answers && p.ListFailure is null
            ? Strings.SetupAlsoAnswering(p.Name, p.Url, p.ChatModels + p.SearchModels)
            : null);
}

/// <summary>`setup/scan` answer, in the order the places were looked at; <c>Offered</c>: the address of the server to
/// offer (<see cref="ServerScan.Offered"/>), null when none answers.</summary>
internal sealed record SetupScanResult(List<SetupPlaceDto> Places, string? Offered);

/// <summary>A server the card names: its address, its type, the key typed for it (null: none typed), and the graphics
/// memory the person chose for it on the card (null: none chosen).</summary>
internal sealed record SetupServerParams(string Url, string Provider = "", string? ApiKey = null, double? CardGb = null);

/// <summary>Where the card's memory figure comes from: chosen on the card, the budget set for that address, this
/// machine's card read for a server that runs on it, or unknown (the card asks).</summary>
internal enum SetupBudgetSource { Chosen, Configured, Detected, Unknown }

/// <summary>A model to download, measured for a card of <c>CardGb</c>. <c>Measured</c>, <c>Button</c>, <c>Downloading</c>
/// and <c>Size</c>: its line, its button, its progress title and its size, in the editor's language.</summary>
internal sealed record SetupDownloadDto(
    string Name, string Tag, double DownloadGb, int CardGb, string Measured, string Button, string Downloading, string Size);

/// <summary>A graphics memory the card offers to choose, with its label in the editor's language.</summary>
internal sealed record SetupCardChoice(int Gb, string Label);

/// <summary>`setup/plan` answer. <c>ChatModel</c> null: no chat model there — <c>Downloads</c> (Ollama) or nothing to
/// download from here (<c>NoDownload</c> says where to get one), and <c>Listed</c> names what the server has. The
/// sentences come composed: <c>Lead</c>, <c>Legend</c> (the card's memory), <c>FitText</c>. <c>CardChoices</c>: the
/// memories to choose from when the figure was not found (unknown) or was chosen (to change it); empty otherwise.
/// <c>WindowText</c>: the context window <c>setup/apply</c> writes, or why it stays. <c>SearchNote</c>: why code search
/// stays on keywords beside installed embedding models.</summary>
internal sealed record SetupPlanDto(
    string? ChatModel, string? CompletionModel, string? SearchModel, int ChatCount,
    double BudgetGb, double NeededGb, bool Fits, List<SetupDownloadDto> Downloads, List<string> Listed,
    string Lead, string Legend, string? FitText, string? ItsModels, string? NoDownload,
    List<SetupCardChoice> CardChoices, string? CardQuestion, string? WindowText = null, string? SearchNote = null)
{
    /// <summary>The memories offered: the cards the recommendations are measured for.</summary>
    internal static readonly int[] Cards = [8, 12, 16, 24];

    public static SetupPlanDto Of(FirstRunPlan p, SetupBudgetSource source = SetupBudgetSource.Configured)
    {
        static string Gb(double gb) => gb.ToString("0.#", System.Globalization.CultureInfo.CurrentCulture);
        var name = InferenceProviderFactory.DisplayName(p.Provider);
        var noChat = p.ChatModel is null;
        // Asked only where the answer changes the card: what fits beside the models there, or what to download. A server
        // without a chat model that takes no download from here has nothing to size.
        var asks = source is SetupBudgetSource.Unknown or SetupBudgetSource.Chosen
                   && (!noChat || p.Downloads.Count > 0 || p.NoneFits);
        return new(
            p.ChatModel, p.CompletionModel, p.SearchModel, p.ChatCount, p.BudgetGb, Math.Round(p.NeededGb, 1), p.Fits,
            [.. p.Downloads.Select(d => new SetupDownloadDto(
                d.Name, d.Tag, d.DownloadGb, d.CardGb, Strings.SetupMeasuredCard(d.CardGb), Strings.SetupDownload(d.Name),
                Strings.SetupDownloading(d.Name), Strings.SetupDownloadSize(Gb(d.DownloadGb))))],
            [.. p.Listed],
            noChat ? Strings.SetupNoModelLead(name, p.Url) : Strings.SetupFoundLead(name, p.Url, p.InstalledCount),
            noChat
                ? p.BudgetGb > 0 ? Strings.SetupDownloadLegend(Gb(p.BudgetGb)) : Strings.SetupDownloadLegendUnknown
                : p.BudgetGb > 0 ? Strings.SetupForThisCard(Gb(p.BudgetGb)) : Strings.SetupProposed,
            // A window sized for the card uses the memory "to spare": its own line says the models and it fit.
            noChat || p.BudgetGb <= 0 || p.NeededGb <= 0 || p.Window?.Tokens > ContextWindowChoice.Factory ? null
                : p.Fits ? Strings.SetupFits(Gb(p.BudgetGb - p.NeededGb))
                : Strings.SetupDoesNotFit(Gb(p.NeededGb), Gb(p.BudgetGb)),
            noChat && p.Listed.Count > 0 ? Strings.SetupItsModels(string.Join(", ", p.Listed)) : null,
            !noChat || p.Downloads.Count > 0 ? null
                : p.NoneFits ? Strings.SetupNoneFits(Gb(p.BudgetGb), name)
                : Strings.SetupNoDownloadHere(name),
            asks ? [.. Cards.Select(gb => new SetupCardChoice(gb, Strings.SetupDownloadSize(Gb(gb))))] : [],
            !asks ? null : source == SetupBudgetSource.Unknown ? Strings.SetupCardUnknown : Strings.SetupServerCard,
            // An unknown card is already asked on the card: its question is the remedy, not /hardware.
            p.Window is { Tokens: not null } || !asks ? p.Window?.Sentence : null,
            // No code-search model proposed beside models that are installed: why the search stays on keywords.
            p.SearchModel is null && p.SearchPassedOver is { Count: > 0 } passed
                ? Strings.SuggestEmbeddingBelowKeywords(passed) : null);
    }
}

/// <summary>`setup/apply` — the server and the models chosen. <c>CompletionModel</c> null: the chat model;
/// <c>SearchModel</c> null: automatic; <c>CardGb</c>: the graphics memory chosen on the card, saved with the server.</summary>
internal sealed record SetupApplyParams(
    string Url, string Provider, string ChatModel, string? CompletionModel = null, string? SearchModel = null,
    string? ApiKey = null, double? CardGb = null);

/// <summary>`setup/apply` answer: the server type changed, the editor restarts the host. <c>WindowText</c>: the context
/// window written, or why it stays — said in the chat, which outlives the card (a download never showed it).</summary>
internal sealed record SetupApplyResult(bool RestartNeeded, string? WindowText = null);

/// <summary>`setup/pull` — the model to download and the server the card shows.</summary>
internal sealed record SetupPullParams(string Tag, string Url, string Provider, string? ApiKey = null);

/// <summary>`setup/pull` answer; <c>Error</c> says why it failed.</summary>
internal sealed record SetupPullResult(bool Ok, string? Error);
