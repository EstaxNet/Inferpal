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

            // /test writes to a SEPARATE file, so there is nothing for the adapter's in-place
            // code-action path to do with it: it fell through to `Handled = false` and the literal
            // string "/test" reached the model, which improvised. Served here instead.
            case SlashCodeAction { Kind: SlashCodeActionKind.Test }:
                return await RunGenerateTestsAsync(s, ct);

            // Other code actions: the adapter owns them (/fix /refactor /doc → codeAction/run,
            // /explain /review → active-document prompt).
            default:
                return new SlashCommandResult(Handled: false);
        }
    }

    /// <summary>
    /// The session's background-task queue, wired on first use to an agent run restricted to the
    /// read-only registry (<see cref="BackgroundTaskToolRegistry"/>): a detached run explores and
    /// reports, it never writes or executes, so it can never raise an approval prompt while the
    /// user is typing.
    /// </summary>
    private BackgroundTaskQueue TaskQueue(HostSession s) => s.GetOrCreateTasks(
        runner: async (task, onStep, ct) =>
        {
            // Proposal mode (§18): the editing tools become available, against a registry whose
            // approval service records instead of granting — so the run still cannot write. Same
            // construction as the VS view-model, because the mode must not differ between editors.
            var recorder = task.ProposeWrites ? new ProposalRecorder() : null;
            var registry = recorder is null
                ? new BackgroundTaskToolRegistry(s.Tools)
                : new BackgroundTaskToolRegistry(s.Tools.WithApprovalService(recorder), recorder);

            var suffix = recorder is null
                ? BackgroundTaskToolRegistry.SystemPromptSuffix
                : BackgroundTaskToolRegistry.ProposalPromptSuffix;

            var history = new List<Inferpal.Models.ChatMessageDto>
            {
                new("system", BuildSystemPromptText(s) + suffix),
                new("user",   task.Objective),
            };

            // RunAgentAsync never throws for network/backend errors — they come back in the
            // result — so a failed task carries the model's own explanation as its report.
            var run = await s.Client.RunAgentAsync(
                ModelRouter.Resolve(s.Config, ModelRole.Agent),
                history,
                registry,
                onStep:  onStep,
                onToken: null,
                ct:      ct);

            return new BackgroundTaskQueue.TaskRunOutcome(
                run.FinalResponse, recorder?.Proposals ?? []);
        },
        onFinished: snapshot => Notify("chat/step", new { text = Strings.TaskFinishedNotice(snapshot.Id) }));

    /// <summary>Current content of a proposed file, or null when it is not there.</summary>
    private static string? ReadFileForProposal(string path)
    {
        try   { return File.Exists(path) ? File.ReadAllText(path) : null; }
        catch (Exception ex) { Diagnostics.Swallow($"ReadFileForProposal({path})", ex); return null; }
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
        // /resume runs OUTSIDE the single-turn gate: during a step pause the chat turn
        // still holds it (that pause is precisely what /resume releases).
        if (delegated.Id == SlashCommandId.Resume)
        {
            if (s.StepResume is not null)
            {
                s.StepResume.TrySetResult(true);
                return new SlashCommandResult(true, null);
            }
            return new SlashCommandResult(true, "No agent step is currently paused.");
        }

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
                    return new SlashCommandResult(true, await ProjectFileCommandHandler.HandleAsync(
                        s.RootDir, "context.md", Strings.SlashContextNotFound, Strings.SlashContextLoaded, cts.Token));

                case SlashCommandId.Memory:
                    return new SlashCommandResult(true, await ProjectFileCommandHandler.HandleAsync(
                        s.RootDir, "memory.md", Strings.SlashMemoryNotFound, Strings.SlashMemoryLoaded, cts.Token));

                case SlashCommandId.Index:
                    return new SlashCommandResult(true,
                        IndexCommandHandler.Handle(s.Index, s.Config, parts, s.RootDir));

                case SlashCommandId.History:
                    return new SlashCommandResult(true, await HistoryCommandHandler.HandleAsync(
                        s.Store, parts, DateTime.UtcNow, cts.Token));

                case SlashCommandId.UndoRun:
                    return new SlashCommandResult(true, await UndoRunCommandHandler.HandleAsync(
                        s.Tools.History, parts, s.RootDir, cts.Token));

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
                {
                    var result = DiagnosticsCommandHandler.Handle(parts, new DiagnosticsExportContext(
                        s.Config, "VS Code host",
                        WorkspaceRoot: string.IsNullOrEmpty(s.RootDir) ? null : s.RootDir));
                    return result.CopyToClipboard is { } bundle
                        ? new SlashCommandResult(true, result.Message, [new SlashEffectDto("copyToClipboard", bundle)])
                        : new SlashCommandResult(true, result.Message);
                }

                case SlashCommandId.Replay:
                    return new SlashCommandResult(true,
                        ReplayCommandHandler.Handle(s.Tools.History.Runs, parts, s.RootDir));

                case SlashCommandId.Branch:
                    return await HandleBranchSlashAsync(s, parts, cts.Token);

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

                case SlashCommandId.Task:
                {
                    // Detached run: deliberately NOT bound to this turn's token — the task keeps
                    // going after the command that submitted it returns. Progress is not streamed
                    // to the chat (it would interrupt what the user is doing); only the completion
                    // notice is, and `/task <id>` shows the report.
                    var task = TaskCommandHandler.Handle(TaskQueue(s), parts);

                    // `/task apply <id> <n>`: the write goes through the real registry, so the
                    // ordinary approval prompt appears and /undo-run covers it (§18, decision (b)).
                    if (task.Apply is { } proposal)
                        return new SlashCommandResult(true,
                            await TaskProposalApplication.ApplyAsync(
                                proposal, s.Tools, ReadFileForProposal, cts.Token,
                                beginRun: () => s.Tools.History.BeginRun()));

                    return new SlashCommandResult(true, task.Message);
                }

                case SlashCommandId.AgentStep:
                    // Same texts as the VS toggle (deliberately English, model-adjacent UX).
                    s.StepMode = !s.StepMode;
                    return new SlashCommandResult(true, s.StepMode
                        ? "🦶 **Step mode ON** — the agent will pause after each tool call. Use ▶ Resume (or `/resume`) to continue."
                        : "Step mode **OFF**.",
                        [new SlashEffectDto("stateChange", s.StepMode ? "on" : "off", "stepMode")]);

                case SlashCommandId.Plan:
                {
                    // Roadmap §17. Same handler as the VM: bare `/plan` still toggles read-only
                    // plan mode, everything else works on a file under .inferpal/plans/.
                    var result = PlanCommandHandler.Handle(
                        s.RootDir, parts,
                        s.History.LastOrDefault(m => m.Role == "assistant")?.Content,
                        s.ActivePlan);

                    if (result.ToggleMode)
                    {
                        s.PlanMode = !s.PlanMode;
                        RefreshSystemMessage(s);
                        return new SlashCommandResult(true,
                            s.PlanMode ? Strings.PlanModeOn : Strings.PlanModeOff,
                            [new SlashEffectDto("stateChange", s.PlanMode ? "on" : "off", "planMode")]);
                    }

                    if (result.SetActivePlan is { } active)
                        s.ActivePlan = active.Length == 0 ? null : active;

                    // `/plan save` wrote the file; opening it is the editor's job, as for /onboard.
                    return result.OpenPath is { } path
                        ? new SlashCommandResult(true, result.Message, [new SlashEffectDto("openFile", path)])
                        : new SlashCommandResult(true, result.Message);
                }

                case SlashCommandId.Check:
                {
                    // Roadmap §15: same handler as the VM, so the anchored findings are identical
                    // on both sides. Progress uses the chat/step channel like /bench and /tdd.
                    var result = await CheckCommandHandler.HandleAsync(
                        s.Client, s.Config, s.RootDir ?? Directory.GetCurrentDirectory(), parts,
                        git: GitProcess.For(s.RootDir),
                        onProgress: progress => Notify("chat/step", new { text = progress }),
                        cts.Token);

                    // `init` writes through the same helper as /rules|/checks|/prompts init.
                    if (result.Scaffold is { } scaffold)
                        return await HandleScaffoldSlashAsync(
                            new RulesChecksPromptsCommandHandler.CommandListResult(null, scaffold),
                            Strings.ChecksScaffolded, cts.Token);

                    return new SlashCommandResult(true, result.Message);
                }

                case SlashCommandId.Onboard:
                {
                    // Roadmap §19. The profile itself is applied (index exclusions) or merely
                    // reported by the Core; the host only performs the IO it decided on.
                    var result = await OnboardCommandHandler.HandleAsync(
                        s.Client, s.Config, s.RootDir ?? Directory.GetCurrentDirectory(), parts,
                        git: GitProcess.For(s.RootDir),
                        onProgress: progress => Notify("chat/step", new { text = progress }),
                        cts.Token);

                    if (result.Scaffold is { } scaffold)
                        return await HandleScaffoldSlashAsync(
                            new RulesChecksPromptsCommandHandler.CommandListResult(null, scaffold),
                            Strings.OnboardProfileScaffolded, cts.Token);

                    if (result.SaveConfig) s.Config.Save();

                    if (result.NewDefaultModel is { } model)
                        return new SlashCommandResult(true, result.Message,
                            [new SlashEffectDto("stateChange", model, "model")]);

                    if (result.Write is { } write)
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(write.Path)!);
                        await File.WriteAllTextAsync(write.Path, write.Content, System.Text.Encoding.UTF8, cts.Token);
                        if (result.RefreshSystemPrompt) RefreshSystemMessage(s);
                        return new SlashCommandResult(true, result.Message,
                            [new SlashEffectDto("openFile", write.Path)]);
                    }

                    return new SlashCommandResult(true, result.Message);
                }

                case SlashCommandId.Debug:
                {
                    // Roadmap §21. The handler never drives the debugger itself: it either reports,
                    // or hands the model an instruction that goes through debug_control /
                    // debug_inspect — and therefore through the approval on the one action that
                    // executes the user's program.
                    var result = await DebugCommandHandler.HandleAsync(s.Tools.Debug, parts, cts.Token);
                    return result.SendAsPrompt is { } prompt
                        ? new SlashCommandResult(true, null, [new SlashEffectDto("sendAsPrompt", prompt)])
                        : new SlashCommandResult(true, result.Message);
                }

                case SlashCommandId.Commit:
                {
                    // Proposes only: the message lands in the input box behind `/commit-exec`, so
                    // the user reads it before anything is committed.
                    // No token streaming: `chat/token` feeds the assistant bubble of a chat turn,
                    // and there is none open during a slash command. The status line carries the
                    // wait, exactly like /bench and /tdd.
                    Notify("chat/step", new { text = Strings.CommitProposingLabel });
                    var proposal = await CommitCommandHandler.ProposeAsync(
                        s.Client, s.Config, GitProcess.For(s.RootDir), onToken: null, cts.Token);

                    if (proposal.Proposal is not { } proposed)
                        return new SlashCommandResult(true, proposal.Message ?? proposal.Notice);

                    var markdown = proposal.Notice is { } note
                        ? note + "\n\n" + Strings.CommitConfirmHint
                        : Strings.CommitConfirmHint;

                    return new SlashCommandResult(true, markdown,
                        [new SlashEffectDto("setPrompt", $"/commit-exec {proposed}")]);
                }

                case SlashCommandId.CommitExec:
                {
                    var message = string.Join(" ", parts[1..]).Trim();
                    if (string.IsNullOrWhiteSpace(message))
                        return new SlashCommandResult(true, Strings.SlashUsage("/commit-exec <message>"));

                    var run = await CommitCommandHandler.ExecuteAsync(
                        message, GitProcess.For(s.RootDir), cts.Token);

                    return new SlashCommandResult(true,
                        (run.Ok ? "✅ `git commit`" : "❌ `git commit`") + "\n\n```\n" + run.Output + "\n```");
                }

                // Not portable yet (VS-only UX or planned for a later phase): a deterministic
                // localized answer beats falling through to the model with a raw "/command".
                case SlashCommandId.FixBuild:
                case SlashCommandId.Setup:
                case SlashCommandId.TestBuildBanner:
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

    /// <summary>/index [rebuild] — RAG status / manual re-index (same output as the VS VM).</summary>


    /// <summary>
    /// <c>/branch</c> — listing is answered from the host's own history (turn numbering only needs
    /// the user messages, which both sides agree on); the two stateful outcomes come back as
    /// effects the adapter applies with its full transcript: <c>branchRequest</c> (→
    /// <c>session/branch</c>) and <c>loadSession</c> (→ <c>session/load</c>).
    /// </summary>
    private static async Task<SlashCommandResult> HandleBranchSlashAsync(
        HostSession s, string[] parts, CancellationToken ct)
    {
        var transcript = s.History
            .Where(m => m.Role is "user" or "assistant" or "tool")
            .Select(m => new SavedMessage(m.Role, m.Content ?? string.Empty))
            .ToList();

        var sessions = await s.Store.ListWithPreviewAsync(ct);
        var result   = BranchCommandHandler.Handle(parts, transcript, s.CurrentSessionName, sessions);

        if (result.Message is { } message)
            return new SlashCommandResult(true, message);

        // The bubble is localized here; the adapter only has to load the session.
        if (result.SwitchTo is { } target)
            return new SlashCommandResult(true, Strings.BranchSwitched(target),
                [new SlashEffectDto("loadSession", target)]);

        return new SlashCommandResult(true, null,
            [new SlashEffectDto("branchRequest", result.ForkTurn!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture))]);
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

    /// <summary>/template [id] — shared decision (TemplateCommandHandler); the host applies it
    /// by resetting its history and asking the adapter to clear the transcript.</summary>
    private SlashCommandResult HandleTemplateSlash(HostSession s, string[] parts)
    {
        var result = TemplateCommandHandler.Handle(parts);
        if (result.Apply is not { } tmpl)
            return new SlashCommandResult(true, result.Message);

        s.TemplateSuffix = tmpl.SystemSuffix;
        ResetHistory(s);
        return new SlashCommandResult(true, tmpl.Greeting, [new SlashEffectDto("clearTranscript")]);
    }

    /// <summary>/docs add|list|remove|reindex — external documentation sources; crawls run in
    /// the background with progress surfaced through chat/step.</summary>
    /// <summary>/docs add|list|remove|reindex — shared handler; crawl progress is surfaced
    /// through the same chat/step notifications the agent loop uses.</summary>
    private async Task<SlashCommandResult> HandleDocsSlashAsync(HostSession s, string[] parts, CancellationToken ct)
    {
        var progress = new Progress<string>(msg => Notify("chat/step", new { text = msg }));
        return new SlashCommandResult(true,
            await DocsCommandHandler.HandleAsync(s.Config, s.Docs, parts, progress, ct));
    }

    /// <summary>
    /// <c>/test</c> — generate unit tests for the active document into the conventional test file
    /// beside it, then ask the adapter to open it.
    /// </summary>
    /// <remarks>
    /// Two deliberate differences from VS, both consequences of the port rather than choices:
    /// <see cref="IEditorSurface"/> exposes no selection, so the whole file is the input; and an
    /// existing test file is rewritten on disk rather than through an undoable editor edit — the
    /// adapter opens it right after, so the change is visible and revertable by the editor's own
    /// file history.
    /// </remarks>
    private async Task<SlashCommandResult> RunGenerateTestsAsync(HostSession s, CancellationToken ct)
    {
        var doc = await s.Editor.GetActiveDocumentAsync(ct);
        if (doc is null || string.IsNullOrWhiteSpace(doc.Text))
            return new SlashCommandResult(true, Strings.SlashNoActiveDocument);

        var plan = await TestGenerationPlanner.PlanAsync(
            s.Client, ModelRouter.Resolve(s.Config, ModelRole.CodeActions), doc.Path, doc.Text, ct);

        if (plan.NoChange) return new SlashCommandResult(true, Strings.TestsNoChange);
        if (!plan.Ok)      return new SlashCommandResult(true, Strings.TestsGenerateFailed);

        try
        {
            var dir = Path.GetDirectoryName(plan.TestPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(plan.TestPath, plan.Content, System.Text.Encoding.UTF8, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Diagnostics.Swallow("HostServer.GenerateTests", ex);
            return new SlashCommandResult(true, Strings.TestsGenerateFailed);
        }

        return new SlashCommandResult(true,
            plan.Extended ? Strings.TestsExtended(plan.TestFileName) : Strings.TestsGenerated(plan.TestFileName),
            [new SlashEffectDto("openFile", plan.TestPath)]);
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
