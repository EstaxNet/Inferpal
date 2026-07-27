using System.IO;
using System.Text.Json;
using Inferpal.Localization;

namespace Inferpal.Services.Tools;

internal class ApplyDiffTool : ITool
{
    private readonly IApprovalService    _approval;
    private readonly FileHistoryService  _history;
    private readonly SmartFixValidator?  _smartFix;
    private readonly Action<DiffInfo?>?  _setDiff;
    private readonly Func<string?>       _getWorkspaceRoot;

    public ApplyDiffTool(IApprovalService approval, FileHistoryService history, Func<string?> getWorkspaceRoot, SmartFixValidator? smartFix = null, Action<DiffInfo?>? setDiff = null)
    {
        _approval         = approval;
        _history          = history;
        _getWorkspaceRoot = getWorkspaceRoot;
        _smartFix         = smartFix;
        _setDiff          = setDiff;
    }

    public string Name => "apply_diff";

    public string Description =>
        "Modifies a file by replacing old_content with new_content. More precise than write_file for " +
        "targeted changes. old_content should match the existing text; if an exact match fails, a " +
        "whitespace-tolerant match is attempted (indentation / trailing spaces / line endings). " +
        "By default exactly one match is required; set occurrence to 'first' or 'all' for multiple.";

    public object Parameters => new
    {
        type = "object",
        properties = new
        {
            path        = new { type = "string", description = "Absolute path to the file to modify." },
            old_content = new { type = "string", description = "Text to replace." },
            new_content = new { type = "string", description = "Replacement text." },
            occurrence  = new { type = "string", description = "'unique' (default: require exactly one match), 'first' (replace the first of several), or 'all' (replace every match)." },
        },
        required = new[] { "path", "old_content", "new_content" },
    };

    public async Task<string> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var path       = PathSanitizer.Sanitize(args.GetProperty("path").GetString());
        PathSanitizer.AssertUnderRoot(path, _getWorkspaceRoot());
        var oldContent = args.GetProperty("old_content").GetString() ?? throw new ArgumentException("old_content required");
        var newContent = args.GetProperty("new_content").GetString() ?? "";
        var occurrence = args.TryGetProperty("occurrence", out var occ) ? occ.GetString() : null;

        if (!File.Exists(path))
            return Strings.ToolFileNotFound(path);

        var fileContent = await File.ReadAllTextAsync(path, ct);

        var resolution = ApplyDiffMatcher.Resolve(fileContent, oldContent, newContent, occurrence);
        if (resolution.Modified is null)
            return resolution.Count > 1 ? Strings.DiffAmbiguous(resolution.Count, path) : Strings.DiffOldNotFound(path);

        var modified = resolution.Modified;

        // Show the change in the approval prompt so the user confirms the actual diff, not just a path.
        var details  = Strings.DiffConfirm(path);
        var diffText = DiffComputer.ComputeText(fileContent, modified);
        if (diffText is not null) details += "\n\n" + diffText;

        if (!await _approval.RequestApprovalAsync("apply_diff", details, ct, subject: path))
            return Strings.DiffCancelled;

        var snapPath = await _history.SnapshotAsync(path, ct);
        var snapNote = string.IsNullOrEmpty(snapPath) ? string.Empty : Strings.HistoryNote(snapPath);

        await File.WriteAllTextAsync(path, modified, ct);

        _setDiff?.Invoke(new DiffInfo(fileContent, modified, path));

        var smartFixNote = _smartFix is not null
            ? "\n\n" + (await _smartFix.ValidateAsync(path, ct) ?? string.Empty)
            : string.Empty;

        return Strings.DiffOk(path) + snapNote + smartFixNote;
    }
}
