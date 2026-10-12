using Inferpal.Localization;
using Inferpal.Services.CodeActions;

namespace Inferpal.Services.Presentation;

/// <summary>Everything an approval prompt knows: the tool, what it acts on, the change it would make.</summary>
/// <param name="Subject">The raw subject the rules match (a path for the file tools), or <c>null</c>.</param>
/// <param name="Message">The one-sentence prompt every editor can show, whatever else it can draw.</param>
internal sealed record ApprovalPrompt(string ToolName, string Details, string? Subject, DiffInfo? Diff, string Message);

/// <summary>One line of an approval card's preview: <c>add</c>, <c>del</c>, <c>ctx</c> or <c>gap</c>.</summary>
internal sealed record ApprovalPreviewLine(string Kind, string Text);

/// <summary>An approval as a card in the chat: what will happen, to what, and the start of the change.</summary>
/// <param name="Meta">"12 lines" for a new file, "+4 −2" for a change, empty otherwise.</param>
/// <param name="More">"… 6 more lines" when the preview stops before the change does; empty otherwise.</param>
/// <param name="AlwaysTooltip">What "Always this session" grants: this tool, without asking, until the editor restarts.</param>
internal sealed record ApprovalCardModel(
    string Title, string Subject, string Meta, IReadOnlyList<ApprovalPreviewLine> Preview, string More,
    string AlwaysTooltip, string Message);

/// <summary>Builds <see cref="ApprovalCardModel"/> — the same card in both editors.</summary>
internal static class ApprovalCard
{
    /// <summary>How many lines of the change the card shows before "… N more lines".</summary>
    internal const int PreviewLines = 8;

    private static readonly HashSet<string> FileChanges = new(StringComparer.Ordinal)
    {
        "write_file", "apply_diff", "apply_edits", "restore_file", "update_memory", "insert_at_cursor", "replace_selection",
    };

    public static ApprovalCardModel Build(ApprovalPrompt prompt, string? root)
    {
        // A file tool's subject is its path; any other tool's is the string its rules match (an MCP call's raw JSON plus
        // its values), which is not for display: what it runs is in its details.
        // ⚠ A change to SEVERAL files (apply_edits) has every path as its subject, one per line, so that the rules match
        // each. Read as one path, the card is titled "Change a file" and names the first: the human approving a two-file
        // batch is told about one of them.
        var pathSubject = FileChanges.Contains(prompt.ToolName) || prompt.ToolName == "delete_file";
        var paths = pathSubject && prompt.Subject is { Length: > 0 } written
            ? written.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            : [];

        var created = prompt.Diff is { OldText.Length: 0 } && prompt.ToolName == "write_file";
        var title = prompt.ToolName switch
        {
            "write_file" when created => Strings.ApprovalCreateFile,
            "delete_file"             => Strings.ApprovalDeleteFile,
            "run_command"             => Strings.ApprovalRunCommand,
            "fetch_url"               => Strings.ApprovalReadPage,
            "web_search"              => Strings.ApprovalSearchWeb,
            var t when FileChanges.Contains(t) && paths.Length > 1 => Strings.ApprovalChangeFiles(paths.Length),
            var t when FileChanges.Contains(t) => Strings.ApprovalChangeFile,
            var t                     => Strings.ApprovalUseTool(t),
        };

        var subject = paths.Length > 0 ? Names(paths, root) : FirstLine(prompt.Details);
        var meta = string.Empty;
        var preview = new List<ApprovalPreviewLine>();
        var more = 0;

        if (prompt.Diff is { } diff)
        {
            if (created)
            {
                var lines = diff.NewText.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
                meta = Strings.ApprovalLineCount(lines.Length);
                preview.AddRange(lines.Take(PreviewLines).Select(l => new ApprovalPreviewLine("add", l)));
                more = Math.Max(0, lines.Length - PreviewLines);
            }
            else
            {
                var (added, removed) = RunSummary.LineDelta(diff.OldText, diff.NewText);
                meta = $"+{added} −{removed}";
                var diffLines = DiffComputer.Compute(diff.OldText.Replace("\r\n", "\n"), diff.NewText.Replace("\r\n", "\n"));
                preview.AddRange(diffLines.Take(PreviewLines).Select(l => new ApprovalPreviewLine(
                    l.Prefix switch { "+" => "add", "-" => "del", "…" => "gap", _ => "ctx" }, l.Text)));
                more = Math.Max(0, diffLines.Count - PreviewLines);
            }
        }
        else
        {
            // ⚠ Without a diff, the card is the only view of what will run, and the approval is the boundary: nothing of
            // it is elided — every line of a command, every character of a tool's arguments. A diff can be capped,
            // "Open diff" shows the rest; a command cut after eight lines, or an MCP call cut to its first 160
            // characters, was approved unread in Visual Studio, which has no other view of it.
            var lines = prompt.Details.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
            if (prompt.ToolName == "run_command" || lines.Length > 1 || lines[0].Length > SubjectChars)
            {
                // ⚠ A file tool without a structured diff (apply_edits, rename_symbol, delete_file, restore_file) has
                // its change RENDERED in its details by DiffComputer.ComputeText. Read as plain lines, every one was
                // context: the card showed "-" and "+" as text, the change uncoloured. A command's lines stay as
                // written — a "-" there is a character of what runs, not a removal.
                preview.AddRange(pathSubject || prompt.ToolName == "rename_symbol"
                    ? RenderedDiff(lines)
                    : lines.Select(l => new ApprovalPreviewLine("ctx", l)));
                if (!pathSubject) subject = string.Empty;
            }
        }

        return new ApprovalCardModel(title, subject, meta, preview, more > 0 ? Strings.ApprovalMoreLines(more) : string.Empty,
                                     Strings.ApprovalAlwaysTooltip(prompt.ToolName), prompt.Message);
    }

