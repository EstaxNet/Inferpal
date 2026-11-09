using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Runtime.Serialization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Inferpal.Commands;
using Inferpal.Config;
using Inferpal.Localization;
using Inferpal.Models;
using Inferpal.Services;
using Inferpal.Services.Docs;
using Inferpal.Services.Rag;
using Inferpal.Services.Tools;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Editor;
using Microsoft.VisualStudio.Extensibility.Shell;
using Microsoft.VisualStudio.Extensibility.Settings;
using Microsoft.VisualStudio.Extensibility.UI;
using Microsoft.VisualStudio.Threading;

namespace Inferpal.ToolWindow;

internal partial class InferpalToolWindowData
{
    /// <summary>
    /// The editor this front-end runs in, as the model is told it (see
    /// <see cref="Services.Prompting.SystemPromptBuilder.EnvironmentFacts"/>). Declared here rather
    /// than inferred in the Core, which stays editor-agnostic by construction.
    /// </summary>
    internal const string EditorName = "Visual Studio";

    #region RAG indexing, first run, OODA & compaction

    // ── RAG indexing ──────────────────────────────────────────────────────────

    /// <summary>
    /// Waits for VS to finish initializing, then triggers background RAG indexing.
    /// Retries up to 5 times (separated by 5 s each) until the solution root can be located.
    /// </summary>
    private async Task StartRagIndexingAsync()
    {
        try
        {
            // Give VS time to fully initialize before the first attempt
            await Task.Delay(4_000).ConfigureAwait(false);

            if (!_config.RagEnabled) return;

            for (int attempt = 0; attempt < 5; attempt++)
            {
                if (attempt > 0)
                    await Task.Delay(5_000).ConfigureAwait(false);

                // Use FindReliableProjectRoot (signal → open files → CWD-anchored .sln) for a
                // *reliable* root.
                // FindProjectRoot() always returns something (falls back to CWD), so
                // the guard `!string.IsNullOrEmpty(root)` was always true and RAG could
                // start against the wrong directory.  We only start once we have a real
                // .sln-anchored root; if none is found after 5 attempts we give up.
                var root = FindReliableProjectRoot();
                if (root is not null)
                {
                    // The heartbeat may already have indexed it (RAG on, root pinned): a second pass would restart it.
                    if (!string.Equals(_indexService.IndexedRoot, root, StringComparison.OrdinalIgnoreCase))
                        _indexService.StartIndexing(root);
                    return;
                }
            }
        }
        catch (Exception ex) { Diagnostics.Swallow("Rag.StartIndexing", ex); }
    }

    /// <summary>
    /// Pins the workspace root the file tools confine to and the approval overlay is read from —
    /// RAG on or off — and follows the solution the in-process package reports. Called on each
    /// heartbeat tick and before each send. The decision lives in <c>WorkspaceRootPin</c>; the
    /// initial indexing pass stays with <c>StartRagIndexingAsync</c>.
    /// </summary>
    private void PinWorkspaceRoot()
    {
        try
        {
            var current = _indexService.RootDir;
            var (action, root) = WorkspaceRootPin.Decide(
                _config.RagEnabled,
                current,
                // The folder that holds the whole solution, not only the .sln's own (SolutionExtent) — or the folder
                // Visual Studio has open without a solution.
                SolutionExtent.ActiveRoot(),
                // Walks the file system: only worth it while nothing is pinned.
                string.IsNullOrEmpty(current) ? FindReliableProjectRoot() : null,
                _indexService.IndexedRoot);

            // ⚠ A solution switch with RAG on answers Index: the new root is pinned as well — its modes, its MCP servers —
            // before its pass starts (the pass declares its ignore files). The same root, indexed because RAG was just
            // turned on, is not pinned again: a refresh tears every MCP server down.
            if (action == RootPinAction.Pin
                || action == RootPinAction.Index && !PathComparer.SameDirectory(root, current))
            {
                _indexService.SetRoot(root!);
                // The conversation on screen goes on in the new solution: said there, once per switch.
                if (!string.IsNullOrEmpty(current) && !PathComparer.SameDirectory(current, root)) SaySolutionChanged(root!);
                // Another workspace has its own modes: the composer's segment follows.
                _ = RefreshModesQuietlyAsync();
                // The repository's MCP servers are read from the root: a new one has its own to ask about — once the turn
                // that pinned it has ended (a refresh tears every server down, InferpalSettingsData says why).
                _ = _tools.Mcp.RefreshWhenIdleAsync(() => _sendStarting || TurnDoor.Busy,
                                                    TimeSpan.FromMilliseconds(500));
            }
            if (action == RootPinAction.Index) _indexService.StartIndexing(root!);
        }
        catch (Exception ex) { Diagnostics.Swallow("Rag.PinWorkspaceRoot", ex); }
    }

