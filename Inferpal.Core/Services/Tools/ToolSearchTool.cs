using System.Text.Json;
using Inferpal.Services.Execution;

namespace Inferpal.Services.Tools;

/// <summary>
/// <c>tool_search</c>: loads the MCP tools that wait on demand (<see cref="OnDemandTools"/>), by keyword or by name.
/// </summary>
/// <remarks>⚠ Offered only while a server is on demand: otherwise its every answer would be "nothing to load".</remarks>
internal sealed class ToolSearchTool(OnDemandTools onDemand) : ITool
{
    public string Name => "tool_search";

    public string Description => onDemand.Description();

    public object Parameters => new
    {
        type = "object",
        properties = new
        {
            query = new { type = "string", description = "Keywords for the tool you need, or \"select:name1,name2\" to load tools by name." },
        },
        required = new[] { "query" },
    };

    public bool IsOffered => onDemand.HasDeferred;

    public Task<string> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var query = args.Str("query") ?? throw new ArgumentException("query is required.");
        return Task.FromResult(onDemand.Search(query));
    }
}
