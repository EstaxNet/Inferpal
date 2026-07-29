using System.Text.Json;
using Inferpal.Localization;
using Inferpal.Services;
using Inferpal.Services.Commands;
using Inferpal.Services.Docs;
using Inferpal.Services.Persistence;
using StreamJsonRpc;

namespace Inferpal.Host;

/// <summary>
/// `command/slash` — the headless side of the slash commands. Routing is the same
/// <see cref="SlashCommandRouter"/> as the VS extension and execution reuses the shared pure
/// handlers; editor-side side effects come back as typed <see cref="SlashEffectDto"/> entries.
/// Commands whose UX is intrinsically editor-side (code actions, /explain, /review) return
/// <c>Handled = false</c>; commands not portable yet return a localized "unavailable" bubble
/// (never <c>Handled = false</c>, which would send the raw command to the model).
/// </summary>
internal sealed partial class HostServer
{
    [JsonRpcMethod("command/slash", UseSingleObjectParameterDeserialization = true)]
    public async Task<SlashCommandResult> CommandSlashAsync(SlashCommandParams p, CancellationToken ct)
    {
        var s = Session();
        var templates = SlashTemplates.Load(s.Config, string.IsNullOrEmpty(s.RootDir) ? null : s.RootDir);

        switch (SlashCommandRouter.Route(p.Text, templates))
        {
            case SlashInfoAction info:
                return new SlashCommandResult(true, info.Message);

            case SlashPromptAction prompt:
                // Expanded user template: the adapter re-enters the normal chat pipeline with it.
                return new SlashCommandResult(true, null, [new SlashEffectDto("sendAsPrompt", prompt.Prompt)]);

            case SlashToolAction tool:
                return await RunSlashToolAsync(s, tool, ct);

            case SlashDelegatedAction delegated:
                return await RunDelegatedSlashAsync(s, delegated, p, ct);

            // Code actions: the adapter owns them (/fix /refactor /doc → codeAction/run,
            // /explain /review → active-document prompt, /test → not ported yet).
            default:
                return new SlashCommandResult(Handled: false);
        }
    }

    /// <summary>Direct tool invocation (/read /ls /grep /run /git /map …): executed by the
    /// registry — approvals and permission rules apply exactly as in an agent run.</summary>
    private async Task<SlashCommandResult> RunSlashToolAsync(HostSession s, SlashToolAction tool, CancellationToken ct)
    {
        var cts = AcquireTurn(ct);
        try
        {
            var args   = JsonSerializer.SerializeToElement(tool.Args);
            var result = await s.Tools.ExecuteAsync(tool.Tool, args, cts.Token);

            return tool.AttachAs is not null
                ? new SlashCommandResult(true, null, [new SlashEffectDto("attachChip", result, tool.AttachAs)])
                : new SlashCommandResult(true, result);
        }
        catch (OperationCanceledException)
        {
            return new SlashCommandResult(true, Strings.MsgCancelled);
        }
        catch (Exception ex)
        {
            Diagnostics.Swallow("HostServer.SlashTool", ex);
            return new SlashCommandResult(true, Strings.MsgError(ex.Message));
        }
        finally
        {
            ReleaseTurn(cts);
        }
    }