    /// <summary>
    /// Another solution opened under the conversation on screen: it goes on there — that solution's files, index, rules
    /// and tools —, and is still saved with the conversations of the one it belongs to (<see cref="ConversationRoot"/>).
    /// Said once per switch; nothing for a window without a question, nor for a conversation saved nowhere.
    /// </summary>
    private void SaySolutionChanged(string root) => Post(() =>
    {
        // Back in the conversation's own solution: nothing to say, it works where it is saved.
        if (_conversationRoot.Root is not { } owner || PathComparer.SameDirectory(owner, root))
        {
            _solutionChangeSaid = null;
            return;
        }
        // Once per switch (the heartbeat and a send may both pin it), and never for a root that only grew or narrowed
        // around the same solution (its folder corrected at the start), nor for a window without a question.
        if (PathComparer.SameDirectory(_solutionChangeSaid, root) || Nested(owner, root)
            || !Messages.Any(m => m.Role == "user")) return;
        _solutionChangeSaid = root;
        var notice = ChatMessageItem.NoticeMsg(Strings.SolutionChangedNotice(
            Path.GetFileName(Path.TrimEndingDirectorySeparator(root)), root, Path.GetFileName(Path.TrimEndingDirectorySeparator(owner))));
        ApplyItemTheme(notice);
        Messages.Insert(Messages.Count - 2, notice);
        ScrollToBottom();
    });

    /// <summary>
    /// A solution opened under a conversation restored without a workspace, which stays (<see cref="RestoreLateSlotAsync"/>):
    /// it works in that solution now, and is still saved with the conversations of windows without one. Once per switch, as
    /// <see cref="SaySolutionChanged"/>, never for a root that only grew or narrowed around the same solution.
    /// </summary>
    /// <param name="ownKept">Whether the solution has a conversation of its own, left in its place.</param>
    private void SayRootlessConversation(string root, bool ownKept) => Post(() =>
    {
        // Never inside a turn: tried again at the next tick, once it has ended.
        if (IsLoading || _sendStarting)
        {
            _lateRestoreTried = null;
            return;
        }
        if (!RootlessOnScreen || !Messages.Any(m => m.Role == "user")
            || _rootlessSaidFor is { } said && Nested(said, root)) return;
        _rootlessSaidFor = root;
        var name   = Path.GetFileName(Path.TrimEndingDirectorySeparator(root));
        var notice = ChatMessageItem.NoticeMsg(Strings.SolutionOpenedOverRootless(name, root)
                                               + (ownKept ? " " + Strings.SolutionOwnConversationKept(name) : string.Empty));
        ApplyItemTheme(notice);
        Messages.Insert(Messages.Count - 2, notice);
        // Said of that conversation: it goes with it if a later solution replaces it.
        _rootlessRestore?.Items.Add(notice);
        ScrollToBottom();
    });

    /// <summary>The root the conversation restored without a workspace was last said for (<see cref="SayRootlessConversation"/>),
    /// on the VM context.</summary>
    private string? _rootlessSaidFor;

    /// <summary>The root the last solution change was said for (<see cref="SaySolutionChanged"/>), on the VM context.</summary>
    private string? _solutionChangeSaid;

