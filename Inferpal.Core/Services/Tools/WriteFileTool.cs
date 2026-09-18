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
        var root    = _getWorkspaceRoot();
        var path    = PathSanitizer.Sanitize(args.Str("path"), root);
        PathSanitizer.AssertUnderRoot(path, root);
        var content = args.Str("content") ?? throw new ArgumentException("content is required.");

        var exists     = File.Exists(path);
        var oldContent = exists ? await File.ReadAllTextAsync(path, ct) : string.Empty;
        var details    = exists
            ? Strings.WriteOverwrite(path, content.Length)
            : Strings.WriteCreate(path, content.Length);

        // Pass the structured change so the approval prompt shows the actual diff, not just a path
        // (colored viewer in VS; textual fallback elsewhere).
        if (!await _approval.RequestApprovalAsync("write_file", details, ct, subject: path,
                diff: new DiffInfo(oldContent, content, path)))
            return Strings.WriteCancelled;

        // One branch only: the net KNOWS how to tell "nothing to back up, so this write creates the
        // file" from "the backup failed". The `else` that declared the creation by hand was the only
        // site doing it — and that shape, a case handled OUTSIDE the funnel, is what left
        // update_memory out of /undo-run's perimeter.
        var (saved, snapPath) = await _history.BackUpBeforeChangeAsync(path, ct);
        if (!saved) return FileHistoryService.BackupFailedMessage(path);
        var snapNote = string.IsNullOrEmpty(snapPath) ? string.Empty : Strings.HistoryNote(snapPath);

        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        await SafeFileWriter.WritePreservingAsync(path, content, ct);

        _setDiff?.Invoke(new DiffInfo(oldContent, content, path));

        var smartFixNote = _smartFix is not null
            ? "\n\n" + (await _smartFix.ValidateAsync(path, ct) ?? string.Empty)
            : string.Empty;

        return Strings.WriteOk(path, content.Length) + snapNote + smartFixNote;
    }
}
