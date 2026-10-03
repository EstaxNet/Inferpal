using System.Text;
using System.Text.Json;
using Inferpal.Models;

namespace Inferpal.Services.Inference;

/// <summary>
/// Tool calling for a model whose server cannot render the tools it is sent: the definitions travel in the system
/// prompt as text, the model answers with <c>&lt;tool_call&gt;{json}&lt;/tool_call&gt;</c> blocks (read back by
/// <c>InlineToolCallParser</c>), and the calls and results already in the conversation are written as text too.
/// </summary>
/// <remarks>
/// ⚠ Gemma 4's bundled chat template, on LM Studio, calls a macro it never defines (<c>format_type_argument</c>) as soon
/// as a request carries <c>tools</c>: every request with tools is refused with "Error rendering prompt with jinja
/// template", while the same request without them works. The model itself calls tools well; only the template cannot
/// write them. Nothing a client sends can repair the template, so the tools go where the template does not look.
/// The history is rewritten whole: a <c>tool</c> message or an assistant <c>tool_calls</c> field would send the
/// template back down the same path.
/// </remarks>
internal static class PromptedTools
{
    /// <summary>
    /// What a model writes once its call is written: the opening of the tool's response — ours, or Gemma's own token.
    /// </summary>
    /// <remarks>
    /// ⚠ A model that calls tools ends its turn by opening the response it waits for (Gemma 4's own format ends a call
    /// with <c>&lt;tool_call|&gt;&lt;|tool_response&gt;</c>, and the engine stops there). With the tools in the prompt
    /// nothing stops it: it writes the result itself — an invented file, an invented search — and goes on calling and
    /// answering until the request's time runs out. The request asks the server to stop there, and the client stops
    /// reading there for a server that does not.
    /// </remarks>
    internal static readonly string[] ResponseMarkers = ["<tool_response>", "<|tool_response>"];

    /// <summary>
    /// Where the model began a tool response in <paramref name="content"/>, looking only at what the last
    /// <paramref name="appended"/> characters could have completed; -1 when it has not.
    /// </summary>
    internal static int ResponseStart(StringBuilder content, int appended)
    {
        var longest = ResponseMarkers.Max(m => m.Length);
        var from    = Math.Max(0, content.Length - appended - longest + 1);
        var tail    = content.ToString(from, content.Length - from);
        var found   = ResponseMarkers.Select(m => tail.IndexOf(m, StringComparison.Ordinal)).Where(i => i >= 0).DefaultIfEmpty(-1).Min();
        return found < 0 ? -1 : from + found;
    }

    /// <summary>Whether a server refusal says the model's chat template could not be rendered.</summary>
    internal static bool IsTemplateRefusal(string serverMessage) =>
        serverMessage.Contains("Error rendering prompt with jinja template", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether a server refused the request because of its tools: a template that fails on them (LM Studio), or a model
    /// whose template declares none — Ollama's "&lt;model&gt; does not support tools", decided before the model loads,
    /// through its native API and its <c>/v1</c> surface alike.
    /// </summary>
    /// <remarks>⚠ Ollama words its other capability refusals the same way ("does not support thinking", "… chat"):
    /// only the tools one is answered by moving the tools into the prompt.</remarks>
    internal static bool IsToolRefusal(string serverMessage) =>
        IsTemplateRefusal(serverMessage)
        || serverMessage.Contains("does not support tools", StringComparison.OrdinalIgnoreCase);

    /// <summary>"server|model" pairs whose server refused tools: their tools go in the prompt, in both clients.</summary>
    internal static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> Models = new();

    private static readonly JsonSerializerOptions Compact = new() { WriteIndented = false };

    /// <summary>The system-prompt block that declares <paramref name="tools"/> and how to call them.</summary>
    internal static string Instructions(IReadOnlyList<ToolDefinition> tools)
    {
        var sb = new StringBuilder();
        sb.Append("## Tools\n\n")
          .Append("You can call the tools listed below. To call one, write a block exactly like this one:\n")
          .Append("<tool_call>\n{\"name\": \"tool_name\", \"arguments\": {\"parameter\": \"value\"}}\n</tool_call>\n")
          .Append("The arguments are a JSON object that follows the tool's parameters. Write the block and stop: the result ")
          .Append("comes back in a <tool_response> block. Several blocks call several tools. When no tool is needed, ")
          .Append("answer normally, without any block.\n\nAvailable tools:\n");
        foreach (var tool in tools)
            sb.Append(JsonSerializer.Serialize(new
            {
                name        = tool.Function.Name,
                description = tool.Function.Description,
                parameters  = tool.Function.Parameters,
            }, Compact)).Append('\n');
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// The conversation with the tools in the system prompt and every call and result written as text: no
    /// <c>tool</c> message and no <c>tool_calls</c> field is left, and two messages of the same role in a row become one.
    /// </summary>
    internal static List<OpenAiRequestMessage> Rewrite(List<OpenAiRequestMessage> mapped, IReadOnlyList<ToolDefinition> tools)
    {
        var block = Instructions(tools);
        var text  = new List<(string Role, string Content)>(mapped.Count + 1);

        if (mapped.Count == 0 || mapped[0].Role != "system")
            text.Add(("system", block));

        foreach (var m in mapped)
        {
            if (m.Role == "system" && text.Count == 0)
                text.Add(("system", string.IsNullOrWhiteSpace(m.Content) ? block : m.Content + "\n\n" + block));
            else if (m.Role == "assistant" && m.ToolCalls is { Count: > 0 } calls)
            {
                var sb = new StringBuilder(m.Content ?? string.Empty);
                foreach (var call in calls)
                {
                    if (sb.Length > 0) sb.Append('\n');
                    sb.Append("<tool_call>\n{\"name\": ").Append(JsonSerializer.Serialize(call.Function.Name))
                      .Append(", \"arguments\": ").Append(ArgumentsObject(call.Function.Arguments)).Append("}\n</tool_call>");
                }
                text.Add(("assistant", sb.ToString()));
            }
            else if (m.Role == "tool")
                text.Add(("user", "<tool_response>\n" + (m.Content ?? string.Empty) + "\n</tool_response>"));
            else
                text.Add((m.Role, m.Content ?? string.Empty));
        }

        var merged = new List<OpenAiRequestMessage>(text.Count);
        foreach (var (role, content) in text)
        {
            if (merged.Count > 0 && merged[^1].Role == role)
                merged[^1] = merged[^1] with { Content = merged[^1].Content + "\n\n" + content };
            else
                merged.Add(new OpenAiRequestMessage(role, content));
        }
        return merged;
    }

    /// <summary>The same rewrite in Ollama's native message shape: only roles and text are left.</summary>
    internal static List<ChatMessageDto> Rewrite(List<ChatMessageDto> messages, IReadOnlyList<ToolDefinition> tools) =>
        Rewrite(OpenAiCompatibleClient.MapMessages(messages), tools)
            .Select(m => new ChatMessageDto(m.Role, m.Content))
            .ToList();

    /// <summary>The call's arguments as a JSON object; the raw text quoted when it is not one.</summary>
    private static string ArgumentsObject(string arguments)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(arguments) ? "{}" : arguments);
            if (doc.RootElement.ValueKind == JsonValueKind.Object) return doc.RootElement.GetRawText();
        }
        catch (JsonException) { }
        return JsonSerializer.Serialize(arguments);
    }
}
