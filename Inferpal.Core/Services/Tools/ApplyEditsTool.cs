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
        if (!args.TryGetProperty("edits", out var editsEl) || editsEl.ValueKind != JsonValueKind.Array)
            return Strings.ApplyEditsEmpty;

        var root  = _getWorkspaceRoot();
        var edits = new List<Edit>();
        foreach (var e in editsEl.EnumerateArray())
        {
            if (e.ValueKind != JsonValueKind.Object) continue;
            var path = PathSanitizer.Sanitize(e.TryGetProperty("path", out var p) ? p.GetString() : null);
            PathSanitizer.AssertUnderRoot(path, root);
            var old = e.TryGetProperty("old_content", out var o) ? o.GetString() : null;
            var neu = e.TryGetProperty("new_content", out var n) ? n.GetString() : null;
            var occ = e.TryGetProperty("occurrence",  out var c) ? c.GetString() : null;
            if (string.IsNullOrEmpty(path) || old is null || neu is null) continue;
            edits.Add(new Edit(path, old, neu, occ));
        }
        if (edits.Count == 0) return Strings.ApplyEditsEmpty;

        // ── Phase 1: resolve ALL edits in memory (nothing written yet) ─────────
        var current  = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var original = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

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

        // ── Phase 2: snapshot + write (all edits already validated) ────────────
        foreach (var path in changed)
        {
            await _history.SnapshotAsync(path, ct);   // recorded in the active run → /undo-run
            await File.WriteAllTextAsync(path, current[path], ct);
        }

        // Smart Fix once: building any edited file validates its project (covers same-project edits).
        var smartFixNote = _smartFix is not null
            ? "\n\n" + (await _smartFix.ValidateAsync(changed[0], ct) ?? string.Empty)
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
