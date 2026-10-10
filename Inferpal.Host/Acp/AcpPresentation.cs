using System.Text;
using System.Text.Json;
using Inferpal.Services.Presentation;

namespace Inferpal.Host.Acp;

/// <summary>
/// What of a streamed answer an ACP client is sent: the text the chat shows (<see cref="MarkdownParser.ShownText"/>,
/// reasoning tags left out), as the pieces it did not have yet.
/// </summary>
/// <remarks>
/// <para>
/// ⚠ A piece sent cannot be taken back: an ACP client appends every <c>agent_message_chunk</c>. So a tag still being
/// written (<c>&lt;thi</c>) is held until it is complete — sent, it would be text the next token turns into reasoning.
/// </para>
/// <para>
/// ⚠ When what is shown stops extending what was sent — a lone <c>&lt;/think&gt;</c> closing reasoning the chat
/// template opened makes everything before it reasoning — the rest goes after a blank line: the client already shows the
/// text, and only the next pieces can be right.
/// </para>
/// </remarks>
internal sealed class AcpShownStream
{
    private static readonly string[] Tags = ["<think>", "</think>"];

    private readonly StringBuilder _raw = new();
    private readonly StringBuilder _sent = new();
    private string _baseline = string.Empty;

    /// <summary>Everything sent so far, as the client shows it.</summary>
    public string Sent => _sent.ToString();

    /// <summary>The piece to send after <paramref name="token"/> (often empty).</summary>
    public string Append(string token)
    {
        _raw.Append(token);
        var raw = _raw.ToString();
        var shown = MarkdownParser.ShownText("assistant", raw[..(raw.Length - PartialTagLength(raw))]);

        string piece;
        if (shown.StartsWith(_baseline, StringComparison.Ordinal))
            piece = shown[_baseline.Length..];
        else if (shown.Length == 0)
            return string.Empty;   // the boundary moved and nothing is shown yet: wait for the answer
        else
            piece = (_sent.Length > 0 ? "\n\n" : string.Empty) + shown;

        _baseline = shown;
        _sent.Append(piece);
        return piece;
    }

    /// <summary>How many trailing characters of <paramref name="raw"/> are the start of a reasoning tag.</summary>
    internal static int PartialTagLength(string raw)
    {
        var lt = raw.LastIndexOf('<');
        if (lt < 0 || raw.Length - lt >= 9) return 0;
        var tail = raw[lt..];
        foreach (var tag in Tags)
            if (tail.Length < tag.Length && tag.StartsWith(tail, StringComparison.OrdinalIgnoreCase)) return tail.Length;
        return 0;
    }
}

/// <summary>How a tool call is drawn in an ACP client: its kind, its title, the files it is about.</summary>
internal static class AcpToolCalls
{
    /// <summary>
    /// The ACP kind of a tool — the icon and grouping a client draws. A table, with <c>other</c> for everything it does
    /// not name (MCP tools, the user's shell tools, a tool added later): a wrong icon at worst, never a wrong action.
    /// </summary>
    public static string Kind(string tool) => tool switch
    {
        "read_file" or "list_files" or "get_active_document" or "get_open_editors" or "get_git_status"
            or "get_solution_info" or "generate_project_map" or "read_skill_file" or "analyze_code"
            or "get_debugger_state"                                                   => "read",
        "search_in_files" or "search_codebase" or "search_docs"                       => "search",
        "write_file" or "apply_diff" or "apply_edits" or "rename_symbol" or "insert_at_cursor"
            or "replace_selection" or "restore_file" or "update_memory"               => "edit",
        "delete_file"                                                                 => "delete",
        "run_command" or "run_tests" or "get_diagnostics"                             => "execute",
        "fetch_url" or "web_search"                                                   => "fetch",
        _                                                                             => "other",
    };

    /// <summary>The tool and what it acts on ("read_file · src/Program.cs"), the target cut to one readable line — a
    /// path under <paramref name="root"/> shown relative to it.</summary>
    public static string Title(string tool, JsonElement args, string? root = null)
    {
        var subject = Services.Execution.ToolRegistry.ExtractSubject(args);
        if (string.IsNullOrWhiteSpace(subject)) return tool;
        var line = subject.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (root is not null && System.IO.Path.IsPathRooted(line) && Services.Tools.PathSanitizer.IsUnderRoot(line, root))
            line = System.IO.Path.GetRelativePath(root, line).Replace('\\', '/');
        if (line.Length > 120) line = line[..119] + "…";
        return $"{tool} · {line}";
    }

