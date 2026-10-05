using System.IO;
using Inferpal.Localization;
using Inferpal.Models;
using Inferpal.Services.Execution;
using Inferpal.Services.Inference;

namespace Inferpal.Services.CodeActions;

/// <summary>
/// What <c>/test</c> should write, and where — everything except the writing itself.
/// </summary>
/// <param name="Ok">False when there is nothing usable to apply (see the other flags).</param>
/// <param name="TestPath">Absolute path of the test file, existing or to be created.</param>
/// <param name="TestFileName">File name only, for the message shown to the user.</param>
/// <param name="Extended">True when an existing test file is being extended rather than created.</param>
/// <param name="NoChange">
/// The model judged there was nothing worth testing, or nothing left to add. Distinct from a
/// failure: nothing must be written, and the user must be told why the chat stayed quiet.
/// </param>
/// <param name="Content">Full content to write; empty unless <paramref name="Ok"/>.</param>
/// <param name="Unreadable">
/// The test file exists and could not be read. ⚠ The one outcome where carrying on <b>destroys</b>
/// something: with nothing read, the plan is indistinguishable from "there is no test file", and
/// the branch that applies it writes a brand-new file straight over the old one — on the VS side
/// with no snapshot and no undoable edit. Refused, and the cause named.
/// </param>
/// <param name="Refusal">
/// The model's reply is not a test file, and the sentence that says why — the three screens of <c>/test</c> print it
/// as is, so a new reason is one factory here, not three branches.
/// </param>
internal sealed record TestGenerationPlan(
    bool Ok, string TestPath, string TestFileName, bool Extended, bool NoChange, string Content,
    bool Unreadable = false, string? Refusal = null)
{
    public static TestGenerationPlan Failed(string testPath = "", bool extended = false) =>
        new(false, testPath, Path.GetFileName(testPath), extended, false, string.Empty);

    /// <summary>The existing test file could not be read: nothing is written over it.</summary>
    public static TestGenerationPlan CannotRead(string testPath) =>
        new(false, testPath, Path.GetFileName(testPath), Extended: true, NoChange: false,
            string.Empty, Unreadable: true);

    /// <summary>
    /// The answer stopped at the model's length limit: it is the file's first part, and extending
    /// REWRITES the whole test file — applied, it would delete every test past the cut. Nothing is written.
    /// </summary>
    public static TestGenerationPlan CutAtLimit(string testPath, bool extended) =>
        new(false, testPath, Path.GetFileName(testPath), extended, NoChange: false, string.Empty,
            Refusal: Strings.CodeActionReplyCut);

    /// <summary>
    /// The model wrote only reasoning, promoted by the client as its reply: thinking prose, not a test file — written,
    /// it would land in the project as source.
    /// </summary>
    public static TestGenerationPlan OnlyReasoning(string testPath, bool extended, string model) =>
        new(false, testPath, Path.GetFileName(testPath), extended, NoChange: false, string.Empty,
            Refusal: Strings.MsgOnlyReasoningFrom(model));
}

/// <summary>
/// The editor-agnostic half of the test-generation code action: pick the conventional test path,
/// ask the model, and decide what should end up on disk.
/// </summary>
/// <remarks>
/// <para>
/// Extracted from the VS-only pipeline so the headless host can serve <c>/test</c> too. Before
/// that, <c>command/slash</c> returned <c>Handled = false</c> for it and the VS Code adapter did
/// not intercept it either, so the literal string "/test" reached the model, which improvised an
/// answer about a command it knows nothing about — the exact failure the router's own header
/// forbids.
/// </para>
/// <para>
/// Applying the plan is left to the caller because that is where the editors genuinely differ: VS
/// replaces the document through an undoable editor edit, the host writes the file and asks the
/// adapter to open it.
/// </para>
/// </remarks>
internal static class TestGenerationPlanner
{
    /// <summary>
    /// The sentence naming the framework the test project uses, for a NEW test file — an existing one shows its own.
    /// Empty when nothing says; the instruction then asks the model to infer it, as before.
    /// </summary>
    internal static string FrameworkLine(string testPath) =>
        TestFilePathResolver.FrameworkFor(testPath) is { } framework
            ? $"\nThe test project uses {framework}: write the tests for {framework}."
        // A Rust integration test is a crate of its own: written like a unit test (`use super::*`), it does not compile.
        : TestFilePathResolver.CargoLibraryName(testPath) is { } crate
            ? $"\nThis file is a Rust integration test in the crate's tests/ folder: it is compiled as its own crate, so "
            + $"reach the code with `use {crate}::…` — only the library's public items are visible."
            : string.Empty;

