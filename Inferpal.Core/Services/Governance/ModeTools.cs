using System.Text.Json;
using System.Text.RegularExpressions;
using Inferpal.Services.Mcp;
using Inferpal.Services.Tools;

namespace Inferpal.Services.Governance;

/// <summary>
/// The tools a mode keeps: families of built-in tools, MCP servers and tools, the user's own tools, and — from Roo — a
/// pattern every edit must match. A mode only ever NARROWS what is offered: nothing here grants an approval, a rule or a
/// tool that is turned off.
/// </summary>
/// <remarks>
/// ⚠ A name nobody recognizes is NAMED and dropped (<see cref="NotCarried"/>), never read as "all tools": a mode written for
/// another product lists tools that do not exist here, and widening on an unknown name would hand a read-only mode the
/// writes. The Copilot names are the ones the agents of <c>github/awesome-copilot</c> write, older ones included.
/// </remarks>
internal sealed class ModeTools
{
    /// <summary>The built-in tools of each family a mode can name. Every registered built-in tool is in one
    /// (<c>ChatModesTests</c>): one left out would be read as a user's own tool, kept only with <c>execute</c>.</summary>
    /// <remarks>⚠ <c>debug_inspect</c> is under <c>execute</c>, not <c>read</c>: its <c>evaluate</c> runs an expression in
    /// the paused program, method calls included — Plan mode leaves it out for that reason.</remarks>
    internal static readonly IReadOnlyDictionary<string, string[]> Families = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
    {
        ["read"] =
        [
            "read_file", "list_files", "get_active_document", "get_open_editors", "get_solution_info", "generate_project_map",
            "analyze_code", "read_skill_file", "get_diagnostics", "get_git_status", "get_debugger_state",
        ],
        ["search"]  = ["search_in_files", "search_codebase", "search_docs", "list_files"],
        ["edit"]    =
        [
            "write_file", "apply_diff", "apply_edits", "delete_file", "rename_symbol", "insert_at_cursor", "replace_selection",
            "restore_file", "update_memory",
        ],
        ["execute"] = ["run_command", "run_tests", "debug_control", "debug_inspect"],
        ["web"]     = ["fetch_url", "web_search"],
    };

    /// <summary>The tool that searches the MCP tools offered on demand: kept with any MCP grant.</summary>
    private const string ToolSearch = "tool_search";

    /// <summary>Copilot's tool names that are not a family name, as its agents write them (alone, or after a family:
    /// <c>edit/editFiles</c>) — each read as the Inferpal tools that do the same.</summary>
    private static readonly IReadOnlyDictionary<string, string[]> CopilotNames = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
    {
        ["codebase"]      = ["search_codebase"],
        ["editFiles"]     = Families["edit"],
        ["createFile"]    = ["write_file"],
        ["readFile"]      = ["read_file"],
        ["listDirectory"] = ["list_files"],
        ["fileSearch"]    = ["list_files"],
        ["textSearch"]    = ["search_in_files"],
        ["searchResults"] = ["search_in_files"],
        ["findTestFiles"] = ["list_files", "search_in_files"],
        ["problems"]      = ["get_diagnostics"],
        ["usages"]        = ["analyze_code"],
        ["changes"]       = ["get_git_status"],
        ["fetch"]         = ["fetch_url"],
        ["runCommands"]   = Families["execute"],
        ["runInTerminal"] = ["run_command"],
        ["terminalCommand"] = ["run_command"],
        ["shell"]         = ["run_command"],
        ["runTests"]      = ["run_tests"],
        ["testFailure"]   = ["run_tests"],
        ["memory"]        = ["update_memory"],
    };

    /// <summary>Copilot's tools that have no counterpart here (its tasks, terminals, todo list, sub-agents, editor API).</summary>
    private static readonly HashSet<string> CopilotWithoutCounterpart = new(StringComparer.OrdinalIgnoreCase)
    {
        "todo", "todos", "agent", "runSubagent", "vscode", "vscodeAPI", "new", "extensions", "githubRepo", "openSimpleBrowser",
        "terminalLastCommand", "terminalSelection", "getTerminalOutput", "runTasks", "runTask", "createAndRunTask",
        "getTaskOutput", "runNotebooks",
    };

