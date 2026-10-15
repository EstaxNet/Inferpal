namespace Inferpal.Services.CodeActions;

/// <summary>
/// What the user typed after a code-action command (<c>/fix the off-by-one in the loop</c>), given to the model with the
/// action's own instruction.
/// </summary>
/// <remarks>
/// ⚠ Dropped, <c>/fix the off-by-one in the loop</c> ran the generic "find and fix the bug" — another change, or "nothing
/// to change" — and <c>/explain why does this stop?</c> explained the whole file, with nothing said: an answer to a
/// question nobody asked, under the one that was.
/// </remarks>
internal static class CodeActionRequest
{
    /// <summary>The line that carries the request to the model, after an instruction it qualifies; empty without one.
    /// Model-facing, so in English, like the instructions.</summary>
    public static string Line(string? request) =>
        string.IsNullOrWhiteSpace(request)
            ? string.Empty
            : $"\nThe user asks for this, and it comes before the rest: {request.Trim()}";

    /// <summary>The question of an action answered in the chat (<c>/explain</c>, <c>/review</c>): the action's
    /// sentence, then the user's own words.</summary>
    public static string InTheQuestion(string sentence, string? request) =>
        string.IsNullOrWhiteSpace(request) ? sentence : $"{sentence}\n\n{request.Trim()}";
}
