using System.Text.Json;
using Inferpal.Localization;
using Inferpal.Models;

namespace Inferpal.Services.Inference;

/// <summary>
/// What a chat request carries, in estimated tokens (~4 characters a token): the tool definitions, the
/// system prompt, the earlier conversation and the last message.
/// </summary>
/// <remarks>
/// ⚠ A request too large for the context window is refused (or, on Ollama, silently loses its head), and
/// the message that says so must name the part that is too large: blaming the agent's tool definitions
/// whatever the request held sends the user to disable tools on a code action that sends none, or on a
/// question whose attached file is the whole problem — a remedy that changes nothing. Each part carries
/// the gesture that shrinks it, largest first.
/// </remarks>
internal readonly record struct RequestSize(int Tools, int SystemPrompt, int Earlier, int Last)
{
    public int Total => Tools + SystemPrompt + Earlier + Last;

    public static RequestSize Of(IReadOnlyList<ChatMessageDto> messages, IReadOnlyList<ToolDefinition>? defs)
    {
        var system = messages.Count > 0 && messages[0].Role == "system" ? 1 : 0;
        var last   = messages.Count > system ? 1 : 0;
        return new(
            ToolChars(defs) / 4,
            AgentOrchestrator.EstimateChars(messages.Take(system)) / 4,
            AgentOrchestrator.EstimateChars(messages.Skip(system).Take(messages.Count - system - last)) / 4,
            AgentOrchestrator.EstimateChars(messages.Skip(messages.Count - last)) / 4);
    }

    /// <summary>One line per part that is not empty, largest first, each naming what shrinks it.</summary>
    public string Breakdown() =>
        string.Join("\n", new (int Tokens, Func<int, string> Line)[]
            {
                (Tools,        Strings.ContextPartTools),
                (SystemPrompt, Strings.ContextPartSystem),
                (Earlier,      Strings.ContextPartEarlier),
                (Last,         Strings.ContextPartLast),
            }
            .Where(p => p.Tokens > 0)
            .OrderByDescending(p => p.Tokens)
            .Select(p => "- " + p.Line(p.Tokens)));

    private static int ToolChars(IReadOnlyList<ToolDefinition>? defs)
    {
        if (defs is not { Count: > 0 }) return 0;
        try   { return JsonSerializer.Serialize(defs).Length; }
        catch (Exception ex)
        {
            Diagnostics.Swallow("RequestSize.ToolChars", ex);
            return defs.Count * 400;   // rough per-tool budget when a schema does not serialize
        }
    }
}
