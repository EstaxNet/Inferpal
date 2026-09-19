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

    public ApplyEditsTool(IApprovalService approval, FileHistoryService history, Func<string?> getWorkspaceRoot, SmartFixValidator? smartFix = null)
    {
        _approval         = approval;
        _history          = history;
        _getWorkspaceRoot = getWorkspaceRoot;
        _smartFix         = smartFix;
    }

    public string Name => "apply_edits";

    public string Description =>
        "Applies multiple edits across one or more files atomically (all-or-nothing): each edit " +
        "replaces old_content with new_content in its file (whitespace-tolerant, like apply_diff). " +
        "If ANY edit cannot be applied, NO file is changed. Use for coordinated refactors that span " +
        "several files (e.g. rename across call sites). Files are snapshotted so /undo-run reverts the whole set.";

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
        },
        required = new[] { "edits" },
    };

    private sealed record Edit(string Path, string Old, string New, string? Occurrence);

    public async Task<string> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        // ⚠ Two outcomes, not one. "You sent no edits" and "your edits were not a list" do not
        // send the model to the same place: told the first for a double-encoded list — the shape a
        // small model emits — it resends the same payload. The per-entry shape check below already
        // names its offender; this is the same event one level up.
        if (!args.Has("edits"))
            return Strings.ApplyEditsEmpty;
        if (!args.Array("edits", out var editsEl))
            return Strings.ApplyEditsAborted("'edits' must be an array of edit objects");

        var root  = _getWorkspaceRoot();
        var edits = new List<Edit>();
        var index = 0;
        foreach (var e in editsEl.EnumerateArray())
        {
            index++;

            // ⚠ A malformed entry used to be `continue`d away, and the answer then reported the
            // success of the SURVIVORS: "Applied 3 edit(s)" on a batch of 5. The model cannot see
            // the two that were dropped -- and the description IT reads promises the opposite,
            // word for word: "If ANY edit cannot be applied, NO file is changed". The matching
            // failure below already aborts the whole batch naming the offending edit; a read
            // failure is the same event and is said the same way.
            if (e.ValueKind != JsonValueKind.Object)
                return Strings.ApplyEditsAborted($"edit #{index} is not an object");

            // No null check on `path`: Sanitize already throws a readable, localised message when
            // it is missing (ToolPathRequired). The check that used to be here could never fire.
            var path = PathSanitizer.Sanitize(e.Str("path"), root);
            PathSanitizer.AssertUnderRoot(path, root);

            var old = e.Str("old_content");
            var neu = e.Str("new_content");
            if (old is null)
                return Strings.ApplyEditsAborted(
                    $"edit #{index} in {RelPath(root, path)}: 'old_content' is missing or is not a string");
            if (neu is null)
                return Strings.ApplyEditsAborted(
                    $"edit #{index} in {RelPath(root, path)}: 'new_content' is missing or is not a string "
                    + "(send \"\" to delete the matched block)");

            // Same vocabulary as apply_diff, same reader. Aborted like any malformed edit: an
            // unrecognised value became 'unique' and the whole batch came back with
            // "ambiguous (N matches)", which blames a perfectly correct old_content.
            var occurrence = e.Keyword("occurrence");
            if (ApplyDiffMatcher.RejectOccurrence(occurrence) is { } badOccurrence)
                return Strings.ApplyEditsAborted(
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

        for (int i = 0; i < edits.Count; i++)
        {
            var edit = edits[i];
            if (!current.ContainsKey(edit.Path))
            {
                if (!File.Exists(edit.Path)) return Strings.ToolFileNotFound(edit.Path);
                var content = await File.ReadAllTextAsync(edit.Path, ct);
                current[edit.Path]  = content;
                original[edit.Path] = content;
            }

            var res = ApplyDiffMatcher.Resolve(current[edit.Path], edit.Old, edit.New, edit.Occurrence);
            if (res.Modified is null)
            {
                var reason = res.Count > 1 ? $"ambiguous ({res.Count} matches)" : "no exact or fuzzy match";
                var rel    = RelPath(root, edit.Path);
                return Strings.ApplyEditsAborted($"edit #{i + 1} in {rel}: {reason} for old_content");
            }
            current[edit.Path] = res.Modified;
        }

        // Only the files whose content actually changed get written.
        var changed = current.Where(kv => !string.Equals(kv.Value, original[kv.Key], StringComparison.Ordinal))
                             .Select(kv => kv.Key).ToList();
        if (changed.Count == 0) return Strings.ApplyEditsOk(edits.Count, 0);

        // ── Approval: one prompt with the combined diff across files ───────────
        var details = BuildApprovalDetails(root, changed, original, current);
        var subject = string.Join("\n", changed);   // permission rules match any affected path
        if (!await _approval.RequestApprovalAsync("apply_edits", details, ct, subject: subject))
            return Strings.DiffCancelled;

        // ── Phase 2: back up EVERY file, then write (all edits already validated) ─
        // A backup that cannot be saved stops the batch before anything is written.
        foreach (var path in changed)
        {
            var (saved, _) = await _history.BackUpBeforeChangeAsync(path, ct);   // recorded in the active run → /undo-run
            if (!saved) return FileHistoryService.BackupFailedMessage(path);
        }

        // Approved and backed up: the writes no longer observe cancellation, so a Stop cannot leave
        // half the batch applied. ⚠ A write that FAILS puts back the files already written — the
        // description promises the model "if ANY edit cannot be applied, NO file is changed".
        // ⚠ Funnel shared with rename_symbol, which carried the opposite failure: it collected the
        // error and carried on. The all-or-nothing property lives in the writer, not copied into
        // each tool — that is what makes the third one inherit it.
        var write = await SafeFileWriter.WriteAllOrRollBackAsync(
            [.. changed.Select(p => (p, current[p], original[p]))]);

        if (!write.Ok)
        {
            var reason = $"writing {RelPath(root, write.FailedPath!)} failed ({write.Error})";
            return write.Stuck.Count == 0
                ? Strings.ApplyEditsAborted(reason)
                : $"Error: {reason}, and {string.Join(", ", write.Stuck.Select(s => RelPath(root, s)))} "
                  + "could not be put back — restore it with restore_file.";
        }

        // Smart Fix once: building any edited file validates its project (covers same-project edits).
        var smartFixNote = _smartFix is not null
            ? "\n\n" + (await _smartFix.ValidateManyAsync(changed, ct) ?? string.Empty)
            : string.Empty;

        return Strings.ApplyEditsOk(edits.Count, changed.Count) + smartFixNote.TrimEnd();
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

    private static string RelPath(string? root, string path) =>
        string.IsNullOrEmpty(root) ? path : Path.GetRelativePath(root, path);
}