    /// <summary>Whether one folder holds the other: the same solution, its root grown or narrowed.</summary>
    private static bool Nested(string a, string b)
    {
        static string Folder(string p) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(p)) + Path.DirectorySeparatorChar;
        var (fa, fb) = (Folder(a), Folder(b));
        return fa.StartsWith(fb, PathComparer.Comparison) || fb.StartsWith(fa, PathComparer.Comparison);
    }

    // ── First-Run Auto-Discovery ───────────────────────────────────────────────
    // Model classification and choice live in ModelCatalog (unit-tested, shared with
    // the settings VM's embedding dropdown).

    /// <summary>
    /// On first launch: pings the backend, discovers models, picks the best one, and posts a welcome bubble. Clears
    /// <see cref="InferpalConfig.IsFirstRun"/> once a model is chosen — also run again when the connection comes back.
    /// </summary>
    /// <returns>Whether the conversation heard about the server (<see cref="FirstRunSpokeAsync"/>).</returns>
    private async Task<bool> StartFirstRunDiscoveryAsync()
    {
        // One run at a time: the window's start and a heartbeat that sees the backend come back can both ask.
        if (Interlocked.Exchange(ref _firstRunInFlight, 1) == 1) return false;
        try
        {
            if (!_config.IsFirstRun) return false;

            // Let the heartbeat complete its first check (2 s delay + RTT) before we act.
            await Task.Delay(3_500).ConfigureAwait(false);

            if (!_config.IsFirstRun) return false; // guard against re-entry

            // First-run posts its bubbles directly (no user turn exists yet to attach a reply to).
            return await RunSetupDiscoveryAsync(FirstRunPresentAsync, CancellationToken.None, automatic: true).ConfigureAwait(false);
        }
        // ⚠ The first run is the one moment where silence costs the most: the window opens on an
        // EMPTY conversation, so a swallowed failure leaves someone who has just installed the
        // extension with a chat that did nothing and no cause — and the ring of /diagnostics is not
        // where they look. Said where it happened, and naming the gesture that re-runs it.
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Diagnostics.Swallow("Rag.FirstRun", ex);
            var detail = ex.Message;
            try { await FirstRunPresentAsync(Strings.FirstRunFailed(detail)).ConfigureAwait(false); return true; }
            catch (Exception notice) { Diagnostics.Swallow("Rag.FirstRunNotice", notice); }
        }
        finally { Interlocked.Exchange(ref _firstRunInFlight, 0); }
        return false;
    }

    private static readonly TimeSpan FirstRunWait = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Whether the window's first run said what answers where: the heartbeat's first failed check waits for it, and says the
    /// server down only if it did not. Asked once: Retry is a gesture, and gets its sentence.
    /// </summary>
    /// <remarks>⚠ Bounded: the window's whole connection state hangs on the heartbeat, and a discovery that does not end must
    /// not stop it. Past the wait, the heartbeat says what it knows.</remarks>
    private async Task<bool> FirstRunSpokeAsync(CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _firstRunAtStart, null) is not { } discovery) return false;
        try { return await discovery.WaitAsync(FirstRunWait, ct).ConfigureAwait(false); }
        catch (TimeoutException) { return false; }
    }

    /// <summary>Presenter used by the automatic first-run path: inserts a themed assistant bubble.</summary>
    private Task FirstRunPresentAsync(string text) => RunOnVMContextAsync(() =>
    {
        // Two discoveries can find the same thing (an install's card, then the heartbeat that sees its server come back,
        // on an Ollama still without a model): the same notice twice in a row says nothing new.
        if (Messages.Count >= 3 && Messages[Messages.Count - 3] is { ToolName: SessionManager.NoticeMarker } last && last.Content == text) return;
        var msg = ChatMessageItem.NoticeMsg(text);
        ApplyItemTheme(msg);
        Messages.Insert(Messages.Count - 2, msg);
        ScrollToBottom();
    });

    /// <summary>
    /// Core setup discovery, shared by the automatic first-run and the manual <c>/setup</c> command:
    /// auto-detects the backend, checks connectivity, discovers and auto-selects chat + embedding
    /// models, seeds the VRAM budget, and reports the result via <paramref name="present"/>. Clears
    /// <see cref="InferpalConfig.IsFirstRun"/> once a model is chosen. Re-runnable on demand (no IsFirstRun guard here).
    /// </summary>
    /// <param name="automatic">The first run, not <c>/setup</c>: an unreachable backend then gets the install card, or is left
    /// to the heartbeat, which waits for this answer and says it with its remedies — the first run, which resumes at every
    /// start until a model is chosen, would otherwise repeat them.</param>
    /// <returns>Whether the conversation heard about the server: false leaves it to the heartbeat.</returns>
    private async Task<bool> RunSetupDiscoveryAsync(Func<string, Task> present, CancellationToken ct, bool automatic = false)
    {
        var url = _config.BaseUrl;

        // ── 0. Backend auto-detection ─────────────────────────────────────────
        // Probe the URL to pick the right backend (Ollama / LM Studio / OpenAI-compatible) without
        // the user choosing manually. When it differs from the configured one, persist it and use a
        // matching client for discovery; the chat's client follows at once (FollowServerForSetup).
        var detected = await Services.Inference.ProviderProbe.DetectAsync(url, _config.ApiKey, ct)
            .ConfigureAwait(false);
        if (detected is not null && !string.Equals(detected, _config.Provider, StringComparison.OrdinalIgnoreCase))
        {
            _config.Provider = detected;
            FollowServerForSetup();
            _config.Save();
        }
        var client = detected is not null ? Services.Inference.InferenceProviderFactory.Create(_config) : _client;

        // ── 1. Connectivity check ─────────────────────────────────────────────
        // A successful probe already proves reachability; otherwise fall back to the client's own check.
        var reachable = detected is not null
            || await client.CheckConnectionAsync(url, ct).ConfigureAwait(false);

        // Where else a server answers: a first run that chose nothing yet takes it (ServerScan.ToAdopt, said in the
        // bubble); otherwise each place's answer goes with the remedy.
        string? foundNote = null;
        async Task<bool> SayFoundAsync(string found)
        {
            await present(found).ConfigureAwait(false);
            return true;
        }
        if (!reachable)
        {
            var places = await Services.Inference.ServerScan.ScanAsync(
                url, _config.ApiKey, Services.Inference.InferenceProviderFactory.Create, ct).ConfigureAwait(false);
            // What another window chose since this one read the settings is a choice: nothing is adopted over it.
            _config.FillFromFile(nameof(InferpalConfig.DefaultModel));
            _config.FillFromFile(nameof(InferpalConfig.BaseUrl));
            _config.FillFromFile(nameof(InferpalConfig.ApiKey));
            if (Services.Inference.ServerScan.ToAdopt(_config, places) is not { } found)
            {
                var placesText = Strings.SetupNothingLead + "\n" + string.Join("\n", places.Select(p => "- " + p.Line));
                // ⚠ Neither this branch nor the next one ENDS the first run: no model has been chosen, and spent here it
                // left the code's default — a model the backend may not have — for every question to come. It completes
                // when the connection comes back (heartbeat) or at the next start.
                if (automatic)
                {
                    // How to get a server the heartbeat cannot say: the install card, once per Visual Studio, and only for
                    // someone who never chose a server (one who did is not sent to install another) and whose server did
                    // not refuse a key (a refusal means one runs). Without a card, the heartbeat says the server is down.
                    return client.ConnectionRefusal is null && Services.Inference.ServerScan.NothingChosen(_config)
                        && Services.Inference.ServerInstall.Offers(places)
                        && await OfferServerInstallAsync($"**{Strings.SetupNothingTitle}**\n\n" + placesText, once: true)
                            .ConfigureAwait(false);
                }
                // A server that refused the check (a wrong API key) is running: "start it" is the wrong remedy.
                await present((client.ConnectionRefusal is { } refusal
                    ? Strings.MsgBackendRefused(url, refusal)
                    : Strings.MsgFirstRunBackendDown(
                        url, Services.Inference.InferenceProviderFactory.DisplayName(_config.Provider)))
                    + "\n\n" + placesText)
                    .ConfigureAwait(false);
                // Only where the installed server would be used: nothing chosen yet, or Ollama's own address chosen.
                if (client.ConnectionRefusal is null && Services.Inference.ServerInstall.Offers(places)
                    && (Services.Inference.ServerScan.NothingChosen(_config)
                        || Services.Inference.ServerScan.SameAddress(url, Services.Inference.ServerInstall.OllamaAddress)))
                    await OfferServerInstallAsync(null, once: false).ConfigureAwait(false);
                return true;
            }

            var before = Services.Inference.ServerChange.Before(_config);
            _config.Provider = found.Provider ?? _config.Provider;
            _config.BaseUrl  = url = found.Url;
            // Another server: the card and the models chosen for the old one start over (measured or picked below).
            Services.Inference.ServerChange.Follow(before, _config);
            FollowServerForSetup();
            _config.Save();
            client    = Services.Inference.InferenceProviderFactory.Create(_config);
            reachable = true;
            foundNote = $"**{Strings.SetupFoundTitle}** — " + (found.ListFailure is null
                ? Strings.SetupFoundLead(found.Name, found.Url, found.ChatModels + found.SearchModels)
                : found.Line) + "\n\n";
        }

        // ── 2. Model discovery ────────────────────────────────────────────────
        var allModels  = await client.ListModelsByKindAsync(ct)
            .ConfigureAwait(false);

        var chatModels = allModels.Chat;
        var embModels  = allModels.Embedding;

        if (chatModels.Count == 0)
        {
            // The measured model for the card, when the card's memory can be known.
            await Services.Hardware.HardwareProfile.EnsureBudgetAsync(_config, ct).ConfigureAwait(false);
            var pull = Services.Inference.ModelAdvisor.DownloadToName(_config.Provider, _config.VramBudgetGb);
            // The backend DETECTED above, not "Ollama": an LM Studio with nothing downloaded lands here too.
            return await SayFoundAsync(foundNote + Strings.MsgFirstRunNoModels(
                Services.Inference.InferenceProviderFactory.DisplayName(_config.Provider),
                client.Capabilities.ModelManagement, pull?.Tag, pull?.Name)).ConfigureAwait(false);
        }

        // ── 3. Auto-configure ─────────────────────────────────────────────────
        // A model the user CHOSE (not the code's default) that the backend has is kept: the first run can now
        // complete late — after a model was picked in the settings while the backend was down, or in another window.
        _config.FillFromFile(nameof(InferpalConfig.DefaultModel));
        _config.FillFromFile(nameof(InferpalConfig.RagEmbeddingModel));
        var chosen = !Services.Inference.ModelCatalog.SameModelName(_config.DefaultModel, new InferpalConfig().DefaultModel)
                     && chatModels.Any(m => Services.Inference.ModelCatalog.SameModelName(m, _config.DefaultModel));
        var best = chosen ? _config.DefaultModel : Services.Inference.ModelCatalog.PickBestChatModel(chatModels);
        _config.DefaultModel = best;
        await RunOnVMContextAsync(() => ActiveModelLabel = best);

        // Auto-pick an embedding model only when nothing is configured yet: the default model's family if installed. Not
        // for code search on a server of its own: these are the chat server's models.
        // None left — only families that search code worse than keywords alone — keeps the field empty: keyword search,
        // said by the index's status.
        if (string.IsNullOrEmpty(_config.RagEmbeddingModel) && embModels.Count > 0
            && Services.Inference.RoleServers.OnChatServer(_config, Services.Inference.ServerRole.Search)
            && Services.Inference.EmbeddingModels.PreferredOf(embModels) is { } preferredEmbedding)
            _config.RagEmbeddingModel = preferredEmbedding;

        _config.IsFirstRun = false;
        _config.Save();

        // ── 4. Hardware fit-check ─────────────────────────────────────────────
        // Auto-seed the VRAM budget if Ollama is local, then warn when the auto-picked
        // chat + embedding set is estimated to overflow it. Silent when the budget is
        // unknown (remote host) or the models comfortably fit.
        await Services.Hardware.HardwareProfile.EnsureBudgetAsync(_config, ct)
            .ConfigureAwait(false);

        // ── 5. Context window ─────────────────────────────────────────────────
        // Sized for the card beside the models that live with the chat's (Ollama only: another server is not even
        // listed); a window the person chose stands, unsaid.
        Services.Inference.WindowAdvice? window = null;
        if (Services.Inference.ContextWindowChoice.Applies(_config.Provider)
            && Services.Inference.ContextWindowChoice.IsAutomatic(_config))
        {
            var installed = await client.ListInstalledModelsAsync(ct).ConfigureAwait(false);
            window = await Services.Inference.ContextWindowChoice.AdviseAsync(client, _config.Provider, best,
                    Services.Inference.ContextWindowChoice.Beside(_config, best, installed), installed, _config.VramBudgetGb, ct)
                .ConfigureAwait(false);
        }
        var windowWritten = window?.Tokens is { } tokens && Services.Inference.ContextWindowChoice.Apply(_config, tokens);
        if (windowWritten) _config.Save();

        var vramWarning = await BuildFirstRunVramWarningAsync(best, ct).ConfigureAwait(false);

        // The count agrees with its noun in the language (Strings.Format): an English "3 models" landed in every
        // translated sentence — "3 models disponible(s)", "3 models 個のモデル".
        var text = foundNote + Strings.MsgFirstRunWelcome(chatModels.Count, best);
        // A size is said only once written: another window may have set one between the read and the write.
        if (window?.Sentence is { } windowSentence && (window.Tokens is null || windowWritten)) text += "\n\n" + windowSentence;
        // Embedding models installed, none picked for code search: why it runs on keywords — as VS Code's card says it.
        if (_config.RagEnabled && string.IsNullOrEmpty(_config.RagEmbeddingModel)
            && Services.Inference.EmbeddingModels.PreferredOf(embModels) is null
            && Services.Inference.EmbeddingModels.PassedOver(embModels) is { Count: > 0 } passedOver)
            text += "\n\n" + Strings.SuggestEmbeddingBelowKeywords(passedOver);
        if (vramWarning is not null) text += "\n\n" + vramWarning;
        return await SayFoundAsync(text).ConfigureAwait(false);
    }

    /// <summary>Handles <c>/setup</c>: re-runs the first-run discovery on demand (re-detect backend, re-pick models).</summary>
    private async Task HandleSetupCommandAsync(string[] parts, CancellationToken ct)
        => await RunSetupDiscoveryAsync(ShowInfoAsync, ct).ConfigureAwait(false);

    /// <summary>
    /// Builds the first-run VRAM overflow warning, or <c>null</c> when the budget is unknown or
    /// the auto-picked chat + embedding models comfortably fit. Estimates footprint from the
    /// models' on-disk size (<c>/api/tags</c>).
    /// </summary>
    private async Task<string?> BuildFirstRunVramWarningAsync(string chatModel, CancellationToken ct)
    {
        if (_config.VramBudgetGb <= 0) return null;

        var installed = await _client.ListInstalledModelsAsync(ct).ConfigureAwait(false);
        if (installed.Count == 0) return null;

        var sizeByName = installed.ToDictionary(m => m.Name, m => m.SizeBytes);

        // The set that will be resident together: chat + embedding (FIM defaults to the chat model).
        var names = new List<string> { chatModel };
        if (!string.IsNullOrEmpty(_config.RagEmbeddingModel)) names.Add(_config.RagEmbeddingModel);

        var sizes = names.Distinct()
            .Select(n => sizeByName.TryGetValue(n, out var s) ? s : 0L)
            .ToList();

        if (Services.Inference.ModelCatalog.TrioFitsBudget(_config.VramBudgetGb, sizes, out var neededGb))
            return null;

        return Strings.MsgFirstRunVramWarning(
            neededGb.ToString("0.#", System.Globalization.CultureInfo.CurrentCulture),
            _config.VramBudgetGb.ToString("0.#", System.Globalization.CultureInfo.CurrentCulture));
    }

    /// <summary>Handles the <c>/index</c> slash command (show status or trigger a rebuild).</summary>
    // A rebuild moves the workspace root (confinement, deny overlay), so it takes a solution-anchored
    // root or none: FindProjectRoot() would fall back to the host's working directory, never the project.
    private async Task HandleRagIndexCommandAsync(string[] parts, CancellationToken ct) =>
        await ShowInfoAsync(Services.Commands.IndexCommandHandler.Handle(
            _indexService, _config, parts, FindReliableProjectRoot()));

    // ── Project context ────────────────────────────────────────────────────────

    // ── OODA Turn Loop ─────────────────────────────────────────────────────────

    private async Task RunOodaSummaryAsync(CancellationToken ct)
    {
        Post(() => CurrentStep = Strings.StatusOodaSummarizing);

        var history = new List<ChatMessageDto>(_history);

        try
        {
            // The request (bounded by the utility model's window), the call and the reading of the reply are shared
            // with the host: SessionRecap.
            var recap = await Services.Agent.SessionRecap.WriteAsync(history, _oodaSummary, _config, _client, ct);
            if (recap.Failure is not null)
            {
                Services.Diagnostics.Record("Ooda.Summary", "The session summary was not written: " + recap.Failure);
                return;
            }
            var summary = recap.Recap ?? string.Empty;
            if (!string.IsNullOrEmpty(summary))
            {
                await RunOnVMContextAsync(() =>
                {
                    _oodaSummary  = summary;
                    ApplySystemPrompt();

                    var recapItem = ChatMessageItem.ToolMsg(
                        "ooda_recap",
                        Strings.MsgOodaRecap(_conversationTurnCount, summary),
                        _config.ToolBubblesExpanded);
                    ApplyItemTheme(recapItem);
                    Messages.Insert(Messages.Count - 2, recapItem);
                    ScrollToBottom();
                });
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Diagnostics.Swallow("Ooda.Summary", ex); }
    }

    // ── Context window management ──────────────────────────────────────────────

    // Range computation, the summarize request, and the history rewrites live in
    // HistoryCompaction (unit-tested); the VM keeps the LLM call, its timeout fuse,
    // and the chat notices.
    /// <summary>The tool definitions the next chat turn carries — added to the conversation by the pre-send check, the
    /// context gauge and the X-Ray, which otherwise all measured a request half the size of the one sent.</summary>
    private int NextTurnToolTokens() =>
        Services.Agent.ContextManager.NextTurnToolTokens(_tools, _toolsEnabled, _planMode, _heldMode);

    /// <param name="onStep">Where the turn shows its steps (its status bubble).</param>
    private async Task CompactOrTruncateAsync(string model, int toolTokens, Action<string> onStep, CancellationToken ct)
    {
        // The decision, the summarising model call and its fuse lived HERE, so on the VS Code side
        // the history was NEVER bounded and three settings of the panel did nothing there. All of
        // that moved into ContextManager; what stays is what cannot move: mutating _history on the
        // VM context (rule 6 - otherwise it is replaced under the turn loop that reads it) and
        // placing the bubbles.
        var decision = await Services.Agent.ContextManager.PrepareAsync(
            _history, _config, _client, _lastPromptTokens,
            onStep: onStep,
            ct: ct, model: model, toolTokens: toolTokens);

        await RunOnVMContextAsync(() =>
        {
            var before = ContextWindowInUse;
            _contextWindowInUse = decision.Window;
            // The prompt was built at the start of the turn, against the window known then: when this check reveals
            // another one — the first question, a model loaded smaller since — its files are re-budgeted now.
            if (decision.Window > 0 && decision.Window != before)
            {
                RefreshSystemPrompt();
                ActiveFileContext.Restore(_history, BuildActiveFileBlock());   // the block too: built for that window
            }
        });

        if (decision.Outcome == Services.Agent.ContextOutcome.None) return;

        await RunOnVMContextAsync(() =>
        {
            // The summary it then holds is kept with the conversation: a restore rebuilds this history, not every turn.
            _summary = Services.Agent.HistoryCompaction.Apply(_history, decision.Plan,
                decision.Outcome == Services.Agent.ContextOutcome.Compacted ? decision.Summary : null, _summary);
            // The one moment the session summary joins the system message: the turns it covers have just left.
            _recapInSystem = _oodaSummary;
            ApplySystemPrompt();
            // And the question being asked gets the active file's block back if the turns just dropped carried it.
            ActiveFileContext.Restore(_history, BuildActiveFileBlock());

            _lastPromptTokens = 0;

            // A successful compaction is a tool event (collapsible); the two fallbacks are
            // warnings — the conversation lost turns, and that is read in plain text. ⚠ The rule is
            // READ from ContextDecision, never rewritten here: both front-ends must answer the
            // same way.
            InsertThemed(decision.IsDegraded
                ? ChatMessageItem.NoticeMsg(decision.Notice)
                : ChatMessageItem.ToolMsg("context_compact", decision.Notice, _config.ToolBubblesExpanded));
            ScrollToBottom();
        });
    }

    // Prompt layering itself lives in SystemPromptBuilder (unit-tested); the VM only
    // gathers its inputs: project root, active-file relative path, template suffix, the held mode.
    private string BuildSystemPrompt()
    {
        var dir = FindProjectRoot();
        return LayeredPrompt(dir, _heldMode);
    }

    /// <summary>The layered system prompt for the project root <paramref name="dir"/>.</summary>
    /// <param name="mode">The mode the conversation is held in — its instructions, last; <c>null</c> for <c>/task</c>,
    /// <c>/tdd</c> and a code action, which the mode does not steer.</param>
    private string LayeredPrompt(string dir, ChatMode? mode)
    {
        var prompt = new SystemPromptBuilder(_config, EditorName, ContextWindowInUse, _indexService.RootDir).Build(
            ModelPrompts.SystemPrompt,
            PersonaLanguage,
            _activeTemplateSuffix,
            dir,
            ActiveFileRelativeTo(dir),
            _xrayDisabledSections,    // sections switched off from the Context X-Ray panel
            _activeFilePath,          // scopes the repository's instructions, even outside the solution's folder
            mode);
        return _planMode ? prompt + PlanModeToolRegistry.SystemPromptSuffix : prompt;
    }

    /// <summary>The active-file block of the next question (<see cref="ActiveFileContext"/>), from the inputs of
    /// <see cref="BuildSystemPrompt"/>: what follows the active file, which the system message no longer carries.</summary>
    private string BuildActiveFileBlock()
    {
        var dir = FindProjectRoot();
        return new SystemPromptBuilder(_config, EditorName, ContextWindowInUse, _indexService.RootDir).ActiveFileBlock(
            PersonaLanguage, dir, ActiveFileRelativeTo(dir), _xrayDisabledSections, _activeFilePath);
    }

    /// <summary>Language of the persona the next prompt carries — none when auto-switching is off.</summary>
    private string? PersonaLanguage => _config.PersonaAutoSwitch ? _personaLanguage : null;

    /// <summary>Rebuilds the base prompt and writes the system message. VM context only.</summary>
    /// <param name="withMode">The held mode's instructions as its last layer — <c>false</c> for a code action, which
    /// runs without the mode.</param>
    private void RefreshSystemPrompt(bool withMode = true)
    {
        _baseSystemPrompt = withMode ? BuildSystemPrompt() : LayeredPrompt(FindProjectRoot(), mode: null);
        ApplySystemPrompt();
    }

    /// <summary>
    /// The only writer of the system message: the base prompt plus the OODA session summary, which
    /// any other rewrite would drop. VM context only.
    /// </summary>
    private void ApplySystemPrompt()
    {
        if (_history.Count == 0 || _history[0].Role != "system") return;
        _history[0] = new ChatMessageDto("system", WithRecap(_baseSystemPrompt));
    }

    /// <summary>A system prompt with the session's recap, as the system message carries it.</summary>
    private string WithRecap(string prompt) =>
        string.IsNullOrEmpty(_recapInSystem) ? prompt : prompt + "\n\n## Session Summary\n\n" + _recapInSystem;

    /// <summary>The system message as a turn sends it, without the held mode's instructions — <c>/task</c> and
    /// <c>/tdd</c> keep their own tools, and a mode's ("never edit a file") must not steer them. <c>null</c> without a
    /// system message.</summary>
    private string? SystemPromptWithoutMode() =>
        _history.Count == 0 || _history[0].Role != "system" ? null
        : _heldMode is null ? _history[0].Content
        : WithRecap(LayeredPrompt(FindProjectRoot(), mode: null));

    // Active editor file path relative to the project root (forward slashes), or null when no file
    // is active or it lives outside the root. Used to scope project rules by glob.
    private string? ActiveFileRelativeTo(string root) =>
        SystemPromptBuilder.RelativeActivePath(root, _activeFilePath);

    // Returns a .sln-anchored root, or null if none can be found yet.
    // Unlike FindProjectRoot(), never falls back to CWD — callers that need a reliable
    // root (e.g. RAG indexing) should use this and retry later if it returns null.
    // ⚠ A root, not a solution folder: a solution may list projects beside its own folder, and the
    // root is the folder that holds them all (SolutionExtent). The locator finds where the solution
    // is; the extent says what root it needs — on every path, or the tools and the .inferpal/ layers
    // would be rooted in two different folders.
    private string? FindReliableProjectRoot() =>
        SolutionExtent.ActiveRoot()
        ?? (ProcessFolderIgnoredForTests ? null : SolutionExtent.RootForDir(_rootLocator.LocateReliable(
            _contextHolder.GetOpenPaths(),
            activeSolutionDir: null,
            Directory.GetCurrentDirectory())));

    /// <summary>Under test, the process's folder is this repository: a window meant to start without a workspace does not
    /// look near it, nor near open files (it has none).</summary>
    internal static bool ProcessFolderIgnoredForTests { get; set; }

    // Resolution order and walk limits live in ProjectRootLocator (unit-tested); the VM
    // only supplies the open editor paths, the authoritative signal, and the CWD.
    private static readonly Services.Signals.ProjectRootLocator _rootLocator = new();

    private string FindProjectRoot(IReadOnlyList<string>? openPaths = null) => _contextHolder.ProjectRoot(openPaths);

    #endregion
}
