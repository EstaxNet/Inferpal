using Inferpal.Config;
using Inferpal.Localization;
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
        [.. ServerScan.Places(Session().Config.BaseUrl).Select(p => new SetupPlaceDto(
            p.Url, p.Expected, "checking", null, null, 0, 0,
            p.Expected is { } code ? InferenceProviderFactory.DisplayName(code) : p.Url, Strings.SetupChecking, null))];

    /// <summary>`setup/scan` — every place a model server usually listens, and what each answered.</summary>
    [JsonRpcMethod("setup/scan")]
    public async Task<SetupScanResult> SetupScanAsync(CancellationToken ct)
    {
        var s = Session();
        var places = await ServerScan.ScanAsync(s.Config.BaseUrl, s.Config.ApiKey, _providerFactory, ct);
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
        var plan = await FirstRunPlanner.PlanAsync(_providerFactory(server), server, await BudgetForAsync(s, server.BaseUrl, ct), ct);
        return SetupPlanDto.Of(plan);
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
        var budget = await BudgetForAsync(s, server.BaseUrl, ct);

        await WithTurnSlotAsync("setup/apply", ct, async _ =>
        {
            var mcpBefore = (s.Config.McpEnabled, s.Config.McpServersJson);
            s.Config.Provider              = server.Provider;
            s.Config.BaseUrl               = server.BaseUrl;
            s.Config.ApiKey                = server.ApiKey;
            s.Config.DefaultModel          = p.ChatModel.Trim();
            s.Config.InlineCompletionModel = p.CompletionModel?.Trim() ?? "";
            s.Config.RagEmbeddingModel     = p.SearchModel?.Trim() ?? "";
            if (s.Config.VramBudgetGb <= 0 && budget > 0) s.Config.VramBudgetGb = budget;
            s.Config.IsFirstRun = false;
            s.Config.Save();
            s.FileSettings = (_fileSettings() ?? s.Config).SnapshotNow();
            await AfterSettingsChangedAsync(s, mcpBefore, ct);
            return true;
        });
        return new(InferenceProviderFactory.PendingSwitch(s.Client, s.Config.Provider) is not null);
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
    public Dictionary<string, string> SetupStrings() => new()
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
        !string.IsNullOrWhiteSpace(p.ApiKey) ? p.ApiKey.Trim()
        : string.Equals(ProviderProbe.RootOf(p.Url), ProviderProbe.RootOf(s.Config.BaseUrl), StringComparison.OrdinalIgnoreCase)
            ? s.Config.ApiKey ?? ""
            : "";

    /// <summary>The card's memory, for the server the card shows: the budget set, else this machine's card when the
    /// server runs on it (read, not saved — <c>setup/apply</c> saves it); 0 = unknown.</summary>
    private static async Task<double> BudgetForAsync(HostSession s, string url, CancellationToken ct)
    {
        if (s.Config.VramBudgetGb > 0 || s.Config.FillFromFile(nameof(InferpalConfig.VramBudgetGb))) return s.Config.VramBudgetGb;
        var bytes = await HardwareProbe.TryDetectLocalVramBytesAsync(url, ct);
        return bytes is > 0 ? Math.Round(bytes.Value / ModelCatalog.BytesPerGb, 1) : 0;
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

/// <summary>A server the card names: its address, its type, and the key typed for it (null: none typed).</summary>
internal sealed record SetupServerParams(string Url, string Provider = "", string? ApiKey = null);

/// <summary>A model to download, measured for a card of <c>CardGb</c>. <c>Measured</c>, <c>Button</c>, <c>Downloading</c>
/// and <c>Size</c>: its line, its button, its progress title and its size, in the editor's language.</summary>
internal sealed record SetupDownloadDto(
    string Name, string Tag, double DownloadGb, int CardGb, string Measured, string Button, string Downloading, string Size);

/// <summary>`setup/plan` answer. <c>ChatModel</c> null: no chat model there — <c>Downloads</c> (Ollama) or nothing to
/// download from here (<c>NoDownload</c> says where to get one), and <c>Listed</c> names what the server has. The
/// sentences come composed: <c>Lead</c>, <c>Legend</c> (the card's memory), <c>FitText</c>.</summary>
internal sealed record SetupPlanDto(
    string? ChatModel, string? CompletionModel, string? SearchModel, int ChatCount,
    double BudgetGb, double NeededGb, bool Fits, List<SetupDownloadDto> Downloads, List<string> Listed,
    string Lead, string Legend, string? FitText, string? ItsModels, string? NoDownload)
{
    public static SetupPlanDto Of(FirstRunPlan p)
    {
        static string Gb(double gb) => gb.ToString("0.#", System.Globalization.CultureInfo.CurrentCulture);
        var name = InferenceProviderFactory.DisplayName(p.Provider);
        var noChat = p.ChatModel is null;
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
            noChat || p.BudgetGb <= 0 || p.NeededGb <= 0 ? null
                : p.Fits ? Strings.SetupFits(Gb(p.BudgetGb - p.NeededGb))
                : Strings.SetupDoesNotFit(Gb(p.NeededGb), Gb(p.BudgetGb)),
            noChat && p.Listed.Count > 0 ? Strings.SetupItsModels(string.Join(", ", p.Listed)) : null,
            !noChat || p.Downloads.Count > 0 ? null
                : p.NoneFits ? Strings.SetupNoneFits(Gb(p.BudgetGb), name)
                : Strings.SetupNoDownloadHere(name));
    }
}

/// <summary>`setup/apply` — the server and the models chosen. <c>CompletionModel</c> null: the chat model;
/// <c>SearchModel</c> null: automatic.</summary>
internal sealed record SetupApplyParams(
    string Url, string Provider, string ChatModel, string? CompletionModel = null, string? SearchModel = null,
    string? ApiKey = null);

/// <summary>`setup/apply` answer: the server type changed, the editor restarts the host.</summary>
internal sealed record SetupApplyResult(bool RestartNeeded);

/// <summary>`setup/pull` — the model to download and the server the card shows.</summary>
internal sealed record SetupPullParams(string Tag, string Url, string Provider, string? ApiKey = null);

/// <summary>`setup/pull` answer; <c>Error</c> says why it failed.</summary>
internal sealed record SetupPullResult(bool Ok, string? Error);
