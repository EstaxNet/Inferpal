using System.Text.Json;
using Inferpal.Localization;
using Inferpal.Models;

namespace Inferpal.Services.Inference;

/// <summary>
/// What a chat request carries, in estimated tokens (~4 characters a token): the tool definitions — and how many of
/// them, of what weight, MCP servers bring — the system prompt, the earlier conversation and the last message.
/// </summary>
/// <remarks>
/// ⚠ A request too large for the context window is refused (or, on Ollama, silently loses its head), and
/// the message that says so must name the part that is too large: blaming the agent's tool definitions
/// whatever the request held sends the user to disable tools on a code action that sends none, or on a
/// question whose attached file is the whole problem — a remedy that changes nothing. Each part carries
/// the gesture that shrinks it, largest first. ⚠ And the MCP servers' share is named: an editor can hand the agent its
/// own server (Qt Creator: 159 tools, most of a 32K window), and "fewer enabled tools" alone does not say whose.
/// </remarks>
internal readonly record struct RequestSize(int Tools, int SystemPrompt, int Earlier, int Last, int McpTools = 0, int McpCount = 0)
{
    public int Total => Tools + SystemPrompt + Earlier + Last;

    public static RequestSize Of(IReadOnlyList<ChatMessageDto> messages, IReadOnlyList<ToolDefinition>? defs)
    {
        var system = messages.Count > 0 && messages[0].Role == "system" ? 1 : 0;
        var last   = messages.Count > system ? 1 : 0;
        var (toolChars, mcpChars, mcpCount) = ToolChars(defs);
        return new(
            toolChars / 4,
            AgentOrchestrator.EstimateChars(messages.Take(system)) / 4,
            AgentOrchestrator.EstimateChars(messages.Skip(system).Take(messages.Count - system - last)) / 4,
            AgentOrchestrator.EstimateChars(messages.Skip(messages.Count - last)) / 4,
            mcpChars / 4,
            mcpCount);
    }

    /// <summary>One line per part that is not empty, largest first, each naming what shrinks it.</summary>
    public string Breakdown()
    {
        var (mcpTokens, mcpCount) = (McpTools, McpCount);
        return string.Join("\n", new (int Tokens, Func<int, string> Line)[]
            {
                (Tools,        t => mcpCount > 0 ? Strings.ContextPartToolsMcp(t, mcpTokens, mcpCount) : Strings.ContextPartTools(t)),
                (SystemPrompt, Strings.ContextPartSystem),
                (Earlier,      Strings.ContextPartEarlier),
                (Last,         Strings.ContextPartLast),
            }
            .Where(p => p.Tokens > 0)
            .OrderByDescending(p => p.Tokens)
            .Select(p => "- " + p.Line(p.Tokens)));
    }

    /// <summary>Estimated tokens of the tool definitions a request carries (their JSON, ~4 characters a token).</summary>
    public static int ToolTokens(IReadOnlyList<ToolDefinition>? defs) => ToolChars(defs).All / 4;

    /// <summary>The definitions' JSON length — the array's, each serialized once — and the MCP servers' share of it.</summary>
    private static (int All, int Mcp, int McpCount) ToolChars(IReadOnlyList<ToolDefinition>? defs)
    {
        if (defs is not { Count: > 0 }) return (0, 0, 0);
        int all = 1 + defs.Count, mcp = 0, mcpCount = 0;   // the brackets and the commas between the definitions
        foreach (var d in defs)
        {
            int chars;
            try   { chars = JsonSerializer.Serialize(d).Length; }
            catch (Exception ex)
            {
                Diagnostics.Swallow("RequestSize.ToolChars", ex);
                chars = 400;   // rough per-tool budget when a schema does not serialize
            }
            all += chars;
            if (!Mcp.McpTool.IsMcpName(d.Function.Name)) continue;
            mcp += chars;
            mcpCount++;
        }
        return (all, mcp, mcpCount);
    }
}
