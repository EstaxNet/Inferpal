using System.Text.Json;

namespace Inferpal.Services.Tools;

/// <summary>
/// Contract for a single agentic tool exposed to the Ollama model.
/// </summary>
/// <remarks>
/// Implement this interface to add a new capability to the assistant.
/// Register the implementation in <see cref="Inferpal.Services.Execution.ToolRegistry"/>.
/// <para>
/// Rules:
/// <list type="bullet">
///   <item><see cref="Name"/>, <see cref="Description"/>, and parameter descriptions must be in English
///         (the model reads them to decide when and how to call the tool).</item>
///   <item>User-visible return messages must use <c>Strings.X(…)</c> keys for localization.</item>
///   <item>Always propagate <see cref="OperationCanceledException"/> — never swallow it.</item>
///   <item>File-modifying tools must snapshot via <see cref="FileHistoryService"/> and request
///         approval via <see cref="IApprovalService"/> before writing.</item>
/// </list>
/// </para>
/// </remarks>
internal interface ITool
{
    /// <summary>Snake_case identifier sent to Ollama (e.g. <c>"read_file"</c>).</summary>
    string Name { get; }

    /// <summary>
    /// One-sentence description read by the model to decide when to invoke this tool.
    /// Be precise and concise; the model relies on this to pick the right tool.
    /// </summary>
    string Description { get; }

    /// <summary>
    /// JSON Schema object describing the arguments the model must supply.
    /// Use an anonymous object matching the Ollama tool-calling schema format.
    /// </summary>
    object Parameters { get; }

    /// <summary>
    /// Whether the tool is offered to the model right now. <c>false</c> while every answer it could give is
    /// "unavailable here".
    /// </summary>
    /// <remarks>
    /// ⚠ A tool whose every answer is "unavailable" costs prompt tokens on every request and teaches a small model
    /// to keep trying: it calls it, reads a remedy only the user can apply (<c>/index rebuild</c>, a setting),
    /// then calls it again or answers without having searched. Read on every request by
    /// <see cref="Inferpal.Services.Execution.ToolRegistry.Definitions"/>, so the tool comes back as soon as it can
    /// answer. A call made anyway — a name read earlier in the thread — still runs and says why it cannot help.
    /// </remarks>
    bool IsOffered => true;

    /// <summary>
    /// Executes the tool and returns a human-readable result string.
    /// The result is appended to the conversation and fed back to the model.
    /// </summary>
    /// <param name="args">JSON arguments supplied by the model, matching <see cref="Parameters"/>.</param>
    /// <param name="ct">Cancellation token — propagate to all async operations.</param>
    /// <returns>A string the model will read to continue the agentic loop.</returns>
    Task<string> ExecuteAsync(JsonElement args, CancellationToken ct);
}
