using System.Text.Json;
using Inferpal.Models;

namespace Inferpal.Services.Execution;

/// <summary>
/// Registry of all tools available to the agentic loop.
/// </summary>
/// <remarks>
/// <see cref="OllamaClient"/> uses this to build the tool list sent to Ollama
/// and to dispatch execution when the model calls a tool by name.
/// </remarks>
internal interface IToolRegistry
{
    /// <summary>
    /// Tool definitions in the Ollama API format, sent with every chat request.
    /// </summary>
    IReadOnlyList<ToolDefinition> Definitions { get; }

    /// <summary>
    /// Executes a tool by name and returns its result string to be appended to the conversation.
    /// Returns an error message (not an exception) for unknown tool names.
    /// </summary>
    Task<string> ExecuteAsync(string name, JsonElement args, CancellationToken ct);

    /// <summary>
    /// Returns the diff produced by the last write_file/apply_diff call and clears it.
    /// Returns <c>null</c> if the last tool did not produce a diff.
    /// </summary>
    DiffInfo? ConsumeDiff();

    /// <summary>
    /// How many writes the current run has made to files — counted before each one lands, when its backup is taken;
    /// <c>null</c> when this registry does not write files or no run is active, and nothing is to be read from it.
    /// </summary>
    int? WritesInRun => null;
}
