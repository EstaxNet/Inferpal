using Inferpal.Services.Tools;

namespace Inferpal.Services.Execution;

/// <summary>Where a tool comes from.</summary>
internal enum ToolSource { BuiltIn, Custom, Mcp }

/// <summary>What the next request does with a tool.</summary>
internal enum ToolState
{
    /// <summary>Declared with the request.</summary>
    Sent,
    /// <summary>Waits until <c>tool_search</c> loads it.</summary>
    OnDemand,
    /// <summary>The user turned it off (<c>/tools off</c>, the Tools page).</summary>
    OffByUser,
    /// <summary>Its MCP server's entry turns it off (<c>disabledTools</c>).</summary>
    OffByEntry,
    /// <summary>Not offered now: it has nothing to work on (no documentation indexed, no server on demand).</summary>
    NotOffered,
}

/// <summary>One tool as <c>/tools</c> and the Tools page list it (<see cref="ToolRegistry.Profile"/>).</summary>
/// <param name="Key">The name it is turned off by (<see cref="ToolSwitches.KeyOf"/>).</param>
/// <param name="Server">Its MCP server's name; <c>null</c> for a built-in or custom tool.</param>
/// <param name="Tokens">What its definition weighs in a request, as <c>RequestSize</c> counts it.</param>
internal sealed record ToolProfileEntry(ITool Tool, string Key, ToolSource Source, string? Server, ToolState State, int Tokens);
