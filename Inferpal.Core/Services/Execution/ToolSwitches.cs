using Inferpal.Services.Mcp;
using Inferpal.Services.Tools;

namespace Inferpal.Services.Execution;

/// <summary>
/// The tools the user turned off (<see cref="Config.InferpalConfig.DisabledTools"/>): how a tool is named there, whether
/// one is off, and how the list is written.
/// </summary>
/// <remarks>
/// <para>
/// ⚠ One name per tool, the one <c>/tools</c> shows (<see cref="KeyOf"/>): a built-in or custom tool by its own name, an MCP
/// tool as <c>server/tool</c> — compared as it is, never turned back into a tool name. Rebuilt, <c>my.server/t</c> would
/// name the tool of <c>my-server</c> (both clean up to <c>mcp__my_server__t</c>, the second renamed <c>_2</c>), and a server
/// named <c>org/github</c> would be split at its own slash: the wrong tool, or none, under "turned off". The name a request
/// calls a tool by (<c>mcp__server__tool</c>) is accepted as well.
/// </para>
/// <para>
/// ⚠ A tool turned off is never offered to the model nor run for it — and only for it: a gesture of the user that runs a
/// tool itself (<c>/diff</c>, <c>@problems</c>, <c>/tdd</c>'s test run) is not the model's call, and keeps working.
/// </para>
/// </remarks>
internal sealed class ToolSwitches
{
    /// <summary>The one tool that cannot be turned off: without it, the tools on demand cannot be reached.</summary>
    internal const string AlwaysOn = ToolSearchTool.ToolName;

    private readonly HashSet<string> _names;

    private ToolSwitches(IReadOnlyList<string> entries)
    {
        Entries = entries;
        _names = new(entries, StringComparer.Ordinal);
    }

    /// <summary>The lines of the setting, as written: trimmed, without blanks, comments or repeats.</summary>
    public IReadOnlyList<string> Entries { get; }

    /// <summary>The switches a setting's text says. Read on every request: the registry keeps the last one
    /// (<c>ToolRegistry.Switches</c>).</summary>
    public static ToolSwitches Parse(string? text) => new(Lines(text ?? string.Empty));

    private static List<string> Lines(string text)
    {
        var lines = new List<string>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#') || lines.Contains(line)) continue;
            lines.Add(line);
        }
        return lines;
    }

    /// <summary>The name a tool is turned off by: its own, or <c>server/tool</c> for an MCP tool.</summary>
    public static string KeyOf(ITool tool) => tool is McpTool m ? $"{m.ServerName}/{m.LocalName}" : tool.Name;

    /// <summary>The name a person types to designate <paramref name="tool"/>: <see cref="KeyOf"/>, or the name a request
    /// calls it by when the key holds a space — <c>/tools on My Server/t</c> splits into two names that designate nothing.</summary>
    public static string Typed(ITool tool) => KeyOf(tool) is var key && key.Any(char.IsWhiteSpace) ? tool.Name : key;

    /// <summary>The setting's text without the lines equal to <paramref name="lines"/>, the others kept as written — a line
    /// that names no tool served here (a server stopped, the other editor's debugger) is turned back on by its text.</summary>
    public static string RemoveLines(string? text, IReadOnlyCollection<string> lines) =>
        string.Join("\n", (text ?? string.Empty).Replace("\r\n", "\n").Split('\n')
                              .Where(l => l.Trim().Length > 0 && !lines.Contains(l.Trim())).Select(l => l.TrimEnd()));

    /// <summary>Whether the user turned <paramref name="tool"/> off — <c>tool_search</c> never is, nor a built-in tool the
    /// person opened for the question being answered (<see cref="OpenForThisQuestion"/>).</summary>
    public bool TurnsOff(ITool tool) =>
        tool.Name != AlwaysOn && (_names.Contains(KeyOf(tool)) || _names.Contains(tool.Name))
        && !(tool is not McpTool && s_openNow.Value is { } open && open.Contains(tool.Name));

    // The flow's own: another editor's turn, or a question asked after this one, never sees it.
    private static readonly AsyncLocal<IReadOnlySet<string>?> s_openNow = new();

    /// <summary>
    /// Offers the built-in <paramref name="tools"/> the person turned off, for the flow that runs one question — the
    /// gesture "use it for this question" of the tool-off notice. Disposing ends it. The setting is never written.
    /// </summary>
    /// <remarks>Built-in tools only: the MCP tools on are cached per switches (<c>ToolRegistry.McpToolsOn</c>).</remarks>
    public static IDisposable OpenForThisQuestion(IReadOnlyCollection<string>? tools)
    {
        var before = s_openNow.Value;
        if (tools is { Count: > 0 }) s_openNow.Value = new HashSet<string>(tools, StringComparer.Ordinal);
        return new Restore(() => s_openNow.Value = before);
    }

    private sealed class Restore(Action undo) : IDisposable
    {
        private Action? _undo = undo;
        public void Dispose() => Interlocked.Exchange(ref _undo, null)?.Invoke();
    }

    /// <summary>The setting's text with <paramref name="tools"/> added (<paramref name="off"/>) or removed, the other lines
    /// kept as written.</summary>
    /// <remarks>A line naming the same tool another way (<c>mcp__s__t</c> for <c>s/t</c>) is removed with it: left, it would
    /// keep the tool off.</remarks>
    public static string Write(string? text, IReadOnlyList<ITool> tools, bool off)
    {
        var raw = (text ?? string.Empty).Replace("\r\n", "\n").Split('\n').Select(l => l.TrimEnd()).Where(l => l.Trim().Length > 0).ToList();
        foreach (var tool in tools)
        {
            raw.RemoveAll(l => Names(l.Trim(), tool));
            if (off) raw.Add(KeyOf(tool));
        }
        return string.Join("\n", raw);
    }

    /// <summary>Whether one line of the setting names <paramref name="tool"/>.</summary>
    private static bool Names(string line, ITool tool) =>
        !line.StartsWith('#') && new ToolSwitches([line]).TurnsOff(tool);
}