    /// <summary>Copilot's own families and tool groups, before a <c>/</c>: what comes after is one of THEIR tools, never an
    /// MCP server's.</summary>
    private static readonly HashSet<string> CopilotGroups = new(StringComparer.OrdinalIgnoreCase)
    {
        "read", "search", "edit", "execute", "web", "vscode", "agent", "todo", "runCommands",
    };

    private readonly HashSet<string> _builtIn = new(StringComparer.Ordinal);
    private readonly List<string> _mcpServers = [];
    private readonly HashSet<string> _mcpTools = new(StringComparer.Ordinal);
    private readonly List<string> _loose = [];
    private readonly List<string> _notCarried = [];
    private bool _allMcp;
    private bool _ownTools;

    /// <summary>The names the file lists that this product has no tool for — said where the mode is shown.</summary>
    public IReadOnlyList<string> NotCarried => _notCarried;

    /// <summary>The pattern a file an edit writes must match (Roo's <c>fileRegex</c>), against its path relative to the
    /// workspace with <c>/</c> separators; <c>null</c> when edits are not narrowed.</summary>
    public Regex? EditPattern { get; private set; }

    /// <summary>The pattern as written, and what its file says it means — for the refusal.</summary>
    public string? EditPatternText { get; private set; }

    /// <summary>What the mode's file says the pattern means (Roo's <c>description</c> beside it), when it says.</summary>
    public string? EditPatternDescription { get; private set; }

    /// <summary>Whether the mode offers a tool by this name (as the registry names it).</summary>
    public bool Allows(string toolName)
    {
        if (McpTool.IsMcpName(toolName))
            return _allMcp || _mcpTools.Contains(toolName)
                   || _mcpServers.Any(s => McpTool.BelongsTo(toolName, s))
                   || _loose.Any(l => McpTool.BelongsTo(toolName, l) || IsMcpToolCalled(toolName, l));
        if (toolName == ToolSearch) return _allMcp || _mcpServers.Count > 0 || _mcpTools.Count > 0 || _loose.Count > 0;
        if (IsBuiltIn(toolName)) return _builtIn.Contains(toolName) || _loose.Contains(toolName, StringComparer.Ordinal);
        // A tool of the user's own (a shell tool): a command, kept with execute, or by its name.
        return _ownTools || _loose.Contains(toolName, StringComparer.Ordinal);
    }

    /// <summary>Whether the mode keeps any tool that can change something — an edit, a command, a debugger action, an MCP
    /// tool, a name it lists that may be one: only a mode that keeps none is shown as "reads, writes nothing".</summary>
    public bool MayWrite =>
        _builtIn.Any(t => !Families["read"].Contains(t) && !Families["search"].Contains(t) && !Families["web"].Contains(t))
        || _ownTools || _allMcp || _mcpServers.Count > 0 || _mcpTools.Count > 0 || _loose.Count > 0;

    /// <summary>The families the mode keeps whole, in the order they are listed — the mode's tools in words.</summary>
    public IReadOnlyList<string> WholeFamilies =>
        [.. Families.Where(f => f.Value.All(_builtIn.Contains)).Select(f => f.Key)];

    internal static bool IsBuiltIn(string toolName) => Families.Values.Any(f => f.Contains(toolName, StringComparer.Ordinal));

