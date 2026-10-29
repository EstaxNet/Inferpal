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

    /// <summary>
    /// The names a call may carry: the declared ones, and the ones a request does not declare but the model can read and
    /// call (MCP tools waiting on demand). The only names a call written as text is recognized by.
    /// </summary>
    /// <remarks>⚠ A decorator that only passes calls through forwards it: answered from its own <see cref="Definitions"/>,
    /// a text call to a tool on demand would be shown as the answer instead of running.</remarks>
    IEnumerable<string> CallableNames => Definitions.Select(d => d.Function.Name);

    /// <summary>
    /// What a call by <paramref name="name"/> answers instead of running when the tool is turned off (by the user, or by
    /// its MCP server's entry); <c>null</c> when nothing turns it off.
    /// </summary>
    /// <remarks>⚠ Asked by the funnel of the MODEL's calls alone (<c>AgentOrchestrator.ExecuteToolSafeAsync</c>): a gesture
    /// of the user that runs a tool itself (<c>/diff</c>, <c>@problems</c>, <c>/tdd</c>'s test run) is not a call of the
    /// model. A decorator forwards it — answered by default, a tool turned off would run in plan mode.</remarks>
    string? TurnedOffRefusal(string name) => null;

    /// <summary>
    /// Whether <paramref name="name"/> is a tool this registry holds — offered right now or not, turned off or not;
    /// <c>false</c> only for a name the model invented.
    /// </summary>
    /// <remarks>⚠ A decorator that narrows the tools forwards it, and answers an invented name with ITS tools
    /// (<see cref="ToolRegistry.UnknownTool(string, IEnumerable{ToolDefinition})"/>): answered by the registry it wraps, the
    /// list would name the tools the mode refuses, and the model's next call would go to one of them. By default every name
    /// is known, and the registry's own answer stands.</remarks>
    bool Knows(string name) => true;
}
