
namespace Inferpal.Services.CodeActions;

/// <summary>
/// Internal system prompts for the test-generation pipeline (<c>TestGenerationEdit</c>),
/// shared by the editor context-menu command (<c>Add unit tests</c>) and the <c>/test</c> slash
/// command.
///
/// <para>These are internal prompts (never shown to the user), so they stay in English and are
/// not localized — the output is code only.</para>
/// </summary>
internal static class TestGenerationPrompts
{
    // Common tail: the output is written straight into a file, so it must be raw code only.
    private const string CodeOnly =
        "\nReply with ONLY the resulting test file contents — no explanation, no commentary, " +
        "no markdown fences, no ```.";

    // Escape hatch: don't generate worthless tests. When the precondition holds, the model must
    // emit the sentinel alone instead of a file (detected by CodeActionSentinel.IsNoChange).
    private const string NoChangeHead = "\nDO NOT generate tests for the sake of it: if ";
    private const string NoChangeTail =
        ", reply with EXACTLY this token and NOTHING else — no test file, no fences: " + CodeActionSentinel.Token;

    /// <summary>System prompt for generating a brand-new, self-contained test file.</summary>
    public const string NewFileSystem =
        "You are a senior test engineer. Generate a complete, idiomatic unit-test file for the code the user provides.\n" +
        "- Infer the language and the conventional test framework from the source (e.g. xUnit for C#, " +
        "pytest for Python, Jest for TS/JS, the testing package for Go, JUnit for Java).\n" +
        "- Include every import/using, the namespace/package, and the test class/fixture so the file compiles as-is.\n" +
        "- Cover the meaningful cases: happy path, boundary/edge cases, and error or exception paths. " +
        "Use clear, descriptive test names.\n" +
        "- Do NOT modify, restate, or re-emit the source under test." +
        CodeOnly +
        NoChangeHead + "the code is trivial (e.g. plain data holders or auto-properties) with no logic worth testing" + NoChangeTail;

    /// <summary>
    /// System prompt for extending an existing test file. The current test file content is supplied
    /// in the user message; the model must return the COMPLETE merged file.
    /// </summary>
    public const string ExtendFileSystem =
        "You are a senior test engineer. The user provides source code and the EXISTING test file for it.\n" +
        "Return the COMPLETE updated test file: keep every existing test verbatim and ADD new tests for " +
        "cases that are not yet covered.\n" +
        "- Preserve the existing imports, namespace/package and test class; extend them as needed.\n" +
        "- Do NOT remove, rename or rewrite existing tests. Do NOT modify the source under test." +
        CodeOnly +
        NoChangeHead + "the existing tests already cover every meaningful case and there is no useful test left to add" + NoChangeTail;

    /// <summary>Instruction line placed before the source code when creating a new test file.</summary>
    public const string Instruction = "Generate unit tests for this code:";

    /// <summary>
    /// System prompt of <c>/test coverage</c>: one new test file for one target, aimed at its uncovered lines.
    /// </summary>
    /// <remarks>
    /// ⚠ "From its SOURCE file": an example test that imports the package's built bundle taught the model to test the
    /// bundle, and its tests passed while covering nothing of the source that was measured. The example is for style.
    /// </remarks>
    public const string CoverageSystem =
        "You write ONE new unit-test file that covers code no test reaches yet. Reply with ONLY the complete content of " +
        "that new file, in a single code block.\n" +
        "- Test only the target you are given, aiming at the lines marked NOT COVERED.\n" +
        "- Use the project's test framework. The code under test must run from its SOURCE file (its path is given), " +
        "never from a built, bundled or packaged copy of it; the example test file shows the style only.\n" +
        "- Every test must assert something observable. Never skip a test, never mark one as expected to fail.\n" +
        "- Never modify, redefine, mock out or re-implement the code under test; only call it.";

    /// <summary>What <c>/test coverage</c> asks when the new file failed: the failure's end, and the whole file again.</summary>
    public const string CoverageRepair = "Reply with ONLY the corrected complete file, in a single code block.";
}
