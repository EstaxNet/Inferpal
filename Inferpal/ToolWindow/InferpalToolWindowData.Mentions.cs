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
    #region Mentions & autocompletion

    // ── Shadow RAG pre-warm ────────────────────────────────────────────────────

    /// <summary>
    /// Debounced (800 ms) background embedding + search triggered while the user types.
    /// Results are cached in <see cref="ProjectIndexService"/> and consumed by
    /// <see cref="SemanticSearchTool"/> to skip the embedding round-trip when the
    /// agent's query matches the typed prompt exactly.
    /// </summary>
    private void TriggerShadowSearch(string prompt)
    {
        _shadowSearchCts?.Cancel();
        _shadowSearchCts?.Dispose();
        _shadowSearchCts = null;

        // Clear stale auto-attach chips immediately when search is reset
        var staleChips = Attachments.Where(a => a.IsAutoAttach).ToList();
        foreach (var chip in staleChips) Attachments.Remove(chip);
        if (staleChips.Count > 0) HasAttachments = Attachments.Count > 0;

        // Nothing to look for (search off, nothing indexed, a short draft, a command): nothing is scheduled.
        if (!Services.Rag.RagAutoContext.MayChip(_indexService, _config, prompt)) return;

        var cts = _shadowSearchCts = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(800, cts.Token);          // debounce — wait for pause in typing
                // The search and its chips are the Core's, the same as VS Code's (RagAutoContext.WhileTypingAsync), run
                // off the UI thread: whether the excerpts cover the file is read from disk.
                var chips = await Services.Rag.RagAutoContext.WhileTypingAsync(_indexService, _config, prompt, cts.Token);
                if (chips.Count > 0)
                    Post(() => UpdateAutoRagAttachments(chips));
            }
            catch (OperationCanceledException) { }         // user kept typing
            catch (Exception ex) { Diagnostics.Swallow("Mentions.Search", ex); }                                      // best-effort
        });
    }

    private void UpdateAutoRagAttachments(IReadOnlyList<Services.Rag.RagAutoContext.TypingChip> chips)
    {
        // Remove existing auto-attach chips
        var toRemove = Attachments.Where(a => a.IsAutoAttach).ToList();
        foreach (var a in toRemove) Attachments.Remove(a);

        foreach (var chip in chips)
        {
            var path = chip.Path;
            // Skip if user already has this file attached manually — by its path, a cut chip's label is not its name —,
            // or removed its chip for this draft
            var fileName = Path.GetFileName(path);
            if (Attachments.Any(a => !a.IsAutoAttach
                                     && (a.Label == fileName || (a.SourcePath ?? a.Origin) is { } held && PathComparer.Default.Equals(held, path))))
                continue;
            if (_removedAutoChips.Contains(path)) continue;

            // The file's best chunks, said to be excerpts when they are (RagAutoContext.ChipContent).
            var content = chip.Content;

            AttachmentItem? item = null;
            item = new AttachmentItem($"🔮 {fileName}", content, () =>
            {
                Post(() =>
                {
                    Attachments.Remove(item!);
                    HasAttachments = Attachments.Count > 0;
                    _removedAutoChips.Add(path);
                });
            }, isAutoAttach: true, sourcePath: path);   // sourcePath: the relevant code does not send its chunks again

            Attachments.Add(item);
        }

        HasAttachments = Attachments.Count > 0;
    }

    // ── @mention completion (typed context providers) ──────────────────────────
    // Typing '@' opens a menu of context categories; query-based ones (@file/@code/@folder)
    // drill into a sub-search, instant ones (@clipboard/@tree/@diff/@problems) attach directly.
    // Parsing, category matching, and the filesystem searches live in MentionController
    // (pure, unit-tested); this VM keeps the debounce, popup UI, and attachments.

    private void TriggerMentionSearch(string prompt)
    {
        _mentionCts?.Cancel();
        _mentionCts?.Dispose();
        _mentionCts = null;

        var palette = ThemePalette.For(_isDark, _isHighContrast);
        var text    = palette.Text;
        var sub     = palette.SuggestionSubtleText;

        switch (MentionController.Parse(prompt))
        {
            // ── 1a. "@code xyz" committed: semantic search is explicit — a single action item. ──
            case MentionCommittedQuery { Category: "code" } code:
            {
                MentionSuggestions.Clear();
                var q = code.Query.Trim();
                if (q.Length > 0)
                    MentionSuggestions.Add(new MentionSuggestion(
                        "🔮 " + q, Strings.MentionCodeDesc, text, sub,
                        ct => SelectCodeMentionAsync(q, ct)));
                else
                    // Just committed "@code " with no query yet — keep the popup open with a
                    // hint instead of leaving the user staring at a silent dead-end.
                    MentionSuggestions.Add(new MentionSuggestion(
                        "🔮 " + Strings.MentionCodeHint, Strings.MentionCodeDesc, text, sub,
                        _ => Task.CompletedTask));
                HasMentionSuggestions = MentionSuggestions.Count > 0;
                return;
            }

            // ── 1b. "@url …" committed: a single action item attaching the typed address. ──
            case MentionCommittedQuery { Category: "url" } url:
            {
                MentionSuggestions.Clear();
                var q = url.Query.Trim();
                MentionSuggestions.Add(q.Length > 0
                    ? new MentionSuggestion("🌐 " + q, Strings.MentionUrlDesc, text, sub, ct => SelectUrlMentionAsync(q, ct))
                    : new MentionSuggestion("🌐 " + Strings.MentionUrlHint, Strings.MentionUrlDesc, text, sub,
                                            _ => Task.CompletedTask));
                HasMentionSuggestions = true;
                return;
            }

            // ── 1c. "@commit …" committed: the typed revision (HEAD before anything is typed), then the latest commits
            //        whose hash or subject matches — debounced, each search runs git. ──
            case MentionCommittedQuery { Category: "commit" } commit:
            {
                var q      = commit.Query.Trim();
                var root   = _indexService.RootDir;
                // The typed line now, never after the search: Enter takes the first item, and an older line would attach
                // the revision typed before.
                MentionSuggestion Typed() => new(
                    "📜 " + (q.Length > 0 ? q : "HEAD"), Strings.MentionCommitDesc, text, sub,
                    ct => SelectCommitMentionAsync(q, ct));
                MentionSuggestions.Clear();
                MentionSuggestions.Add(Typed());
                HasMentionSuggestions = true;
                var catCts = _mentionCts = new CancellationTokenSource();
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(120, catCts.Token);
                        var found = await MentionController.FindCommitsAsync(root, q, catCts.Token);
                        if (catCts.IsCancellationRequested) return;
                        Post(() =>
                        {
                            MentionSuggestions.Clear();
                            MentionSuggestions.Add(Typed());
                            foreach (var c in found)
                            {
                                var sha = c.Sha;
                                MentionSuggestions.Add(new MentionSuggestion(
                                    c.Short, c.Subject, text, sub, ct => SelectCommitMentionAsync(sha, ct)));
                            }
                            HasMentionSuggestions = true;
                        });
                    }
                    catch (OperationCanceledException) { }
                }, catCts.Token);
                return;
            }

            // ── 1d. "@file Foo" / "@folder Bar" committed → debounced filesystem search. ──
            case MentionCommittedQuery committed:
            {
                var ql         = committed.Query.Trim().ToLowerInvariant();
                var openForCat = _contextHolder.GetOpenPaths();
                var isFolder   = committed.Category == "folder";
                var catCts     = _mentionCts = new CancellationTokenSource();
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(120, catCts.Token);
                        var root  = FindProjectRoot(openForCat);
                        var paths = isFolder
                            ? MentionController.FindFolders(root, ql, catCts.Token)
                            : MentionController.FindFiles(root, ql, catCts.Token);
                        if (catCts.IsCancellationRequested) return;
                        Post(() =>
                        {
                            MentionSuggestions.Clear();
                            foreach (var s in BuildPathSuggestions(paths, root, isFolder, text, sub))
                                MentionSuggestions.Add(s);
                            HasMentionSuggestions = MentionSuggestions.Count > 0;
                        });
                    }
                    catch (OperationCanceledException) { }
                }, catCts.Token);
                return;
            }

            // ── 2. Choosing a category (still typing "@xxx", no space yet) ──
            case MentionTypingCategory typing:
            {
                var queryLower = typing.Partial.ToLowerInvariant();

                MentionSuggestions.Clear();
                // Matching categories first (token without the leading '@' starts with what was typed).
                foreach (var category in MentionController.MatchCategories(queryLower))
                {
                    var cat = category;
                    MentionSuggestions.Add(new MentionSuggestion(
                        cat.Token, cat.Desc(), text, sub, ct => SelectCategoryAsync(cat, ct)));
                }

                if (queryLower.Length == 0)
                {
                    // Bare '@' → also list currently open files for quick attach (below the categories).
                    var openPaths = _contextHolder.GetOpenPaths();
                    var root      = FindProjectRoot(openPaths);
                    foreach (var p in openPaths.Take(6).Where(File.Exists))
                    {
                        var path = p;
                        MentionSuggestions.Add(new MentionSuggestion(
                            Path.GetFileName(path), MentionController.RelLabel(path, root), text, sub,
                            ct => SelectFileMentionAsync(path, ct)));
                    }
                    HasMentionSuggestions = MentionSuggestions.Count > 0;
                    return;
                }

                // "@xxx" → keep the category matches and append fuzzy file matches (debounced).
                HasMentionSuggestions = MentionSuggestions.Count > 0;
                var categoryCount  = MentionSuggestions.Count;
                var openFilePaths  = _contextHolder.GetOpenPaths();
                var cts            = _mentionCts = new CancellationTokenSource();
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(120, cts.Token);
                        var root      = FindProjectRoot(openFilePaths);
                        var filePaths = MentionController.FindFiles(root, queryLower, cts.Token);
                        if (cts.IsCancellationRequested) return;
                        Post(() =>
                        {
                            while (MentionSuggestions.Count > categoryCount)
                                MentionSuggestions.RemoveAt(MentionSuggestions.Count - 1);
                            foreach (var s in BuildPathSuggestions(filePaths, root, folders: false, text, sub))
                                MentionSuggestions.Add(s);
                            HasMentionSuggestions = MentionSuggestions.Count > 0;
                        });
                    }
                    catch (OperationCanceledException) { }
                }, cts.Token);
                return;
            }

            default:    // MentionNone — close the popup if it was open.
                if (_hasMentionSuggestions)
                {
                    MentionSuggestions.Clear();
                    HasMentionSuggestions = false;
                }
                return;
        }
    }

    /// <summary>Wraps ranked paths from <see cref="MentionController"/> into popup suggestion items.</summary>
    private List<MentionSuggestion> BuildPathSuggestions(
        IReadOnlyList<string> paths, string rootDir, bool folders, string text, string sub) =>
        paths.Select(p =>
        {
            var path = p;
            return folders
                ? new MentionSuggestion(
                    "📁 " + Path.GetFileName(path), MentionController.RelLabel(path, rootDir), text, sub,
                    ct => SelectFolderMentionAsync(path, ct))
                : new MentionSuggestion(
                    Path.GetFileName(path), MentionController.RelLabel(path, rootDir), text, sub,
                    ct => SelectFileMentionAsync(path, ct));
        }).ToList();

    // ── Category selection ──────────────────────────────────────────────────────

    private async Task SelectCategoryAsync(MentionCategory cat, CancellationToken ct)
    {
        if (cat.QueryBased)
        {
            // Commit "@token " into the prompt; the user then types the provider-specific query.
            await RunOnVMContextAsync(() =>
                Prompt = MentionController.CommitCategory(_prompt, cat.Token));
            return;
        }

        // Instant provider: strip the partial @token, fetch context, attach as a chip.
        await RunOnVMContextAsync(StripMentionToken);
        await AttachInstantAsync(cat.Kind, ct);
    }

    private async Task AttachInstantAsync(MentionKind kind, CancellationToken ct)
    {
        try
        {
            switch (kind)
            {
                case MentionKind.Clipboard:
                {
                    var clip = string.Empty;
                    await RunOnVMContextAsync(() =>
                    {
                        try { clip = System.Windows.Clipboard.GetText() ?? string.Empty; } catch (Exception ex) { Diagnostics.Swallow("Clipboard.ReadForMention", ex); }
                    });
                    if (string.IsNullOrWhiteSpace(clip))
                    {
                        await NotifyMentionAsync(Strings.MentionClipboardEmpty);
                        return;
                    }
                    await RunOnVMContextAsync(() => AddAttachment("📋 @clipboard", clip));
                    break;
                }
                case MentionKind.Tree:
                {
                    // Decided on whether there was a root to map: none is a notice, not a chip.
                    var tree = MentionController.TreeMention(await _tools.ProjectMap.TryGenerateMapAsync(ct), "🌲 @tree");
                    if (tree.Notice is not null)
                    {
                        await NotifyMentionAsync(tree.Notice);
                        return;
                    }
                    await RunOnVMContextAsync(() => AddAttachment(tree.Label!, tree.Content!));
                    break;
                }
                case MentionKind.Diff:
                {
                    // Decided on the report's state: no repository, git refusing or no change is a notice, not a chip.
                    var report = await _tools.GitStatus.ReportAsync(null, includeDiff: true, diffPath: null, ct);
                    var diff   = MentionController.DiffMention(report, "📊 @diff");
                    if (diff.Notice is not null)
                    {
                        await NotifyMentionAsync(diff.Notice);
                        return;
                    }
                    await RunOnVMContextAsync(() => AddAttachment(diff.Label!, diff.Content!));
                    break;
                }
                case MentionKind.Problems:
                {
                    var diags    = await _tools.ExecuteAsync("get_diagnostics", MentionArgs(), ct);
                    var problems = MentionController.ProblemsMention(diags, "⚠ @problems");
                    if (problems.Notice is not null)
                    {
                        await NotifyMentionAsync(problems.Notice);
                        return;
                    }
                    await RunOnVMContextAsync(() => AddAttachment(problems.Label!, problems.Content!));
                    break;
                }
                case MentionKind.Output:
                {
                    // Read inside devenv — a tool window's text does not cross to this process —, asked through a file.
                    var answer = await Services.Signals.OutputPaneSignal.AskAsync(Services.Signals.OutputPaneSignal.AnswerBudget, ct);
                    // The package serves the request: without it loaded, waiting again changes nothing — said apart.
                    var output = MentionController.OutputMention(answer, "▤ @output",
                                                                 Services.Signals.InProcAliveSignal.TryRead() is { HasPackage: true });
                    if (output.Notice is not null)
                    {
                        await NotifyMentionAsync(output.Notice);
                        return;
                    }
                    await RunOnVMContextAsync(() => AddAttachment(output.Label!, output.Content!));
                    break;
                }
                case MentionKind.Debugger:
                {
                    // Read the state directly (not via the tool) so "not paused" can be a
                    // notification instead of a useless attachment chip — the tool has to answer a
                    // sentence, and a decision never re-reads the sentence a tool returned.
                    // Same reader as the tool and as the VS Code mention: three call sites, one
                    // answer to "is the debugger paused, and where?".
                    var state = await Services.Debugging.DebuggerStateReader.TryReadAsync(
                        _debug, _indexService.RootDir, ct);
                    if (state is null)
                    {
                        await NotifyMentionAsync(Strings.MentionDebuggerNone);
                        return;
                    }
                    await RunOnVMContextAsync(() => AddAttachment("🐞 @debugger", state));
                    break;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            var m = ex.Message;
            await NotifyMentionAsync(Strings.AttachError(m));
        }
    }

    private static JsonElement MentionArgs(object? o = null) =>
        JsonDocument.Parse(o is null ? "{}" : JsonSerializer.Serialize(o)).RootElement.Clone();

    private Task NotifyMentionAsync(string message) =>
        RunOnVMContextAsync(() =>
            InsertThemed(ChatMessageItem.NoticeMsg(message)));

    /// <summary>Removes the trailing @mention token (committed "@file foo" or bare "@foo").</summary>
    private void StripMentionToken() => Prompt = MentionController.StripMentionToken(_prompt);

    /// <summary>Loads the indexed documentation, and says in the conversation when it could not be loaded.</summary>
    private async Task LoadDocsAsync()
    {
        try
        {
            if (await _docsIndex.LoadAsync(CancellationToken.None) is { } notice)
                await RunOnVMContextAsync(() => InsertThemed(ChatMessageItem.NoticeMsg(notice)));
        }
        catch (Exception ex) { Diagnostics.Swallow("Docs.Load", ex); }
    }

    /// <param name="typed"><c>false</c> for a chip asked from elsewhere (the Explorer): the draft holds no mention token
    /// to take off.</param>
    private async Task SelectFolderMentionAsync(string folderPath, CancellationToken ct, bool typed = true)
    {
        try
        {
            // A notice, never a chip, when there is no folder to walk (MentionController.FolderRefusal).
            folderPath = MentionController.FolderPath(folderPath, _indexService.RootDir);
            if (MentionController.FolderRefusal(folderPath, _indexService.RootDir) is { } why)
            {
                await NotifyMentionAsync(why);
                return;
            }
            // The unsaved buffers, as read_file shows them: the folder's files as the user sees them in the editor.
            if (_tools.Overlay is { } overlay) await overlay.RefreshAsync(ct);
            var content = await Task.Run(() => MentionController.BuildFolderContext(folderPath, ct, _tools.Overlay), ct);
            var label   = "📁 " + Path.GetFileName(folderPath);
            await RunOnVMContextAsync(() =>
            {
                if (typed) StripMentionToken();
                AddAttachment(label, content);
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            var m = ex.Message;
            await NotifyMentionAsync(Strings.AttachError(m));
        }
    }

    private Task SelectCodeMentionAsync(string query, CancellationToken ct) =>
        AttachMentionOutcomeAsync(async () =>
            // A notice, never a chip, when there is no index to search (MentionController.CodeRefusal); decided on the hit
            // count after: a search that found nothing is a notice too.
            MentionController.CodeRefusal(_indexService) is { } noIndex
                ? new MentionOutcome(null, null, noIndex)
                : MentionController.CodeMention(
                    await _tools.CodeSearch.AnswerAsync(MentionArgs(new { query }), ct),
                    "🔮 " + (query.Length > 40 ? query[..40] + "…" : query)));

    /// <summary><c>@url</c>: the page's text as a chip, or the notice that says why there is none.</summary>
    private Task SelectUrlMentionAsync(string url, CancellationToken ct) =>
        AttachMentionOutcomeAsync(() => MentionController.UrlMentionAsync(url, _tools.PersonFetchRefusal, ct));

    /// <summary><c>@commit</c>: one commit's message and diff as a chip, or the notice that says why there is none.</summary>
    private Task SelectCommitMentionAsync(string revision, CancellationToken ct) =>
        AttachMentionOutcomeAsync(() => MentionController.CommitMentionAsync(_indexService.RootDir, revision, ct));

    /// <summary>Strips the typed mention, then attaches what <paramref name="resolve"/> found or says its notice.</summary>
    private async Task AttachMentionOutcomeAsync(Func<Task<MentionOutcome>> resolve)
    {
        try
        {
            await RunOnVMContextAsync(StripMentionToken);
            var outcome = await resolve();
            if (outcome.Notice is not null)
            {
                await NotifyMentionAsync(outcome.Notice);
                return;
            }
            await RunOnVMContextAsync(() => AddAttachment(outcome.Label!, outcome.Content!));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            await NotifyMentionAsync(Strings.AttachError(ex.Message));
        }
    }

    /// <summary>The Explorer's "Attach to Chat": the chip <c>@folder</c> or <c>@file</c> makes, with their refusals, in the
    /// composer — nothing sent.</summary>
    private void ConsumePendingAttach()
    {
        if (_contextHolder.ConsumePendingAttach() is not { } path) return;
        _ = Directory.Exists(path)
            ? SelectFolderMentionAsync(path, CancellationToken.None, typed: false)
            : SelectFileMentionAsync(path, CancellationToken.None, typed: false);
    }

    /// <param name="typed"><c>false</c> for a chip asked from elsewhere (the Explorer): the draft holds no mention token
    /// to take off.</param>
    private async Task SelectFileMentionAsync(string filePath, CancellationToken ct, bool typed = true)
    {
        try
        {
            // Excluded by the workspace's ignore files: a notice that names the rule, never a chip; a binary file: no text.
            if ((MentionController.FileRefusal(filePath, _indexService.RootDir) ?? MentionController.BinaryRefusal(filePath)) is { } refused)
            {
                await NotifyMentionAsync(refused);
                return;
            }
            // The unsaved buffer, as read_file shows it: the file as the user sees it in the editor.
            if (_tools.Overlay is { } overlay) await overlay.RefreshAsync(ct, [filePath]);
            if (await MentionController.FileMentionTextAsync(filePath, _tools.Overlay, ct) is not { } content)
            {
                await NotifyMentionAsync(Strings.AttachFileTooLarge);
                return;
            }
            var label   = Path.GetFileName(filePath);

            await RunOnVMContextAsync(() =>
            {
                // Setting Prompt triggers TriggerMentionSearch → clears suggestions automatically.
                if (typed) StripMentionToken();
                AddAttachment(label, content, sourcePath: filePath);
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            var m = ex.Message;
            await NotifyMentionAsync(Strings.AttachError(m));
        }
    }

    // ── Slash-command autocomplete ─────────────────────────────────────────────

    private void TriggerSlashSuggestions(string text)
    {
        // Matching (built-ins + user templates, '/prefix' with no space yet) lives in the router.
        var matches = SlashCommandRouter.MatchCommands(text, GetUserTemplates());
        if (matches.Count > 0 || (text.StartsWith('/') && !text.Contains(' ')))
        {
            var palette   = ThemePalette.For(_isDark, _isHighContrast);
            var textColor = palette.Text;
            var subColor  = palette.SuggestionSubtleText;
            SlashSuggestions.Clear();
            foreach (var (cmd, hint) in matches)
                SlashSuggestions.Add(new SlashSuggestion(cmd, hint, textColor, subColor, SelectSlashSuggestionAsync));
            HasSlashSuggestions = SlashSuggestions.Count > 0;
        }
        else if (_hasSlashSuggestions)
        {
            SlashSuggestions.Clear();
            HasSlashSuggestions = false;
        }
    }

    private Task SelectSlashSuggestionAsync(string command, CancellationToken ct)
    {
        Post(() =>
        {
            SlashSuggestions.Clear();
            HasSlashSuggestions = false;
            Prompt = command + " ";
        });
        return Task.CompletedTask;
    }

    #endregion
}