    /// <summary>The paths of the files a call to an edit tool writes, read as the tool reads them (<see cref="ToolArgs"/>:
    /// a number is a name too, a list may come as text); <c>null</c> when the call writes but this cannot name every file
    /// it writes — the active document, a rename across files, a path missing or unreadable — and is refused for it.</summary>
    internal static IReadOnlyList<string>? EditedPaths(string toolName, JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object) return toolName == "update_memory" ? [".inferpal/memory.md"] : null;
        switch (toolName)
        {
            case "write_file" or "apply_diff" or "delete_file" or "restore_file":
                return args.Str("path") is { Length: > 0 } path ? [path] : null;
            case "apply_edits":
                if (!args.Array("edits", out var edits)) return null;
                var paths = new List<string>();
                foreach (var e in edits.EnumerateArray())
                {
                    if (e.ValueKind != JsonValueKind.Object || e.Str("path") is not { Length: > 0 } p) return null;
                    paths.Add(p);
                }
                return paths;
            case "update_memory":
                return [".inferpal/memory.md"];
            default:
                return null;
        }
    }

    /// <summary>Copilot's <c>tools</c> (a list, or one string with commas).</summary>
    public static ModeTools FromCopilot(IEnumerable<string> names)
    {
        var tools = new ModeTools();
        foreach (var raw in names)
        {
            var name = raw.Trim();
            if (name.Length == 0) continue;
            var slash = name.IndexOf('/');
            if (slash < 0)
            {
                if (tools.TakeCopilotName(name)) continue;
                if (CopilotWithoutCounterpart.Contains(name)) tools._notCarried.Add(name);
                else tools._loose.Add(name);   // a tool of the user's, an MCP server, or an MCP tool
                continue;
            }
            var group = name[..slash];
            var tool  = name[(slash + 1)..];
            if (CopilotGroups.Contains(group))
            {
                // edit/editFiles, read/problems: the tool named; a whole family only by its own name.
                if (tool == "*" ? !tools.TakeCopilotName(group) : !tools.TakeCopilotName(tool)) tools._notCarried.Add(name);
            }
            else if (tool == "*") tools._mcpServers.Add(group);
            else tools._mcpTools.Add(McpTool.BuildName(group, tool));
        }
        return tools;
    }

    /// <summary>Roo's <c>groups</c>: <c>read</c>, <c>edit</c> (with or without <c>fileRegex</c>), <c>command</c>,
    /// <c>mcp</c>; <c>browser</c> has no tool here.</summary>
    /// <param name="groups">Each group, its <c>fileRegex</c> and what it means; <c>Unreadable</c> when the group came with
    /// options this could not read as a pattern (Roo refuses such a mode).</param>
    public static ModeTools FromRoo(IEnumerable<(string Group, string? FileRegex, string? Description, bool Unreadable)> groups)
    {
        var tools = new ModeTools();
        foreach (var (group, fileRegex, desc, unreadable) in groups)
        {
            switch (group.ToLowerInvariant())
            {
                case "read":    tools.AddFamily("read"); tools.AddFamily("search"); break;
                case "edit":
                    tools.AddFamily("edit");
                    if (unreadable)
                    {
                        // Options in a shape this cannot read: edits narrowed to nothing, never to all.
                        tools.EditPattern = new Regex("(?!)", RegexOptions.None, RegexBudget.Default);
                        tools.EditPatternText = fileRegex ?? "(unreadable)";
                        tools._notCarried.Add("edit options");
                    }
                    else if (fileRegex is { Length: > 0 })
                    {
                        tools.EditPatternText = fileRegex;
                        tools.EditPatternDescription = desc;
                        // Roo matches case-sensitively; a pattern .NET cannot read narrows edits to nothing, never to all.
                        try { tools.EditPattern = new Regex(fileRegex, RegexOptions.CultureInvariant, RegexBudget.Default); }
                        catch (ArgumentException)
                        {
                            tools.EditPattern = new Regex("(?!)", RegexOptions.None, RegexBudget.Default);
                            tools._notCarried.Add("fileRegex " + fileRegex);
                        }
                    }
                    break;
                case "command": tools.AddFamily("execute"); tools._ownTools = true; break;
                case "mcp":     tools._allMcp = true; break;
                default:        tools._notCarried.Add(group); break;
            }
        }
        return tools;
    }

    private ModeTools() { }

    private void AddFamily(string family)
    {
        foreach (var t in Families[family]) _builtIn.Add(t);
    }

    /// <summary>A family, a Copilot tool name, or an Inferpal tool written by its own name.</summary>
    private bool TakeCopilotName(string name)
    {
        if (Families.ContainsKey(name))
        {
            AddFamily(name);
            if (name.Equals("execute", StringComparison.OrdinalIgnoreCase)) _ownTools = true;
            return true;
        }
        if (CopilotNames.TryGetValue(name, out var tools))
        {
            foreach (var t in tools) _builtIn.Add(t);
            if (name.Equals("runCommands", StringComparison.OrdinalIgnoreCase)) _ownTools = true;
            return true;
        }
        if (IsBuiltIn(name)) { _builtIn.Add(name); return true; }
        return false;
    }

    /// <summary>Whether an MCP tool's own part (after its server) is <paramref name="tool"/>.</summary>
    private static bool IsMcpToolCalled(string toolName, string tool)
    {
        var suffix = McpTool.BuildName("x", tool)["mcp__x".Length..];   // "__<tool>", sanitized as the registry does
        return toolName.EndsWith(suffix, StringComparison.Ordinal);
    }
}
