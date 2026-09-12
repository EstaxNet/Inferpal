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
        var root       = _getWorkspaceRoot();
        var path       = PathSanitizer.Sanitize(args.Str("path"), root);
        PathSanitizer.AssertUnderRoot(path, root);
        var oldContent = args.Str("old_content") ?? throw new ArgumentException("old_content is required.");
        // ⚠ Was `?? ""`, on an argument the schema declares REQUIRED: omitting it therefore
        // became a DELETION of the matched block, and the answer said "diff applied". The model
        // reads a success for a call it wrote wrong, and carries on as if its replacement text
        // were in place. The empty string stays valid -- it is how a block is deleted -- but it
        // has to be written.
        var newContent = args.Str("new_content")
            ?? throw new ArgumentException("new_content is required (send \"\" to delete the matched block).");
        var occurrence = args.Keyword("occurrence");

        if (!File.Exists(path))
            return Strings.ToolFileNotFound(path);

        var fileContent = await File.ReadAllTextAsync(path, ct);

        var resolution = ApplyDiffMatcher.Resolve(fileContent, oldContent, newContent, occurrence);
        if (resolution.Modified is null)
            return resolution.Count > 1 ? Strings.DiffAmbiguous(resolution.Count, path) : Strings.DiffOldNotFound(path);

        var modified = resolution.Modified;

        // Pass the structured change so the approval prompt shows the actual diff, not just a path
        // (colored viewer in VS; textual fallback elsewhere).
        var details = Strings.DiffConfirm(path);
        if (!await _approval.RequestApprovalAsync("apply_diff", details, ct, subject: path,
                diff: new DiffInfo(fileContent, modified, path)))
            return Strings.DiffCancelled;

        var snapPath = await _history.SnapshotAsync(path, ct);
        var snapNote = string.IsNullOrEmpty(snapPath) ? string.Empty : Strings.HistoryNote(snapPath);

        await SafeFileWriter.WritePreservingAsync(path, modified, ct);

        _setDiff?.Invoke(new DiffInfo(fileContent, modified, path));

        var smartFixNote = _smartFix is not null
            ? "\n\n" + (await _smartFix.ValidateAsync(path, ct) ?? string.Empty)
            : string.Empty;

        return Strings.DiffOk(path) + snapNote + smartFixNote;
    }
}
