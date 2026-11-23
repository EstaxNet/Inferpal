using System.IO;
using System.Text;
using System.Text.Json;
using Inferpal.Localization;

namespace Inferpal.Services.Tools;

/// <summary>
/// Applies several edits across one or more files <em>atomically</em>: every edit is resolved in
/// memory first (via <see cref="ApplyDiffMatcher"/>, so the same whitespace-tolerant matching as
/// <c>apply_diff</c>), and the files are written only if <b>all</b> edits resolve. If any edit cannot
/// be matched, nothing is written. Snapshots each changed file beforehand, so a whole multi-file
/// refactor is revertible via <c>/undo-run</c>.
/// </summary>
internal sealed class ApplyEditsTool : ITool
{
    private readonly IApprovalService   _approval;
    private readonly FileHistoryService _history;
    private readonly SmartFixValidator? _smartFix;
    private readonly Func<string?>      _getWorkspaceRoot;

    private readonly Editor.OpenDocumentOverlay? _overlay;

    public ApplyEditsTool(IApprovalService approval, FileHistoryService history, Func<string?> getWorkspaceRoot, SmartFixValidator? smartFix = null, Editor.OpenDocumentOverlay? overlay = null)
    {
        _approval         = approval;
        _history          = history;
        _getWorkspaceRoot = getWorkspaceRoot;
        _smartFix         = smartFix;
        _overlay         = overlay;
    }

    /// <summary>Whether <c>rename_symbol</c> is offered to the model now; <c>null</c>: assumed. A refused hand rename points
    /// to it only then — pointed to a tool turned off, the model's next call is refused too.</summary>
    internal Func<bool>? RenameOffered { get; init; }

    public string Name => "apply_edits";
    /// <inheritdoc/>
    public ToolEffect EffectOf(JsonElement args) => ToolEffect.Writes;

    /// <inheritdoc/>
    /// <remarks>Each file is backed up before it is written: its writes are in the run already.</remarks>
    public bool NeedsWorkTreeWatch => false;

    /// <inheritdoc/>
    /// <remarks>⚠ Not what the repository's own check, run after the write, changes (<see
    /// cref="SmartFixValidator.MayRunWorkspaceCommandFor"/>): that call is watched.</remarks>
    public bool NeedsWorkTreeWatchFor(JsonElement args) => _smartFix?.MayRunWorkspaceCommandFor(Name, args) == true;

    public string Description =>
        "Applies multiple edits across one or more files atomically (all-or-nothing): each edit " +
        "replaces old_content with new_content in its file (whitespace-tolerant, like apply_diff). " +
        "If ANY edit cannot be applied, NO file is changed. Use for coordinated refactors that span " +
        "several files (e.g. rename across call sites). Files are snapshotted so /undo-run reverts the whole set. " +
        "To apply a patch file (.patch, .diff) instead, send patch with its path and no edits: each block is placed where " +
        "its lines stand, new files are created, and the blocks that no longer apply are left out and returned to you.";

    public object Parameters => new
    {
        type = "object",
        properties = new
        {
            edits = new
            {
                type        = "array",
                description = "List of edits, applied in order. Several edits may target the same file.",
                items = new
                {
                    type = "object",
                    properties = new
                    {
                        path        = new { type = "string", description = "Absolute path to the file to edit." },
                        old_content = new { type = "string", description = "Text to replace." },
                        new_content = new { type = "string", description = "Replacement text." },
                        occurrence  = new { type = "string", description = "'unique' (default), 'first', or 'all'." },
                    },
                    required = new[] { "path", "old_content", "new_content" },
                },
            },
            patch = new { type = "string", description = "Path of a patch file (.patch, .diff) to apply, instead of edits." },
        },
    };

    private sealed record Edit(string Path, string Old, string New, string? Occurrence);

    public async Task<string> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        // ⚠ Two outcomes, not one. "You sent no edits" and "your edits were not a list" do not
        // send the model to the same place: told the first for a double-encoded list — the shape a
        // small model emits — it resends the same payload. The per-entry shape check below already
        // names its offender; this is the same event one level up.
        if (args.Has("patch"))
            return args.Has("edits")
                ? ToolFailure.Said(Strings.ApplyEditsAborted("send either edits or patch, not both"))
                : await ApplyPatchAsync(args.Str("patch"), ct);
        if (!args.Has("edits"))
            return Strings.ApplyEditsEmpty;
        if (!args.Array("edits", out var editsEl))
            return ToolFailure.Said(Strings.ApplyEditsAborted("'edits' must be an array of edit objects"));

