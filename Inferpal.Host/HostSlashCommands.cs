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

            // /test writes to a SEPARATE file, so the adapter's in-place code-action path has
            // nothing to do with it: left to it, the literal string "/test" reaches the model, which
            // improvises. Served here, and inside the turn slot like every other slash that infers
            // — outside it, a second inference runs in parallel with a chat turn on the same GPU
            // and chat/cancel cannot stop it.
            case SlashCodeAction { Kind: SlashCodeActionKind.Test } test:
            {
                var cts = AcquireTurn(ct, saysWaits: false);
                try     { return await RunGenerateTestsAsync(s, test.Request, cts.Token); }
                finally { ReleaseTurn(cts); }
            }

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
        runner: async (task, onStep, recorder, ct) =>
        {
            // Proposal mode: the editing tools become available, against a registry whose
            // approval service records (in the queue's recorder, kept if the task is stopped) instead of granting — so the
            // run still cannot write. Same construction as the VS view-model, because the mode must not differ between editors.
            var registry = recorder is null
                ? new BackgroundTaskToolRegistry(s.Tools, currentRoot: () => s.RootDir)
                : new BackgroundTaskToolRegistry(s.Tools.WithApprovalService(recorder), recorder,
                                                 currentRoot: () => s.RootDir);

            var suffix = recorder is null
                ? BackgroundTaskToolRegistry.SystemPromptSuffix
                : BackgroundTaskToolRegistry.ProposalPromptSuffix;

            var history = new List<Inferpal.Models.ChatMessageDto>
            {
                new("system", BuildSystemPromptText(s, withMode: false) + suffix),
                new("user",   ChatTurnPolicy.WithInjectedContext(task.Objective, task.Context)),
            };

            // RunAgentAsync never throws for network/backend errors — they come back in the
            // result, flagged Failed — and TaskRunOutcome.Of turns that into a failed task with its cause.
            var run = await s.Client.RunAgentAsync(
                ModelRouter.Resolve(s.Config, ModelRole.Agent),
                history,
                registry,
                onStep:  onStep,
                onToken: null,
                ct:      ct,
                effort:  ReasoningEffortSetting.Agent(s.Config));

            return BackgroundTaskQueue.TaskRunOutcome.Of(run, recorder?.Proposals ?? []);
        },
        // Dedicated notification, not a chat/step status line: the adapter renders it as a
        // persistent bubble (VS parity) instead of a status wiped by the next setBusy.
        onFinished: snapshot => Notify("task/finished", new { text = TaskCommandHandler.FinishedNotice(snapshot) }),
        // The active file the task was submitted with: its instructions ride with the objective, as with a question.
        submissionContext: () => BuildActiveFileBlock(s));

    /// <summary>Direct tool invocation (/read /ls /grep /run /git /map …): executed by the
    /// registry — approvals and permission rules apply exactly as in an agent run.</summary>
    private async Task<SlashCommandResult> RunSlashToolAsync(HostSession s, SlashToolAction tool, CancellationToken ct)
    {
        // Excluded by the workspace's ignore files: said to the person, translated, and nothing is read.
        if (tool.Excluded(s.RootDir) is { } excluded) return new SlashCommandResult(true, excluded);
        var cts = AcquireTurn(ct);
        try
        {
            var args   = JsonSerializer.SerializeToElement(tool.Args);
            // A command the person typed is theirs: the package check judges what a model adds.
            string result;
            using (PackageCheck.ThePersonsOwnCalls())
                result = SlashToolAction.Shown(await s.Tools.ExecuteAsync(tool.Tool, args, cts.Token));

            // A read that did not happen is a message, never a chip named after the file.
            return tool.AttachesResult(s.RootDir, s.Overlay)
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
            return new SlashCommandResult(true, Strings.NoAgentStepPaused);
        }

        var parts = delegated.Parts;
        var cts   = AcquireTurn(ct);
        try
        {
            switch (delegated.Id)
            {
                case SlashCommandId.Clear:
                    await StartNewConversationAsync(s);
                    return new SlashCommandResult(true, null, [new SlashEffectDto("clearTranscript")]);

                case SlashCommandId.Model:
                    if (parts.Length < 2)
                        return new SlashCommandResult(true, Strings.SlashModelCurrent(s.Config.DefaultModel));
                    if (await ModelsCommandHandler.RefusedForChatAsync(s.Client, parts[1], cts.Token) is { } notForChat)
                        return new SlashCommandResult(true, notForChat);
                    s.Config.DefaultModel = parts[1];
                    s.Config.Save();
                    return new SlashCommandResult(true, await ModelsCommandHandler.SwitchMessageAsync(s.Client, parts[1], cts.Token),
                        [new SlashEffectDto("stateChange", parts[1], "model")]);

                case SlashCommandId.Tools:
                {
                    var result = ToolsCommandHandler.Handle(parts, s.Tools, s.Config, s.ToolsEnabled);
                    // The conversation's switch is the editor's to give back to a host started again: it is said.
                    if (result.SessionTools is { } on)
                    {
                        s.ToolsEnabled = on;
                        return new SlashCommandResult(true, result.Message,
                            [new SlashEffectDto("stateChange", on ? "on" : "off", "tools")]);
                    }
                    if (!result.SaveConfig) return new SlashCommandResult(true, result.Message);
                    await SaveAndApplyAsync(s, (s.Config.McpEnabled, s.Config.McpServersJson), cts.Token);
                    return new SlashCommandResult(true, result.Message, [new SlashEffectDto("configSaved")]);
                }

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
                    return await HandleTemplateSlashAsync(s, parts);

                case SlashCommandId.Docs:
                    return await HandleDocsSlashAsync(s, parts, cts.Token);

                case SlashCommandId.Rules:
                    return await HandleScaffoldSlashAsync(
                        RulesChecksPromptsCommandHandler.Rules(s.RootDir, parts), Strings.RulesScaffolded, cts.Token);

                case SlashCommandId.Checks:
                    return await HandleScaffoldSlashAsync(
                        RulesChecksPromptsCommandHandler.Checks(s.RootDir, parts), Strings.ChecksScaffolded, cts.Token);

                case SlashCommandId.Permissions:
                    return new SlashCommandResult(true,
                        PermissionsCommandHandler.Handle(parts, s.RootDir, s.Config, ApprovalHistory.Shared));

                case SlashCommandId.Prompts:
                {
                    var result = await HandleScaffoldSlashAsync(
                        RulesChecksPromptsCommandHandler.Prompts(s.RootDir, parts, s.Config), Strings.PromptsScaffolded, cts.Token);
                    PromptFilesService.InvalidateCache();   // show up in autocomplete immediately
                    return result;
                }

                case SlashCommandId.Diagnostics:
                {
                    // On the VS Code side there is no in-process peer: both calls return "n/a"
                    // and null, so nothing is shown. That is intended - staying quiet beats
                    // reporting the state of the Visual Studio open next door.
                    // The bundle names the backend state, as the Visual Studio one does with its status
                    // line: a report sent for "nothing answers" must say whether anything answers.
                    string? backend = null;
                    if (parts.Any(p => p.Equals("export", StringComparison.OrdinalIgnoreCase)))
                    {
                        var reachable = false;
                        try { reachable = await s.Client.CheckConnectionAsync(s.Config.BaseUrl, cts.Token); }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex) { Diagnostics.Swallow("HostSlashCommands.DiagnosticsBackend", ex); }
                        // A refused check is a running server: a report that says "unreachable" for a 401 sends
                        // whoever reads it to look for a server that is down.
                        backend = (reachable                                   ? "reachable"
                                   : s.Client.ConnectionRefusal is { } refusal ? "refused the connection check: " + refusal
                                   :                                             "unreachable")
                                  + " (" + InferenceProviderFactory.DisplayName(s.Config.Provider) + ")";
                    }

                    var result = DiagnosticsCommandHandler.Handle(parts, new DiagnosticsExportContext(
                        s.Config, "VS Code host",
                        BackendStatus: backend,
                        WorkspaceRoot: string.IsNullOrEmpty(s.RootDir) ? null : s.RootDir,
                        InProcHalf: InProcAliveSignal.DescribeForBundle(),
                        McpServers: s.Mcp.DescribeForBundle(),
                        WindowInUse: s.ContextWindowInUse),
                        InProcAliveSignal.IsLoadedOrNull());
                    return result.CopyToClipboard is { } bundle
                        ? new SlashCommandResult(true, result.Message, [new SlashEffectDto("copyToClipboard", bundle)])
                        : new SlashCommandResult(true, result.Message);
                }

                case SlashCommandId.Replay:
                    return new SlashCommandResult(true,
                        ReplayCommandHandler.Handle(s.Tools.History.Runs, parts, s.RootDir, s.Tools.History.ForgottenRuns));

                case SlashCommandId.Branch:
                    return await HandleBranchSlashAsync(s, parts, cts.Token);

                case SlashCommandId.Instructions:
                    return new SlashCommandResult(true, RepoInstructionsReport.Markdown(RepoInstructionRows(s)));

                case SlashCommandId.Modes:
                    return new SlashCommandResult(true, ModesCommandHandler.Handle(ChatModes.For(s.Config, RootOf(s))));

                case SlashCommandId.Skill:
                {
                    // The workspace read_skill_file reads, so /skill and the tool see one catalog. The skill is joined
                    // as a chip BEFORE the question is sent: the adapter sends the chips it holds with it.
                    var skill = SkillCommandHandler.Run(s.Config, s.Index.RootDir ?? (string.IsNullOrEmpty(s.RootDir) ? null : s.RootDir), parts);
                    return skill.Message is { } listing
                        ? new SlashCommandResult(true, listing)
                        : new SlashCommandResult(true, null,
                            [new SlashEffectDto("attachChip", skill.Attachment!.Content, skill.Attachment.Label),
                             new SlashEffectDto("sendAsPrompt", skill.Question)]);
                }

                case SlashCommandId.Xray:
                {
                    // The layers the NEXT question will carry — persona, template, rules of the active file,
                    // switched-off sections: built from the root alone, /xray described another prompt than the
                    // one sent, and than the panel shows.
                    var sections = BuildPromptSections(s);
                    return new SlashCommandResult(true, XRayCommandHandler.Handle(
                        sections,
                        AgentOrchestrator.EstimateConversationTokens(s.History),
                        s.ContextWindowInUse,
                        s.Config,
                        toolTokens: ContextManager.NextTurnToolTokens(s.Tools, s.ToolsEnabled, s.PlanMode, CurrentMode(s)),
                        questionLayersInHistory: QuestionLayersInHistory(s),
                        toolsOnDemand: ContextManager.NextTurnToolsOnDemand(s.Tools, s.ToolsEnabled, s.PlanMode, CurrentMode(s)),
                        turnedOff: ContextManager.NextTurnTurnedOff(s.Tools, s.ToolsEnabled, s.PlanMode, CurrentMode(s))));
                }

                case SlashCommandId.Bench:
                {
                    // Long-running (a full micro-eval suite per model). Progress is surfaced through
                    // the same chat/step notifications the agent loop uses.
                    var result = await BenchCommandHandler.HandleAsync(
                        s.Client, s.Config, parts, Step, cts.Token);
                    return new SlashCommandResult(true, result.Message);
                }

                case SlashCommandId.Arena:
                {
                    // Two sequential inference calls; progress goes through the same chat/step
                    // notifications as /bench.
                    var result = await ArenaCommandHandler.HandleAsync(
                        s.Client, s.Config, parts, Step, cts.Token,
                        shown: s.Arena);
                    return new SlashCommandResult(true, result.Message);
                }

                case SlashCommandId.Tdd:
                {
                    // "Fix until green" loop. What Visual Studio keeps, this keeps: each test run as a
                    // run_tests bubble (a status line is overwritten by the next step), and each fix
                    // explanation — gathered ahead of the verdict, there being no assistant bubble to
                    // stream into while a command runs.
                    var fixes  = new List<string>();
                    var activeFile = BuildActiveFileBlock(s);
                    var result = await TddCommandHandler.HandleAsync(
                        s.Client, s.Config, s.Tools,
                        BuildSystemPromptText(s, withMode: false), parts,
                        string.IsNullOrEmpty(s.RootDir) ? null : s.RootDir,
                        onProgress:   Step,
                        onTestReport: (output, green) => Notify("chat/tool", new ToolNotice("run_tests", string.Empty, output, !green)),
                        onStep:       Step,
                        onToken:      null,
                        onFixResult:  text =>
                        {
                            var visible = Inferpal.Services.Presentation.MarkdownParser.StripThinkTags(text);
                            if (Inferpal.Services.Presentation.MarkdownParser.HasPrintableText(visible))
                                lock (fixes) fixes.Add(visible);
                        },
                        cts.Token,
                        s.TestCapture, s.Approval,
                        activeFileContext: activeFile);
                    lock (fixes)
                        return new SlashCommandResult(true, fixes.Count == 0
                            ? result.Message
                            : string.Join("\n\n---\n\n", fixes.Append(result.Message)));
                }

                case SlashCommandId.TestCoverage:
                    return new SlashCommandResult(true, await TestCoverageCommandHandler.HandleAsync(
                        s.Client, s.Config, s.Tools, s.Tools.History, parts, s.RootDir,
                        onProgress: Step, cts.Token));

                case SlashCommandId.Task:
                {
                    // Detached run: deliberately NOT bound to this turn's token — the task keeps
                    // going after the command that submitted it returns. Progress is not streamed
                    // to the chat (it would interrupt what the user is doing); only the completion
                    // notice is, and `/task <id>` shows the report.
                    var task = TaskCommandHandler.Handle(TaskQueue(s), parts);

                    // `/task apply <id> <n>`: the write goes through the real registry, so the
                    // ordinary approval prompt appears and /undo-run covers it).
                    if (task.Apply is { } proposal)
                        try
                        {
                            return new SlashCommandResult(true,
                                await TaskProposalApplication.ApplyAsync(
                                    proposal, s.Tools, TaskProposalApplication.ReadCurrent, cts.Token,
                                    beginRun: path => s.Tools.History.BeginRun(alreadyRead: path)));
                        }
                        finally
                        {
                            // As on the VS side: what is written after the application no longer
                            // belongs to its run, otherwise /undo-run reverts it along with it.
                            s.Tools.History.EndRun();
                        }

                    return new SlashCommandResult(true, task.Message);
                }

                case SlashCommandId.AgentStep:
                    // Same texts as the VS toggle — and localized now. The previous comment read
                    // "deliberately English, model-adjacent UX": the `/plan` next door, three lines
                    // below, returns Strings.PlanModeOn in all ten languages. Two mode-toggle
                    // messages, addressed to the user, in the same switch: the justification was
                    // describing an oversight.
                    s.StepMode = !s.StepMode;
                    return new SlashCommandResult(true,
                        s.StepMode ? Strings.StepModeOn : Strings.StepModeOff,
                        [new SlashEffectDto("stateChange", s.StepMode ? "on" : "off", "stepMode")]);

                case SlashCommandId.Plan:
                {
                    // Same handler as the VM: bare `/plan` toggles read-only plan mode, everything
                    // else works on a file under .inferpal/plans/.
                    var result = PlanCommandHandler.Handle(
                        s.RootDir, parts,
                        s.History.LastOrDefault(m => m.Role == "assistant")?.Content,
                        s.ActivePlan);

                    if (result.ToggleMode)
                    {
                        s.PlanMode = !s.PlanMode;
                        if (s.PlanMode) s.ModeName = null;   // one selector: Plan leaves the mode
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
                    // Same handler as the VM, so the anchored findings are identical on both
                    // sides. Progress uses the chat/step channel like /bench and /tdd.
                    var result = await CheckCommandHandler.HandleAsync(
                        s.Client, s.Config, s.RootDir ?? Directory.GetCurrentDirectory(), parts,
                        git: GitProcess.For(s.RootDir),
                        onProgress: Step,
                        cts.Token);

                    // `init` writes through the same helper as /rules|/checks|/prompts init.
                    if (result.Scaffold is { } scaffold)
                        return await HandleScaffoldSlashAsync(
                            new RulesChecksPromptsCommandHandler.CommandListResult(null, scaffold),
                            Strings.ChecksScaffolded, cts.Token);

                    return new SlashCommandResult(true, result.Message);
                }

                case SlashCommandId.Pr:
                {
                    // Same handler as the VM: the range, the notices and the block to copy are identical everywhere.
                    var message = await PrCommandHandler.HandleAsync(
                        s.Client, s.Config, parts, GitProcess.For(s.RootDir),
                        onProgress: Step, cts.Token);
                    return new SlashCommandResult(true, message);
                }

                case SlashCommandId.Import:
                {
                    // The Core previews or applies; the host saves what it changed and hands the card to the editor
                    // (its Markdown is what the agent protocol, which draws no card, shows).
                    var mcpBefore = (s.Config.McpEnabled, s.Config.McpServersJson);
                    var result = ImportCommandHandler.Handle(parts, Inferpal.Services.Import.ImportContext.For(s.Config, s.RootDir),
                                                             s.Config.SaveFilePath, DateTime.Now);
                    if (result.SaveConfig) await SaveAndApplyAsync(s, mcpBefore, cts.Token);
                    List<SlashEffectDto> saved = result.SaveConfig ? [new SlashEffectDto("configSaved")] : [];
                    // Modes it wrote show in the editor's mode selector at once.
                    if (result.ModesWritten) saved.Add(new SlashEffectDto("modesChanged"));
                    return new SlashCommandResult(true, result.Message, saved, result.Card);
                }

                case SlashCommandId.Onboard:
                {
                    // The profile itself is applied (index exclusions) or merely reported by the
                    // Core; the host only performs the IO it decided on.
                    var mcpBefore = (s.Config.McpEnabled, s.Config.McpServersJson);
                    var result = await OnboardCommandHandler.HandleAsync(
                        s.Client, s.Config, s.RootDir ?? Directory.GetCurrentDirectory(), parts,
                        git: GitProcess.For(s.RootDir),
                        onProgress: Step,
                        cts.Token);

                    if (result.Scaffold is { } scaffold)
                        return await HandleScaffoldSlashAsync(
                            new RulesChecksPromptsCommandHandler.CommandListResult(null, scaffold),
                            Strings.OnboardProfileScaffolded, cts.Token);

                    if (result.SaveConfig) await SaveAndApplyAsync(s, mcpBefore, cts.Token);
                    // ⚠ The extension keeps its own copy of some of these settings (the utility model) and pushes it back
                    // at every host start: told nothing, it undoes the applied profile at the next restart, in silence.
                    List<SlashEffectDto> saved = result.SaveConfig ? [new SlashEffectDto("configSaved")] : [];

                    if (result.NewDefaultModel is { } model)
                        return new SlashCommandResult(true, result.Message,
                            [new SlashEffectDto("stateChange", model, "model"), .. saved]);

                    if (result.Write is { } write)
                    {
                        // Shared with the VS window: backup of the replaced version, encoding-preserving write.
                        var outcome = await Inferpal.Services.Execution.BackedUpFileWriter.WriteAsync(
                            write.Path, write.Content, s.Tools.History, cts.Token);
                        if (!outcome.Written)
                            return new SlashCommandResult(true, Strings.FileNotReplacedNoBackup(write.Path));
                        if (result.RefreshSystemPrompt) RefreshSystemMessage(s);
                        var message = outcome.Snapshot.Length > 0
                            ? result.Message + "\n\n" + Strings.FilePreviousVersionSaved(write.Path)
                            : result.Message;
                        return new SlashCommandResult(true, message,
                            [new SlashEffectDto("openFile", write.Path)]);
                    }

                    return new SlashCommandResult(true, result.Message, saved);
                }

                case SlashCommandId.Debug:
                {
                    // The handler never drives the debugger itself: it either reports, or hands the
                    // model an instruction that goes through debug_control / debug_inspect — and
                    // therefore through the approval on the one action that executes the user's
                    // program.
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
                    Step(Strings.CommitProposingLabel);
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
                        message, GitProcess.ForCommit(s.RootDir), cts.Token);

                    return new SlashCommandResult(true,
                        (run.Ok ? "✅ `git commit`" : "❌ `git commit`") + "\n\n```\n" + run.Output + "\n```");
                }

                // The settings are the front-end's to open: VS Code shows its panel, an ACP thread the browser window.
                case SlashCommandId.Settings:
                    return new SlashCommandResult(true, null, [new SlashEffectDto("openSettings")]);

                // The first-run card is the editor's to draw (VS Code); a client without one (ACP) has its own setup.
                case SlashCommandId.Setup when s.Editor.HasEditor:
                    return new SlashCommandResult(true, null, [new SlashEffectDto("openSetup")]);

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

    /// <summary>
    /// <c>/branch</c> — decided on the adapter's displayed transcript, never on the host's history,
    /// where a question carries the RAG auto-context and compaction renumbers turns: the listing
    /// previewed context blocks and <c>/branch 3</c> could fork another turn than the one listed. The
    /// command hands its arguments back as a <c>branchCommand</c> effect; the adapter answers through
    /// <c>session/branchCommand</c> with its transcript, then forks or switches as decided.
    /// </summary>
    private static Task<SlashCommandResult> HandleBranchSlashAsync(
        HostSession s, string[] parts, CancellationToken ct) =>
        Task.FromResult(new SlashCommandResult(true, null,
            [new SlashEffectDto("branchCommand", string.Join(" ", parts.Skip(1)))]));


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
            if (!ok) return new SlashCommandResult(true, Strings.ModelsPullFailed(model));

            // The first model a fresh install gets: the default nobody chose is replaced now, not at the next start.
            var adopted = await AdoptDefaultModelAsync(s, ct);
            return adopted is { Model: { } m, Notice: { } notice }
                ? new SlashCommandResult(true, Strings.ModelsPulled(model) + "\n\n" + notice, [new SlashEffectDto("stateChange", m, "model")])
                : new SlashCommandResult(true, Strings.ModelsPulled(model));
        }

        var result = await ModelsCommandHandler.HandleAsync(s.Client, s.Config, parts, ct);
        return new SlashCommandResult(true, result.Message);
    }

    /// <summary>/template [id] — shared decision (TemplateCommandHandler); the host applies it
    /// by resetting its history and asking the adapter to clear the transcript.</summary>
    private async Task<SlashCommandResult> HandleTemplateSlashAsync(HostSession s, string[] parts)
    {
        var result = TemplateCommandHandler.Handle(parts);
        if (result.Apply is not { } tmpl)
            return new SlashCommandResult(true, result.Message);

        await StartNewConversationAsync(s, tmpl.SystemSuffix);
        return new SlashCommandResult(true, tmpl.Greeting, [new SlashEffectDto("clearTranscript")]);
    }

    /// <summary>/docs add|list|remove|reindex — external documentation sources, through the shared handler; crawls
    /// run in the background, their progress surfaced through the same chat/step notifications the agent loop uses.</summary>
    private async Task<SlashCommandResult> HandleDocsSlashAsync(HostSession s, string[] parts, CancellationToken ct)
    {
        // The crawl outlives the command (detached): its progress is said, never kept as a turn's step.
        var progress = new Progress<string>(msg => Notify("chat/step", new { text = msg }));
        return new SlashCommandResult(true,
            await DocsCommandHandler.HandleAsync(s.Config, s.Docs, parts, progress, ct));
    }

    /// <summary>
    /// `tests/generate` — the tests of the code the editor sends (its selection, or its whole file), planned against the
    /// test file as the editor holds it, unsaved tests included. Nothing is written here: the editor writes them by an edit
    /// it can undo — an existing test file replaced in its buffer, a new one created — as Visual Studio does.
    /// </summary>
    [JsonRpcMethod("tests/generate", UseSingleObjectParameterDeserialization = true)]
    public async Task<TestsGenerateResult> TestsGenerateAsync(TestsGenerateParams p, CancellationToken ct)
    {
        var s = Session();
        if (string.IsNullOrWhiteSpace(p.Code)) return new(Strings.SlashNoActiveDocument);

        // Inside the turn slot, like every slash that infers: Stop reaches it, and it never runs beside a chat turn.
        var cts = AcquireTurn(ct);
        TestGenerationPlan plan;
        try
        {
            plan = await TestGenerationPlanner.PlanAsync(
                s.Client, ModelRouter.Resolve(s.Config, ModelRole.CodeActions), p.Path, p.Code, cts.Token,
                openText: (path, _) => Task.FromResult(s.Overlay.TryGet(path, out var text) ? text : null),
                request: p.Request);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new(Message: null, Cancelled: true);
        }
        finally { ReleaseTurn(cts); }

        if (TestsNotWritten(plan) is { } why) return new(why);
        return new(TestsWritten(plan), plan.TestPath, plan.Content, plan.Extended, Extends: plan.Extends);
    }

    /// <summary>Why a plan writes no tests, or <c>null</c> when it writes them — one sentence per outcome for both
    /// <c>/test</c> paths.</summary>
    private static string? TestsNotWritten(TestGenerationPlan plan) =>
        plan.NoChange ? Strings.TestsNoChange
        // ⚠ Before the generic refusal: "the file exists and I could not read it" is the one outcome where carrying on
        // overwrites tests, and it names the file to free.
        : plan.Unreadable ? Strings.TestsFileUnreadable(plan.TestFileName)
        : plan.Refusal is { } why ? why
        : !plan.Ok ? Strings.TestsGenerateFailed
        : null;

    /// <summary>What a plan that writes its tests says once they are written.</summary>
    private static string TestsWritten(TestGenerationPlan plan) =>
        plan.Extended ? Strings.TestsExtended(plan.TestFileName) : Strings.TestsGenerated(plan.TestFileName);

    /// <summary>
    /// <c>/test</c> — generate unit tests for the active document into the conventional test file
    /// beside it, then ask the adapter to open it.
    /// </summary>
    /// <remarks>
    /// The path of an editor that hands over no selection and cannot write by an undoable edit (VS Code takes
    /// <c>tests/generate</c>): the whole file is the input, and an existing test file is rewritten on disk.
    /// ⚠ The model returns the WHOLE file, so a test it drops is gone — and the editor's own history
    /// does not cover a file written behind its back. The replaced version is backed up first, and
    /// the reply names the <c>/restore</c> command that brings it back.
    /// </remarks>
    /// <param name="request">What the user typed after <c>/test</c>, given to the model first.</param>
    private async Task<SlashCommandResult> RunGenerateTestsAsync(HostSession s, string? request, CancellationToken ct)
    {
        var doc = await s.Editor.GetActiveDocumentAsync(ct);
        if (doc is null || string.IsNullOrWhiteSpace(doc.Text))
            return new SlashCommandResult(true, Strings.SlashNoActiveDocument);

        // Before the model: the file is written on disk, behind a buffer whose unsaved tests it would not contain.
        if (TestGenerationPlanner.UnsavedTestFile(s.Overlay, doc.Path) is { } unsaved)
            return new SlashCommandResult(true, unsaved);

        var plan = await TestGenerationPlanner.PlanAsync(
            s.Client, ModelRouter.Resolve(s.Config, ModelRole.CodeActions), doc.Path, doc.Text, ct, request: request);

        if (TestsNotWritten(plan) is { } why) return new SlashCommandResult(true, why);

        Inferpal.Services.Execution.BackedUpFileWriter.Outcome outcome;
        try
        {
            outcome = await Inferpal.Services.Execution.BackedUpFileWriter.WriteAsync(
                plan.TestPath, plan.Content, s.Tools.History, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Diagnostics.Swallow("HostServer.GenerateTests", ex);
            return new SlashCommandResult(true, Strings.TestsGenerateFailed);
        }

        if (!outcome.Written)
            return new SlashCommandResult(true, Strings.FileNotReplacedNoBackup(plan.TestPath));

        var message = TestsWritten(plan);
        if (outcome.Snapshot.Length > 0)
            message += "\n\n" + Strings.FilePreviousVersionSaved(plan.TestPath);
        return new SlashCommandResult(true, message, [new SlashEffectDto("openFile", plan.TestPath)]);
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
            .Where(c => c.Editors != MentionEditors.VisualStudio)   // @output reads Visual Studio's Output window
            .Select(c => new MentionCategoryDto(c.Token, c.Desc(), c.QueryBased))
            .ToList();

    /// <summary>Sub-search of @file / @folder under the workspace root (fuzzy, best 8), and of @commit in its repository
    /// (the latest commits, valued by their full object name).</summary>
    [JsonRpcMethod("mention/search", UseSingleObjectParameterDeserialization = true)]
    public async Task<List<MentionItemDto>> MentionSearchAsync(MentionSearchParams p, CancellationToken ct)
    {
        var s = Session();
        if (string.IsNullOrEmpty(s.RootDir))
            return [];

        if (p.Category.Equals("commit", StringComparison.OrdinalIgnoreCase))
            return (await MentionController.FindCommitsAsync(s.RootDir, p.Query, ct))
                .Select(c => new MentionItemDto(c.Short, c.Subject, c.Sha))
                .ToList();

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

    /// <summary>
    /// The chips of the draft the person is typing — Visual Studio's 🔮 chips, from the same search
    /// (<see cref="Services.Rag.RagAutoContext.WhileTypingAsync"/>). Not a turn: it holds no slot, and the adapter cancels it when the
    /// person types on. Best-effort: a search that fails gives no chip, traced.
    /// </summary>
    [JsonRpcMethod("rag/typingChips", UseSingleObjectParameterDeserialization = true)]
    public async Task<List<TypingChipDto>> RagTypingChipsAsync(TypingChipsParams p, CancellationToken ct)
    {
        var s = Session();
        try
        {
            var chips = await Services.Rag.RagAutoContext.WhileTypingAsync(s.Index, s.Config, p.Text ?? string.Empty, ct);
            return chips.Select(c => new TypingChipDto(c.Path, Path.GetFileName(c.Path), c.Lines, c.Content)).ToList();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Diagnostics.Swallow("HostServer.TypingChips", ex);
            return [];
        }
    }

    /// <summary>Materializes a mention host-side: @tree (project map), @diff (git diff),
    /// @folder (folder context body), @code (semantic search), @url (a web page) and @commit (one commit). Tool-backed categories read their
    /// tool's state, never its text: what found or mapped nothing is a notice, not a chip.</summary>
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
                    // Decided on whether there was a root to map: none is a notice, not a chip.
                    var tree = MentionController.TreeMention(await s.Tools.ProjectMap.TryGenerateMapAsync(ct), "🌲 tree");
                    return new MentionResolveResult(tree.Label, tree.Content, tree.Notice);
                }
                case "diff":
                {
                    // Decided on the report's state: no repository, git refusing or no change is a notice, not a chip.
                    var report = await s.Tools.GitStatus.ReportAsync(null, includeDiff: true, diffPath: null, ct);
                    var diff   = MentionController.DiffMention(report, "📊 git diff");
                    return new MentionResolveResult(diff.Label, diff.Content, diff.Notice);
                }
                case "debugger":
                {
                    // Served here rather than by the adapter: the VS Code side answered from
                    // `vscode.debug.activeDebugSession` and attached the session's NAME AND TYPE —
                    // where Visual Studio attaches the stop reason, the call stack and the locals,
                    // and where docs/mentions.md promises "the live debugger break state (via
                    // get_debugger_state)" for both editors. Same reader as the tool, so the two
                    // cannot drift apart again.
                    var state = await Services.Debugging.DebuggerStateReader.TryReadAsync(s.Debug, s.RootDir, ct);
                    return state is null
                        ? new MentionResolveResult(null, null, Strings.MentionDebuggerNone)
                        : new MentionResolveResult("🐞 @debugger", state);
                }
                case "folder" when !string.IsNullOrEmpty(p.Value):
                {
                    // A notice, never a chip, when there is no folder to walk (MentionController.FolderRefusal).
                    var folder = MentionController.FolderPath(p.Value!, s.RootDir);
                    if (MentionController.FolderRefusal(folder, s.RootDir) is { } why) return new MentionResolveResult(null, null, why);
                    var content = MentionController.BuildFolderContext(folder, ct, s.Overlay);
                    return new MentionResolveResult("📁 " + Path.GetFileName(folder.TrimEnd('\\', '/')), content);
                }
                case "code" when !string.IsNullOrWhiteSpace(p.Value):
                {
                    if (MentionController.CodeRefusal(s.Index) is { } noIndex) return new MentionResolveResult(null, null, noIndex);
                    // Decided on the hit count: a search that found nothing is a notice, not a chip.
                    var code = MentionController.CodeMention(await s.Tools.CodeSearch.AnswerAsync(
                        JsonSerializer.SerializeToElement(new { query = p.Value }), ct), "🔮 " + p.Value);
                    return new MentionResolveResult(code.Label, code.Content, code.Notice);
                }
                case "url":
                {
                    var url = await MentionController.UrlMentionAsync(p.Value ?? "", s.Tools.PersonFetchRefusal, ct);
                    return new MentionResolveResult(url.Label, url.Content, url.Notice);
                }
                case "commit":
                {
                    // An empty value is the last commit: the typed line offers HEAD before anything is typed.
                    var commit = await MentionController.CommitMentionAsync(s.RootDir, p.Value ?? "", ct);
                    return new MentionResolveResult(commit.Label, commit.Content, commit.Notice);
                }
                // ⚠ Nulls all the way down are what the adapter shows as NOTHING AT ALL: the
                // mention the user typed leaves no chip and no message, which reads as a broken
                // product. The `debugger` case above was given a Notice for exactly that reason and
                // the reasoning stopped at its own branch — an incomplete mention (`@folder` with
                // no path, `@code` with a blank query) and a category this host does not serve fell
                // through to here, silently, and so did every failure below.
                default:
                    return new MentionResolveResult(null, null, Strings.MentionNothingToAttach(p.Category));
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Diagnostics.Swallow("HostServer.MentionResolve", ex);
            // The same sentence the Visual Studio window writes for the same failure: one mention,
            // two front-ends, one answer.
            return new MentionResolveResult(null, null, Strings.AttachError(Diagnostics.RootMessage(ex)));
        }
    }
}
