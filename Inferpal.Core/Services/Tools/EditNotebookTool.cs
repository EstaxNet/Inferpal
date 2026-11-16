using System.Text.Json;
using Inferpal.Localization;
using Inferpal.Services.CodeActions;
using Inferpal.Services.Documents;

namespace Inferpal.Services.Tools;

/// <summary>
/// <c>edit_notebook</c>: one cell of a Jupyter notebook changed, added or deleted by its id or number — the rest of the file
/// written back as Jupyter writes it (<see cref="NotebookEdit"/>).
/// </summary>
/// <remarks>
/// ⚠ Rewritten whole by <c>write_file</c>, a notebook breaks on one stray comma and every line changes. The card shows the
/// CELL's lines, never the JSON around them (the validated mock-ups, pari 98); approval, backup and the unsaved-buffer and
/// changed-since-approval guards are those of every write.
/// </remarks>
internal sealed class EditNotebookTool(IApprovalService approval, FileHistoryService history, Func<string?> getRoot,
                                       Editor.OpenDocumentOverlay? overlay = null, Action<DiffInfo?>? setDiff = null) : ITool
{
    public string Name => "edit_notebook";

    /// <inheritdoc/>
    public ToolEffect EffectOf(JsonElement args) => ToolEffect.Writes;

    /// <summary>It backs up its own write: the work-tree watch has nothing to add.</summary>
    public bool NeedsWorkTreeWatch => false;

    public string Description =>
        "Changes ONE cell of a Jupyter notebook (.ipynb) — replace its source, insert a cell before or after it, or delete " +
        "it — by the cell's id or its number from 1 (read_file shows them). The other cells, their outputs and the " +
        "notebook's settings are kept exactly; a changed code cell's old outputs are cleared. Never rewrite a notebook " +
        "with write_file. To run it afterwards: run_notebook.";

    public object Parameters => new
    {
        type = "object",
        properties = new
        {
            path      = new { type = "string", description = "The notebook (.ipynb)." },
            cell      = new { type = "string", description = "The cell: its id, or its number from 1." },
            action    = new { type = "string", description = "replace (default), insert_after, insert_before or delete." },
            cell_type = new { type = "string", description = "code (default for a new cell) or markdown; for replace, changes the cell's type." },
            source    = new { type = "string", description = "The cell's whole new source (not needed for delete)." },
        },
        required = new[] { "path", "cell" },
    };

    public async Task<string> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var root = getRoot();
        var path = PathSanitizer.Sanitize(args.Str("path"), root);
        PathSanitizer.AssertUnderRoot(path, root);
        if (!path.EndsWith(".ipynb", StringComparison.OrdinalIgnoreCase)) return "Error: edit_notebook changes .ipynb notebooks only.";
        if (FileTarget.DirectoryRefusal(path) is { } isDirectory) return isDirectory;
        if (FileTarget.UnsavedRefusal(overlay, path) is { } unsaved) return unsaved;
        if (GeneratedFiles.EditRefusal(path) is { } generated) return generated;
        if (!File.Exists(path)) return Execution.ToolFailure.Said(Strings.ToolFileNotFound(path));
        var cell = args.Trimmed("cell") ?? throw new ArgumentException("cell is required (its id, or its number from 1).");
        var action = args.Keyword("action") ?? "replace";

        var before = await TextFileEncoding.ReadTextAsync(path, ct);
        var (change, refusal) = NotebookEdit.Apply(before, cell, action, args.Keyword("cell_type"), args.Str("source"));
        if (change is null) return $"Error: {refusal}.";
        if (FileTarget.EncodingRefusal(path, change.Text) is { } cannotHold) return cannotHold;

        // The path as the workspace names it (two analysis.ipynb are told apart), one line: the card reads its lines.
        var name = (string.IsNullOrEmpty(root) ? Path.GetFileName(path) : Path.GetRelativePath(root, path))
            .Replace('\\', '/').Replace('\r', ' ').Replace('\n', ' ');
        var title = action switch
        {
            "delete"        => Strings.NotebookDeleteCell(change.Number, name),
            "insert_after"  => Strings.NotebookAddCellAfter(change.Number - 1, name),
            "insert_before" => Strings.NotebookAddCellBefore(change.Number, name),
            _               => Strings.NotebookChangeCell(change.Number, name),
        };
        var kind = change.CellType switch { "markdown" => Strings.NotebookMarkdownCell, "raw" => Strings.NotebookRawCell, _ => Strings.NotebookCodeCell };
        var relaidOut = !NotebookEdit.InJupytersLayout(before);
        var note = (relaidOut ? Strings.NotebookLayoutRewritten : Strings.NotebookCardNote)
                   + (action == "replace" && change.CellType == "code" ? " " + Strings.NotebookOutputCleared : string.Empty);
        // The card reads its title, the cell's kind and its note from these lines (ApprovalCard.NotebookCard); the change is
        // the CELL's lines, never the JSON.
        var details = title + "\n" + kind + "\n" + note;
        var cellDiff = new DiffInfo(change.OldSource, change.NewSource, path);
        if (!await approval.RequestApprovalAsync(Name, details, ct, subject: path, diff: cellDiff))
            return Strings.DiffCancelled;
        if (await FileTarget.ChangedSinceApprovalAsync(overlay, new ApprovedFile(path, before), ct) is { } changedMeanwhile) return changedMeanwhile;

        var (saved, snapPath) = await history.BackUpBeforeChangeAsync(path, ct);
        if (!saved) return history.BackupFailedMessage(path);
        await SafeFileWriter.WritePreservingAsync(path, change.Text, ct);
        setDiff?.Invoke(cellDiff);

        var done = action switch
        {
            "delete" => $"Deleted cell {change.Number} of {name}.",
            "replace" => $"Changed cell {change.Number} of {name}" + (change.CellType == "code" ? " (its old outputs cleared)." : "."),
            _ => $"Added cell {change.Number} to {name}.",
        };
        return done + $" The notebook now has {change.Cells} cells. To run it: run_notebook."
               + (relaidOut ? " (It was not laid out as Jupyter writes it: every line of the file was rewritten in Jupyter's layout.)" : string.Empty) + (string.IsNullOrEmpty(snapPath) ? string.Empty : "\n" + Strings.HistoryNote(snapPath));
    }
}
