using System.Text;
using System.Text.Json;
using Inferpal.Localization;
using Inferpal.Services.Tools;

namespace Inferpal.Services.Mcp;

/// <summary>
/// Adapts a single MCP server tool to the agent's <see cref="ITool"/> contract so it
/// flows through <see cref="ToolRegistry"/> exactly like a built-in tool.
/// </summary>
/// <remarks>
/// MCP tools are external code with arbitrary capabilities (filesystem, shell, network), so
/// every call is gated behind <see cref="IApprovalService"/> — the same guard built-in
/// destructive tools (<c>write_file</c>, <c>run_command</c>) use.
/// </remarks>
internal sealed class McpTool : ITool
{
    private readonly IMcpClient       _client;
    private readonly IApprovalService _approval;
    private readonly string           _serverLocalName;

    /// <param name="turnedOffByEntry">The server's entry lists this tool in its <c>disabledTools</c>.</param>
    public McpTool(IMcpClient client, McpToolInfo info, IApprovalService approval, bool turnedOffByEntry = false)
    {
        _client          = client;
        _approval        = approval;
        _serverLocalName = info.Name;
        TurnedOffByEntry = turnedOffByEntry;
        Name             = BuildName(client.ServerName, info.Name);
        Description      = string.IsNullOrWhiteSpace(info.Description)
            ? $"MCP tool '{info.Name}' from server '{client.ServerName}'."
            : info.Description;
        // Local $refs inlined: Ollama drops a $ref, and the parameter it named would reach the model untyped.
        Parameters       = McpSchemaRefs.Inline(info.InputSchema);
    }

    private McpTool(McpTool source, IApprovalService approval, string? name = null)
    {
        _client          = source._client;
        _approval        = approval;
        _serverLocalName = source._serverLocalName;
        TurnedOffByEntry = source.TurnedOffByEntry;
        Name             = name ?? source.Name;
        Description      = source.Description;
        Parameters       = source.Parameters;
    }

    /// <summary>The same tool exposed under another name — used when two servers' names normalise to
    /// the same tool name (<c>my-server</c> and <c>my.server</c>). Calls still go to this tool's server.</summary>
    internal McpTool WithName(string name) => new(this, _approval, name);

    /// <summary>
    /// The same tool gated by a different approval pipeline. The service is captured at
    /// construction, so without rebinding a sibling registry built by
    /// <c>ToolRegistry.WithApprovalService</c> keeps prompting through the ORIGINAL service:
    /// <c>TestFileWriteGuard</c> would not apply to MCP tools, and a prior "Always" grant would let
    /// a model rewrite a test file through an MCP filesystem server with no force-prompt. A
    /// registry's whole surface answers to its own approval service.
    /// </summary>
    internal McpTool WithApproval(IApprovalService approval) =>
        ReferenceEquals(approval, _approval) ? this : new McpTool(this, approval);

    public string Name { get; }
    public string Description { get; }
    public object Parameters { get; }

    /// <summary>The configured name of the server this tool is called on — what the user calls it.</summary>
    internal string ServerName => _client.ServerName;

    /// <summary>The tool's name on its server, before the <c>mcp__&lt;server&gt;__</c> prefix.</summary>
    internal string LocalName => _serverLocalName;

    /// <summary>The server's entry turns this tool off (<c>disabledTools</c>): it is never offered to the model.</summary>
    internal bool TurnedOffByEntry { get; }

    public async Task<string> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var details = args.ValueKind == JsonValueKind.Undefined ? string.Empty : args.GetRawText();

        // The human reads the raw JSON; the RULES read that plus the bare string values. Passing
        // only the JSON hid a path inside its quotes from AgentInstructionFiles — a write to
        // .inferpal/memory.md through an MCP filesystem server, which its remarks name as a source,
        // was not force-prompted. See McpApprovalSubject.
        if (!await _approval.RequestApprovalAsync(Name, details, ct, subject: McpApprovalSubject.From(args)))
            return Strings.McpCancelled;

        // The server may ask the person for values only where this pipeline can draw the card, at this call.
        return await _client.CallToolAsync(_serverLocalName, args, _approval.CanAskForInput ? _approval.AskForInputAsync : null, ct);
    }

    /// <summary>
    /// Builds the Ollama-facing tool name: <c>mcp__&lt;server&gt;__&lt;tool&gt;</c>, with any
    /// character outside <c>[a-zA-Z0-9_]</c> replaced by <c>_</c> (Ollama tool-name constraint).
    /// </summary>
    internal static string BuildName(string server, string tool)
        => $"{Prefix}{Sanitize(server)}__{Sanitize(tool)}";

    private const string Prefix = "mcp__";

    /// <summary>
    /// What every tool of a server named <paramref name="serverName"/> is called by: two servers with the same value
    /// serve tools under the same names (<c>my-server</c> and <c>my.server</c> both give <c>mcp__my_server__*</c>).
    /// </summary>
    internal static string ToolNamesOf(string serverName) => BuildName(serverName, string.Empty);

    /// <summary>
    /// Whether a tool of one server could carry the name of a tool of the other: the same names, one inside the other
    /// (<c>GitHub (work)</c> serves <c>mcp__GitHub__work___*</c>, and <c>GitHub</c>'s tool <c>work___create_issue</c> is
    /// <c>mcp__GitHub__work___create_issue</c>), or names that differ only by case — which the permission rules, matched
    /// without case, do not tell apart.
    /// </summary>
    internal static bool ToolNamesClash(string serverA, string serverB)
    {
        var (a, b) = (ToolNamesOf(serverA), ToolNamesOf(serverB));
        return a.StartsWith(b, StringComparison.OrdinalIgnoreCase) || b.StartsWith(a, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True when <paramref name="toolName"/> is a tool served by an MCP server — whose schema a third party
    /// wrote, free-form objects included.</summary>
    internal static bool IsMcpName(string toolName) => toolName.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>
    /// True when <paramref name="toolName"/> is a name this server's tools would carry.
    /// </summary>
    /// <remarks>
    /// ⚠ Built from <see cref="BuildName"/> rather than by splitting on <c>__</c>: sanitising turns
    /// every non-alphanumeric character into <c>_</c>, so a server or tool whose name already holds
    /// one makes any split ambiguous. Asking the prefix question with the same builder cannot drift.
    /// </remarks>
    internal static bool BelongsTo(string toolName, string serverName)
        => toolName.StartsWith(BuildName(serverName, string.Empty), StringComparison.Ordinal);

    private static string Sanitize(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
            sb.Append(char.IsAsciiLetterOrDigit(ch) || ch == '_' ? ch : '_');
        return sb.ToString();
    }
}
