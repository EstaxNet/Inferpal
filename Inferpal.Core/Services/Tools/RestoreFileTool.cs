using System.IO;
using System.Text.Json;
using Inferpal.Localization;

namespace Inferpal.Services.Tools;

internal class RestoreFileTool : ITool
{
    private readonly IApprovalService   _approval;
    private readonly FileHistoryService _history;
    private readonly Func<string?>      _getWorkspaceRoot;

    private readonly Editor.OpenDocumentOverlay? _overlay;

    public RestoreFileTool(IApprovalService approval, FileHistoryService history, Func<string?> getWorkspaceRoot, Editor.OpenDocumentOverlay? overlay = null)
    {
        _approval         = approval;
        _history          = history;
        _getWorkspaceRoot = getWorkspaceRoot;
        _overlay         = overlay;
    }

    public string Name        => "restore_file";

    /// <inheritdoc/>
    /// <remarks>Each file is backed up before it is written: its writes are in the run already.</remarks>
    public bool NeedsWorkTreeWatch => false;
    /// <inheritdoc/>
    /// <remarks>⚠ No folder is named: where the backups are kept is a setting (in the project, or outside it where no
    /// tool of the model can list them), and a description that names one sends a model looking there — it finds
    /// nothing and answers that no backup exists. This tool finds them itself, wherever they are.</remarks>
    public string Description =>
        "Restores a file from a backup snapshot Inferpal saved before changing it — every write takes one and names " +
        "it in its result. If no snapshot path is provided, uses the most recent backup: you do not need to find it, " +
        "and the backups may be kept outside the workspace, where other tools cannot list them. " +
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
        var path = PathSanitizer.Sanitize(args.Str("path"), root);
        PathSanitizer.AssertUnderRoot(path, root);
        if (FileTarget.DirectoryRefusal(path) is { } isDirectory) return isDirectory;
        if (FileTarget.UnsavedRefusal(_overlay, path) is { } unsaved) return unsaved;

        string? snapPath = null;
        if (args.Str("snapshot") is { } snapRaw)
        {
            snapPath = PathSanitizer.Sanitize(snapRaw, root);
            // A snapshot of THIS file in its history folder is acceptable even outside the root (the
            // history lives at the git root, or outside the project, and every write hands the model
            // that path); any other source file must be inside the workspace.
            if (!_history.IsSnapshotOf(snapPath, path))
            {
                PathSanitizer.AssertUnderRoot(snapPath, root);
                // ⚠ Another file of the workspace as the source copies ITS content into the target: one the ignore files
                // keep out of AI tools would be read back through the file it was restored into.
                if (Governance.ContentExclusions.Of(snapPath) is { } excluded)
                    return excluded.ForModel(snapPath);
            }
        }

        snapPath ??= await _history.FindRestoreCandidateAsync(path, ct);

        if (snapPath is null || !File.Exists(snapPath))
            return Strings.RestoreNotFound(path);

        // Show the actual change in the approval prompt (best-effort for binary snapshots).
        var shown   = ApprovedFile.Unread(path);
        DiffInfo? change = null;
        try
        {
            var exists   = File.Exists(path);
            var current  = exists ? await TextFileEncoding.ReadTextAsync(path, ct) : string.Empty;
            shown        = exists ? new ApprovedFile(path, current) : ApprovedFile.Absent(path);
            if (!TextFileEncoding.IsBinaryFile(snapPath) && !(exists && TextFileEncoding.IsBinaryFile(path)))
                change = new DiffInfo(current, await TextFileEncoding.ReadTextAsync(snapPath, ct), path);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { Diagnostics.Swallow("RestorePreviewDiff", ex); }

        if (!await _approval.RequestApprovalAsync("restore_file", Strings.DiffConfirm(path), ct, subject: path, diff: change))
            return Strings.DiffCancelled;
        if (await FileTarget.ChangedSinceApprovalAsync(_overlay, shown, ct) is { } changedMeanwhile) return changedMeanwhile;

        // Snapshot the current content first so the restore itself is undoable.
        var (saved, preRestore) = await _history.RestoreWithBackupAsync(snapPath, path, ct);
        if (!saved) return _history.BackupFailedMessage(path);
        // The next restore without an explicit snapshot must step back past this one, not undo this restore.
        if (preRestore.Length > 0) _history.MarkTakenByRestore(preRestore);

        return Strings.RestoreOk(path, snapPath);
    }
}
