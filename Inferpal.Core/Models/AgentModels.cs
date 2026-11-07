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

    /// <summary>Whether this is the orchestrator's stand-in for a plan it could not read, not the model's own.</summary>
    [JsonIgnore]
    public bool IsFallback { get; init; }

    /// <summary>How many objects <see cref="TryParse"/> tries before it reads no plan.</summary>
    private const int MaxObjectsTried = 256;

    private static readonly JsonSerializerOptions JsonIn = new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// The first complete JSON object in <paramref name="text"/> that is a plan with at least one step — the last one
    /// with <paramref name="last"/>; <c>null</c> when there is none.
    /// </summary>
    /// <remarks>
    /// ⚠ Never the text from the first <c>{</c> to the last <c>}</c>: a model writes its plan, a sentence, then the first
    /// step's call as an object of its own, and that span is no JSON at all — the run then holds a plan that is not the
    /// model's. One JSON value is read where an object opens (<c>{</c>, blanks, then <c>"</c>), braces inside strings
    /// included. Which plan counts, the first or the last, is the caller's to say (<c>AgentOrchestrator.PlanOf</c>). The
    /// last is the plan that ENDS last: one that holds others (a step that carries steps of its own) ends after them, a
    /// draft before it.
    /// shortcut: past <see cref="MaxObjectsTried"/> objects tried, no plan is read; plan replies are short.
    /// </remarks>
    public static AgentPlan? TryParse(string text, bool last = false)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var bytes = System.Text.Encoding.UTF8.GetBytes(text);   // a '{' byte is never part of a multi-byte character
        (AgentPlan Plan, int End)? latest = null;
        try
        {
            var tried = 0;
            for (var k = 0; k < bytes.Length && tried < MaxObjectsTried; k++)
            {
                var i = last ? bytes.Length - 1 - k : k;
                if (!OpensAnObject(bytes, i)) continue;
                tried++;
                if (ReadAt(bytes, i) is not { } read) continue;
                // Forward, the first plan is the outermost one there: nothing inside it is tried before it.
                if (!last) return read.Plan;
                if (latest is not { } kept || i + read.Length >= kept.End) latest = (read.Plan, i + read.Length);
            }
        }
        catch (Exception ex)
        {
            // The run reads its plan before its loop, outside any guard: an unexpected failure is no plan, never a throw.
            Diagnostics.Swallow("AgentPlan.TryParse", ex);
        }
        return latest?.Plan;
    }

    /// <summary>The plan <paramref name="text"/> is, in its whole, or <c>null</c>: what an echo of the plan is.</summary>
    internal static AgentPlan? TryParseWhole(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        try { return Valid(JsonSerializer.Deserialize<AgentPlan>(text, JsonIn)); }
        catch (JsonException) { return null; }   // malformed: no plan
        catch (Exception ex)
        {
            Diagnostics.Swallow("AgentPlan.TryParseWhole", ex);
            return null;
        }
    }

    /// <summary>Whether an object opens at <paramref name="i"/>: a brace, blanks, then a property name.</summary>
    private static bool OpensAnObject(byte[] bytes, int i)
    {
        if (bytes[i] != (byte)'{') return false;
        var j = i + 1;
        while (j < bytes.Length && bytes[j] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n') j++;
        return j < bytes.Length && bytes[j] == (byte)'"';
    }

    /// <summary>The plan read from the one JSON value at <paramref name="start"/> and its length in bytes, or
    /// <c>null</c> when no plan reads from there.</summary>
    private static (AgentPlan Plan, int Length)? ReadAt(byte[] bytes, int start)
    {
        try
        {
            var reader = new Utf8JsonReader(bytes.AsSpan(start));
            if (!reader.Read()) return null;
            var plan = Valid(JsonSerializer.Deserialize<AgentPlan>(ref reader, JsonIn));
            return plan is null ? null : (plan, (int)reader.BytesConsumed);
        }
        catch (JsonException) { return null; }   // no complete object from here, or one no plan reads from
    }

    /// <summary><paramref name="plan"/> when it has at least one step, its nulls replaced; <c>null</c> otherwise.</summary>
    private static AgentPlan? Valid(AgentPlan? plan)
    {
        if (plan is null) return null;
        // Valid JSON can still carry the nulls a model wrote: a null step passed the "at least one step"
        // check and the first iteration dereferenced it — an exception the run does not catch, where an
        // unreadable plan falls back.
        plan.Steps ??= [];
        plan.Steps.RemoveAll(step => step is null);
        if (plan.Steps.Count == 0) return null;
        plan.Goal ??= string.Empty;
        foreach (var step in plan.Steps) step.Description ??= string.Empty;
        return plan;
    }

    // Non-ASCII text stays readable: the JSON is read by a model, never embedded in HTML.
    private static readonly JsonSerializerOptions JsonOut = new()
    {
        Encoder                = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>The plan in the JSON shape the model is asked to write — what <see cref="TryParse"/> reads back.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, JsonOut);

    /// <summary>What opens the goal line of <see cref="ToMarkdown"/>.</summary>
    internal const string GoalPrefix = "**🗂 Plan:**";

    /// <summary>The icon each step state is written with — the table <see cref="Services.Presentation.PlanCard"/> reads back.</summary>
    internal static readonly IReadOnlyList<(string Icon, AgentStepStatus Status)> StepIcons =
    [
        ("🔄", AgentStepStatus.Active),
        ("✅", AgentStepStatus.Done),
        ("❌", AgentStepStatus.Failed),
        ("⏭", AgentStepStatus.Skipped),
        ("⏳", AgentStepStatus.Pending),
    ];

    /// <summary>
    /// The plan as Markdown: what a session saves, what an export writes, and what the plan card is drawn from
    /// (<see cref="Services.Presentation.PlanCard.Read"/>).
    /// </summary>
    public string ToMarkdown()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"{GoalPrefix} {Goal}");
        sb.AppendLine();
        foreach (var step in Steps)
        {
            var icon = StepIcons.FirstOrDefault(i => i.Status == step.Status).Icon ?? "⏳";
            sb.AppendLine($"{icon} {step.Index}. {step.Description}");
        }
        return sb.ToString().TrimEnd();
    }
}