    /// <summary>The arguments as an object, or <c>null</c> when the model wrote something that is not one.</summary>
    public static JsonElement? RawInput(string input)
    {
        try
        {
            using var doc = JsonDocument.Parse(input);
            return doc.RootElement.ValueKind == JsonValueKind.Object ? doc.RootElement.Clone() : null;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>A tool's output as a client draws it: whole, or cut on a line with the size of what was left out.</summary>
    /// <remarks>Display only — the model has the output it had. Cut, the cut is said.</remarks>
    public static string Shown(string output, int max = 20_000)
    {
        if (output.Length <= max) return output;
        var cut = output.LastIndexOf('\n', max);
        if (cut < max / 2) cut = max;
        return output[..cut] + $"\n… ({output.Length - cut:N0} more characters)";
    }

    /// <summary>
    /// <see cref="Shown"/> in a code block, as a tool's text content is sent to a client.
    /// </summary>
    /// <remarks>
    /// ⚠ Clients render a tool's text content as Markdown, which rewrites the very paths an output names: a backslash
    /// before a dot is an escape (<c>ws\.inferpal</c> shows <c>ws.inferpal</c>) and <c>--</c> turns into a dash. A code
    /// block is drawn as written. Its fence is longer than any run of backticks in the output, which cannot close it.
    /// </remarks>
    public static string Block(string output, int max = 20_000)
    {
        var shown = Shown(output, max).TrimEnd('\r', '\n');
        int longest = 0, run = 0;
        foreach (var c in shown)
        {
            run = c == '`' ? run + 1 : 0;
            if (run > longest) longest = run;
        }
        var fence = new string('`', Math.Max(3, longest + 1));
        return $"{fence}\n{shown}\n{fence}";
    }

    /// <summary>The tool a drawn title names (<c>"read_file · alpha.txt"</c> → <c>read_file</c>) — a bare name as is.</summary>
    public static string ToolOf(string title)
    {
        var at = title.IndexOf(" · ", StringComparison.Ordinal);
        return at < 0 ? title : title[..at];
    }
}

/// <summary>One file the user's message points at: by link (read from disk) or embedded (its text given).</summary>
internal sealed record AcpAttachment(string Name, string? Path, string? Text);

/// <summary>The user's message read from ACP content blocks.</summary>
/// <param name="Text">What the user wrote, links to non-files kept as Markdown links.</param>
/// <param name="Notes">What the message carried that Inferpal does not read (an image), said to the model.</param>
internal sealed record AcpPromptContent(string Text, IReadOnlyList<AcpAttachment> Attachments, IReadOnlyList<string> Notes);

/// <summary>Reads <c>session/prompt</c>'s content blocks.</summary>
internal static class AcpPromptBlocks
{
    /// <summary>The message, or the reason it is not one (thrown as invalid params by the caller).</summary>
    public static AcpPromptContent Read(JsonElement prompt)
    {
        if (prompt.ValueKind != JsonValueKind.Array || prompt.GetArrayLength() == 0)
            throw new ArgumentException("'prompt' must be a non-empty array of content blocks.");

        var text = new StringBuilder();
        var attachments = new List<AcpAttachment>();
        var notes = new List<string>();
        foreach (var block in prompt.EnumerateArray())
        {
            if (block.ValueKind != JsonValueKind.Object || !block.TryGetProperty("type", out var typeEl)
                || typeEl.ValueKind != JsonValueKind.String)
                throw new ArgumentException("Every content block must be an object with a string 'type'.");

            switch (typeEl.GetString())
            {
                case "text":
                    text.Append(Str(block, "text") ?? throw new ArgumentException("A 'text' block needs a string 'text'."));
                    break;
                case "resource_link":
                {
                    var uri  = Str(block, "uri")  ?? throw new ArgumentException("A 'resource_link' block needs a string 'uri'.");
                    var name = Str(block, "name") ?? throw new ArgumentException("A 'resource_link' block needs a string 'name'.");
                    if (FilePath(uri) is { } path) attachments.Add(new AcpAttachment(name, path, null));
                    else text.Append($" [{name}]({uri})");
                    break;
                }
                case "resource":
                {
                    if (!block.TryGetProperty("resource", out var res) || res.ValueKind != JsonValueKind.Object)
                        throw new ArgumentException("A 'resource' block needs an object 'resource'.");
                    var uri = Str(res, "uri") ?? string.Empty;
                    if (Str(res, "text") is { } body)
                        attachments.Add(new AcpAttachment(NameOf(uri), FilePath(uri), body));
                    else
                        notes.Add($"[A binary resource was attached ({NameOf(uri)}); this agent reads text only.]");
                    break;
                }
                case "image":
                    notes.Add("[An image was attached; this agent cannot see images.]");
                    break;
                case "audio":
                    notes.Add("[An audio clip was attached; this agent cannot hear audio.]");
                    break;
                default:
                    throw new ArgumentException($"Unknown content block type '{typeEl.GetString()}'.");
            }
        }
        return new AcpPromptContent(text.ToString(), attachments, notes);
    }

    private static string? Str(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>The local path of a <c>file://</c> URI (or of a bare absolute path); <c>null</c> for anything else.</summary>
    internal static string? FilePath(string uri)
    {
        if (Uri.TryCreate(uri, UriKind.Absolute, out var u))
        {
            if (u.IsFile) return u.LocalPath;
            // "C:\x" parses as a URI with the scheme "c": a Windows path, not a link.
            if (u.Scheme.Length == 1 && System.IO.Path.IsPathRooted(uri)) return System.IO.Path.GetFullPath(uri);
            return null;
        }
        return System.IO.Path.IsPathRooted(uri) ? System.IO.Path.GetFullPath(uri) : null;
    }

    private static string NameOf(string uri)
    {
        var path = FilePath(uri);
        return path is null ? uri : System.IO.Path.GetFileName(path);
    }
}
