using System.Text.Json;

namespace Inferpal.Services.Tools;

/// <summary>
/// Asks the person one question, with the answers the model proposes, on a card in the chat — instead of guessing between
/// several meanings of a request the workspace cannot settle.
/// </summary>
/// <remarks>
/// ⚠ Offered only where a card can be drawn (<see cref="IApprovalService.CanAskForInput"/>: never a background
/// <c>/task</c>, never an Agent Client Protocol client), since a question nobody can see would hold the run. ⚠ The
/// description says when NOT to ask: a model that asks at every step turns the agent into a form (docs/probes/pari100).
/// </remarks>
internal sealed class AskQuestionTool(IApprovalService approval) : ITool
{
    /// <summary>The most answers a question proposes.</summary>
    internal const int MaxOptions = 4;

    public string Name => "ask_question";

    /// <inheritdoc/>
    public ToolEffect EffectOf(JsonElement args) => ToolEffect.None;

    public bool IsOffered => approval.CanAskForInput;

    public string Description =>
        "Asks the user ONE question and waits for the answer. Ask FIRST, before reading anything, when the request names " +
        "something the workspace has several of ('the form' when there are three forms, 'the database' when there are two) " +
        "or leaves a choice only the user can make (which format, which behaviour): guessing then edits the wrong thing. " +
        "Do not ask when the request names its target, when reading one file would answer, to confirm a step, or for " +
        "permission (writing already asks). Offer 2 to 4 short answers.";

    public object Parameters => new
    {
        type = "object",
        properties = new
        {
            question = new { type = "string", description = "The question, in the user's language." },
            options  = new { type = "array", items = new { type = "string" }, description = "2 to 4 short answers to propose." },
        },
        required = new[] { "question" },
    };

    public async Task<string> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var question = args.Str("question")?.Trim();
        if (string.IsNullOrEmpty(question)) return "Error: question is empty: write the question to ask.";
        var options = new List<string>();
        if (args.Array("options", out var list))
            foreach (var o in list.EnumerateArray())
                if (o.AsText()?.Trim() is { Length: > 0 } text && !options.Contains(text))
                    options.Add(text);
        var dropped = options.Count > MaxOptions ? options.Count - MaxOptions : 0;
        if (dropped > 0) options = options.Take(MaxOptions).ToList();

        var answer = await approval.AskQuestionAsync(question, options, ct);
        var note = dropped > 0 ? $"(Only the first {MaxOptions} of your answers were shown.) " : string.Empty;
        return answer.Outcome switch
        {
            QuestionOutcome.Answered => note + $"The user answered: {answer.Text}",
            QuestionOutcome.LetDecide => note + "The user let you decide. Go on, and say in your answer what you assumed.",
            QuestionOutcome.Note => note + "The user did not pick an answer: they wrote you a note instead. Read it before going on.",
            _ => note + "The question could not be shown here. Decide yourself, and say in your answer what you assumed.",
        };
    }
}
