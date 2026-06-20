using System.Text.Json;
using System.Text.Json.Serialization;
using Inferpal.Services;

namespace Inferpal.Models;

// ── Agent Plan — produced before any tools are called ────────────────────────

/// <summary>
/// Controls the per-HTTP-request deadline inside <c>OllamaClient.SendChatAsync</c>
/// and <c>RunAgentAsync</c>.  Values are in seconds.
/// </summary>
internal enum TaskComplexity
{
    /// <summary>Short read-only tasks: InlineEdit, code actions (explain/fix/doc/refactor/test).</summary>
    Quick  = 120,
    /// <summary>Default: agent turns, user chat with tools, planning responses.</summary>
    Normal = 300,
    /// <summary>Reserved for future deep-reasoning tasks.</summary>
    Deep   = 600,
}

/// <summary>Status of a single step in an autonomous agent plan.</summary>
public enum AgentStepStatus
{
    /// <summary>Not yet started.</summary>
    Pending,
    /// <summary>Currently executing.</summary>
    Active,
    /// <summary>Completed successfully.</summary>
    Done,
    /// <summary>Encountered an error but execution continued.</summary>
    Failed,
    /// <summary>Determined to be unnecessary and skipped.</summary>
    Skipped,
}

/// <summary>A single step in an <see cref="AgentPlan"/>.</summary>
public class AgentPlanStep
{
    [JsonPropertyName("i")]
    public int Index { get; set; }

    [JsonPropertyName("desc")]
    public string Description { get; set; } = string.Empty;

    /// <summary>Expected tool name for this step (optional hint from the model).</summary>
    [JsonPropertyName("tool")]
    public string? ToolHint { get; set; }

    /// <summary>Execution status — updated live as the orchestrator runs.</summary>
    [JsonIgnore]
    public AgentStepStatus Status { get; set; } = AgentStepStatus.Pending;

    /// <summary>Brief observation text added after the step's tools execute.</summary>
    [JsonIgnore]
    public string? Observation { get; set; }
}

/// <summary>
/// Structured plan produced by the model at the start of an autonomous agent run.
/// JSON shape: <c>{"goal":"…","steps":[{"i":1,"desc":"…","tool":"read_file"},…]}</c>
/// </summary>
public class AgentPlan
{
    [JsonPropertyName("goal")]
    public string Goal { get; set; } = string.Empty;

    [JsonPropertyName("steps")]
    public List<AgentPlanStep> Steps { get; set; } = [];

    /// <summary>
    /// Parses the first JSON object found in <paramref name="text"/>.
    /// Returns <c>null</c> when no valid plan with at least one step can be extracted.
    /// </summary>
    public static AgentPlan? TryParse(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        int start = text.IndexOf('{');
        int end   = text.LastIndexOf('}');
        if (start < 0 || end <= start) return null;

        var json = text[start..(end + 1)];
        try
        {
            var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var plan = JsonSerializer.Deserialize<AgentPlan>(json, opts);
            if (plan?.Steps is { Count: > 0 }) return plan;
        }
        catch { /* malformed JSON — fall through */ }
        return null;
    }

    /// <summary>Formats the plan as a Markdown block for display in the chat UI.</summary>
    public string ToMarkdown()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"**🗂 Plan:** {Goal}");
        sb.AppendLine();
        foreach (var step in Steps)
        {
            var icon = step.Status switch
            {
                AgentStepStatus.Active  => "🔄",
                AgentStepStatus.Done    => "✅",
                AgentStepStatus.Failed  => "❌",
                AgentStepStatus.Skipped => "⏭",
                _                      => "⏳",
            };
            sb.AppendLine($"{icon} {step.Index}. {step.Description}");
        }
        return sb.ToString().TrimEnd();
    }
}

// ── Orchestrator result ───────────────────────────────────────────────────────

/// <summary>Full result returned by <see cref="Inferpal.Services.AgentOrchestrator"/>.</summary>
internal record OrchestratorResult(
    string               FinalResponse,
    AgentPlan?           Plan,
    List<ToolExecution>  Executions,
    List<ChatMessageDto> UpdatedHistory,
    int                  TokensUsed,
    int                  PromptTokens,
    bool                 WasLoopDetected,
    bool                 ReachedIterationLimit)
{
    /// <summary>Creates an error result (no plan, no executions).</summary>
    internal static OrchestratorResult Error(string message, List<ChatMessageDto> history) =>
        new(message, null, [], history, 0, 0, false, false);
}

// ── Low-level HTTP primitives ─────────────────────────────────────────────────

/// <summary>
/// Result of one <c>/api/chat</c> HTTP call (streaming only — tool execution is not included).
/// </summary>
internal record ChatTurnResult(
    /// <summary>Accumulated text tokens from the model's response.</summary>
    string             TextContent,
    /// <summary>Tool calls requested by the model, or <c>null</c> / empty if none.</summary>
    List<ToolCallDto>? ToolCalls,
    int                TokensUsed,
    int                PromptTokens);

/// <summary>Thrown by <c>OllamaClient.SendChatAsync</c> on HTTP / network failure.</summary>
internal sealed class AgentHttpException(string message, bool isTimeout)
    : Exception(message)
{
    internal bool IsTimeout { get; } = isTimeout;
}
