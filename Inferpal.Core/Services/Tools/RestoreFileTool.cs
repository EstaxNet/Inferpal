using System.IO;
using System.Text.Json;
using Inferpal.Localization;

namespace Inferpal.Services.Tools;

internal class RestoreFileTool : ITool
{
    private readonly IApprovalService   _approval;
    private readonly FileHistoryService _history;
    private readonly Func<string?>      _getWorkspaceRoot;

    public RestoreFileTool(IApprovalService approval, FileHistoryService history, Func<string?> getWorkspaceRoot)
    {
        _approval         = approval;
        _history          = history;
        _getWorkspaceRoot = getWorkspaceRoot;
    }

    public string Name        => "restore_file";
    public string Description =>
        "Restores a file from a previously saved backup snapshot in .inferpal/history/. " +
        "If no snapshot path is provided, uses the most recent backup. " +
        "Use this to undo changes made by write_file or apply_diff.";

    public object Parameters => new
    {
        type = "object",
        properties = new
        {
            path     = new { type = "string", description = "Absolute path to the file to restore." },
            snapshot = new { type = "string", description = "(Optional) Absolute path to a specific snapshot file. If omitted, uses the most recent backup." }
        },
        required = new[] { "path" }
    };

    public async Task<string> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        // Restoring OVERWRITES the target with the snapshot's content: same guardrails as
        // write_file — workspace confinement (target AND source snapshot, so the model can't
        // copy an arbitrary on-disk file into/over anything) + diff approval + pre-snapshot.
        var root = _getWorkspaceRoot();
        var path = PathSanitizer.Sanitize(args.GetProperty("path").GetString());
        PathSanitizer.AssertUnderRoot(path, root);

        string? snapPath = null;
        if (args.TryGetProperty("snapshot", out var snapEl) && snapEl.ValueKind == JsonValueKind.String)
        {
            var snapRaw = snapEl.GetString();
            if (snapRaw is not null)
            {
                snapPath = PathSanitizer.Sanitize(snapRaw);
                PathSanitizer.AssertUnderRoot(snapPath, root);
            }
        }

        snapPath ??= _history.FindMostRecentSnapshot(path);

        if (snapPath is null || !File.Exists(snapPath))
            return Strings.RestoreNotFound(path);

        // Show the actual change in the approval prompt (best-effort for binary snapshots).
        var details = Strings.DiffConfirm(path);
        try
        {
            var current  = File.Exists(path) ? await File.ReadAllTextAsync(path, ct) : string.Empty;
            var restored = await File.ReadAllTextAsync(snapPath, ct);
            var diffText = DiffComputer.ComputeText(current, restored);
            if (diffText is not null) details += "\n\n" + diffText;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { Diagnostics.Swallow("RestorePreviewDiff", ex); }

        if (!await _approval.RequestApprovalAsync("restore_file", details, ct, subject: path))
            return Strings.DiffCancelled;

        // Snapshot the current content first so the restore itself is undoable.
        if (File.Exists(path))
            await _history.SnapshotAsync(path, ct);

        await _history.RestoreAsync(snapPath, path, ct);
        return Strings.RestoreOk(path, snapPath);
    }
}
