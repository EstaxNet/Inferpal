using System.Text.Json;

namespace Inferpal.Services.Tools;

/// <summary>
/// Agent tool: generates a compact architectural map of the current project.
/// Scans all .cs source files and reports namespace distribution, type inventory,
/// dependency edges, and cross-reference hotspots — O(N) over files.
/// </summary>
internal sealed class GenerateProjectMapTool : ITool
{
    private readonly ProjectMapService _mapService;

    public GenerateProjectMapTool(ProjectMapService mapService) =>
        _mapService = mapService;

    public string Name => "generate_project_map";

    public string Description =>
        "Generate a compact architectural map of the project: namespace distribution, " +
        "class/interface/record inventory, internal dependency edges between namespaces, " +
        "and cross-reference hotspots. Use this to understand the codebase layout before " +
        "diving into specific files.";

    public object Parameters => new
    {
        type       = "object",
        properties = new
        {
            refresh = new
            {
                type        = "boolean",
                description = "Force a fresh scan instead of returning a cached result (default: false)."
            }
        },
        required = Array.Empty<string>()
    };

    public async Task<string> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        bool refresh = args.TryGetProperty("refresh", out var rv) &&
                       rv.ValueKind == JsonValueKind.True;

        if (refresh)
            _mapService.Invalidate();

        return await _mapService.GenerateMapAsync(ct);
    }
}