        var root  = _getWorkspaceRoot();
        var edits = new List<Edit>();
        var index = 0;
        var total = editsEl.GetArrayLength();
        foreach (var e in editsEl.EnumerateArray())
        {
            index++;

            // ⚠ A malformed entry aborts the batch; `continue`d away, the answer reports the
            // success of the SURVIVORS — "Applied 3 edit(s)" on a batch of 5, with the two dropped
            // ones invisible to the model, while the description IT reads promises the opposite
            // word for word: "If ANY edit cannot be applied, NO file is changed". Same event and
            // same wording as the matching failure below.
            if (e.ValueKind != JsonValueKind.Object)
                return Aborted(index, total, $"edit #{index} is not an object");

            // No null check on `path`: Sanitize already throws a readable, localised message when
            // it is missing (ToolPathRequired), so a check here could never fire.
            var path = PathSanitizer.Sanitize(e.Str("path"), root);
            PathSanitizer.AssertUnderRoot(path, root);

            var old = e.Str("old_content");
            var neu = e.Str("new_content");
            if (old is null)
                return Aborted(index, total,
                    $"edit #{index} in {RelPath(root, path)}: 'old_content' is missing or is not a string");
            if (neu is null)
                return Aborted(index, total,
                    $"edit #{index} in {RelPath(root, path)}: 'new_content' is missing or is not a string "
                    + "(send \"\" to delete the matched block)");

            // Same vocabulary as apply_diff, same reader. Aborted like any malformed edit: an
            // unrecognised value became 'unique' and the whole batch came back with
            // "ambiguous (N matches)", which blames a perfectly correct old_content.
            var occurrence = e.Keyword("occurrence");
            if (ApplyDiffMatcher.RejectOccurrence(occurrence) is { } badOccurrence)
                return Aborted(index, total,
                    $"edit #{index} in {RelPath(root, path)}: {badOccurrence}");

            edits.Add(new Edit(path, old, neu, occurrence));
        }
        if (edits.Count == 0) return Strings.ApplyEditsEmpty;

        // ── Phase 1: resolve ALL edits in memory (nothing written yet) ─────────
        // ⚠ PathComparer, not OrdinalIgnoreCase: under Linux `A.cs` and `a.cs` are two files, and
        // confusing them here applied the edit to one file's content and then wrote it under the
        // other's name. This is the one site of that class which loses data.
        var current  = new Dictionary<string, string>(PathComparer.Default);
        var original = new Dictionary<string, string>(PathComparer.Default);
        var copies   = new Dictionary<string, CopiedContext.Copy>(PathComparer.Default);   // the first copy per file

        for (int i = 0; i < edits.Count; i++)
        {
            var edit = edits[i];
            if (!current.ContainsKey(edit.Path))
            {
                if (FileTarget.DirectoryRefusal(edit.Path) is { } isDirectory) return isDirectory;
                if (FileTarget.UnsavedRefusal(_overlay, edit.Path) is { } unsaved) return unsaved;
                if (GeneratedFiles.EditRefusal(edit.Path) is { } generated) return generated;
                if (!File.Exists(edit.Path)) return ToolFailure.Said(Strings.ToolFileNotFound(edit.Path));
                var content = await TextFileEncoding.ReadTextAsync(edit.Path, ct);
                current[edit.Path]  = content;
                original[edit.Path] = content;
            }

            var res = ApplyDiffMatcher.Resolve(current[edit.Path], edit.Old, edit.New, edit.Occurrence);
            if (res.Modified is null)
            {
                var reason = res.Count > 1 ? $"ambiguous ({res.Count} matches)" : "no exact or fuzzy match";
                var rel    = RelPath(root, edit.Path);
                var hint   = RenameOffered?.Invoke() != false && RenameIntent.Of(edits.Select(e => (e.Old, e.New))) is { } rename
                    ? RenameIntent.RefusalHint(rename) : string.Empty;
                var closest = res.Count > 1 ? null : ApplyDiffMatcher.Closest(current[edit.Path], edit.Old);
                return Aborted(i + 1, edits.Count, $"edit #{i + 1} in {rel}: {reason} for old_content") + closest + hint;
            }
            // Judged against what the earlier edits of the batch made of the file, as the match was.
            if (!copies.ContainsKey(edit.Path) && CopiedContext.Find(current[edit.Path], res.Zones, edit.New) is { } copy)
                copies[edit.Path] = copy;
            current[edit.Path] = res.Modified;
        }