    private async Task<SlashCommandResult> RunDelegatedSlashAsync(
        HostSession s, SlashDelegatedAction delegated, SlashCommandParams p, CancellationToken ct)
    {
        var parts = delegated.Parts;
        var cts   = AcquireTurn(ct);
        try
        {
            switch (delegated.Id)
            {
                case SlashCommandId.Clear:
                    ResetHistory(s);
                    return new SlashCommandResult(true, null, [new SlashEffectDto("clearTranscript")]);

                case SlashCommandId.Model:
                    if (parts.Length < 2)
                        return new SlashCommandResult(true, Strings.SlashModelCurrent(s.Config.DefaultModel));
                    s.Config.DefaultModel = parts[1];
                    s.Config.Save();
                    return new SlashCommandResult(true, Strings.SlashModelChanged(parts[1]),
                        [new SlashEffectDto("stateChange", parts[1], "model")]);

                case SlashCommandId.Tools:
                    if (parts.Length < 2 || parts[1] is not ("on" or "off"))
                        return new SlashCommandResult(true, Strings.SlashToolsCurrent(s.ToolsEnabled ? "on" : "off"));
                    s.ToolsEnabled = parts[1] == "on";
                    return new SlashCommandResult(true, Strings.SlashToolsChanged(parts[1]));

                case SlashCommandId.Export:
                    return new SlashCommandResult(true, null, [new SlashEffectDto("exportRequest")]);

                case SlashCommandId.Context:
                    return await ReadProjectFileAsync(s, "context.md",
                        Strings.SlashContextNotFound, Strings.SlashContextLoaded, cts.Token);

                case SlashCommandId.Memory:
                    return await ReadProjectFileAsync(s, "memory.md",
                        Strings.SlashMemoryNotFound, Strings.SlashMemoryLoaded, cts.Token);

                case SlashCommandId.Index:
                    return HandleIndexSlash(s, parts);

                case SlashCommandId.History:
                    return await HandleHistorySlashAsync(s, parts, cts.Token);

                case SlashCommandId.UndoRun:
                    return await HandleUndoRunSlashAsync(s, parts, cts.Token);

                case SlashCommandId.PHistory:
                {
                    var result = PHistoryCommandHandler.Handle(p.PromptHistory ?? [], parts);
                    return result.FillPrompt is { } fill
                        ? new SlashCommandResult(true, null, [new SlashEffectDto("setPrompt", fill)])
                        : new SlashCommandResult(true, result.Message);
                }

                case SlashCommandId.Models:
                    return await HandleModelsSlashAsync(s, parts, cts.Token);

                case SlashCommandId.Hardware:
                {
                    var result = await HardwareCommandHandler.HandleAsync(s.Config, s.Client, parts, cts.Token);
                    // The handler never persists config (so tests don't touch %APPDATA%); apply + save here.
                    if (result.SetBudgetGb is { } gb)
                    {
                        s.Config.VramBudgetGb = gb;
                        s.Config.Save();
                    }
                    return new SlashCommandResult(true, result.Message);
                }

                case SlashCommandId.Note:
                {
                    var result = await NotesCommandHandler.HandleNoteAsync(s.RootDir, parts, DateTime.Now, cts.Token);
                    if (result.RefreshSystemPrompt)
                        RefreshSystemMessage(s);
                    return new SlashCommandResult(true, result.Message);
                }

                case SlashCommandId.Notes:
                {
                    var result = await NotesCommandHandler.HandleNotesAsync(s.RootDir, parts, cts.Token);
                    return new SlashCommandResult(true, result.Message);
                }

                case SlashCommandId.Snippets:
                {
                    var result = await SnippetsCommandHandler.HandleAsync(parts, cts.Token);
                    return result.CopyToClipboard is { } code
                        ? new SlashCommandResult(true, result.Message, [new SlashEffectDto("copyToClipboard", code)])
                        : new SlashCommandResult(true, result.Message);
                }

                case SlashCommandId.Template:
                    return HandleTemplateSlash(s, parts);

                case SlashCommandId.Docs:
                    return await HandleDocsSlashAsync(s, parts, cts.Token);

                case SlashCommandId.Rules:
                    return await HandleScaffoldSlashAsync(
                        RulesChecksPromptsCommandHandler.Rules(s.RootDir, parts), Strings.RulesScaffolded, cts.Token);

                case SlashCommandId.Checks:
                    return await HandleScaffoldSlashAsync(
                        RulesChecksPromptsCommandHandler.Checks(s.RootDir, parts), Strings.ChecksScaffolded, cts.Token);

                case SlashCommandId.Prompts:
                {
                    var result = await HandleScaffoldSlashAsync(
                        RulesChecksPromptsCommandHandler.Prompts(s.RootDir, parts), Strings.PromptsScaffolded, cts.Token);
                    PromptFilesService.InvalidateCache();   // show up in autocomplete immediately
                    return result;
                }

                case SlashCommandId.Diagnostics:
                    return new SlashCommandResult(true, DiagnosticsCommandHandler.Handle(parts));

                case SlashCommandId.Replay:
                    return new SlashCommandResult(true,
                        ReplayCommandHandler.Handle(s.Tools.History.Runs, parts, s.RootDir));

                case SlashCommandId.Xray:
                {
                    var sections = new SystemPromptBuilder(s.Config).BuildSections(
                        Strings.SystemPrompt,
                        projectRoot: string.IsNullOrEmpty(s.RootDir) ? null : s.RootDir);
                    return new SlashCommandResult(true, XRayCommandHandler.Handle(
                        sections,
                        AgentOrchestrator.EstimateTokens(s.History),
                        s.Config.ContextWindowSize,
                        s.Config.RagAutoContextEnabled));
                }

                case SlashCommandId.Bench:
                {
                    // Long-running (a full micro-eval suite per model). Progress is surfaced through
                    // the same chat/step notifications the agent loop uses.
                    var result = await BenchCommandHandler.HandleAsync(
                        s.Client, parts, progress => Notify("chat/step", new { text = progress }), cts.Token);
                    return new SlashCommandResult(true, result.Message);
                }

                case SlashCommandId.Arena:
                {
                    // Two sequential inference calls; progress goes through the same chat/step
                    // notifications as /bench.
                    var result = await ArenaCommandHandler.HandleAsync(
                        s.Client, s.Config, parts, progress => Notify("chat/step", new { text = progress }), cts.Token);
                    return new SlashCommandResult(true, result.Message);
                }

                case SlashCommandId.Tdd:
                {
                    // "Fix until green" loop; test reports, agent steps and per-round fix summaries
                    // all flow through the same chat/step notifications as the agent loop.
                    var result = await TddCommandHandler.HandleAsync(
                        s.Client, s.Config, s.Tools,
                        BuildSystemPromptText(s), parts,
                        string.IsNullOrEmpty(s.RootDir) ? null : s.RootDir,
                        onProgress:   msg => Notify("chat/step", new { text = msg }),
                        onTestReport: (output, _) => Notify("chat/step", new { text = output }),
                        onStep:       st => Notify("chat/step", new { text = st }),
                        onToken:      null,
                        onFixResult:  null,
                        cts.Token);
                    return new SlashCommandResult(true, result.Message);
                }

                // Not portable yet (VS-only UX or planned for a later phase): a deterministic
                // localized answer beats falling through to the model with a raw "/command".
                case SlashCommandId.Commit:
                case SlashCommandId.CommitExec:
                case SlashCommandId.FixBuild:
                case SlashCommandId.Check:
                case SlashCommandId.Setup:
                case SlashCommandId.TestBuildBanner:
                case SlashCommandId.AgentStep:
                case SlashCommandId.Plan:
                case SlashCommandId.Resume:
                    return new SlashCommandResult(true, Strings.SlashHeadlessUnavailable);

                default:
                    return new SlashCommandResult(Handled: false);
            }
        }
        catch (OperationCanceledException)
        {
            return new SlashCommandResult(true, Strings.MsgCancelled);
        }
        catch (Exception ex)
        {
            Diagnostics.Swallow("HostServer.SlashDelegated", ex);
            return new SlashCommandResult(true, Strings.MsgError(ex.Message));
        }
        finally
        {
            ReleaseTurn(cts);
        }
    }

