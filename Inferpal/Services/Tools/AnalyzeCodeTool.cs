using System.Text.Json;

namespace Inferpal.Services.Tools;

/// <summary>
/// Unified static-analysis facade. Dispatches to one of three syntax/regex analyzers by
/// <c>mode</c>, replacing the former standalone <c>trace_dependency</c> / <c>analyze_impact</c> /
/// <c>trace_nexus</c> tools. Exposing one tool instead of three near-identical ones shrinks the
/// per-request tool list and removes the choice paralysis a small local model faces when several
/// "understand the code" tools look interchangeable.
/// </summary>
/// <remarks>
/// The sub-tools each parse their own arguments from the shared <see cref="JsonElement"/> and
/// ignore extras, so the facade simply forwards <paramref name="args"/> unchanged. The only
/// reserved name is <c>mode</c> (the strategy selector) — nexus's bridge filter was renamed to
/// <c>bridges</c> to avoid the clash.
/// </remarks>
internal sealed class AnalyzeCodeTool : ITool
{
    private readonly ITool _callgraph; // trace_dependency
    private readonly ITool _impact;    // analyze_impact
    private readonly ITool _nexus;     // trace_nexus

    public AnalyzeCodeTool(Func<string?> getRoot)
    {
        _callgraph = new TraceDependencyTool(getRoot);
        _impact    = new AnalyzeImpactTool(getRoot);
        _nexus     = new NexusIntelligenceTool(getRoot);
    }

    public string Name => "analyze_code";

    public string Description =>
        "Static code analysis. Choose 'mode':\n" +
        "• 'callgraph' — methods in a file, what they call (callees) and/or who calls them (callers), " +
        "cross-file up to 'depth'. Requires 'path'.\n" +
        "• 'impact' — blast radius of changing a file: dependent files, tests and entry points " +
        "(direct + transitive). Requires 'path'.\n" +
        "• 'nexus' — cross-language bridges between C# and TypeScript/JS (REST endpoints, JS interop, " +
        "SignalR hubs). Scans 'root' (defaults to the solution root).";

    public object Parameters => new
    {
        type = "object",
        properties = new
        {
            mode      = new { type = "string",  description = "Which analysis to run: 'callgraph' | 'impact' | 'nexus'." },
            path      = new { type = "string",  description = "Absolute path to the source file. Required for 'callgraph' and 'impact'." },
            root      = new { type = "string",  description = "Root directory to scan. Used by 'nexus'; defaults to the solution root." },
            symbol    = new { type = "string",  description = "Focus on a single method/type name. Used by 'callgraph' and 'impact'." },
            depth     = new { type = "integer", description = "Cross-file recursion depth (callgraph: 0-3, default 1; impact: 1-3, default 2)." },
            direction = new { type = "string",  description = "'callgraph' only: 'callees' (default) | 'callers' | 'both'." },
            focus     = new { type = "string",  description = "'nexus' only: filter to a specific route / function / hub-method name." },
            bridges   = new { type = "string",  description = "'nexus' only: 'rest' | 'interop' | 'signalr' | 'all' (default)." }
        },
        required = new[] { "mode" }
    };

    public async Task<string> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var mode = args.TryGetProperty("mode", out var mv) ? mv.GetString()?.Trim().ToLowerInvariant() : null;

        return mode switch
        {
            "callgraph" or "calls" or "trace" or "dependency" => await _callgraph.ExecuteAsync(args, ct),
            "impact"    or "blast"                            => await _impact.ExecuteAsync(args, ct),
            "nexus"     or "bridges" or "crosslang"           => await _nexus.ExecuteAsync(args, ct),
            _ => $"Unknown mode '{mode}'. Use one of: 'callgraph', 'impact', 'nexus'.",
        };
    }
}
