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

    #region Indexation RAG, premier lancement, OODA & compaction

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
                ActiveSolutionSignal.TryReadSolutionDir(),
                // Walks the file system: only worth it while nothing is pinned.
                string.IsNullOrEmpty(current) ? FindReliableProjectRoot() : null);

            if      (action == RootPinAction.Pin)   _indexService.SetRoot(root!);
            else if (action == RootPinAction.Index) _indexService.StartIndexing(root!);
        }
        catch (Exception ex) { Diagnostics.Swallow("Rag.PinWorkspaceRoot", ex); }
    }

    // ── First-Run Auto-Discovery ───────────────────────────────────────────────
    // Model classification and choice live in ModelCatalog (unit-tested, shared with
    // the settings VM's embedding dropdown).

    /// <summary>
    /// On first launch: pings Ollama, discovers models, picks the best one,
    /// and posts a welcome bubble. Clears <see cref="InferpalConfig.IsFirstRun"/> permanently.
    /// </summary>
    private async Task StartFirstRunDiscoveryAsync()
    {
        try
        {
            if (!_config.IsFirstRun) return;

            // Let the heartbeat complete its first check (2 s delay + RTT) before we act.
            await Task.Delay(3_500).ConfigureAwait(false);

            if (!_config.IsFirstRun) return; // guard against re-entry

            // First-run posts its bubbles directly (no user turn exists yet to attach a reply to).
            await RunSetupDiscoveryAsync(FirstRunPresentAsync).ConfigureAwait(false);
        }
        catch (Exception ex) { Diagnostics.Swallow("Rag.FirstRun", ex); }
    }

    /// <summary>Presenter used by the automatic first-run path: inserts a themed assistant bubble.</summary>
    private Task FirstRunPresentAsync(string text) => RunOnVMContextAsync(() =>
    {
        var msg = ChatMessageItem.AssistantMsg(text);
        ApplyItemTheme(msg);
        Messages.Insert(Messages.Count - 2, msg);
        ScrollToBottom();
    });

    /// <summary>
    /// Core setup discovery, shared by the automatic first-run and the manual <c>/setup</c> command:
    /// auto-detects the backend, checks connectivity, discovers and auto-selects chat + embedding
    /// models, seeds the VRAM budget, and reports the result via <paramref name="present"/>. Always
    /// clears <see cref="InferpalConfig.IsFirstRun"/>. Re-runnable on demand (no IsFirstRun guard here).
    /// </summary>
    private async Task RunSetupDiscoveryAsync(Func<string, Task> present)
    {
        var url = _config.BaseUrl;

        // ── 0. Backend auto-detection ─────────────────────────────────────────
        // Probe the URL to pick the right backend (Ollama / LM Studio / OpenAI-compatible) without
        // the user choosing manually. When it differs from the configured one, persist it and use a
        // matching client for discovery; the active singleton is rebuilt on the next VS reload.
        var detected = await Services.Inference.ProviderProbe.DetectAsync(url, _config.ApiKey, CancellationToken.None)
            .ConfigureAwait(false);
        if (detected is not null && !string.Equals(detected, _config.Provider, StringComparison.OrdinalIgnoreCase))
        {
            _config.Provider = detected;
            _config.Save();
        }
        var client = detected is not null ? Services.Inference.InferenceProviderFactory.Create(_config) : _client;

        // ── 1. Connectivity check ─────────────────────────────────────────────
        // A successful probe already proves reachability; otherwise fall back to the client's own check.
        var reachable = detected is not null
            || await client.CheckConnectionAsync(url, CancellationToken.None).ConfigureAwait(false);

        if (!reachable)
        {
            _config.IsFirstRun = false;
            _config.Save();
            await present(Strings.MsgFirstRunBackendDown(url)).ConfigureAwait(false);
            return;
        }

        // ── 2. Model discovery ────────────────────────────────────────────────
        var allModels  = await client.ListModelsAsync(CancellationToken.None)
            .ConfigureAwait(false);

        var chatModels = allModels.Where(m => !Services.Inference.ModelCatalog.IsEmbeddingModel(m)).ToList();
        var embModels  = allModels.Where(m =>  Services.Inference.ModelCatalog.IsEmbeddingModel(m)).ToList();

        if (chatModels.Count == 0)
        {
            _config.IsFirstRun = false;
            _config.Save();
            await present(Strings.MsgFirstRunNoModels).ConfigureAwait(false);
            return;
        }

        // ── 3. Auto-configure ─────────────────────────────────────────────────
        var best = Services.Inference.ModelCatalog.PickBestChatModel(chatModels);
        _config.DefaultModel = best;
        await RunOnVMContextAsync(() => ActiveModelLabel = best);

        // Auto-pick first embedding model only when nothing is configured yet.
        if (string.IsNullOrEmpty(_config.RagEmbeddingModel) && embModels.Count > 0)
            _config.RagEmbeddingModel = embModels[0];

        _config.IsFirstRun = false;
        _config.Save();

        // ── 4. Hardware fit-check ─────────────────────────────────────────────
        // Auto-seed the VRAM budget if Ollama is local, then warn when the auto-picked
        // chat + embedding set is estimated to overflow it. Silent when the budget is
        // unknown (remote host) or the models comfortably fit.
        await Services.Hardware.HardwareProfile.EnsureBudgetAsync(_config, CancellationToken.None)
            .ConfigureAwait(false);

        var vramWarning = await BuildFirstRunVramWarningAsync(best).ConfigureAwait(false);

        var countLabel = chatModels.Count == 1 ? "1 model" : $"{chatModels.Count} models";
        var text = Strings.MsgFirstRunWelcome(countLabel, best);
        if (vramWarning is not null) text += "\n\n" + vramWarning;
        await present(text).ConfigureAwait(false);
    }

    /// <summary>Handles <c>/setup</c>: re-runs the first-run discovery on demand (re-detect backend, re-pick models).</summary>
    private async Task HandleSetupCommandAsync(string[] parts, CancellationToken ct)
        => await RunSetupDiscoveryAsync(ShowInfoAsync).ConfigureAwait(false);

    /// <summary>
    /// Builds the first-run VRAM overflow warning, or <c>null</c> when the budget is unknown or
    /// the auto-picked chat + embedding models comfortably fit. Estimates footprint from the
    /// models' on-disk size (<c>/api/tags</c>).
    /// </summary>
    private async Task<string?> BuildFirstRunVramWarningAsync(string chatModel)
    {
        if (_config.VramBudgetGb <= 0) return null;

        var installed = await _client.ListInstalledModelsAsync(CancellationToken.None).ConfigureAwait(false);
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

        var summarizeHistory = new List<ChatMessageDto>(_history)
        {
            new("user", Strings.OodaSummarizePrompt)
        };

        try
        {
            var result = await _client.RunAgentAsync(
                model:   await ModelRouter.ResolveUtilityAsync(_config, _client, ct),
                history: summarizeHistory,
                tools:   EmptyToolRegistry.Instance,
                onStep:  _ => { },
                onToken: null,
                ct:      ct);

            var summary = result.FinalResponse?.Trim();
            if (!string.IsNullOrEmpty(summary))
            {
                await RunOnVMContextAsync(() =>
                {
                    _oodaSummary  = summary;
                    _history[0]   = new ChatMessageDto("system",
                        _baseSystemPrompt + "\n\n## Session Summary\n\n" + _oodaSummary);

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
    private async Task CompactOrTruncateAsync(CancellationToken ct)
    {
        // The decision, the summarising model call and its fuse lived HERE, so on the VS Code side
        // the history was NEVER bounded and three settings of the panel did nothing there. All of
        // that moved into ContextManager; what stays is what cannot move: mutating _history on the
        // VM context (rule 6 - otherwise it is replaced under the turn loop that reads it) and
        // placing the bubbles.
        var decision = await Services.Agent.ContextManager.PrepareAsync(
            _history, _config, _client, _lastPromptTokens,
            onStep: step => Post(() => CurrentStep = step),
            ct: ct);

        if (decision.Outcome == Services.Agent.ContextOutcome.None) return;

        await RunOnVMContextAsync(() =>
        {
            if (decision.Outcome == Services.Agent.ContextOutcome.Compacted)
                Services.Agent.HistoryCompaction.ApplySummary(_history, decision.Plan, decision.Summary!);
            else
                Services.Agent.HistoryCompaction.ApplyTruncation(_history, decision.Plan);

            _lastPromptTokens = 0;

            // A successful compaction is a tool event (collapsible); both fallbacks are warnings -
            // the conversation has lost turns, and that reads in plain text.
            InsertThemed(decision.Outcome == Services.Agent.ContextOutcome.Compacted
                ? ChatMessageItem.ToolMsg("context_compact", decision.Notice, _config.ToolBubblesExpanded)
                : ChatMessageItem.AssistantMsg(decision.Notice));
            ScrollToBottom();
        });
    }

    // Prompt layering itself lives in SystemPromptBuilder (unit-tested); the VM only
    // gathers its inputs: project root, active-file relative path, template suffix.
    private string BuildSystemPrompt(string? language = null)
    {
        var dir = FindProjectRoot();
        var prompt = new SystemPromptBuilder(_config, EditorName).Build(
            Strings.SystemPrompt,
            language,
            _activeTemplateSuffix,
            dir,
            ActiveFileRelativeTo(dir),
            _xrayDisabledSections);   // sections switched off from the Context X-Ray panel
        return _planMode ? prompt + PlanModeToolRegistry.SystemPromptSuffix : prompt;
    }

    // Active editor file path relative to the project root (forward slashes), or null when no file
    // is active or it lives outside the root. Used to scope project rules by glob.
    private string? ActiveFileRelativeTo(string root)
    {
        var path = _activeFilePath;
        if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(root)) return null;
        try
        {
            var rel = Path.GetRelativePath(root, path);
            if (rel.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(rel)) return null;
            return rel.Replace('\\', '/');
        }
        catch { return null; }
    }

    // Returns a .sln-anchored root, or null if none can be found yet.
    // Unlike FindProjectRoot(), never falls back to CWD — callers that need a reliable
    // root (e.g. RAG indexing) should use this and retry later if it returns null.
    private string? FindReliableProjectRoot() =>
        _rootLocator.LocateReliable(
            _contextHolder.GetOpenPaths(),
            ActiveSolutionSignal.TryReadSolutionDir(),
            Directory.GetCurrentDirectory());

    // Resolution order and walk limits live in ProjectRootLocator (unit-tested); the VM
    // only supplies the open editor paths, the authoritative signal, and the CWD.
    private static readonly Services.Signals.ProjectRootLocator _rootLocator = new();

    private string FindProjectRoot(IReadOnlyList<string>? openPaths = null) =>
        _rootLocator.Locate(
            openPaths ?? _contextHolder.GetOpenPaths(),
            ActiveSolutionSignal.TryReadSolutionDir(),
            Directory.GetCurrentDirectory());

    #endregion
}
