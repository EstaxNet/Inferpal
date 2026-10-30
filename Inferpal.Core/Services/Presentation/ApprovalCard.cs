using Inferpal.Localization;
using Inferpal.Services.CodeActions;

namespace Inferpal.Services.Presentation;

/// <summary>Everything an approval prompt knows: the tool, what it acts on, the change it would make.</summary>
/// <param name="Subject">The raw subject the rules match (a path for the file tools), or <c>null</c>.</param>
/// <param name="Message">The one-sentence prompt every editor can show, whatever else it can draw.</param>
/// <param name="Rule">The narrow rule the card offers beside "Always this session", or <c>null</c>.</param>
internal sealed record ApprovalPrompt(string ToolName, string Details, string? Subject, DiffInfo? Diff, string Message,
                                      Execution.ApprovalRuleProposal.Proposal? Rule = null);

/// <summary>One line of an approval card's preview: <c>add</c>, <c>del</c>, <c>ctx</c> or <c>gap</c>.</summary>
internal sealed record ApprovalPreviewLine(string Kind, string Text);

/// <summary>An approval as a card in the chat: what will happen, to what, and the start of the change.</summary>
/// <param name="Meta">"12 lines" for a new file, "+4 −2" for a change, empty otherwise.</param>
/// <param name="More">"… 6 more lines" when the preview stops before the change does; empty otherwise.</param>
/// <param name="AlwaysTooltip">What "Always this session" grants: this tool, without asking, in this workspace, until a
/// restart or until another workspace opens.</param>
/// <param name="RuleLabel">The button of the narrow rule ("Always: commands that start with `dotnet test`"); empty when
/// the card offers none.</param>
/// <param name="RuleTooltip">What that button writes, the rule spelled out; empty when none.</param>
internal sealed record ApprovalCardModel(
    string Title, string Subject, string Meta, IReadOnlyList<ApprovalPreviewLine> Preview, string More,
    string AlwaysTooltip, string Message, string RuleLabel = "", string RuleTooltip = "");

/// <summary>Builds <see cref="ApprovalCardModel"/> — the same card in both editors.</summary>
internal static class ApprovalCard
{
    /// <summary>How many lines of the change the card shows before "… N more lines".</summary>
    internal const int PreviewLines = 8;

    /// <summary>
    /// How long a card waits unanswered before the editor draws attention to it (Visual Studio: the taskbar flashes;
    /// VS Code: a notification while it waits) — only for a window without the focus. VS Code says the same number
    /// (<c>APPROVAL_ATTENTION_MS</c>).
    /// </summary>
    /// <remarks>⚠ A run started before its author goes elsewhere stops at its first card: without the signal, nothing
    /// outside the conversation says so, and the run waits on a window nobody looks at.</remarks>
    internal static readonly TimeSpan AttentionDelay = TimeSpan.FromSeconds(15);

    private static readonly HashSet<string> FileChanges = new(StringComparer.Ordinal)
    {
        "write_file", "apply_diff", "apply_edits", "restore_file", "update_memory", "insert_at_cursor", "replace_selection",
    };

    /// <summary>
    /// The tools whose details carry their change as <c>DiffComputer.ComputeText</c> rendered it — named, never inferred
    /// from a path subject.
    /// </summary>
    /// <remarks>⚠ Inferred, the text <c>insert_at_cursor</c>, <c>replace_selection</c> or <c>update_memory</c> writes is
    /// read as diff marks: a YAML list shown as removals, indented code one space short — on the card that is the only view
    /// of it. A tool missing here shows its change uncoloured, never altered.</remarks>
    private static readonly HashSet<string> RenderTheirChange = new(StringComparer.Ordinal)
    {
        "apply_edits", "rename_symbol",
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
                preview.AddRange(diffLines.Take(PreviewLines).Select(l => l.Prefix == "…"
                    ? new ApprovalPreviewLine("gap", l.Text.Trim())
                    : new ApprovalPreviewLine(l.Prefix switch { "+" => "add", "-" => "del", _ => "ctx" }, l.Text)));
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
                // ⚠ The tools that render their change into their details (RenderTheirChange) are drawn as a change:
                // read as plain lines, "-" and "+" show as text, the change uncoloured. Every other tool's lines stay as
                // written — a "-" in a command, or in the text an editor tool inserts (a YAML list), is a character of
                // what runs or is written, never a removal.
                preview.AddRange(RenderTheirChange.Contains(prompt.ToolName)
                    ? RenderedDiff(lines)
                    : lines.Select(l => new ApprovalPreviewLine("ctx", l)));
                if (!pathSubject) subject = string.Empty;
            }
        }

        return new ApprovalCardModel(title, subject, meta, preview, more > 0 ? Strings.ApprovalMoreLines(more) : string.Empty,
                                     Strings.ApprovalAlwaysTooltip(prompt.ToolName), prompt.Message,
                                     prompt.Rule is { } rule ? Strings.ApprovalAlwaysRule(rule.Describe) : string.Empty,
                                     prompt.Rule is { } spelled ? Strings.ApprovalRuleTooltip(string.Join("\n", spelled.Lines)) : string.Empty);
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