    // ── Individual handlers ──────────────────────────────────────────────────────

    /// <summary>/context and /memory: shows the corresponding `.inferpal/*.md` project file.</summary>
    private static async Task<SlashCommandResult> ReadProjectFileAsync(
        HostSession s, string fileName,
        Func<string, string> notFound, Func<string, int, string, string> loaded, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(s.RootDir))
            return new SlashCommandResult(true, Strings.SlashContextNoSln);

        var path = Path.Combine(s.RootDir, ".inferpal", fileName);
        if (!File.Exists(path))
            return new SlashCommandResult(true, notFound(path));

        var content = await File.ReadAllTextAsync(path, System.Text.Encoding.UTF8, ct);
        var preview = content.Length > 400 ? content[..400] + "…" : content;
        return new SlashCommandResult(true, loaded(path, content.Length, preview));
    }

    /// <summary>/index [rebuild] — RAG status / manual re-index (same output as the VS VM).</summary>
    private SlashCommandResult HandleIndexSlash(HostSession s, string[] parts)
    {
        if (parts.Length >= 2 && parts[1].Equals("rebuild", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrEmpty(s.RootDir))
                return new SlashCommandResult(true, "⚠ Cannot locate solution root — open a file first.");
            s.Index.StartIndexing(s.RootDir);
            return new SlashCommandResult(true, $"🔄 RAG re-indexing started: `{s.RootDir}`");
        }

        var model = string.IsNullOrEmpty(s.Config.RagEmbeddingModel) ? "nomic-embed-text" : s.Config.RagEmbeddingModel;
        var sb    = new System.Text.StringBuilder();
        sb.AppendLine("**RAG Index**");
        sb.AppendLine();
        if (!s.Config.RagEnabled)
        {
            sb.AppendLine("Status: **disabled** (`ragEnabled = false` in settings)");
            sb.AppendLine();
            sb.AppendLine("Enable it to get semantic cross-file search via `search_codebase`.");
        }
        else if (s.Index.ChunkCount == 0 && !s.Index.IsIndexing)
        {
            sb.AppendLine($"Status: {(s.Index.Status is { Length: > 0 } st ? st : "not started")}");
            sb.AppendLine();
            sb.AppendLine("Use `/index rebuild` to build the index manually.");
        }
        else
        {
            sb.AppendLine($"Status : {s.Index.Status}");
            sb.AppendLine($"Chunks : {s.Index.ChunkCount:N0}");
            sb.AppendLine($"Root   : `{s.Index.RootDir}`");
            sb.AppendLine($"Model  : `{model}`");
            sb.AppendLine($"Top-K  : {s.Config.RagTopK}");
            sb.AppendLine();
            sb.AppendLine("Use `/index rebuild` to force a full re-index.");
        }
        return new SlashCommandResult(true, sb.ToString().TrimEnd());
    }

    /// <summary>/history [term] — saved-session list or full-text search (same store as VS).</summary>
    private static async Task<SlashCommandResult> HandleHistorySlashAsync(
        HostSession s, string[] parts, CancellationToken ct)
    {
        if (parts.Length >= 2)
        {
            var term    = string.Join(" ", parts[1..]);
            var matches = await s.Store.SearchAsync(term, ct);
            return new SlashCommandResult(true, matches.Count == 0
                ? Strings.HistoryNoResults(term)
                : SessionManager.FormatHistorySearch(term, matches, DateTime.UtcNow));
        }

        var sessions = await s.Store.ListWithPreviewAsync(ct);
        return new SlashCommandResult(true, sessions.Count == 0
            ? Strings.HistoryNoSessions
            : SessionManager.FormatHistoryList(sessions, DateTime.UtcNow));
    }

    /// <summary>/undo-run [list] — reverts (or lists) the change-tracking runs of this session.</summary>
    private static async Task<SlashCommandResult> HandleUndoRunSlashAsync(
        HostSession s, string[] parts, CancellationToken ct)
    {
        var runs = s.Tools.History.Runs;

        if (parts.Length >= 2 && parts[1].Equals("list", StringComparison.OrdinalIgnoreCase))
        {
            var withChanges = runs.Where(r => r.FileCount > 0).ToList();
            if (withChanges.Count == 0)
                return new SlashCommandResult(true, Strings.UndoRunNone);

            var sb = new System.Text.StringBuilder();
            sb.AppendLine(Strings.UndoRunListHeader(withChanges.Count));
            foreach (var r in withChanges)
                sb.AppendLine($"- {r.StartedAt:HH:mm:ss} — {r.FileCount} file(s)");
            return new SlashCommandResult(true, sb.ToString().TrimEnd());
        }

        var run = runs.FirstOrDefault(r => r.FileCount > 0);
        if (run is null)
            return new SlashCommandResult(true, Strings.UndoRunNone);

        var result = await s.Tools.History.UndoRunAsync(run, ct);
        var lines  = new System.Text.StringBuilder();
        lines.AppendLine(Strings.UndoRunResult(result.Restored.Count, result.Deleted.Count));
        string Rel(string path) => string.IsNullOrEmpty(s.RootDir) ? path : Path.GetRelativePath(s.RootDir, path);
        foreach (var f in result.Restored) lines.AppendLine($"  ↩ {Rel(f)}");
        foreach (var f in result.Deleted)  lines.AppendLine($"  🗑 {Rel(f)}");
        foreach (var f in result.Failed)   lines.AppendLine($"  ⚠ {Rel(f)}");
        return new SlashCommandResult(true, lines.ToString().TrimEnd());
    }

    /// <summary>/models … — pull streams its progress through chat/step (the adapter's status
    /// line), everything else is the shared pure handler.</summary>
    private async Task<SlashCommandResult> HandleModelsSlashAsync(HostSession s, string[] parts, CancellationToken ct)
    {
        var sub = parts.Length >= 2 ? parts[1].ToLowerInvariant() : "list";
        if (sub == "pull")
        {
            if (!s.Client.Capabilities.ModelManagement)
                return new SlashCommandResult(true, Strings.ModelsBackendUnsupported);
            if (parts.Length < 3)
                return new SlashCommandResult(true, Strings.ModelsPullUsage);

            var model = string.Join(" ", parts[2..]);
            Notify("chat/step", new { text = Strings.ModelsPulling(model) });
            var ok = await s.Client.PullModelAsync(model,
                status => Notify("chat/step", new { text = Strings.ModelsPullingStatus(model, status) }), ct);
            return new SlashCommandResult(true, ok ? Strings.ModelsPulled(model) : Strings.ModelsPullFailed(model));
        }

        var result = await ModelsCommandHandler.HandleAsync(s.Client, parts, ct);
        return new SlashCommandResult(true, result.Message);
    }

    /// <summary>/template [id] — lists templates or reseeds the session with one (suffix kept
    /// for later system-prompt rebuilds, like the VS VM's <c>_activeTemplateSuffix</c>).</summary>
    private static SlashCommandResult HandleTemplateSlash(HostSession s, string[] parts)
    {
        if (parts.Length < 2)
            return new SlashCommandResult(true, SessionManager.FormatTemplateList());

        var id   = parts[1].ToLowerInvariant();
        var tmpl = SessionManager.FindTemplate(id);
        if (tmpl is null)
            return new SlashCommandResult(true, $"Unknown template `{id}`. Type `/template` to see the list.");

        s.TemplateSuffix = tmpl.SystemSuffix;
        ResetHistory(s);
        return new SlashCommandResult(true, tmpl.Greeting, [new SlashEffectDto("clearTranscript")]);
    }

    /// <summary>/docs add|list|remove|reindex — external documentation sources; crawls run in
    /// the background with progress surfaced through chat/step.</summary>
    private async Task<SlashCommandResult> HandleDocsSlashAsync(HostSession s, string[] parts, CancellationToken ct)
    {
        var sub   = parts.Length >= 2 ? parts[1].ToLowerInvariant() : "list";
        var sites = DocSite.Parse(s.Config.DocSitesJson);

        switch (sub)
        {
            case "add":
            {
                if (parts.Length < 3 || !DocSite.IsValidHttpUrl(parts[2]))
                    return new SlashCommandResult(true, Strings.DocsUsage);

                var title = parts.Length > 3 ? string.Join(" ", parts[3..]) : null;
                var site  = DocSite.Create(parts[2], title);
                s.Config.DocSitesJson = DocSite.Serialize(DocSite.Upsert(sites, site));
                s.Config.Save();

                // Crawl + embed in the background; progress surfaces as chat/step updates.
                var progress = new Progress<string>(msg => Notify("chat/step", new { text = msg }));
                _ = Task.Run(() => s.Docs.AddOrReindexAsync(site, progress, CancellationToken.None));
                return new SlashCommandResult(true, Strings.DocsAdded(site.Title));
            }

            case "remove":
            {
                if (parts.Length < 3)
                    return new SlashCommandResult(true, Strings.DocsUsage);

                var id      = parts[2].ToLowerInvariant();
                var updated = DocSite.Remove(sites, id);
                if (updated is null)
                    return new SlashCommandResult(true, Strings.DocsNoSites);

                s.Config.DocSitesJson = DocSite.Serialize(updated);
                s.Config.Save();
                await s.Docs.RemoveAsync(id, ct);
                return new SlashCommandResult(true, Strings.DocsRemoved(id));
            }

            case "reindex":
            {
                var target = parts.Length >= 3
                    ? sites.FirstOrDefault(x => x.Id == parts[2].ToLowerInvariant())
                    : null;
                var toIndex = target is not null ? [target] : sites.ToArray();
                if (toIndex.Length == 0)
                    return new SlashCommandResult(true, Strings.DocsNoSites);

                var progress = new Progress<string>(msg => Notify("chat/step", new { text = msg }));
                _ = Task.Run(async () =>
                {
                    foreach (var site in toIndex)
                        await s.Docs.AddOrReindexAsync(site, progress, CancellationToken.None);
                });
                return new SlashCommandResult(true, Strings.DocsReindexing(target?.Title ?? $"{toIndex.Length}"));
            }

            default:
            {
                if (sites.Count == 0)
                    return new SlashCommandResult(true, Strings.DocsNoSites);

                var stats = s.Docs.Sites.ToDictionary(x => x.Site.Id, x => (x.PageCount, x.ChunkCount));
                return new SlashCommandResult(true, DocSite.FormatList(sites, stats));
            }
        }
    }

    /// <summary>/rules /checks /prompts — list, or `init` scaffolds the example file
    /// (created only if absent) and asks the adapter to open it.</summary>
    private static async Task<SlashCommandResult> HandleScaffoldSlashAsync(
        RulesChecksPromptsCommandHandler.CommandListResult result,
        Func<string, string> confirm, CancellationToken ct)
    {
        if (result.Scaffold is not { } scaffold)
            return new SlashCommandResult(true, result.Message);

        var path = Path.Combine(scaffold.Dir, scaffold.FileName);
        Directory.CreateDirectory(scaffold.Dir);
        if (!File.Exists(path))
            await File.WriteAllTextAsync(path, scaffold.Content, System.Text.Encoding.UTF8, ct);
        return new SlashCommandResult(true, confirm(path), [new SlashEffectDto("openFile", path)]);
    }

    /// <summary>Re-seeds the in-memory system message after a project-layer change (/note).</summary>
    private static void RefreshSystemMessage(HostSession s)
    {
        if (s.History.Count > 0 && s.History[0].Role == "system")
            s.History[0] = new Models.ChatMessageDto("system", BuildSystemPromptText(s));
    }

    // ── Typed @-mentions ─────────────────────────────────────────────────────────

    /// <summary>The @mention categories with their localized descriptions (Core's
    /// MentionController — the same list as the VS popup).</summary>
    [JsonRpcMethod("mention/categories")]
    public List<MentionCategoryDto> MentionCategories()
        => MentionController.Categories
            .Select(c => new MentionCategoryDto(c.Token, c.Desc(), c.QueryBased))
            .ToList();

    /// <summary>Sub-search of @file / @folder under the workspace root (fuzzy, best 8).</summary>
    [JsonRpcMethod("mention/search", UseSingleObjectParameterDeserialization = true)]
    public List<MentionItemDto> MentionSearch(MentionSearchParams p, CancellationToken ct)
    {
        var s = Session();
        if (string.IsNullOrEmpty(s.RootDir))
            return [];

        var query = p.Query.Trim().ToLowerInvariant();
        var paths = p.Category.ToLowerInvariant() switch
        {
            "file"   => MentionController.FindFiles(s.RootDir, query, ct),
            "folder" => MentionController.FindFolders(s.RootDir, query, ct),
            _        => [],
        };
        return paths
            .Select(full => new MentionItemDto(
                Path.GetFileName(full) is { Length: > 0 } name ? name : full,
                MentionController.RelLabel(full, s.RootDir),
                full))
            .ToList();
    }

    /// <summary>Materializes a mention host-side: @tree (project map), @diff (git diff),
    /// @folder (folder context body) and @code (semantic search). Tool-backed categories run
    /// through the registry, so permission rules apply as usual.</summary>
    [JsonRpcMethod("mention/resolve", UseSingleObjectParameterDeserialization = true)]
    public async Task<MentionResolveResult> MentionResolveAsync(MentionResolveParams p, CancellationToken ct)
    {
        var s = Session();
        try
        {
            switch (p.Category.ToLowerInvariant())
            {
                case "tree":
                {
                    var map = await s.Tools.ExecuteAsync(
                        "generate_project_map", JsonSerializer.SerializeToElement(new { }), ct);
                    return new MentionResolveResult("🌲 tree", map);
                }
                case "diff":
                {
                    var diff = await s.Tools.ExecuteAsync(
                        "get_git_status", JsonSerializer.SerializeToElement(new { include_diff = true }), ct);
                    return new MentionResolveResult("📊 git diff", diff);
                }
                case "folder" when !string.IsNullOrEmpty(p.Value):
                {
                    var content = MentionController.BuildFolderContext(p.Value!, ct);
                    return new MentionResolveResult("📁 " + Path.GetFileName(p.Value!.TrimEnd('\\', '/')), content);
                }
                case "code" when !string.IsNullOrWhiteSpace(p.Value):
                {
                    var hits = await s.Tools.ExecuteAsync(
                        "search_codebase", JsonSerializer.SerializeToElement(new { query = p.Value }), ct);
                    return new MentionResolveResult("🔮 " + p.Value, hits);
                }
                default:
                    return new MentionResolveResult(null, null);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Diagnostics.Swallow("HostServer.MentionResolve", ex);
            return new MentionResolveResult(null, null);
        }
    }
}
