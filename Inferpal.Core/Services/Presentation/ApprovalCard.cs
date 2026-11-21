using Inferpal.Localization;
using Inferpal.Services.CodeActions;

namespace Inferpal.Services.Presentation;

/// <summary>Everything an approval prompt knows: the tool, what it acts on, the change it would make.</summary>
/// <param name="Subject">The raw subject the rules match (a path for the file tools), or <c>null</c>.</param>
/// <param name="Message">The one-sentence prompt every editor can show, whatever else it can draw.</param>
/// <param name="Rule">The narrow rule the card offers beside "Always this session", or <c>null</c>.</param>
/// <param name="Forced">The card is forced (opaque execution, the agent's own instructions, the repository's code, a deny
/// that could not be read): it offers Yes and No only — "Always" would grant nothing, the next identical call asks again.</param>
internal sealed record ApprovalPrompt(string ToolName, string Details, string? Subject, DiffInfo? Diff, string Message,
                                      Execution.ApprovalRuleProposal.Proposal? Rule = null, bool Forced = false);

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
/// <param name="OffersAlways">Whether the card offers "Always this session": never on a forced card, where it would grant
/// nothing (<see cref="ApprovalPrompt.Forced"/>).</param>
/// <param name="AllowLabel">The allowing button's words when not "Allow once" ("Send once"); empty for the usual ones.</param>
/// <param name="AlwaysLabel">The "Always" button's words when it opens less than the tool ("Always for localhost:5000");
/// empty for "Always this session".</param>
/// <param name="Warning">A warning above the answers (what leaves the machine); empty for none.</param>
/// <param name="Note">A line under the answers saying what they mean here; empty for none.</param>
/// <param name="Outbound">Data may leave the machine: the card is drawn as a warning, and Deny is its default answer.</param>
/// <param name="Rows">Facts the card states one per line, label beside value (a database's engine, server and account);
/// <c>null</c> for none.</param>
/// <param name="Binary">A binary file to write (a capture saved as PNG): its picture, and the one it replaces, in place of
/// a preview of lines; <c>null</c> for anything else.</param>
/// <param name="OpenPath">A file the card offers to open in an editor tab, read-only (a script of the agent's, pari 161);
/// empty for none.</param>
internal sealed record ApprovalCardModel(
    string Title, string Subject, string Meta, IReadOnlyList<ApprovalPreviewLine> Preview, string More,
    string AlwaysTooltip, string Message, string RuleLabel = "", string RuleTooltip = "", bool OffersAlways = true,
    string AllowLabel = "", string AlwaysLabel = "", string Warning = "", string Note = "", bool Outbound = false,
    IReadOnlyList<ApprovalCardRow>? Rows = null, RunFileBinary? Binary = null, string OpenPath = "");

