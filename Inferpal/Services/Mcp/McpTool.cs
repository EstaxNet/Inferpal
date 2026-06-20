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

    public McpTool(IMcpClient client, McpToolInfo info, IApprovalService approval)
    {
        _client          = client;
        _approval        = approval;
        _serverLocalName = info.Name;
        Name             = BuildName(client.ServerName, info.Name);
        Description      = string.IsNullOrWhiteSpace(info.Description)
            ? $"MCP tool '{info.Name}' from server '{client.ServerName}'."
            : info.Description;
        Parameters       = info.InputSchema;
    }

    public string Name { get; }
    public string Description { get; }
    public object Parameters { get; }

    public async Task<string> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var details = args.ValueKind == JsonValueKind.Undefined ? string.Empty : args.GetRawText();
        if (!await _approval.RequestApprovalAsync(Name, details, ct))
            return Strings.McpCancelled;

        return await _client.CallToolAsync(_serverLocalName, args, ct);
    }

    /// <summary>
    /// Builds the Ollama-facing tool name: <c>mcp__&lt;server&gt;__&lt;tool&gt;</c>, with any
    /// character outside <c>[a-zA-Z0-9_]</c> replaced by <c>_</c> (Ollama tool-name constraint).
    /// </summary>
    internal static string BuildName(string server, string tool)
        => $"mcp__{Sanitize(server)}__{Sanitize(tool)}";

    private static string Sanitize(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
            sb.Append(char.IsAsciiLetterOrDigit(ch) || ch == '_' ? ch : '_');
        return sb.ToString();
    }
}