        // Only the files whose content actually changed get written.
        var changed = current.Where(kv => !string.Equals(kv.Value, original[kv.Key], StringComparison.Ordinal))
                             .Select(kv => kv.Key).ToList();
        if (changed.Count == 0) return Strings.ApplyEditsOk(edits.Count, 0);
        foreach (var p in changed)
            if (FileTarget.EncodingRefusal(p, current[p]) is { } cannotHold) return ToolFailure.Said(Strings.ApplyEditsAborted(cannotHold));
        // All or nothing, like every other refusal of the batch: one file left unparsable, or holding lines twice, refuses
        // them all. ⚠ Every file is judged, never up to the first refusal: each refusal is let through once sent again,
        // so the same batch sent again passes whole instead of being refused file after file.
        var broken = new List<string>();
        foreach (var p in changed)
            if (await SyntaxGuard.RefusalAsync(p, original[p], current[p], root, _history.CurrentRunId, ct,
                    copies.GetValueOrDefault(p)) is { } refusal) broken.Add(refusal);
        if (broken.Count > 0) return ToolFailure.Said(Strings.ApplyEditsAborted(string.Join("\n", broken)));

        // ── Approval: one prompt with the combined diff across files ───────────
        var details = BuildApprovalDetails(root, changed, original, current);
        var subject = string.Join("\n", changed);   // permission rules match any affected path
        // Each file's own change travels with the batch: a /task proposal is applied file by file, from its diff.
        var files = changed.Select(p => new FileChange(new DiffInfo(original[p], current[p], p),
                                                       BuildApprovalDetails(root, [p], original, current))).ToList();
        // The designer's file in the batch: the person decides, warned, on a card no rule approves.
        var designers = changed.Select(GeneratedFiles.DesignerWarning).OfType<string>().ToList();
        if (designers.Count > 0) details = string.Join("\n", designers) + "\n\n" + details;
        if (!await _approval.RequestBatchApprovalAsync("apply_edits", details, files, ct, subject: subject, forcePrompt: designers.Count > 0))
            return Strings.DiffCancelled;
        // Before the first backup: the batch stays all or nothing.
        if (await FileTarget.ChangedSinceApprovalAsync(_overlay, changed.Select(p => new ApprovedFile(p, original[p])), ct) is { } typedMeanwhile)
            return ToolFailure.Said(Strings.ApplyEditsAborted(typedMeanwhile));

        // ── Phase 2: back up EVERY file, then write (all edits already validated) ─
        // A backup that cannot be saved stops the batch before anything is written.
        foreach (var path in changed)
        {
            var (saved, _) = await _history.BackUpBeforeChangeAsync(path, ct);   // recorded in the active run → /undo-run
            if (!saved) return _history.BackupFailedMessage(path);
        }

        // Approved and backed up: the writes no longer observe cancellation, so a Stop cannot leave
        // half the batch applied. ⚠ A write that FAILS puts back the files already written — the
        // description promises the model "if ANY edit cannot be applied, NO file is changed".
        // ⚠ Funnel shared with rename_symbol, which carried the opposite failure: it collected the
        // error and carried on. The all-or-nothing property lives in the writer, not copied into
        // each tool — that is what makes the third one inherit it.
        var write = await SafeFileWriter.WriteAllOrRollBackAsync(
            [.. changed.Select(p => (p, current[p], original[p]))]);

        if (!write.Ok) return WriteFailed(write, root);

        // Smart Fix once: building any edited file validates its project (covers same-project edits).
        var smartFixNote = _smartFix is not null
            ? "\n\n" + (await _smartFix.ValidateManyAsync(changed, ct) ?? string.Empty)
            : string.Empty;

