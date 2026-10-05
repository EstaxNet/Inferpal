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
    #region Attachments, pins & workspace context

    private async Task<string> BuildWorkspaceContextAsync(CancellationToken ct)
    {
        const int TimeoutMs = 5000;
        string? solutionInfo = null, openEditors = null;

        try
        {
            using var cts1   = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts1.CancelAfter(TimeoutMs);
            var solutionJson = JsonSerializer.Serialize(new { });
            var solutionArgs = JsonDocument.Parse(solutionJson).RootElement.Clone();
            solutionInfo     = await _tools.ExecuteAsync("get_solution_info", solutionArgs, cts1.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { } // timeout — skip silently
        catch (Exception ex) { Diagnostics.Swallow("WorkspaceContext.SolutionInfo", ex); }

        try
        {
            using var cts2   = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts2.CancelAfter(TimeoutMs);
            var editorsJson  = JsonSerializer.Serialize(new { });
            var editorsArgs  = JsonDocument.Parse(editorsJson).RootElement.Clone();
            openEditors      = await _tools.ExecuteAsync("get_open_editors", editorsArgs, cts2.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { } // timeout — skip silently
        catch (Exception ex) { Diagnostics.Swallow("WorkspaceContext.OpenEditors", ex); }

        // Same composer as the VS Code host.
        return Services.Prompting.WorkspaceContext.Compose(solutionInfo, openEditors);
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
        if (!RagAutoContext.IsEnabled(_config))                               return string.Empty;
        if (_indexService.ChunkCount == 0 || _client.IsEmbeddingCircuitOpen) return string.Empty;

        var trimmed = userText.Trim();
        if (trimmed.Length < 12 || trimmed.StartsWith('/')) return string.Empty;   // same gate as the shadow

        try
        {
            // Warm shadow (pre-computed while typing) → free; otherwise one bounded embed + search.
            var (_, results) = _indexService.TryGetShadow(trimmed);
            if (results is null || results.Count == 0)
            {
                // The model the index's vectors came from; none (no embedding model) = no auto-context, as before.
                if (_indexService.QueryEmbeddingModel is not { } model) return string.Empty;
                var embedding = await Services.Inference.EmbeddingModels.EmbedCodeQueryAsync(_client, model, trimmed, ct);
                if (embedding is null) return string.Empty;
                results = await _indexService.SearchAsync(embedding, trimmed, RagAutoContext.DefaultMaxChunks, ct);
            }
            if (results is null || results.Count == 0) return string.Empty;

            var attached = attachments
                .Where(a => !string.IsNullOrEmpty(a.SourcePath))
                .Select(a => a.SourcePath!)
                .ToHashSet(PathComparer.Default);

            return RagAutoContext.Build(results, attached,
                notYetReindexed: _indexService.NotYetReindexed.ToHashSet(PathComparer.Default));
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
        try { return await _contextHolder.ResolveActiveViewAsync(_vs, ct); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { Diagnostics.Swallow("Editor.ResolveActiveView", ex); return null; }
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
                InsertThemed(ChatMessageItem.NoticeMsg(Strings.AttachError(m))));
            return;
        }

        if (view is null)
        {
            await RunOnVMContextAsync(() =>
                InsertThemed(ChatMessageItem.NoticeMsg(Strings.AttachNoActiveFile)));
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
                InsertThemed(ChatMessageItem.NoticeMsg(Strings.AttachReadError(m))));
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
                InsertThemed(ChatMessageItem.NoticeMsg(Strings.AttachSelectionError(m))));
            return;
        }

        if (view is null)
        {
            await RunOnVMContextAsync(() =>
                InsertThemed(ChatMessageItem.NoticeMsg(Strings.AttachNoActiveFile)));
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
                InsertThemed(ChatMessageItem.NoticeMsg(Strings.AttachSelectionReadError(m))));
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
                    InsertThemed(ChatMessageItem.NoticeMsg(Strings.AttachFileTooLarge)));
                return;
            }

            // Decoded like the file an edit will rewrite (legacy code pages included): quoted back, it must match.
            var content = await Services.Tools.TextFileEncoding.ReadTextAsync(filePath, ct);
            var label   = Path.GetFileName(filePath);
            await RunOnVMContextAsync(() => AddAttachment(label, content, sourcePath: filePath));
        }
        catch (Exception ex)
        {
            var m = ex.Message;
            await RunOnVMContextAsync(() =>
                InsertThemed(ChatMessageItem.NoticeMsg(Strings.BrowseError(m))));
        }
    }

    private static Task<string?> ShowOpenFileDialogAsync() =>
        StaDialog.RunAsync(() =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title  = Strings.DialogAttachTitle,
                Filter = Strings.DialogAllFiles
            };
            return dlg.ShowDialog() == true ? dlg.FileName : null;
        }, "AttachFileDialog");

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
        var chip         = ThemePalette.For(_isDark, _isHighContrast);
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

    /// <summary>
    /// Rebuilds the pinned-chip strip from <c>config.PinnedContextFiles</c> — at startup, and whenever the
    /// configuration is saved.
    /// </summary>
    /// <remarks>
    /// ⚠ The SETTING is the source, never the chips: built once at startup, the chips went stale as soon as the
    /// settings window pinned or disabled a file, and the next pin or unpin here wrote the stale strip back — a file
    /// pinned in Settings silently unpinned, a file disabled there enabled again. The host reads the live setting on
    /// every edit (<c>pins/add</c>, <c>pins/remove</c>); so does this window now.
    /// </remarks>
    private void LoadPinnedFilesFromConfig()
    {
        PinnedFiles.Clear();
        foreach (var path in PinnedFilesPolicy.ParseActive(_config.PinnedContextFiles))
            CreatePinnedChip(path);
        HasPinnedFiles = PinnedFiles.Count > 0;
    }

    /// <summary>Adds <paramref name="path"/> to the pinned set (deduplicated, capped) and
    /// persists the change to config. No-op when already pinned or the cap is reached.</summary>
    private void AddPinnedFile(string path)
    {
        path = path.Trim();
        var current = PinnedFilesPolicy.ParseActive(_config.PinnedContextFiles);
        switch (PinnedFilesPolicy.Decide(current, path))
        {
            case PinDecision.CapReached:
                InsertThemed(ChatMessageItem.NoticeMsg(Strings.PinLimitReached(PinnedFilesPolicy.MaxPinned)));
                return;
            case PinDecision.Duplicate:
            case PinDecision.Invalid:
                return;
        }

        SavePinnedFiles([.. current, path]);
    }

    private void CreatePinnedChip(string path)
    {
        PinnedFileItem? item = null;
        item = new PinnedFileItem(path, Path.GetFileName(path), () => Post(() =>
            SavePinnedFiles(PinnedFilesPolicy.ParseActive(_config.PinnedContextFiles)
                .Where(p => !string.Equals(p, path, PathComparer.Comparison)).ToList())));
        var chip         = ThemePalette.For(_isDark, _isHighContrast);
        item.Background  = chip.PinChipBg;
        item.Foreground  = chip.PinChipText;
        item.BorderColor = chip.PinChipBorder;
        PinnedFiles.Add(item);
    }

    /// <param name="active">The pinned set as it is now: the live setting, edited.</param>
    private void SavePinnedFiles(IReadOnlyList<string> active)
    {
        _config.PinnedContextFiles = PinnedFilesPolicy.Serialize(active, _config.PinnedContextFiles);
        LoadPinnedFilesFromConfig();
        try
        {
            _config.Save();
        }
        catch (Exception ex)
        {
            // A locked or read-only config: the chip changed, the file did not — after a reload the pin would be
            // back, or gone, with nothing saying why.
            Diagnostics.Swallow("Chat.SavePinnedFiles", ex);
            InsertThemed(ChatMessageItem.NoticeMsg(Strings.SettingsSaveFailed(ex.Message)));
        }
    }

    #endregion
}
