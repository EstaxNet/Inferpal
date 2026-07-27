using System.Text.Json;
using Inferpal.Models;

namespace Inferpal.Services.Execution;

/// <summary>
/// A no-op <see cref="IToolRegistry"/> that exposes no tools.
/// Used when tool calling is disabled (e.g. the planning phase of the autonomous agent,
/// one-time model overrides, or when the user has toggled tools off).
/// </summary>
internal sealed class EmptyToolRegistry : IToolRegistry
{
    /// <summary>Singleton — no state, safe to share.</summary>
    public static readonly EmptyToolRegistry Instance = new();

    public IReadOnlyList<ToolDefinition> Definitions => [];

    public Task<string> ExecuteAsync(string name, JsonElement args, CancellationToken ct)
        => Task.FromResult("(tools disabled)");

    public DiffInfo? ConsumeDiff() => null;
}
