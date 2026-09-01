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
    #region Historique de prompt & commandes (contexte, fix-build, git, rules)

    // ── Prompt history navigation ──────────────────────────────────────────────

    private void UpdateHistoryCommandState()
    {
        HistoryUpCommand.CanExecute   = _promptHistory.CanUp;
        HistoryDownCommand.CanExecute = _promptHistory.CanDown;
    }

    private void LoadPromptHistory()
    {
        try
        {
            if (!File.Exists(_promptHistoryFile)) return;
            var json = File.ReadAllText(_promptHistoryFile, System.Text.Encoding.UTF8);
            _promptHistory.Load(JsonSerializer.Deserialize<List<string>>(json) ?? []);
        }
        catch (Exception ex) { Diagnostics.Swallow("PromptHistory.Load", ex); }
    }

    private void SavePromptHistory()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_promptHistoryFile)!);
            File.WriteAllText(_promptHistoryFile,
                JsonSerializer.Serialize(_promptHistory.Entries),
                System.Text.Encoding.UTF8);
        }
        catch (Exception ex) { Diagnostics.Swallow("PromptHistory.Save", ex); }
    }

    private Task HistoryUpAsync(object? _, CancellationToken ct)
    {
        if (!_promptHistory.CanUp) return Task.CompletedTask;
        _navigatingHistory = true;
        Prompt = _promptHistory.Up(_prompt); // stashes the live draft on the first step
        _navigatingHistory = false;
        return Task.CompletedTask;
    }

    private Task HistoryDownAsync(object? _, CancellationToken ct)
    {
        if (!_promptHistory.CanDown) return Task.CompletedTask;
        _navigatingHistory = true;
        Prompt = _promptHistory.Down(_prompt); // restores the draft when stepping past the newest entry
        _navigatingHistory = false;
        return Task.CompletedTask;
    }

    private async Task HandleContextCommandAsync(CancellationToken ct)
    {
        var dir = FindProjectRoot();
        // VS-specific pre-check: without a solution the root is meaningless here.
        if (Directory.GetFiles(dir, "*.sln", SearchOption.TopDirectoryOnly).Length == 0)
        {
            await ShowInfoAsync(Strings.SlashContextNoSln);
            return;
        }

        await ShowInfoAsync(await Services.Commands.ProjectFileCommandHandler.HandleAsync(
            dir, "context.md", Strings.SlashContextNotFound, Strings.SlashContextLoaded, ct));
    }

    // /branch          → branch points of this conversation + the family tree
    // /branch <n>       → fork at turn n (the conversation continues in the branch)
    // /branch <name>    → switch to an existing session/branch
    private async Task HandleBranchCommandAsync(string[] parts, CancellationToken ct)
    {
        // Uniform with the other two callers of RestoreConversation. Unreachable during a turn
        // today (SendAsync turns Enter into a cancel while IsLoading), but the invariant belongs
        // with the operation, not with the current routing of one keystroke.
        await SettleCurrentTurnAsync();

        List<SavedMessage> snapshot = [];
        var currentName = string.Empty;
        await RunOnVMContextAsync(() =>
        {
            snapshot    = SessionManager.BuildSnapshot(
                Messages.Select(m => (m.Role, m.Content, m.ToolName, m.Timestamp)));
            currentName = _currentSessionName;
        });

        var sessions = await _store.ListWithPreviewAsync(ct);
        var result   = Services.Commands.BranchCommandHandler.Handle(parts, snapshot, currentName, sessions);

        if (result.Message is { } message)
        {
            await ShowInfoAsync(message);
            return;
        }

        // ── Switch to an existing branch ──────────────────────────────────────
        if (result.SwitchTo is { } target)
        {
            var session = await _store.LoadAsync(target, ct);
            if (session is null) { await ShowInfoAsync(Strings.BranchUnknown(target)); return; }

            await RunOnVMContextAsync(() =>
            {
                RestoreConversation(session.Messages, target);
                RefreshSessionsList();
                ScrollToBottom();
            });
            await ShowInfoAsync(Strings.BranchSwitched(target));
            return;
        }

        // ── Fork at a turn ────────────────────────────────────────────────────
        var plan = BranchManager.Plan(snapshot, result.ForkTurn!.Value, currentName, sessions, DateTime.Now);
        if (plan is null) { await ShowInfoAsync(Strings.BranchNoConversation); return; }

        // The parent goes to disk first — with the conversation as it stands now, which may have
        // moved on since it was loaded: forking must never be what loses the discarded half.
        await _store.SaveAsync(plan.ParentName, plan.ParentMessages, ct,
                               parent: plan.ParentParent, forkTurn: plan.ParentForkTurn);

        await _store.SaveAsync(plan.BranchName, plan.BranchMessages, ct,
                               parent: plan.ParentName, forkTurn: plan.ForkTurn);

        await RunOnVMContextAsync(() =>
        {
            RestoreConversation(plan.BranchMessages, plan.BranchName);
            RefreshSessionsList();
            ScrollToBottom();
        });
        await ShowInfoAsync(Strings.BranchCreated(plan.BranchName, plan.ForkTurn, plan.ParentName));
    }

    // Logique partagée avec le Host (HistoryCommandHandler) — la VM n'apporte que la bulle.
    private async Task HandleHistoryCommandAsync(string[] parts, CancellationToken ct) =>
        await ShowInfoAsync(await Services.Commands.HistoryCommandHandler.HandleAsync(
            _store, parts, DateTime.UtcNow, ct));

    // /undo-run [list] — logique partagée avec le Host (UndoRunCommandHandler).
    private async Task HandleUndoRunCommandAsync(string[] parts, CancellationToken ct) =>
        await ShowInfoAsync(await Services.Commands.UndoRunCommandHandler.HandleAsync(
            _tools.History, parts, FindProjectRoot(), ct));

    private async Task HandleMemoryCommandAsync(CancellationToken ct) =>
        await ShowInfoAsync(await Services.Commands.ProjectFileCommandHandler.HandleAsync(
            FindProjectRoot(), "memory.md", Strings.SlashMemoryNotFound, Strings.SlashMemoryLoaded, ct));

    // ── Fix-build loop ─────────────────────────────────────────────────────────

    private async Task HandleFixBuildCommandAsync(string[] parts, CancellationToken ct)
    {
        const int MaxRounds = 5;

        CancellationTokenSource? localCts = null;
        await RunOnVMContextAsync(() =>
        {
            localCts    = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _currentCts = localCts;
            IsLoading   = true;
        });
        if (localCts is null) return;
        var tok = localCts.Token;

        try
        {
            // Resolve project path once — bypasses CWD bug in GetDiagnosticsTool
            string? slnPath = null;
            if (parts.Length >= 2)
            {
                slnPath = string.Join(" ", parts[1..]);
            }
            else
            {
                var root = FindProjectRoot();
                slnPath = Directory.GetFiles(root, "*.sln", SearchOption.TopDirectoryOnly).FirstOrDefault()
                       ?? Directory.GetFiles(root, "*.csproj", SearchOption.TopDirectoryOnly).FirstOrDefault();
            }

            var diagArgsJson = slnPath is not null
                ? JsonSerializer.Serialize(new { path = slnPath })
                : "{}";

            for (int round = 1; round <= MaxRounds; round++)
            {
                tok.ThrowIfCancellationRequested();
                await RunOnVMContextAsync(() => CurrentStep = $"🔨 Build {round}/{MaxRounds}…");

                // ── Build ──────────────────────────────────────────────────────
                string buildOutput;
                try
                {
                    var argsElem = JsonDocument.Parse(diagArgsJson).RootElement.Clone();
                    buildOutput  = await _tools.ExecuteAsync(GetDiagnosticsTool.ToolName, argsElem, tok);
                }
                catch (Exception ex) { buildOutput = Strings.MsgError(ex.Message); }

                bool hasErrors = GetDiagnosticsTool.OutputHasBuildErrors(buildOutput);

                await RunOnVMContextAsync(() =>
                {
                    var label    = hasErrors ? "❌ get_diagnostics" : "✅ get_diagnostics";
                    var diagItem = ChatMessageItem.ToolMsg(label, buildOutput, expanded: true);
                    ApplyItemTheme(diagItem);
                    if (hasErrors)
                        diagItem.InitFixCallback(buildOutput,
                            rawErrors => Post(() => Prompt = BuildFixPrompt(rawErrors)));
                    Messages.Insert(Messages.Count - 2, diagItem);
                    ScrollToBottom();
                });

                // ── Success ────────────────────────────────────────────────────
                if (!hasErrors)
                {
                    await ShowInfoAsync(Strings.FixBuildSuccess(round));
                    return;
                }

                // ── Give up ────────────────────────────────────────────────────
                if (round == MaxRounds)
                {
                    await ShowInfoAsync(Strings.FixBuildGiveUp(MaxRounds));
                    return;
                }

                // ── Fix iteration ──────────────────────────────────────────────
                tok.ThrowIfCancellationRequested();
                await RunFixIterationAsync(buildOutput, round, tok);
            }
        }
        catch (OperationCanceledException)
        {
            await RunOnVMContextAsync(() =>
            {
                InsertThemed(ChatMessageItem.AssistantMsg(Strings.MsgCancelled));
                ScrollToBottom();
            });
        }
        catch (Exception ex)
        {
            var msg = ex.Message;
            await RunOnVMContextAsync(() =>
            {
                InsertThemed(ChatMessageItem.AssistantMsg(Strings.MsgError(msg)));
                ScrollToBottom();
            });
        }
        finally
        {
            await RunOnVMContextAsync(() =>
            {
                localCts?.Dispose();
                _currentCts = null;
                IsLoading   = false;
                CurrentStep = string.Empty;
            });
        }
    }

    private async Task RunFixIterationAsync(string buildOutput, int round, CancellationToken ct)
    {
        var fixHistory = new List<ChatMessageDto>
        {
            _history[0],                          // system prompt (context + memory)
            new("user", BuildFixPrompt(buildOutput))
        };

        ChatMessageItem? streamItem = null;

        await RunOnVMContextAsync(() =>
        {
            CurrentStep = $"🔧 Fix {round}…";
            streamItem  = ChatMessageItem.StreamingMsg();
            streamItem.Label = $"🔧 Fix {round}";
            ApplyItemTheme(streamItem);
            Messages.Insert(Messages.Count - 2, streamItem);
            ScrollToBottom();
        });

        AgentResult result;
        try
        {
            using var sink = new ThrottledTokenSink(chunk => Post(() => { if (streamItem is not null) streamItem.Content += chunk; }));
            result = await _client.RunAgentAsync(
                model:   _config.DefaultModel,
                history: fixHistory,
                tools:   _tools,
                onStep:  step  => Post(() => CurrentStep = step),
                onToken: token => sink.Append(token),
                ct:      ct);
            sink.Stop();
        }
        catch
        {
            // streamItem was inserted before streaming started — discard it if empty/invisible
            // so it doesn't leave an orphaned streaming bubble in the chat.
            await RunOnVMContextAsync(() => streamItem = FinalizeStreamingBubble(streamItem));
            throw; // re-throw so RunSmartFixAsync's catch handles user messaging
        }

        await RunOnVMContextAsync(() =>
        {
            var insertIdx = streamItem is not null
                ? Messages.IndexOf(streamItem)
                : Messages.Count - 2;

            foreach (var exec in result.Executions)
            {
                var preview  = exec.Output.Length > 500
                    ? exec.Output[..500] + Strings.MsgTruncated
                    : exec.Output;
                var toolItem = ChatMessageItem.ToolMsg(
                    exec.Name, Strings.MsgToolOutput(exec.Input, preview), _config.ToolBubblesExpanded);
                ApplyItemTheme(toolItem);
                Messages.Insert(insertIdx++, toolItem);
            }

            streamItem = FinalizeStreamingBubble(streamItem);

            if (streamItem is null)
            {
                var visibleFinal = Services.Presentation.MarkdownParser.StripThinkTags(result.FinalResponse);
                if (Services.Presentation.MarkdownParser.HasPrintableText(visibleFinal))
                {
                    var msg = ChatMessageItem.AssistantMsg(visibleFinal);
                    ApplyItemTheme(msg);
                    Messages.Insert(Messages.Count - 2, msg);
                }
            }

            ScrollToBottom();
        });
    }

    // ── Git commit assistant ───────────────────────────────────────────────────

    /// <summary>
    /// <c>/commit</c> — proposes a message for the current change and pre-fills
    /// <c>/commit-exec</c> with it. Nothing is committed here.
    /// </summary>
    /// <remarks>
    /// The decisions (which diff, which model, how the proposal is cleaned) live in
    /// <see cref="Services.Commands.CommitCommandHandler"/> so the Host proposes the same message;
    /// the VM adds the streaming bubble and the prompt box.
    /// </remarks>
    private async Task HandleCommitCommandAsync(CancellationToken ct)
    {
        ChatMessageItem? streamItem = null;
        await RunOnVMContextAsync(() =>
        {
            streamItem       = ChatMessageItem.StreamingMsg();
            streamItem.Label = Strings.CommitProposingLabel;
            ApplyItemTheme(streamItem);
            Messages.Insert(Messages.Count - 2, streamItem);
            IsLoading   = true;
            CurrentStep = Strings.StatusThinking;
            ScrollToBottom();
        });

        using var sink = new ThrottledTokenSink(
            chunk => Post(() => { if (streamItem is not null) streamItem.Content += chunk; }));

        Services.Commands.CommitCommandHandler.CommitProposal proposal;
        try
        {
            proposal = await Services.Commands.CommitCommandHandler.ProposeAsync(
                _client, _config, Services.GitProcess.For(FindProjectRoot()),
                onToken: token => sink.Append(token), ct);
        }
        catch (OperationCanceledException)
        {
            await RunOnVMContextAsync(() =>
            {
                streamItem  = FinalizeStreamingBubble(streamItem);
                IsLoading   = false;
                CurrentStep = string.Empty;
            });
            return;
        }
        finally { sink.Stop(); }

        await RunOnVMContextAsync(() =>
        {
            streamItem  = FinalizeStreamingBubble(streamItem);
            IsLoading   = false;
            CurrentStep = string.Empty;

            if (proposal.Notice is { } notice)
                InsertThemed(ChatMessageItem.AssistantMsg(notice));

            if (proposal.Message is { } message)
                InsertThemed(ChatMessageItem.AssistantMsg(message));

            if (proposal.Proposal is { } proposed)
            {
                Prompt = $"/commit-exec {proposed}";
                var hint = ChatMessageItem.AssistantMsg(Strings.CommitConfirmHint);
                ApplyItemTheme(hint);
                Messages.Insert(Messages.Count - 2, hint);
            }
            ScrollToBottom();
        });
    }

    // ── Rules & Checks (.inferpal/rules, .inferpal/checks) ─────────────────

    // AI-reviews the current git diff against .inferpal/checks. /check init scaffolds an example;
    // /check <name> runs a single check. 100% local — no diff leaves the machine.
    /// <summary>
    /// <c>/check</c> — the whole flow lives in <see cref="Services.Commands.CheckCommandHandler"/>
    /// so the Host serves the same command (roadmap §15); the VM only supplies git and the UI.
    /// </summary>
    /// <remarks>
    /// The answer is no longer streamed token by token: findings are anchored to the diff once the
    /// review is complete, and a half-parsed location is worse than a slightly later one. The
    /// status line carries the wait.
    /// </remarks>
    private async Task HandleCheckCommandAsync(string[] parts, CancellationToken ct)
    {
        var root = FindProjectRoot();

        var result = await Services.Commands.CheckCommandHandler.HandleAsync(
            _client, _config, root, parts,
            git: Services.GitProcess.For(root),
            onProgress: p => Post(() => CurrentStep = p),
            ct);

        Post(() => CurrentStep = string.Empty);

        if (result.Scaffold is { } s)
            await ScaffoldFileAsync(s.Dir, s.FileName, s.Content, Strings.ChecksScaffolded);
        else if (result.Message is { } msg)
            await ShowInfoAsync(msg);
    }

    /// <summary>
    /// <c>/plan</c> — read-only plan mode, and the persistent plans of <c>.inferpal/plans/</c>
    /// (roadmap §17). Same handler as the host: the list of sub-commands exists once.
    /// </summary>
    private async Task HandlePlanCommandAsync(string[] parts, CancellationToken ct)
    {
        var result = Services.Commands.PlanCommandHandler.Handle(
            FindProjectRoot(), parts, LastAssistantText(), _activePlan);

        // Bare `/plan` still toggles plan mode; the toggle owns its own message.
        if (result.ToggleMode) { await TogglePlanModeAsync(); return; }

        if (result.SetActivePlan is { } active) _activePlan = active.Length == 0 ? null : active;

        if (result.Message is { } message) await ShowInfoAsync(message);

        if (result.OpenPath is { } path)
        {
            try { await _vs.Documents().OpenTextDocumentAsync(new Uri(path), ct); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { Diagnostics.Swallow($"HandlePlanCommandAsync.Open({path})", ex); }
        }
    }

    /// <summary>
    /// The model's most recent answer — what <c>/plan save</c> turns into a file. Anchors are
    /// excluded for the same reason the session save excludes them: they are layout, not content.
    /// </summary>
    private string? LastAssistantText() =>
        _history.LastOrDefault(m => m.Role == "assistant")?.Content;

    /// <summary>
    /// <c>/onboard</c> — the committable project profile (roadmap §19): report it, apply the part
    /// the user explicitly asks for, or draft <c>.inferpal/context.md</c>. Same handler as the
    /// host, so both front-ends refuse and recommend exactly the same things.
    /// </summary>
    private async Task HandleOnboardCommandAsync(string[] parts, CancellationToken ct)
    {
        var root = FindProjectRoot();

        var result = await Services.Commands.OnboardCommandHandler.HandleAsync(
            _client, _config, root, parts,
            git: Services.GitProcess.For(root),
            onProgress: p => Post(() => CurrentStep = p),
            ct);

        Post(() => CurrentStep = string.Empty);

        if (result.Scaffold is { } scaffold)
        {
            await ScaffoldFileAsync(
                scaffold.Dir, scaffold.FileName, scaffold.Content, Strings.OnboardProfileScaffolded);
            return;
        }

        // The handler mutates the config but never persists it (tests must not touch %APPDATA%).
        if (result.SaveConfig) _config.Save();

        // Same refresh as /model: without it the status label keeps naming the previous model.
        if (result.NewDefaultModel is { } model)
            await RunOnVMContextAsync(() => ActiveModelLabel = model);

        if (result.Write is { } write)
        {
            try
            {
                var dir = Path.GetDirectoryName(write.Path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                await File.WriteAllTextAsync(write.Path, write.Content, System.Text.Encoding.UTF8, ct);
                await _vs.Documents().OpenTextDocumentAsync(new Uri(write.Path), ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                await ShowInfoAsync(Strings.MsgError(ex.Message));
                return;
            }
        }

        if (result.RefreshSystemPrompt)
            // context.md is part of the system prompt: pick it up without waiting for a /clear.
            await RunOnVMContextAsync(() =>
            {
                _baseSystemPrompt = BuildSystemPrompt();
                if (_history.Count > 0 && _history[0].Role == "system")
                    _history[0] = new ChatMessageDto("system", _baseSystemPrompt);
            });

        if (result.Message is { } msg) await ShowInfoAsync(msg);
    }

    private async Task HandleRulesCommandAsync(string[] parts, CancellationToken ct)
    {
        var result = Services.Commands.RulesChecksPromptsCommandHandler.Rules(FindProjectRoot(), parts);
        if (result.Scaffold is { } s)
            await ScaffoldFileAsync(s.Dir, s.FileName, s.Content, Strings.RulesScaffolded);
        else if (result.Message is { } msg)
            await ShowInfoAsync(msg);
    }

    private async Task HandlePromptsCommandAsync(string[] parts, CancellationToken ct)
    {
        var result = Services.Commands.RulesChecksPromptsCommandHandler.Prompts(FindProjectRoot(), parts);
        if (result.Scaffold is { } s)
        {
            await ScaffoldFileAsync(s.Dir, s.FileName, s.Content, Strings.PromptsScaffolded);
            PromptFilesService.InvalidateCache();   // show up in autocomplete immediately
        }
        else if (result.Message is { } msg)
            await ShowInfoAsync(msg);
    }

    private async Task HandleChecksCommandAsync(string[] parts, CancellationToken ct)
    {
        var result = Services.Commands.RulesChecksPromptsCommandHandler.Checks(FindProjectRoot(), parts);
        if (result.Scaffold is { } s)
            await ScaffoldFileAsync(s.Dir, s.FileName, s.Content, Strings.ChecksScaffolded);
        else if (result.Message is { } msg)
            await ShowInfoAsync(msg);
    }

    // Writes a scaffold file only if it does not already exist, creating the directory as needed,
    // then confirms with the localized message (which receives the file path).
    private async Task ScaffoldFileAsync(string dir, string fileName, string content, Func<string, string> confirm)
    {
        var path = Path.Combine(dir, fileName);
        try
        {
            Directory.CreateDirectory(dir);
            if (!File.Exists(path))
                await File.WriteAllTextAsync(path, content, System.Text.Encoding.UTF8);
        }
        catch (Exception ex)
        {
            await ShowInfoAsync(Strings.MsgError(ex.Message));
            return;
        }
        await ShowInfoAsync(confirm(path));
    }

    // Streams a one-shot assistant reply (no tools) into a fresh chat bubble, reusing the
    // empty-bubble guards and cancel/error handling from the /commit flow.
    private async Task StreamAssistantReplyAsync(List<ChatMessageDto> history, string label, CancellationToken ct)
    {
        ChatMessageItem? streamItem = null;
        await RunOnVMContextAsync(() =>
        {
            streamItem       = ChatMessageItem.StreamingMsg();
            streamItem.Label = label;
            ApplyItemTheme(streamItem);
            Messages.Insert(Messages.Count - 2, streamItem);
            IsLoading   = true;
            CurrentStep = Strings.StatusThinking;
            ScrollToBottom();
        });

        void Finalize()
        {
            streamItem  = FinalizeStreamingBubble(streamItem);
            IsLoading   = false;
            CurrentStep = string.Empty;
            ScrollToBottom();
        }

        try
        {
            using var sink = new ThrottledTokenSink(chunk => Post(() => { if (streamItem is not null) streamItem.Content += chunk; }));
            await _client.RunAgentAsync(
                model:   _config.DefaultModel,
                history: history,
                tools:   EmptyToolRegistry.Instance,
                onStep:  _ => { },
                onToken: token => sink.Append(token),
                ct:      ct);
            sink.Stop();
            await RunOnVMContextAsync(Finalize);
        }
        catch (OperationCanceledException)
        {
            await RunOnVMContextAsync(Finalize);
        }
        catch (Exception ex)
        {
            var msg = ex.Message;
            await RunOnVMContextAsync(() =>
            {
                Finalize();
                InsertThemed(ChatMessageItem.AssistantMsg(Strings.MsgError(msg)));
                ScrollToBottom();
            });
        }
    }

    /// <summary><c>/commit-exec &lt;message&gt;</c> — stage if needed, then commit (shared handler).</summary>
    private async Task HandleCommitExecAsync(string message, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            await ShowInfoAsync(Strings.SlashUsage("/commit-exec <message>"));
            return;
        }

        var result = await Services.Commands.CommitCommandHandler.ExecuteAsync(
            message, Services.GitProcess.For(FindProjectRoot()), ct);

        await RunOnVMContextAsync(() =>
        {
            var item = ChatMessageItem.ToolMsg(
                result.Ok ? "✅ git commit" : "❌ git commit", result.Output, expanded: true);
            ApplyItemTheme(item);
            Messages.Insert(Messages.Count - 2, item);
            ScrollToBottom();
        });
    }

    /// <summary>Git for the chat commands — the shared runner, so the Host behaves identically.</summary>
    private static Task<(string Output, int ExitCode)> RunGitAsync(
        string args, string workDir, CancellationToken ct)
        => Services.GitProcess.RunAsync(args, workDir, ct);

    #endregion
}
