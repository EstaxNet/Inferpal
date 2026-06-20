using Inferpal.Services;

namespace Inferpal.Commands;

/// <summary>
/// System prompts + short instructions for the in-place code actions (Refactor / Fix /
/// Add-docs), shared by the context-menu commands (<see cref="InPlaceCodeActionBase"/>)
/// and the equivalent chat slash commands (<c>/refactor</c>, <c>/fix</c>, <c>/doc</c>).
///
/// <para>These are internal prompts (never shown to the user), so they stay in English —
/// the output is code only, language-agnostic — and are not localized.</para>
/// </summary>
internal static class InPlaceCodeActionPrompts
{
    // Common tail: every action must reply with code only and preserve indentation, so the
    // result can be dropped straight back into the document.
    private const string CodeOnly =
        "Reply with ONLY the resulting code — no explanation, no markdown fences, no ```.\n" +
        "CRITICAL: reproduce EXACTLY the same leading whitespace / indentation on EVERY line as the " +
        "original, including opening and closing braces. Do not add or remove blank lines at the start or end.";

    // Escape hatch: don't churn already-good code. When the precondition holds, the model must
    // skip the rewrite and emit the sentinel alone (detected by CodeActionSentinel.IsNoChange).
    private const string NoChangeHead = "\n\nDO NOT change well-written code: if ";
    private const string NoChangeTail =
        ", reply with EXACTLY this token and NOTHING else — no code, no fences: " + CodeActionSentinel.Token;

    public const string RefactorInstruction = "Refactor this code:";
    public const string RefactorSystem =
        "You are an expert software engineer performing an in-place refactor. " +
        "Rewrite the given code to be clearer, simpler and more maintainable WITHOUT changing " +
        "its observable behavior or public API. Keep the same programming language. " + CodeOnly +
        NoChangeHead + "the code is already clear, simple and maintainable and a refactor would not genuinely improve it" + NoChangeTail;

    public const string FixInstruction = "Find and fix the bug or issue in this code:";
    public const string FixSystem =
        "You are an expert software engineer fixing code in place. The given code contains a bug, " +
        "issue or code smell. Apply the minimal change that corrects it, without altering unrelated code. " +
        "Keep the same programming language. " + CodeOnly +
        NoChangeHead + "the code is already correct and you cannot find a real bug, issue or code smell to fix" + NoChangeTail;

    public const string DocstringInstruction = "Add documentation comments to this code:";
    public const string DocstringSystem =
        "You are an expert software engineer adding documentation comments in place. Add idiomatic " +
        "documentation comments (XML /// doc for C#, docstrings for Python, JSDoc for JS/TS, etc.) to all " +
        "public types, methods, properties and parameters in the given code. Do NOT change any executable code. " +
        "Keep the same programming language. " + CodeOnly +
        NoChangeHead + "the code is already adequately documented and adding more comments would be redundant" + NoChangeTail;
}