    /// <param name="sourcePath">Path of the file under test — decides where the tests go.</param>
    /// <param name="sourceCode">Selection if the editor has one, otherwise the whole file.</param>
    /// <param name="openText">The test file's text in the editor when it is open there, else null — given by an editor
    /// that writes the result INTO that buffer (Visual Studio). ⚠ Planned from the disk, the result replaces a buffer
    /// that holds unsaved tests the model never saw, and they disappear.</param>
    /// <summary>
    /// The refusal of <c>/test</c> when the test file it would rewrite ON DISK has unsaved changes in the editor; null
    /// otherwise.
    /// </summary>
    /// <remarks>
    /// ⚠ The VS Code host writes the file on disk: planned from the disk, the unsaved tests are not in the result, and
    /// written behind the dirty buffer, the next save of that buffer undoes the new tests — or the old ones are lost.
    /// </remarks>
    public static string? UnsavedTestFile(Editor.OpenDocumentOverlay? overlay, string sourcePath)
    {
        var testPath = TestFilePathResolver.Resolve(sourcePath);
        return overlay is not null && overlay.TryGetUnsaved(testPath, out _)
            ? Strings.TestsFileUnsaved(Path.GetFileName(testPath))
            : null;
    }

    public static async Task<TestGenerationPlan> PlanAsync(
        IInferenceProvider client, string model, string sourcePath, string sourceCode,
        CancellationToken ct, Func<string, CancellationToken, Task<string?>>? openText = null)
    {
        if (string.IsNullOrWhiteSpace(sourceCode)) return TestGenerationPlan.Failed();

        var sourceName = Path.GetFileName(sourcePath);
        var testPath   = TestFilePathResolver.Resolve(sourcePath);
        var testName   = Path.GetFileName(testPath);

        // Read any existing test file so the model extends it instead of clobbering it.
        // ⚠ And a read that FAILS is not "there is no test file": swallowing it left `existing`
        // null, which is the very state that means "create a new one" — i.e. the clobbering this
        // read exists to prevent, on the branch that has no undo.
        string? existing = null;
        if (openText is not null)
        {
            try { existing = await openText(testPath, ct); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { Diagnostics.Swallow($"TestGenerationPlanner.OpenText({testName})", ex); }
        }
        if (existing is null && File.Exists(testPath))
        {
            try { existing = await Tools.TextFileEncoding.ReadTextAsync(testPath, ct); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Diagnostics.Swallow($"TestGenerationPlanner.Read({testName})", ex);
                return TestGenerationPlan.CannotRead(testPath);
            }
        }
        var extend = !string.IsNullOrWhiteSpace(existing);

        var system = extend ? TestGenerationPrompts.ExtendFileSystem : TestGenerationPrompts.NewFileSystem;
        var user   = extend
            ? $"Existing test file ({testName}):\n\n{existing}\n\nSource under test ({sourceName}):\n\n{sourceCode}"
            : $"{TestGenerationPrompts.Instruction}{FrameworkLine(testPath)}\n\nSource file: {sourceName}\n\n{sourceCode}";

        ChatTurnResult result;
        try
        {
            result = await client.SendChatAsync(
                model,
                [new("system", system), new("user", user)],
                EmptyToolRegistry.Instance, onToken: null, ct, TaskComplexity.Quick);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Diagnostics.Swallow("TestGenerationPlanner.SendChat", ex);
            return TestGenerationPlan.Failed(testPath, extend);
        }

        if (result.CutAtLimit)
            return TestGenerationPlan.CutAtLimit(testPath, extend);
        if (result.AnswerIsReasoning)
            return TestGenerationPlan.OnlyReasoning(testPath, extend, model);

        var content = InlineEditResponse.Clean(result.TextContent);

        if (CodeActionSentinel.IsNoChange(content))
            return new TestGenerationPlan(false, testPath, testName, extend, NoChange: true, string.Empty);

        if (string.IsNullOrWhiteSpace(content))
            return TestGenerationPlan.Failed(testPath, extend);

        // ⚠ An extended file keeps its OWN line endings and final line break, like every other rewrite of a file: the
        // model writes LF without a final break, and both editors replace the whole file with this text — in a CRLF file
        // every line shows as changed, under a diff ending on "\ No newline at end of file".
        if (extend && existing!.Contains('\n'))
            content = LineEndings.WithFinalBreakOf(existing, LineEndings.ToEol(content, LineEndings.Dominant(existing)));

        return new TestGenerationPlan(true, testPath, testName, extend, false, content);
    }
}
