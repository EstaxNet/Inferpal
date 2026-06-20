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
    #region Mentions & autocomplétion

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

        // Guard: RAG must be enabled, index ready, embedding circuit healthy
        if (!_config.RagEnabled
            || _indexService.ChunkCount == 0
            || _client.IsEmbeddingCircuitOpen)
            return;

        // Skip slash commands and very short prompts (not worth embedding)
        var trimmed = prompt.Trim();
        if (trimmed.Length < 12 || trimmed.StartsWith('/')) return;

        var model = string.IsNullOrEmpty(_config.RagEmbeddingModel)
            ? "nomic-embed-text"
            : _config.RagEmbeddingModel;

        var cts = _shadowSearchCts = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(800, cts.Token);          // debounce — wait for pause in typing
                await _indexService.ShadowPreWarmAsync(trimmed, model, cts.Token);

                // Auto-attach top RAG results as dismissable cyan chips
                var (_, results) = _indexService.TryGetShadow(trimmed);
                if (results?.Count > 0)
                {
                    var topPaths = results
                        .Select(r => r.Chunk.FilePath)
                        .Where(p => !string.IsNullOrEmpty(p))
                        .Distinct()
                        .Take(2)
                        .ToList();
                    Post(() => UpdateAutoRagAttachments(topPaths, results));
                }
            }
            catch (OperationCanceledException) { }         // user kept typing
            catch { }                                      // best-effort
        });
    }

    private void UpdateAutoRagAttachments(
        List<string> paths,
        List<(RagChunk Chunk, float Score)> results)
    {
        // Remove existing auto-attach chips
        var toRemove = Attachments.Where(a => a.IsAutoAttach).ToList();
        foreach (var a in toRemove) Attachments.Remove(a);

        foreach (var path in paths)
        {
            // Skip if user already has this file attached manually
            var fileName = Path.GetFileName(path);
            if (Attachments.Any(a => !a.IsAutoAttach && a.Label == fileName)) continue;

            // Use the best chunk content for the file (most relevant)
            var chunks    = results.Where(r => r.Chunk.FilePath == path).Take(3).ToList();
            var content   = string.Join("\n\n...\n\n", chunks.Select(r => r.Chunk.Content));

            AttachmentItem? item = null;
            item = new AttachmentItem($"🔮 {fileName}", content, () =>
            {
                Post(() =>
                {
                    Attachments.Remove(item!);
                    HasAttachments = Attachments.Count > 0;
                });
            }, isAutoAttach: true, sourcePath: path);   // sourcePath enables auto-context dedup

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

        var palette = ThemePalette.For(_isDark);
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

            // ── 1b. "@file Foo" / "@folder Bar" committed → debounced filesystem search. ──
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
                        try { clip = System.Windows.Clipboard.GetText() ?? string.Empty; } catch { }
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
                    var map = await _tools.ExecuteAsync("generate_project_map", MentionArgs(), ct);
                    await RunOnVMContextAsync(() => AddAttachment("🌲 @tree", map));
                    break;
                }
                case MentionKind.Diff:
                {
                    var diff = await _tools.ExecuteAsync(
                        "get_git_status", MentionArgs(new { include_diff = true }), ct);
                    await RunOnVMContextAsync(() => AddAttachment("📊 @diff", diff));
                    break;
                }
                case MentionKind.Problems:
                {
                    var diags = await _tools.ExecuteAsync("get_diagnostics", MentionArgs(), ct);
                    await RunOnVMContextAsync(() => AddAttachment("⚠ @problems", diags));
                    break;
                }
                case MentionKind.Debugger:
                {
                    // Read the signal directly (not via the tool) so "not paused" can be a
                    // notification instead of a useless attachment chip.
                    var snap = Services.VsIntegration.DebuggerStateSignal.TryRead();
                    if (snap is null)
                    {
                        await NotifyMentionAsync(Strings.MentionDebuggerNone);
                        return;
                    }
                    var state = Services.VsIntegration.DebuggerStateSignal.Format(snap);
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
            Messages.Insert(Messages.Count - 2, ChatMessageItem.AssistantMsg(message)));

    /// <summary>Removes the trailing @mention token (committed "@file foo" or bare "@foo").</summary>
    private void StripMentionToken() => Prompt = MentionController.StripMentionToken(_prompt);

    private async Task SelectFolderMentionAsync(string folderPath, CancellationToken ct)
    {
        try
        {
            var content = await Task.Run(() => MentionController.BuildFolderContext(folderPath, ct), ct);
            var label   = "📁 " + Path.GetFileName(folderPath);
            await RunOnVMContextAsync(() =>
            {
                StripMentionToken();
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

    private async Task SelectCodeMentionAsync(string query, CancellationToken ct)
    {
        try
        {
            await RunOnVMContextAsync(StripMentionToken);
            var result = await _tools.ExecuteAsync(
                "search_codebase", MentionArgs(new { query }), ct);
            var label = "🔮 " + (query.Length > 40 ? query[..40] + "…" : query);
            await RunOnVMContextAsync(() => AddAttachment(label, result));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            var m = ex.Message;
            await NotifyMentionAsync(Strings.AttachError(m));
        }
    }

    private async Task SelectFileMentionAsync(string filePath, CancellationToken ct)
    {
        try
        {
            if (new FileInfo(filePath).Length > 512_000)
            {
                await NotifyMentionAsync(Strings.AttachFileTooLarge);
                return;
            }

            var content = await File.ReadAllTextAsync(filePath, ct);
            var label   = Path.GetFileName(filePath);

            await RunOnVMContextAsync(() =>
            {
                // Setting Prompt triggers TriggerMentionSearch → clears suggestions automatically.
                StripMentionToken();
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
            var palette   = ThemePalette.For(_isDark);
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
