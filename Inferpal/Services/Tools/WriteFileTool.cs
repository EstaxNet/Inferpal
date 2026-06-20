using System.IO;
using System.Text.Json;
using Inferpal.Localization;
using Inferpal.Services;

namespace Inferpal.Services.Tools;

internal class WriteFileTool : ITool
{
    private readonly IApprovalService       _approval;
    private readonly FileHistoryService     _history;
    private readonly SmartFixValidator?     _smartFix;
    private readonly Action<DiffInfo?>?     _setDiff;
    private readonly Func<string?>          _getWorkspaceRoot;

    public WriteFileTool(IApprovalService approval, FileHistoryService history, Func<string?> getWorkspaceRoot, SmartFixValidator? smartFix = null, Action<DiffInfo?>? setDiff = null)
    {
        _approval         = approval;
        _history          = history;
        _getWorkspaceRoot = getWorkspaceRoot;
        _smartFix         = smartFix;
        _setDiff          = setDiff;
    }

    public string Name => "write_file";
    public string Description => "Writes or replaces the content of a file. Creates the file if absent.";
    public object Parameters => new
    {
        type = "object",
        properties = new
        {
            path    = new { type = "string", description = "Absolute path to the file." },
            content = new { type = "string", description = "Content to write." }
        },
        required = new[] { "path", "content" }
    };

    public async Task<string> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var path    = PathSanitizer.Sanitize(args.GetProperty("path").GetString());
        PathSanitizer.AssertUnderRoot(path, _getWorkspaceRoot());
        var content = args.GetProperty("content").GetString()!;

        var exists     = File.Exists(path);
        var oldContent = exists ? await File.ReadAllTextAsync(path, ct) : string.Empty;
        var details    = exists
            ? Strings.WriteOverwrite(path, content.Length)
            : Strings.WriteCreate(path, content.Length);

        // Show the change in the approval prompt so the user confirms the actual diff, not just a path.
        var diffText = DiffComputer.ComputeText(oldContent, content);
        if (diffText is not null) details += "\n\n" + diffText;

        if (!await _approval.RequestApprovalAsync("write_file", details, ct, subject: path))
            return Strings.WriteCancelled;

        var snapNote = string.Empty;
        if (exists)
        {
            var snapPath = await _history.SnapshotAsync(path, ct);
            if (!string.IsNullOrEmpty(snapPath))
                snapNote = Strings.HistoryNote(snapPath);
        }
        else
        {
            _history.NoteCreated(path);   // no prior content → /undo-run deletes it
        }

        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        await File.WriteAllTextAsync(path, content, ct);

        _setDiff?.Invoke(new DiffInfo(oldContent, content, path));

        var smartFixNote = _smartFix is not null
            ? "\n\n" + (await _smartFix.ValidateAsync(path, ct) ?? string.Empty)
            : string.Empty;

        return Strings.WriteOk(path, content.Length) + snapNote + smartFixNote;
    }
}
