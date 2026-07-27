using System.IO;
using System.Text.Json;
using Inferpal.Localization;
using Inferpal.Services;

namespace Inferpal.Services.Tools;

internal class DeleteFileTool : ITool
{
    private readonly IApprovalService   _approval;
    private readonly FileHistoryService _history;
    private readonly Func<string?>      _getWorkspaceRoot;

    public DeleteFileTool(IApprovalService approval, FileHistoryService history, Func<string?> getWorkspaceRoot)
    {
        _approval         = approval;
        _history          = history;
        _getWorkspaceRoot = getWorkspaceRoot;
    }

    public string Name        => "delete_file";
    public string Description => "Permanently deletes a file. A snapshot is saved beforehand so restore_file can undo the deletion.";
    public object Parameters  => new
    {
        type = "object",
        properties = new
        {
            path = new { type = "string", description = "Absolute path to the file to delete." }
        },
        required = new[] { "path" }
    };

    public async Task<string> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var path = PathSanitizer.Sanitize(args.GetProperty("path").GetString());
        PathSanitizer.AssertUnderRoot(path, _getWorkspaceRoot());

        if (!File.Exists(path))
            return Strings.ToolFileNotFound(path);

        // Show what is being removed in the approval prompt (best-effort: skip for binary/locked files).
        var details = Strings.DeleteConfirm(path);
        try
        {
            var diffText = DiffComputer.ComputeText(await File.ReadAllTextAsync(path, ct), string.Empty);
            if (diffText is not null) details += "\n\n" + diffText;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { Diagnostics.Swallow("DeletePreviewDiff", ex); }

        if (!await _approval.RequestApprovalAsync("delete_file", details, ct, subject: path))
            return Strings.DeleteCancelled;

        var snapPath = await _history.SnapshotAsync(path, ct);
        var snapNote = string.IsNullOrEmpty(snapPath) ? string.Empty : Strings.HistoryNote(snapPath);

        File.Delete(path);
        return Strings.DeleteOk(path) + snapNote;
    }
}
