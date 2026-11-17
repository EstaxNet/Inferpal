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
        if (_indexService.ChunkCount == 0 || _indexService.SearchClient.IsEmbeddingCircuitOpen) return string.Empty;

        var trimmed = userText.Trim();
        if (trimmed.Length < 12 || Services.SlashCommandRouter.LooksLikeCommand(trimmed)) return string.Empty;   // same gate as the shadow

        try
        {
            // Warm shadow (pre-computed while typing) → free; otherwise one bounded embed + search.
            var (_, results) = _indexService.TryGetShadow(trimmed);
            if (results is null || results.Count == 0)
            {
                // The model the index's vectors came from; none (no embedding model) = no auto-context, as before.
                if (_indexService.QueryEmbeddingModel is not { } model) return string.Empty;
                var embedding = await Services.Inference.EmbeddingModels.EmbedCodeQueryAsync(_indexService.SearchClient, model, trimmed, ct);
                if (embedding is null) return string.Empty;
                results = await _indexService.SearchAsync(embedding, trimmed, RagAutoContext.DefaultMaxChunks, ct);
            }
            if (results is null || results.Count == 0) return string.Empty;

            // ⚠ A WHOLE file only: past the cut of a cut chip, the code is in the prompt nowhere. A 🔮 chip counts as
            // its file: it holds the file's best chunks for this very search, and not counted the relevant code sends
            // those same chunks again, ahead of another file's — what it leaves out is the file's lower-ranked ones.
            var attached = attachments
                .Where(a => !string.IsNullOrEmpty(a.SourcePath) && !a.IsCut)
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
        // ⚠ An editor that did not answer is not "no document": it reaches the person as such, translated.
        catch (TimeoutException ex) { throw new TimeoutException(Strings.EditorNoAnswer, ex); }
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
                await RunOnVMContextAsync(() => AddAttachment($"Selection ({fileName})", content, origin: path));
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
        if (Services.Documents.ChatImages.Handles(filePath))
        {
            await AttachImageAsync(filePath, pasted: false, ct);
            return;
        }

        try
        {
            // ⚠ The checks of @file, by its reader: an excluded file, a binary (decoded, it was attached as noise), a PDF or
            // DOCX without text — each said; a document's text extracted. A protected PDF asks for its password first.
            if (MentionController.FileRefusal(filePath, _indexService.RootDir) is null)
                await MentionController.OpenIfProtectedAsync(filePath, _tools.Approval, ct);
            if ((MentionController.FileRefusal(filePath, _indexService.RootDir) ?? await MentionController.BinaryRefusalAsync(filePath, ct)) is { } refused)
            {
                await RunOnVMContextAsync(() => InsertThemed(ChatMessageItem.NoticeMsg(refused)));
                return;
            }
            if (await MentionController.FileMentionTextAsync(filePath, _tools.Overlay, ct) is not { } content)
            {
                await RunOnVMContextAsync(() =>
                    InsertThemed(ChatMessageItem.NoticeMsg(Strings.AttachFileTooLarge)));
                return;
            }
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

    /// <summary>
    /// An image the person attaches — a file picked, dropped or mentioned, or one pasted (<paramref name="pasted"/>, saved by
    /// the in-process half) — read by the Core's one reader: format, size, reduced past 1920 × 1080; refusals said. The chip
    /// says its size, and when the model the question asks does not see images.
    /// </summary>
    private async Task AttachImageAsync(string path, bool pasted, CancellationToken ct)
    {
        try
        {
            if (!pasted && MentionController.FileRefusal(path, _indexService.RootDir) is { } refused)
            {
                await NotifyMentionAsync(refused);
                return;
            }
            var count = 0;
            await RunOnVMContextAsync(() => count = Attachments.Count(a => a.Image is not null));
            if (count >= Services.Documents.ChatImages.MaxPerQuestion)
            {
                await NotifyMentionAsync(Services.Presentation.ImageCards.TooMany);
                return;
            }
            var name = Path.GetFileName(path);
            if (new FileInfo(path).Length > Services.Documents.ChatImages.MaxBytes)
            {
                await NotifyMentionAsync(Strings.ImageTooLarge(name, (int)(Services.Documents.ChatImages.MaxBytes / (1024 * 1024))));
                return;
            }
            var read = Services.Documents.ChatImages.Read(await File.ReadAllBytesAsync(path, ct), name);
            if (read.Image is not { } image)
            {
                await NotifyMentionAsync(read.Refusal ?? string.Empty);
                return;
            }
            var model = await ImageModelAsync(null, ct);
            bool? sees = null;
            try { sees = await _client.SeesImagesAsync(model, ct); }
            catch (System.Net.Http.HttpRequestException ex) { Diagnostics.Swallow("ImageChip.Sees", ex); }
            var chipModel = Services.Presentation.ImageCards.Chip(image, pasted, sees == false ? model : null);
            var detail = chipModel.Warning.Length > 0 ? chipModel.Detail + " · " + chipModel.Warning : chipModel.Detail;
            // A model that does not see: its text goes instead (pari 108) — the chip says so from the moment it is attached,
            // and the banner over the composer offers to see it or to ask a model that sees.
            Services.Presentation.ImageTextModel? ocrText = null;
            IReadOnlyList<string> vision = [];
            if (sees == false
                && (await Task.Run(() => Services.Presentation.ImageCards.Texts([image], ct), ct)).Read is [var text])
            {
                (ocrText, detail) = (text, text.Chip);
                try { vision = await _client.VisionModelsAsync(ct); }
                catch (System.Net.Http.HttpRequestException ex) { Diagnostics.Swallow("ImageChip.Vision", ex); }
            }
            await RunOnVMContextAsync(() =>
            {
                AttachmentItem? item = null;
                item = new AttachmentItem(image.Name, string.Empty, () => Post(() =>
                {
                    Attachments.Remove(item!);
                    HasAttachments = Attachments.Count > 0;
                    RemoveImageText(item!);
                }), origin: pasted ? null : path) { Image = image, ImageFile = path, Detail = detail };
                if (ocrText is not null) AddImageText(item, ocrText, model, vision);
                var chip         = ThemePalette.For(_isDark, _isHighContrast);
                item.Background  = chip.AttachChipBg;
                item.Foreground  = chip.AttachChipText;
                item.BorderColor = chip.AttachChipBorder;
                Attachments.Add(item);
                HasAttachments = true;
            });
            if (read.Note is { } note) await NotifyMentionAsync(note);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await NotifyMentionAsync(Strings.AttachError(ex.Message));
        }
    }

    /// <summary>
    /// The model the next question answers with, as the turn resolves it: a one-time model, else the agent's or the chat's
    /// role, then the held mode's own model when the server has one — what the chip and the not-seen card ask about.
    /// </summary>
    private async Task<string> ImageModelAsync(string? oneTimeModel, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(oneTimeModel)) return oneTimeModel;
        var model = ModelRouter.Resolve(_config, (_config.AgentModeEnabled || _planMode) && _heldMode is null && _toolsEnabled
                                                 ? ModelRole.Agent : ModelRole.Chat);
        if (_heldMode is { } mode) model = (await ChatModes.PickModelAsync(_client, mode, model, ct)).Item1 ?? model;
        return model;
    }

    /// <summary>Adds a chip to the question being written: its text capped, said when cut.</summary>
    /// <param name="label">What the chip and the question show.</param>
    /// <param name="content">The text the chip sends.</param>
    /// <param name="sourcePath">The file a whole-file chip holds — pinnable, and skipped by the auto-context.</param>
    /// <param name="origin">The file the text comes from when it is not <paramref name="sourcePath"/> (a selection).</param>
    private void AddAttachment(string label, string content, string? sourcePath = null, string? origin = null)
    {
        // ⚠ Every chip of a file's text comes here: one the workspace's ignore files exclude is refused, and said.
        if (Services.Governance.ContentExclusions.NoticeFor(origin ?? sourcePath, _indexService.RootDir) is { } excluded)
        {
            InsertThemed(ChatMessageItem.NoticeMsg(excluded));
            return;
        }
        // ⚠ Every chip the user adds comes here: a whole file was sent whatever its size (the active document without any
        // limit), past the window the backend refuses or drops the head, and nothing said so. Cut as VS Code cuts, the
        // cut said to the model (the marker) and to the person (the label, on the chip and under the question).
        var capped = Services.Agent.AttachmentCap.Of(content);
        if (capped.Cut) label = Strings.AttachmentCutLabel(label, capped.Shown, capped.Total);
        content = capped.Text;
        // Attached again — dropped twice, picked again with @: one chip, not two (a second would send it twice), holding
        // what was just attached — a file edited since the first attach goes as it is now.
        var repeat = Attachments.FirstOrDefault(a => !a.IsAutoAttach && a.Repeats(label, content, sourcePath));
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
        }), sourcePath: sourcePath, onPin: onPin, isCut: capped.Cut, origin: origin);
        var chip         = ThemePalette.For(_isDark, _isHighContrast);
        item.Background  = chip.AttachChipBg;
        item.Foreground  = chip.AttachChipText;
        item.BorderColor = chip.AttachChipBorder;
        var at = repeat is null ? -1 : Attachments.IndexOf(repeat);
        if (at >= 0) { Attachments.RemoveAt(at); Attachments.Insert(at, item); }
        else Attachments.Add(item);
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
