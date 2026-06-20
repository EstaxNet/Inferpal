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
    #region Pièces jointes, pins & contexte workspace

    private async Task<string> BuildWorkspaceContextAsync(CancellationToken ct)
    {
        const int TimeoutMs = 5000;
        var sb = new StringBuilder("## Workspace context (auto-injected on session start)\n\n");

        try
        {
            using var cts1   = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts1.CancelAfter(TimeoutMs);
            var solutionJson = JsonSerializer.Serialize(new { });
            var solutionArgs = JsonDocument.Parse(solutionJson).RootElement.Clone();
            var solutionInfo = await _tools.ExecuteAsync("get_solution_info", solutionArgs, cts1.Token);
            if (!string.IsNullOrWhiteSpace(solutionInfo))
                sb.AppendLine("### Solution\n").AppendLine(solutionInfo).AppendLine();
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { } // timeout — skip silently
        catch { }

        try
        {
            using var cts2   = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts2.CancelAfter(TimeoutMs);
            var editorsJson  = JsonSerializer.Serialize(new { });
            var editorsArgs  = JsonDocument.Parse(editorsJson).RootElement.Clone();
            var openEditors  = await _tools.ExecuteAsync("get_open_editors", editorsArgs, cts2.Token);
            if (!string.IsNullOrWhiteSpace(openEditors))
                sb.AppendLine("### Open editors\n").AppendLine(openEditors);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { } // timeout — skip silently
        catch { }

        var result = sb.ToString().TrimEnd();
        return result.Length > "## Workspace context (auto-injected on session start)".Length + 5
            ? result
            : string.Empty;
    }

    /// <summary>
    /// Retrieves the most relevant indexed chunks for <paramref name="userText"/> and formats them
    /// as a budget-capped context block (see <see cref="RagAutoContext"/>), skipping chunks whose
    /// file is already attached. Reuses the pre-warmed shadow result when available (free), else runs
    /// one bounded embed + search. Best-effort: any failure yields no auto-context.
    /// </summary>
    private async Task<string> BuildAutoContextAsync(
        string userText, IReadOnlyList<AttachmentItem> attachments, CancellationToken ct)
    {
        if (!_config.RagAutoContextEnabled || !_config.RagEnabled)            return string.Empty;
        if (_indexService.ChunkCount == 0 || _client.IsEmbeddingCircuitOpen) return string.Empty;

        var trimmed = userText.Trim();
        if (trimmed.Length < 12 || trimmed.StartsWith('/')) return string.Empty;   // same gate as the shadow

        try
        {
            // Warm shadow (pre-computed while typing) → free; otherwise one bounded embed + search.
            var (_, results) = _indexService.TryGetShadow(trimmed);
            if (results is null || results.Count == 0)
            {
                var model = string.IsNullOrEmpty(_config.RagEmbeddingModel) ? "nomic-embed-text" : _config.RagEmbeddingModel;
                var embedding = await _client.GetEmbeddingAsync(trimmed, model, ct);
                if (embedding is null) return string.Empty;
                results = await _indexService.SearchAsync(embedding, trimmed, RagAutoContext.DefaultMaxChunks, ct);
            }
            if (results is null || results.Count == 0) return string.Empty;

            var attached = attachments
                .Where(a => !string.IsNullOrEmpty(a.SourcePath))
                .Select(a => a.SourcePath!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            return RagAutoContext.Build(results, attached);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { Diagnostics.Swallow("RagAutoContext", ex); return string.Empty; }
    }

    private Task ToggleSearchAsync(object? _, CancellationToken ct)
    {
        Post(() =>
        {
            IsSearchOpen = !IsSearchOpen;
            if (!IsSearchOpen)
            {
                SearchQuery = string.Empty;
            }
        });
        return Task.CompletedTask;
    }

    private Task ClearSearchAsync(object? _, CancellationToken ct)
    {
        Post(() =>
        {
            SearchQuery  = string.Empty;
            IsSearchOpen = false;
        });
        return Task.CompletedTask;
    }

    // ── Attachments ───────────────────────────────────────────────────────────

    private async Task<ITextViewSnapshot?> ResolveActiveViewAsync(CancellationToken ct)
    {
        if (_contextHolder.LatestView is not null)
            return _contextHolder.LatestView;

        if (_contextHolder.Context is not null)
        {
            try { return await _vs.Editor().GetActiveTextViewAsync(_contextHolder.Context, ct); }
            catch { }
        }
        return null;
    }

    private async Task AttachFileAsync(object? _, CancellationToken ct)
    {
        IsAttachMenuOpen = false;
        ITextViewSnapshot? view = null;
        try
        {
            view = await ResolveActiveViewAsync(ct);
        }
        catch (Exception ex)
        {
            var m = ex.Message;
            await RunOnVMContextAsync(() =>
                Messages.Insert(Messages.Count - 2, ChatMessageItem.AssistantMsg(Strings.AttachError(m))));
            return;
        }

        if (view is null)
        {
            await RunOnVMContextAsync(() =>
                Messages.Insert(Messages.Count - 2,
                    ChatMessageItem.AssistantMsg(Strings.AttachNoActiveFile)));
            return;
        }

        try
        {
            var path    = view.Document.Uri.LocalPath;
            var label   = Path.GetFileName(path);
            var content = view.Document.Text.CopyToString();
            await RunOnVMContextAsync(() => AddAttachment(label, content, sourcePath: path));
        }
        catch (Exception ex)
        {
            var m = ex.Message;
            await RunOnVMContextAsync(() =>
                Messages.Insert(Messages.Count - 2, ChatMessageItem.AssistantMsg(Strings.AttachReadError(m))));
        }
    }

    private async Task AttachSelectionAsync(object? _, CancellationToken ct)
    {
        IsAttachMenuOpen = false;
        ITextViewSnapshot? view = null;
        try
        {
            view = await ResolveActiveViewAsync(ct);
        }
        catch (Exception ex)
        {
            var m = ex.Message;
            await RunOnVMContextAsync(() =>
                Messages.Insert(Messages.Count - 2, ChatMessageItem.AssistantMsg(Strings.AttachSelectionError(m))));
            return;
        }

        if (view is null)
        {
            await RunOnVMContextAsync(() =>
                Messages.Insert(Messages.Count - 2,
                    ChatMessageItem.AssistantMsg(Strings.AttachNoActiveFile)));
            return;
        }

        try
        {
            var path     = view.Document.Uri.LocalPath;
            var fileName = Path.GetFileName(path);
            var sel      = view.Selection;
            if (!sel.IsEmpty)
            {
                // Selection snippet has no standalone on-disk path → not pinnable.
                var content = sel.Extent.CopyToString();
                await RunOnVMContextAsync(() => AddAttachment($"Selection ({fileName})", content));
            }
            else
            {
                var content = view.Document.Text.CopyToString();
                await RunOnVMContextAsync(() => AddAttachment(fileName, content, sourcePath: path));
            }
        }
        catch (Exception ex)
        {
            var m = ex.Message;
            await RunOnVMContextAsync(() =>
                Messages.Insert(Messages.Count - 2, ChatMessageItem.AssistantMsg(Strings.AttachSelectionReadError(m))));
        }
    }

    private async Task BrowseFileAsync(object? _, CancellationToken ct)
    {
        IsAttachMenuOpen = false;
        string? filePath = await ShowOpenFileDialogAsync();
        if (filePath is null) return;

        try
        {
            if (new FileInfo(filePath).Length > 512_000)
            {
                await RunOnVMContextAsync(() =>
                    Messages.Insert(Messages.Count - 2,
                        ChatMessageItem.AssistantMsg(Strings.AttachFileTooLarge)));
                return;
            }

            var content = await File.ReadAllTextAsync(filePath, ct);
            var label   = Path.GetFileName(filePath);
            await RunOnVMContextAsync(() => AddAttachment(label, content, sourcePath: filePath));
        }
        catch (Exception ex)
        {
            var m = ex.Message;
            await RunOnVMContextAsync(() =>
                Messages.Insert(Messages.Count - 2,
                    ChatMessageItem.AssistantMsg(Strings.BrowseError(m))));
        }
    }

    private static Task<string?> ShowOpenFileDialogAsync()
    {
        var tcs = new TaskCompletionSource<string?>();
        var thread = new System.Threading.Thread(() =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title  = "Select File to Attach",
                Filter = "All Files (*.*)|*.*"
            };
            tcs.SetResult(dlg.ShowDialog() == true ? dlg.FileName : null);
        });
        thread.SetApartmentState(System.Threading.ApartmentState.STA);
        thread.Start();
        return tcs.Task;
    }

    private void AddAttachment(string label, string content, string? sourcePath = null)
    {
        AttachmentItem? item = null;
        Action? onPin = sourcePath is null ? null : () => Post(() =>
        {
            // Promote to a persistent pinned file, then drop the transient chip.
            AddPinnedFile(sourcePath);
            Attachments.Remove(item!);
            HasAttachments = Attachments.Count > 0;
        });
        item = new AttachmentItem(label, content, () => Post(() =>
        {
            Attachments.Remove(item!);
            HasAttachments = Attachments.Count > 0;
        }), sourcePath: sourcePath, onPin: onPin);
        var chip         = ThemePalette.For(_isDark);
        item.Background  = chip.AttachChipBg;
        item.Foreground  = chip.AttachChipText;
        item.BorderColor = chip.AttachChipBorder;
        Attachments.Add(item);
        HasAttachments = true;
    }

    // ── Pinned context files ────────────────────────────────────────────────────
    // Persistent files always injected into the system prompt (see BuildSystemPrompt).
    // Managed directly from the chat input card (gold 📌 chips) instead of the raw
    // path textbox in Settings. Parsing, dedup/cap rules and the '#'-disabled
    // round-trip live in PinnedFilesPolicy; this VM only owns the chips.

    /// <summary>Pins the active editor file as a persistent context file; falls back to a
    /// file-picker when no editor is active. Bound to the 📌 toolbar button.</summary>
    private async Task PinFileAsync(object? _, CancellationToken ct)
    {
        IsAttachMenuOpen = false;
        string? path = null;
        try
        {
            var view = await ResolveActiveViewAsync(ct);
            if (view is not null)
                path = view.Document.Uri.LocalPath;
        }
        catch { /* fall through to picker */ }

        path ??= await ShowOpenFileDialogAsync();
        if (string.IsNullOrEmpty(path)) return;

        await RunOnVMContextAsync(() => AddPinnedFile(path!));
    }

    /// <summary>Rebuilds the pinned-chip strip from <c>config.PinnedContextFiles</c> at startup.</summary>
    private void LoadPinnedFilesFromConfig()
    {
        foreach (var path in PinnedFilesPolicy.ParseActive(_config.PinnedContextFiles))
            CreatePinnedChip(path);
        HasPinnedFiles = PinnedFiles.Count > 0;
    }

    /// <summary>Adds <paramref name="path"/> to the pinned set (deduplicated, capped) and
    /// persists the change to config. No-op when already pinned or the cap is reached.</summary>
    private void AddPinnedFile(string path)
    {
        path = path.Trim();
        var current = PinnedFiles.Select(p => p.Path).ToList();
        switch (PinnedFilesPolicy.Decide(current, path))
        {
            case PinDecision.CapReached:
                Messages.Insert(Messages.Count - 2,
                    ChatMessageItem.AssistantMsg(Strings.PinLimitReached(PinnedFilesPolicy.MaxPinned)));
                return;
            case PinDecision.Duplicate:
            case PinDecision.Invalid:
                return;
        }

        CreatePinnedChip(path);
        HasPinnedFiles = true;
        SavePinnedFiles();
    }

    private void CreatePinnedChip(string path)
    {
        PinnedFileItem? item = null;
        item = new PinnedFileItem(path, Path.GetFileName(path), () => Post(() =>
        {
            PinnedFiles.Remove(item!);
            HasPinnedFiles = PinnedFiles.Count > 0;
            SavePinnedFiles();
        }));
        var chip         = ThemePalette.For(_isDark);
        item.Background  = chip.PinChipBg;
        item.Foreground  = chip.PinChipText;
        item.BorderColor = chip.PinChipBorder;
        PinnedFiles.Add(item);
    }

    private void SavePinnedFiles()
    {
        _config.PinnedContextFiles = PinnedFilesPolicy.Serialize(
            PinnedFiles.Select(p => p.Path), _config.PinnedContextFiles);
        _config.Save();
    }

    #endregion
}