// ── Orchestrator result ───────────────────────────────────────────────────────

/// <summary>Full result returned by <see cref="Inferpal.Services.Agent.AgentOrchestrator"/>.</summary>
/// <param name="Failed">The run FAILED and <see cref="FinalResponse"/> is the message that says so — see
/// <c>AgentResult.Failed</c>: shown, never kept as the answer the model gave.</param>
/// <param name="AnswerRepeating">The cut answer was stopped because the model was repeating itself
/// (<see cref="ChatTurnResult.StoppedRepeating"/>).</param>
/// <param name="WaitRanOut">Failed because the wait for a first word ran out, the server silent
/// (<see cref="AgentHttpException.PrefillOverran"/>): the screens offer the time limits.</param>
internal record OrchestratorResult(
    string               FinalResponse,
    AgentPlan?           Plan,
    List<ToolExecution>  Executions,
    List<ChatMessageDto> UpdatedHistory,
    int                  TokensUsed,
    int                  PromptTokens,
    bool                 WasLoopDetected,
    bool                 ReachedIterationLimit,
    bool                 AnswerCut = false,
    bool                 Failed    = false,
    bool                 AnswerRepeating = false,
    bool                 WaitRanOut = false)
{
    /// <summary>Creates an error result (no plan, no executions).</summary>
    internal static OrchestratorResult Error(string message, List<ChatMessageDto> history, bool waitRanOut = false) =>
        new(message, null, [], history, 0, 0, false, false, Failed: true, WaitRanOut: waitRanOut);
}

// ── Low-level HTTP primitives ─────────────────────────────────────────────────

/// <summary>
/// Result of one <c>/api/chat</c> HTTP call (streaming only — tool execution is not included).
/// </summary>
/// <param name="TextContent">Accumulated text tokens from the model's response.</param>
/// <param name="ToolCalls">Tool calls requested by the model, or <c>null</c> / empty if none.</param>
/// <param name="StoppedRepeating">The client stopped the response because the model was repeating itself.
/// <see cref="CutAtLimit"/> is then true too — the text is incomplete, and every reader that refuses a cut answer must
/// refuse this one — and this says why, so the notice names the remedy that works instead of "increase the context
/// length".</param>
/// <param name="AnswerIsReasoning">The model wrote no answer, only reasoning, and <see cref="TextContent"/> IS that
/// reasoning — promoted so a chat bubble keeps what the user saw streaming by. ⚠ A reader that turns the reply into an
/// ARTIFACT (a summary that replaces turns, a review's verdict) must not take it: it is a draft ("We need to
/// summarize…"), often stopped at the length limit because the reasoning used the whole context.</param>
internal record ChatTurnResult(
    string             TextContent,
    List<ToolCallDto>? ToolCalls,
    int                TokensUsed,
    int                PromptTokens,
    bool               CutAtLimit = false,
    bool               StoppedRepeating = false,
    bool               AnswerIsReasoning = false);

/// <summary>Thrown by <c>OllamaClient.SendChatAsync</c> on HTTP / network failure.</summary>
internal sealed class AgentHttpException(string message, bool isTimeout, bool prefillOverran = false)
    : Exception(message)
{
    internal bool IsTimeout { get; } = isTimeout;

    /// <summary>The wait ran out before the first token: the server was still reading the prompt, as far as anyone can
    /// tell — not a silence in the middle of an answer, and not a reason to call the server unreachable.</summary>
    internal bool PrefillOverran { get; } = prefillOverran;
}
