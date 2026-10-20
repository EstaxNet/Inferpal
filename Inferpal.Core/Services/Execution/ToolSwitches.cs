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

    /// <summary>Whether the user turned <paramref name="tool"/> off — <c>tool_search</c> never is.</summary>
    public bool TurnsOff(ITool tool) =>
        tool.Name != AlwaysOn && (_names.Contains(KeyOf(tool)) || _names.Contains(tool.Name));

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
