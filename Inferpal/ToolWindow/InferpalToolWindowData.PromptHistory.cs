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
        catch { }
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
        catch { }
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
        if (Directory.GetFiles(dir, "*.sln", SearchOption.TopDirectoryOnly).Length == 0)
        {
            await ShowInfoAsync(Strings.SlashContextNoSln);
            return;
        }

        var path = Path.Combine(dir, ".inferpal", "context.md");
        if (!File.Exists(path))
        {
            await ShowInfoAsync(Strings.SlashContextNotFound(path));
            return;
        }

        try
        {
            var content = await File.ReadAllTextAsync(path, System.Text.Encoding.UTF8, ct);
            var preview = content.Length > 400 ? content[..400] + "…" : content;
            await ShowInfoAsync(Strings.SlashContextLoaded(path, content.Length, preview));
        }
        catch (Exception ex)
        {
            await ShowInfoAsync(Strings.MsgError(ex.Message));
        }
    }

    private async Task HandleHistoryCommandAsync(string[] parts, CancellationToken ct)
    {
        if (parts.Length >= 2)
        {
            // ── Search mode ───────────────────────────────────────────────────
            var term    = string.Join(" ", parts[1..]);
            var matches = await _store.SearchAsync(term, ct);

            if (matches.Count == 0)
            {
                await ShowInfoAsync(Strings.HistoryNoResults(term));
                return;
            }

            await ShowInfoAsync(SessionManager.FormatHistorySearch(term, matches, DateTime.UtcNow));
        }
        else
        {
            // ── List mode ─────────────────────────────────────────────────────
            var sessions = await _store.ListWithPreviewAsync(ct);

            if (sessions.Count == 0)
            {
                await ShowInfoAsync(Strings.HistoryNoSessions);
                return;
            }

            await ShowInfoAsync(SessionManager.FormatHistoryList(sessions, DateTime.UtcNow));
        }
    }

    // /undo-run         → revert every file changed during the most recent agent run
    // /undo-run list    → list the change-tracking runs of this session
    private async Task HandleUndoRunCommandAsync(string[] parts, CancellationToken ct)
    {
        var runs = _tools.History.Runs;

        if (parts.Length >= 2 && parts[1].Equals("list", StringComparison.OrdinalIgnoreCase))
        {
            var withChanges = runs.Where(r => r.FileCount > 0).ToList();
            if (withChanges.Count == 0) { await ShowInfoAsync(Strings.UndoRunNone); return; }

            var sb = new System.Text.StringBuilder();
            sb.AppendLine(Strings.UndoRunListHeader(withChanges.Count));
            foreach (var r in withChanges)
                sb.AppendLine($"- {r.StartedAt:HH:mm:ss} — {r.FileCount} file(s)");
            await ShowInfoAsync(sb.ToString().TrimEnd());
            return;
        }

        var run = runs.FirstOrDefault(r => r.FileCount > 0);
        if (run is null) { await ShowInfoAsync(Strings.UndoRunNone); return; }

        var result = await _tools.History.UndoRunAsync(run, ct);

        var lines = new System.Text.StringBuilder();
        lines.AppendLine(Strings.UndoRunResult(result.Restored.Count, result.Deleted.Count));
        string Root() => FindProjectRoot();
        foreach (var p in result.Restored) lines.AppendLine($"  ↩ {System.IO.Path.GetRelativePath(Root(), p)}");
        foreach (var p in result.Deleted)  lines.AppendLine($"  🗑 {System.IO.Path.GetRelativePath(Root(), p)}");
        foreach (var p in result.Failed)   lines.AppendLine($"  ⚠ {System.IO.Path.GetRelativePath(Root(), p)}");
        await ShowInfoAsync(lines.ToString().TrimEnd());
    }

    private async Task HandleMemoryCommandAsync(CancellationToken ct)
    {
        var dir  = FindProjectRoot();
        var path = Path.Combine(dir, ".inferpal", "memory.md");

        if (!File.Exists(path))
        {
            await ShowInfoAsync(Strings.SlashMemoryNotFound(path));
            return;
        }

        try
        {
            var content = await File.ReadAllTextAsync(path, System.Text.Encoding.UTF8, ct);
            var preview = content.Length > 400 ? content[..400] + "…" : content;
            await ShowInfoAsync(Strings.SlashMemoryLoaded(path, content.Length, preview));
        }
        catch (Exception ex)
        {
            await ShowInfoAsync(Strings.MsgError(ex.Message));
        }
    }

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
                Messages.Insert(Messages.Count - 2, ChatMessageItem.AssistantMsg(Strings.MsgCancelled));
                ScrollToBottom();
            });
        }
        catch (Exception ex)
        {
            var msg = ex.Message;
            await RunOnVMContextAsync(() =>
            {
                Messages.Insert(Messages.Count - 2, ChatMessageItem.AssistantMsg(Strings.MsgError(msg)));
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
                tools:   (IToolRegistry)_tools,
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

    private async Task HandleCommitCommandAsync(CancellationToken ct)
    {
        var root = FindProjectRoot();

        // Staged diff first; fall back to unstaged if nothing staged
        var (staged, _) = await RunGitAsync("diff --staged", root, ct);
        bool nothingStaged = string.IsNullOrWhiteSpace(staged);

        string diffContext;
        if (nothingStaged)
        {
            var (status, _) = await RunGitAsync("status --short", root, ct);
            if (string.IsNullOrWhiteSpace(status))
            {
                await ShowInfoAsync(Strings.CommitNothingToCommit);
                return;
            }
            var (diff, _) = await RunGitAsync("diff", root, ct);
            diffContext = Services.GitCommitPolicy.BuildUnstagedContext(status, diff);
            await ShowInfoAsync(Strings.CommitNothingStaged);
        }
        else
        {
            diffContext = Services.GitCommitPolicy.BuildStagedContext(staged);
        }

        diffContext = Services.GitCommitPolicy.CapDiff(diffContext);

        // Stream the LLM's proposed commit message
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

        try
        {
            var commitHistory = Services.GitCommitPolicy.BuildProposalRequest(diffContext);

            using var sink = new ThrottledTokenSink(chunk => Post(() => { if (streamItem is not null) streamItem.Content += chunk; }));
            var result = await _client.RunAgentAsync(
                model:   _config.DefaultModel,
                history: commitHistory,
                tools:   EmptyToolRegistry.Instance,
                onStep:  _ => { },
                onToken: token => sink.Append(token),
                ct:      ct);
            sink.Stop();

            // Think tags are stripped so reasoning-model output doesn't land in the prompt
            var proposed = Services.GitCommitPolicy.CleanProposal(result.FinalResponse);

            await RunOnVMContextAsync(() =>
            {
                streamItem  = FinalizeStreamingBubble(streamItem);
                IsLoading   = false;
                CurrentStep = string.Empty;

                if (!string.IsNullOrWhiteSpace(proposed))
                {
                    Prompt = $"/commit-exec {proposed}";
                    var hint = ChatMessageItem.AssistantMsg(Strings.CommitConfirmHint);
                    ApplyItemTheme(hint);
                    Messages.Insert(Messages.Count - 2, hint);
                }
                ScrollToBottom();
            });
        }
        catch (OperationCanceledException)
        {
            await RunOnVMContextAsync(() =>
            {
                streamItem  = FinalizeStreamingBubble(streamItem);
                IsLoading   = false;
                CurrentStep = string.Empty;
            });
        }
        catch (Exception ex)
        {
            var msg = ex.Message;
            await RunOnVMContextAsync(() =>
            {
                streamItem  = FinalizeStreamingBubble(streamItem);
                IsLoading   = false;
                CurrentStep = string.Empty;
                Messages.Insert(Messages.Count - 2, ChatMessageItem.AssistantMsg(Strings.MsgError(msg)));
                ScrollToBottom();
            });
        }
    }

    // ── Rules & Checks (.inferpal/rules, .inferpal/checks) ─────────────────

    // AI-reviews the current git diff against .inferpal/checks. /check init scaffolds an example;
    // /check <name> runs a single check. 100% local — no diff leaves the machine.
    private async Task HandleCheckCommandAsync(string[] parts, CancellationToken ct)
    {
        var root = FindProjectRoot();
        var arg  = parts.Length >= 2 ? string.Join(" ", parts[1..]).Trim() : null;

        if (string.Equals(arg, "init", StringComparison.OrdinalIgnoreCase))
        {
            // Single source of truth for the checks scaffold (dir/file/content): reuse the handler.
            var scaffold = Services.Commands.RulesChecksPromptsCommandHandler.Checks(root, parts).Scaffold!;
            await ScaffoldFileAsync(scaffold.Dir, scaffold.FileName, scaffold.Content, Strings.ChecksScaffolded);
            return;
        }

        var checks = ChecksService.Load(Path.Combine(root, ".inferpal", "checks"));
        if (checks.Count == 0) { await ShowInfoAsync(Strings.ChecksNone); return; }

        if (!string.IsNullOrEmpty(arg))
        {
            var one = checks.FirstOrDefault(c => c.Name.Equals(arg, StringComparison.OrdinalIgnoreCase));
            if (one is null) { await ShowInfoAsync(Strings.CheckUnknownName(arg)); return; }
            checks = [one];
        }

        // Current diff: staged first, fall back to unstaged + status (mirror /commit).
        var (staged, _) = await RunGitAsync("diff --staged", root, ct);
        string diff;
        if (string.IsNullOrWhiteSpace(staged))
        {
            var (unstaged, _) = await RunGitAsync("diff", root, ct);
            var (status, _)   = await RunGitAsync("status --short", root, ct);
            if (string.IsNullOrWhiteSpace(unstaged) && string.IsNullOrWhiteSpace(status))
            {
                await ShowInfoAsync(Strings.CheckNoDiff);
                return;
            }
            diff = Services.GitCommitPolicy.BuildUnstagedContext(status, unstaged);
        }
        else
        {
            diff = Services.GitCommitPolicy.BuildStagedContext(staged);
        }

        diff = Services.GitCommitPolicy.CapDiff(diff);

        var history = new List<ChatMessageDto>
        {
            new("system", Strings.CheckReviewSystemPrompt),
            new("user",   ChecksService.BuildReviewPrompt(checks, diff)),
        };

        await StreamAssistantReplyAsync(history, Strings.CheckReviewingLabel, ct);
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
                Messages.Insert(Messages.Count - 2, ChatMessageItem.AssistantMsg(Strings.MsgError(msg)));
                ScrollToBottom();
            });
        }
    }

    private async Task HandleCommitExecAsync(string message, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            await ShowInfoAsync(Strings.SlashUsage("/commit-exec <message>"));
            return;
        }

        var root    = FindProjectRoot();
        var safeMsg = Services.GitCommitPolicy.EscapeMessage(message);

        // If nothing is staged, auto-stage tracked modified files (git add -u)
        var (stagedFiles, _) = await RunGitAsync("diff --staged --name-only", root, ct);
        if (string.IsNullOrWhiteSpace(stagedFiles))
            await RunGitAsync("add -u", root, ct);

        var (output, exitCode) = await RunGitAsync($"commit -m \"{safeMsg}\"", root, ct);

        await RunOnVMContextAsync(() =>
        {
            var label = exitCode == 0 ? "✅ git commit" : "❌ git commit";
            var text  = string.IsNullOrWhiteSpace(output) ? "(no output)" : output;
            var item  = ChatMessageItem.ToolMsg(label, text, expanded: true);
            ApplyItemTheme(item);
            Messages.Insert(Messages.Count - 2, item);
            ScrollToBottom();
        });
    }

    private static async Task<(string Output, int ExitCode)> RunGitAsync(
        string args, string workDir, CancellationToken ct)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("git", args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
                WorkingDirectory       = workDir,
            };
            using var proc   = System.Diagnostics.Process.Start(psi)!;
            var stdout        = await proc.StandardOutput.ReadToEndAsync(ct);
            var stderr        = await proc.StandardError.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);
            var combined      = stdout.Trim();
            if (!string.IsNullOrWhiteSpace(stderr))
                combined += (combined.Length > 0 ? "\n" : "") + stderr.Trim();
            return (combined, proc.ExitCode);
        }
        catch (Exception ex) { return (ex.Message, -1); }
    }

    #endregion
}
