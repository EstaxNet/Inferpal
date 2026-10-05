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
        var created = prompt.Diff is { OldText.Length: 0 } && prompt.ToolName == "write_file";
        var title = prompt.ToolName switch
        {
            "write_file" when created => Strings.ApprovalCreateFile,
            "delete_file"             => Strings.ApprovalDeleteFile,
            "run_command"             => Strings.ApprovalRunCommand,
            "fetch_url"               => Strings.ApprovalReadPage,
            "web_search"              => Strings.ApprovalSearchWeb,
            var t when FileChanges.Contains(t) => Strings.ApprovalChangeFile,
            var t                     => Strings.ApprovalUseTool(t),
        };

        // A file tool's subject is its path; any other tool's is the string its rules match (an MCP call's raw JSON plus
        // its values), which is not for display: what it runs is in its details.
        var pathSubject = FileChanges.Contains(prompt.ToolName) || prompt.ToolName == "delete_file";
        var subject = pathSubject && prompt.Subject is { Length: > 0 } path ? Relative(path, root) : FirstLine(prompt.Details);
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
                preview.AddRange(lines.Select(l => new ApprovalPreviewLine("ctx", l)));
                if (!pathSubject) subject = string.Empty;
            }
        }

        return new ApprovalCardModel(title, subject, meta, preview, more > 0 ? Strings.ApprovalMoreLines(more) : string.Empty,
                                     Strings.ApprovalAlwaysTooltip(prompt.ToolName), prompt.Message);
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

    /// <summary>The longest single line the card's header shows as its subject; a longer one goes to the preview.</summary>
    internal const int SubjectChars = 160;

    private static string FirstLine(string text)
    {
        var line = text.Replace("\r\n", "\n").Split('\n')[0];
        return line.Length > SubjectChars ? line[..SubjectChars] + "…" : line;
    }
}