/// <summary>One fact a card states: <paramref name="Label"/> beside <paramref name="Value"/>.</summary>
internal sealed record ApprovalCardRow(string Label, string Value);

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
        "apply_edits", "rename_symbol", "replace_in_files",
    };

    /// <summary>
    /// <c>http_request</c>'s card, read from its subject (<c>METHOD scheme://host:port</c>): on this machine, "Always" for
    /// that address only; anywhere else, a warning card whose default answer is Deny, with no "Always" at all.
    /// </summary>
    private static ApprovalCardModel? HttpCard(ApprovalPrompt prompt, List<ApprovalPreviewLine> preview)
    {
        var space = prompt.Subject?.IndexOf(' ') ?? -1;
        if (space < 0 || !Uri.TryCreate(prompt.Subject![(space + 1)..], UriKind.Absolute, out var origin)) return null;
        var method = prompt.Subject[..space];
        var hostPort = origin.Authority.ToLowerInvariant();
        if (preview.Count == 0) preview.AddRange(prompt.Details.Replace("\r\n", "\n").Split('\n').Select(l => new ApprovalPreviewLine("ctx", l)));
        if (Tools.HttpRequestTool.IsLoopback(origin))
        {
            var where = $"{origin.Host.ToLowerInvariant()}:{origin.Port}";
            return new ApprovalCardModel(Strings.ApprovalCallApp, Strings.ApprovalCallAppWhere(where), string.Empty, preview,
                                         string.Empty, Strings.ApprovalAlwaysForTooltip(prompt.Subject[(space + 1)..]), prompt.Message,
                                         OffersAlways: !prompt.Forced, AlwaysLabel: Strings.ApprovalAlwaysFor(where),
                                         Note: prompt.Forced ? string.Empty : Strings.ApprovalCallAppNote);
        }
        // The rule that would trust this server, spelled whole: method, scheme, host, port — nothing less covers it.
        var rule = $"allow http_request ^{method} {System.Text.RegularExpressions.Regex.Escape(prompt.Subject[(space + 1)..])}$";
        return new ApprovalCardModel(Strings.ApprovalSendElsewhere, Strings.ApprovalSendElsewhereWhere(hostPort), string.Empty, preview,
                                     string.Empty, string.Empty, prompt.Message, OffersAlways: false,
                                     AllowLabel: Strings.ApprovalSendOnce, Warning: Strings.ApprovalSendElsewhereWarning,
                                     Note: Strings.ApprovalSendElsewhereNote(rule), Outbound: true);
    }

    /// <summary><c>interact_page</c>'s card, for an address no app of the agent's serves: the address, what will happen in
    /// it, and an "Always" for that address only.</summary>
    private static ApprovalCardModel? ActCard(ApprovalPrompt prompt, List<ApprovalPreviewLine> preview)
    {
        var space = prompt.Subject?.IndexOf(' ') ?? -1;
        if (space < 0 || !Uri.TryCreate(prompt.Subject![(space + 1)..], UriKind.Absolute, out var origin)) return null;
        var where = $"{origin.Host.ToLowerInvariant()}:{origin.Port}";
        if (preview.Count == 0) preview.AddRange(prompt.Details.Replace("\r\n", "\n").Split('\n').Select(l => new ApprovalPreviewLine("ctx", l)));
        return new ApprovalCardModel(Strings.ApprovalActInPage, Strings.ApprovalCallAppWhere(where), string.Empty, preview, string.Empty,
                                     Strings.ApprovalActAlwaysTooltip(where), prompt.Message, OffersAlways: !prompt.Forced,
                                     AlwaysLabel: Strings.ApprovalAlwaysFor(where),
                                     Note: prompt.Forced ? string.Empty : Strings.ApprovalActInPageNote);
    }

    /// <summary>
    /// <c>query_database</c>'s card, read from its details (<see cref="Tools.QueryDatabaseTool.Details"/>): the database, where
    /// the project declares it, its engine, server and account, and the query as it will run. On this machine, "Always" for
    /// that database; elsewhere, a warning card whose default answer is Deny, with no "Always".
    /// </summary>
    private static ApprovalCardModel? DatabaseCard(ApprovalPrompt prompt)
    {
        var text = prompt.Details.Replace("\r\n", "\n");
        var blank = text.IndexOf("\n\n", StringComparison.Ordinal);
        var head = (blank < 0 ? text : text[..blank]).Split('\n');
        var facts = new Dictionary<string, string>(StringComparer.Ordinal);
        // The first of each fact: the tool writes each once, and a later line is never a fact of its own.
        foreach (var line in head)
            if (line.IndexOf(": ", StringComparison.Ordinal) is var colon and > 0) facts.TryAdd(line[..colon], line[(colon + 2)..]);
        if (!facts.TryGetValue("database", out var database) || !facts.TryGetValue("asks", out var asks)) return null;
        var here = facts.GetValueOrDefault("where") == "this machine";
        var account = facts.GetValueOrDefault("account", string.Empty);
        if (account.StartsWith("windows ", StringComparison.Ordinal)) account = Strings.DatabaseWindowsAccount(account[8..]);
        account = facts.GetValueOrDefault("rights") switch
        {
            "read-only"    => Strings.DatabaseAccountReadOnly(account),
            "can write"    => Strings.DatabaseAccountCanWrite(account),
            "unknown"      => Strings.DatabaseAccountUnknown(account),
            _              => Strings.DatabaseAccountNotRead(account),
        };
        var server = facts.GetValueOrDefault("server", string.Empty);
        var rows = new List<ApprovalCardRow>
        {
            new(Strings.DatabaseRowFoundIn, facts.GetValueOrDefault("found in", string.Empty)),
            new(Strings.DatabaseRowEngine, facts.GetValueOrDefault("engine", string.Empty)),
            new(Strings.DatabaseRowServer, here ? Strings.DatabaseServerHere(server) : server),
            new(Strings.DatabaseRowAccount, account),
        };
        var preview = blank < 0 ? [] : text[(blank + 2)..].Split('\n').Select(l => new ApprovalPreviewLine("ctx", l)).ToList();
        var subject = asks == "query" ? Strings.ApprovalDatabaseQuery
                    : asks.StartsWith("describe ", StringComparison.Ordinal) ? Strings.ApprovalDatabaseStructure(asks[9..])
                    : Strings.ApprovalDatabaseTables;
        var warnings = new List<string>();
        if (facts.GetValueOrDefault("rights") == "can write") warnings.Add(Strings.DatabaseCanWrite);
        if (!here) warnings.Add(Strings.DatabaseElsewhere);
        var always = here && !prompt.Forced;
        return new ApprovalCardModel(Strings.ApprovalReadDatabase(database), subject, string.Empty, preview, string.Empty,
                                     always ? Strings.DatabaseAlwaysTooltip(database) : string.Empty, prompt.Message,
                                     OffersAlways: always, AlwaysLabel: always ? Strings.ApprovalAlwaysFor(database) : string.Empty,
                                     Warning: string.Join("\n\n", warnings), Note: Strings.DatabaseReadNote, Outbound: !here, Rows: rows);
    }

    /// <summary>A script of the agent's (<see cref="Shell.ScriptRun"/>): titled as one, its language, length and folder under
    /// the title, the script itself whole — the card is the only view of what will run —, and the file offered to open.</summary>
    private static ApprovalCardModel? ScriptCard(ApprovalPrompt prompt, string? root, bool keepsGrantsAcrossSolutions)
    {
        if (Shell.ScriptRun.ReadDetails(prompt.Details) is not { } script) return null;
        var lines = script.Code.Split('\n');
        var folder = root is not null && Shell.ShellSession.SameFolder(script.Folder, root)
            ? Path.GetFileName(Path.TrimEndingDirectorySeparator(root))
            : script.Folder;
        return new ApprovalCardModel(Strings.ApprovalRunScript, Strings.ApprovalScriptWhere(script.Interpreter, lines.Length, folder),
                                     string.Empty, lines.Select(l => new ApprovalPreviewLine("ctx", l)).ToList(), string.Empty,
                                     keepsGrantsAcrossSolutions ? Strings.ApprovalAlwaysTooltip(prompt.ToolName)
                                                                : Strings.ApprovalAlwaysTooltipUntilRestart(prompt.ToolName),
                                     prompt.Message, OffersAlways: !prompt.Forced, Note: Strings.ApprovalScriptNote,
                                     OpenPath: Shell.ScriptRun.ViewOf(script.File));
    }

    public static ApprovalCardModel Build(ApprovalPrompt prompt, string? root, bool keepsGrantsAcrossSolutions = false)
    {
        if (prompt.ToolName == "query_database" && DatabaseCard(prompt) is { } database) return database;
        if (prompt.ToolName == "run_command" && ScriptCard(prompt, root, keepsGrantsAcrossSolutions) is { } scriptCard) return scriptCard;
        // A file tool's subject is its path; any other tool's is the string its rules match (an MCP call's raw JSON plus
        // its values), which is not for display: what it runs is in its details.
        // ⚠ A change to SEVERAL files (apply_edits) has every path as its subject, one per line, so that the rules match
        // each. Read as one path, the card is titled "Change a file" and names the first: the human approving a two-file
        // batch is told about one of them.
        var pathSubject = FileChanges.Contains(prompt.ToolName) || prompt.ToolName == "delete_file";
        var paths = pathSubject && prompt.Subject is { Length: > 0 } written
            ? written.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            : [];

        var created = prompt.Diff is { Binary: not null } binaryDiff
            ? binaryDiff.Creates
            : prompt.Diff is { OldText.Length: 0 } && prompt.ToolName == "write_file";
        var title = prompt.ToolName switch
        {
            "write_file" when created => Strings.ApprovalCreateFile,
            "delete_file"             => Strings.ApprovalDeleteFile,
            // The repository's validator after a write (Smart Fix) runs a command too: never titled by its internal name.
            "run_command" or "smart_fix_validator" => Strings.ApprovalRunCommand,
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

        // A binary file has no lines to show: its kind and size are the meta, its picture is on the card (Binary).
        if (prompt.Diff is { Binary: { } binary }) meta = binary.Label;
        else if (prompt.Diff is { } diff)
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
            if (prompt.ToolName is "run_command" or "smart_fix_validator" || lines.Length > 1 || lines[0].Length > SubjectChars)
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

        // view_page on a served page asks with http_request's subject and agreement: the same card.
        if (prompt.ToolName is "http_request" or "view_page" && HttpCard(prompt, preview) is { } http) return http;
        if (prompt.ToolName == "interact_page" && ActCard(prompt, preview) is { } act) return act;

        var card = new ApprovalCardModel(title, subject, meta, preview, more > 0 ? Strings.ApprovalMoreLines(more) : string.Empty,
                                     // What "Always" lasts: across solutions in one Visual Studio; until the host restarts elsewhere.
                                     keepsGrantsAcrossSolutions ? Strings.ApprovalAlwaysTooltip(prompt.ToolName)
                                                                : Strings.ApprovalAlwaysTooltipUntilRestart(prompt.ToolName),
                                     prompt.Message,
                                     // A button is plain text: the description's Markdown code marks would show as such.
                                     prompt.Rule is { } rule ? Strings.ApprovalAlwaysRule(rule.Describe.Replace("`", "")) : string.Empty,
                                     prompt.Rule is { } spelled ? Strings.ApprovalRuleTooltip(string.Join("\n", spelled.Lines)) : string.Empty,
                                     OffersAlways: !prompt.Forced, Binary: prompt.Diff?.Binary);
        // edit_notebook: titled by the cell ("Change cell 3 of analysis.ipynb"), its kind beside the count, its note under the
        // answers — sent as the details' three lines (EditNotebookTool); the preview is the CELL's change.
        if (prompt.ToolName == "edit_notebook" && prompt.Details.Split('\n') is [var cellTitle, var cellKind, var cellNote, ..])
            return card with { Title = cellTitle, Subject = cellKind, Note = cellNote };
        return card;
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