    /// <summary>
    /// The lines of a file tool's details, the blocks DiffComputer.ComputeText rendered read back by their mark. A block
    /// opens under an empty line or a "### path" header and holds while every line carries a mark (space, +, -, …);
    /// anything else is context, whole.
    /// </summary>
    internal static IEnumerable<ApprovalPreviewLine> RenderedDiff(IEnumerable<string> lines)
    {
        var inBlock = false;
        foreach (var line in lines)
        {
            if (line.Length == 0 || line.StartsWith("### ", StringComparison.Ordinal))
            {
                inBlock = true;
                yield return new ApprovalPreviewLine("ctx", line);
                continue;
            }
            var kind = inBlock ? line[0] switch { '+' => "add", '-' => "del", '…' => "gap", ' ' => "ctx", _ => null } : null;
            if (kind is null)
            {
                inBlock = false;
                yield return new ApprovalPreviewLine("ctx", line);
                continue;
            }
            yield return new ApprovalPreviewLine(kind, kind == "gap" ? line[1..].Trim() : line[1..]);
        }
    }

    private static string Relative(string path, string? root)
    {
        if (string.IsNullOrEmpty(root)) return path;
        try
        {
            var relative = System.IO.Path.GetRelativePath(root, path);
            return relative.StartsWith("..", StringComparison.Ordinal) || System.IO.Path.IsPathRooted(relative) ? path : relative;
        }
        catch (ArgumentException) { return path; }
    }

    /// <summary>
    /// The files a change touches, as the header names them: each relative to the workspace, the list cut at
    /// <see cref="SubjectChars"/> — the title counts them all, and the preview heads each file's change with its path.
    /// </summary>
    private static string Names(IReadOnlyList<string> paths, string? root)
    {
        if (paths.Count == 1) return Relative(paths[0], root);
        var names = string.Join(", ", paths.Select(p => Relative(p, root)));
        return names.Length > SubjectChars ? names[..SubjectChars] + "…" : names;
    }

    /// <summary>The longest single line the card's header shows as its subject; a longer one goes to the preview.</summary>
    internal const int SubjectChars = 160;

    private static string FirstLine(string text)
    {
        var line = text.Replace("\r\n", "\n").Split('\n')[0];
        return line.Length > SubjectChars ? line[..SubjectChars] + "…" : line;
    }
}