        var staleNote = RenameIntent.Of(edits.Select(e => (e.Old, e.New))) is { } renamed && !string.IsNullOrEmpty(root)
            ? RenameIntent.StaleNote(renamed, root, changed[0], RenameOffered?.Invoke() != false)
            : string.Empty;

        return Strings.ApplyEditsOk(edits.Count, changed.Count) + staleNote + smartFixNote.TrimEnd();
    }

    /// <summary>
    /// The patch at <paramref name="patchArg"/> applied: each file's blocks placed where their lines stand (<see
    /// cref="Documents.PatchText.Apply"/>), new files created, on ONE approval card, backed up for <c>/undo-run</c>.
    /// </summary>
    /// <remarks>
    /// ⚠ Not all or nothing, unlike edits: a patch made on another state of the code has blocks that no longer apply, and
    /// refusing it whole leaves the person doing every block by hand. What is written is what the card shows; what is left
    /// out — a block, a deletion, a rename, a binary file, a file the workspace keeps out — is named on the card and handed
    /// back to the model with its text. A patch path is judged as every written path is (<see cref="Documents.PatchText.Locate"/>).
    /// </remarks>
    private async Task<string> ApplyPatchAsync(string? patchArg, CancellationToken ct)
    {
        var root = _getWorkspaceRoot();
        var patchPath = PathSanitizer.Sanitize(patchArg, root);
        // Read as read_file reads: inside the workspace only — the blocks that do not apply go back to the model verbatim.
        PathSanitizer.AssertUnderRoot(patchPath, root);
        if (!File.Exists(patchPath)) return ToolFailure.Said(Strings.ToolFileNotFound(patchPath));
        var patchText = _overlay is not null && _overlay.TryGetUnsaved(patchPath, out var buffered)
            ? buffered : await TextFileEncoding.ReadTextAsync(patchPath, ct);
        if (Documents.PatchText.Parse(patchText) is not { } patch)
            return ToolFailure.Said(Strings.ApplyEditsAborted(
                $"{Path.GetFileName(patchPath)} holds no file diff (no 'diff --git', or '---' and '+++' lines followed by '@@' blocks)"));

        var original = new Dictionary<string, string?>(PathComparer.Default);   // null: the file does not exist yet
        var current  = new Dictionary<string, string>(PathComparer.Default);
        var leftOut  = new List<string>();      // for the card and the model: what the patch asks that is not written
        var rejected = new List<string>();      // for the model: each block not placed, with its text
        var placed   = 0;
        var located  = patch.Files.Select(f => (File: f, Where: Documents.PatchText.Locate(f, root))).ToList();
        if (_overlay is not null)
            await _overlay.RefreshAsync(ct, located.Select(l => l.Where.Path).OfType<string>().Where(File.Exists).ToList());

        foreach (var (file, (path, why)) in located)
        {
            ct.ThrowIfCancellationRequested();
            var name = file.Path;
            switch (file.Kind)
            {
                case Documents.PatchText.FileKind.Delete:
                    leftOut.Add($"{name}: deleted by the patch, not done here (delete_file deletes it)");
                    continue;
                case Documents.PatchText.FileKind.Binary:
                    leftOut.Add($"{name}: a binary change, whose content the patch text does not hold");
                    continue;
                case Documents.PatchText.FileKind.ModeOnly:
                    leftOut.Add($"{name}: only its file mode changes, not done here");
                    continue;
                case Documents.PatchText.FileKind.Copy:
                    leftOut.Add($"{file.NewPath}: a copy of {file.OldPath} with changes, not done here (copy the file, then edit the copy)");
                    continue;
                case Documents.PatchText.FileKind.Rename:
                    leftOut.Add($"{file.OldPath} → {file.NewPath}: the rename is not done here (move the file with a command)"
                              + (file.Hunks.Count > 0 ? "; its content changes are applied under its current name" : ""));
                    if (file.Hunks.Count == 0) continue;
                    break;
            }
            if (file.ModeChanged) leftOut.Add($"{name}: its file mode change is not done here");
            if (why is not null) { leftOut.Add($"{name}: not touched, {why}"); continue; }
            if (path is null) { leftOut.Add($"{name}: not in the workspace"); continue; }

            if (!current.ContainsKey(path))
            {
                var exists = File.Exists(path);
                if (exists || file.Kind != Documents.PatchText.FileKind.Add)
                {
                    if (!exists) { leftOut.Add($"{name}: not in the workspace"); continue; }
                    if (FileTarget.DirectoryRefusal(path) is { } isDirectory) { leftOut.Add($"{name}: {isDirectory}"); continue; }
                    if (FileTarget.UnsavedRefusal(_overlay, path) is { } unsaved) { leftOut.Add($"{name}: {unsaved}"); continue; }
                    if (GeneratedFiles.EditRefusal(path) is { } generated) { leftOut.Add($"{name}: {generated}"); continue; }
                    if (TextFileEncoding.IsBinaryFile(path)) { leftOut.Add($"{name}: a binary file in the workspace"); continue; }
                    var text = await TextFileEncoding.ReadTextAsync(path, ct);
                    original[path] = text;
                    current[path]  = text;
                }
                else
                {
                    original[path] = null;
                    current[path]  = "";
                }
            }
            // ⚠ Any file already there, changed by an earlier commit of the series or not: an add never lands on top of it.
            if (file.Kind == Documents.PatchText.FileKind.Add && (original[path] is not null || current[path].Length > 0))
            {
                // A file the patch adds that is already here: never overwritten, said, unless it already holds the result.
                var added = Documents.PatchText.Apply("", file).Content;
                leftOut.Add(string.Equals(added.ReplaceLineEndings("\n"), current[path].ReplaceLineEndings("\n"), StringComparison.Ordinal)
                    ? $"{name}: already holds what the patch adds"
                    : $"{name}: the patch creates it, and it already exists — not overwritten");
                continue;
            }
            var (content, results) = Documents.PatchText.Apply(current[path], file);
            current[path] = content;
            for (var k = 0; k < results.Count; k++)
            {
                var r = results[k];
                switch (r.Outcome)
                {
                    case Documents.PatchText.Outcome.Applied or Documents.PatchText.Outcome.Shifted:
                        placed++;
                        break;
                    case Documents.PatchText.Outcome.AlreadyApplied:
                        leftOut.Add($"{name}: block {k + 1} (line {r.Hunk.OldStart}) is already applied");
                        break;
                    default:
                        leftOut.Add($"{name}: block {k + 1} (line {r.Hunk.OldStart}) no longer applies");
                        rejected.Add($"{name}, block {k + 1}:\n{r.Hunk.Text}");
                        break;
                }
            }
        }

        var changed = current.Where(kv => !string.Equals(kv.Value, original[kv.Key], StringComparison.Ordinal))
                             .Select(kv => kv.Key).ToList();
        var notes = leftOut.Count == 0 ? "" : "\nNot written:\n" + string.Join("\n", leftOut.Select(l => "- " + l));
        var handBack = rejected.Count == 0 ? ""
            : "\n\nThe blocks that no longer apply, as the patch writes them — apply each by hand with apply_diff on the file as "
              + "it is now, or tell the user it is left out:\n" + string.Join("\n\n", rejected);
        if (changed.Count == 0)
            return "Nothing written: no block of the patch applies to the workspace as it is." + notes + handBack;
        foreach (var p in changed)
            if (FileTarget.EncodingRefusal(p, current[p]) is { } cannotHold) return ToolFailure.Said(Strings.ApplyEditsAborted(cannotHold));
        var broken = new List<string>();
        foreach (var p in changed)
            if (await SyntaxGuard.RefusalAsync(p, original[p], current[p], root, _history.CurrentRunId, ct) is { } refusal) broken.Add(refusal);
        if (broken.Count > 0) return ToolFailure.Said(Strings.ApplyEditsAborted(string.Join("\n", broken))) + notes;

        var before = changed.ToDictionary(p => p, p => original[p] ?? "", PathComparer.Default);
        var details = BuildApprovalDetails(root, changed, before, current);
        if (leftOut.Count > 0) details = Strings.ApplyPatchLeftOut(string.Join("; ", leftOut)) + "\n\n" + details;
        var files = changed.Select(p => new FileChange(new DiffInfo(before[p], current[p], p),
                                                       BuildApprovalDetails(root, [p], before, current))).ToList();
        var designers = changed.Select(GeneratedFiles.DesignerWarning).OfType<string>().ToList();
        if (designers.Count > 0) details = string.Join("\n", designers) + "\n\n" + details;
        if (!await _approval.RequestBatchApprovalAsync("apply_edits", details, files, ct, subject: string.Join("\n", changed),
                                                       forcePrompt: designers.Count > 0))
            return Strings.DiffCancelled;
        if (await FileTarget.ChangedSinceApprovalAsync(_overlay, changed.Select(p => new ApprovedFile(p, original[p])), ct) is { } typedMeanwhile)
            return ToolFailure.Said(Strings.ApplyEditsAborted(typedMeanwhile));

        foreach (var path in changed)
        {
            var (saved, _) = await _history.BackUpBeforeChangeAsync(path, ct);   // a created file is recorded as created
            if (!saved) return _history.BackupFailedMessage(path);
        }
        try
        {
            foreach (var path in changed.Where(p => original[p] is null))
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ToolFailure.Said(Strings.ApplyEditsAborted($"a folder for a new file could not be created ({ex.Message})"));
        }
        var write = await SafeFileWriter.WriteAllOrRollBackAsync([.. changed.Select(p => (p, current[p], before[p]))]);
        if (!write.Ok) return WriteFailed(write, root);
        var smartFixNote = _smartFix is not null ? "\n\n" + (await _smartFix.ValidateManyAsync(changed, ct) ?? string.Empty) : string.Empty;
        return $"Patch applied: {placed} block{(placed == 1 ? "" : "s")} in {changed.Count} file{(changed.Count == 1 ? "" : "s")} "
             + $"({string.Join(", ", changed.Select(p => RelPath(root, p)))})." + notes + handBack + smartFixNote.TrimEnd();
    }

    private static string BuildApprovalDetails(
        string? root, List<string> changed, Dictionary<string, string> original, Dictionary<string, string> current)
    {
        var sb = new StringBuilder();
        sb.Append(Strings.ApplyEditsConfirm(changed.Count));
        foreach (var path in changed)
        {
            sb.Append("\n\n### ").Append(RelPath(root, path));
            var diff = DiffComputer.ComputeText(original[path], current[path], maxLines: 20);
            if (diff is not null) sb.Append('\n').Append(diff);
        }
        return sb.ToString();
    }

    /// <summary>What the model is told when writing the lot failed: what was not written, and what could not be put back.</summary>
    internal static string WriteFailed(SafeFileWriter.MultiWriteResult write, string? root)
    {
        var reason = $"writing {RelPath(root, write.FailedPath!)} failed ({write.Error})";
        return write.Stuck.Count == 0
            ? ToolFailure.Said(Strings.ApplyEditsAborted(reason))
            : $"Error: {reason}, and {string.Join(", ", write.Stuck.Select(s => RelPath(root, s)))} "
              + $"could not be put back — restore {(write.Stuck.Count == 1 ? "it" : "them")} with restore_file when it is offered; otherwise tell the user: /undo-run puts the run's files back.";
    }

    // A path as every message names it: relative with forward slashes under the workspace, whole outside it.
    private static string RelPath(string? root, string path) => Governance.ContentExclusions.Shown(path, root);

    /// <summary>
    /// The refusal of a batch because of edit #<paramref name="failed"/> — naming, in a batch of several, the edits
    /// that were not written either and asking for all of them again.
    /// </summary>
    /// <remarks>
    /// ⚠ Told only which edit failed, a model corrects that one and sends it alone: the batch's other edits — correct
    /// ones — are never written, and the model answers that the rename is done. "No changes applied" states the fact;
    /// naming the edits it covers is what gets them sent again.
    /// </remarks>
    private static string Aborted(int failed, int total, string reason)
    {
        if (total <= 1) return ToolFailure.Said(Strings.ApplyEditsAborted(reason));
        var others = string.Join(", ", Enumerable.Range(1, total).Where(i => i != failed).Select(i => $"#{i}"));
        return ToolFailure.Said(Strings.ApplyEditsAborted(
            $"{reason}; the other {total - 1} edit(s) ({others}) were not written either. "
            + $"Send all {total} edits again in one apply_edits call, with edit #{failed} corrected"));
    }
}
