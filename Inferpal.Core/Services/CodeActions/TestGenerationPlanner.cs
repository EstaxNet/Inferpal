using System.IO;
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
internal sealed record TestGenerationPlan(
    bool Ok, string TestPath, string TestFileName, bool Extended, bool NoChange, string Content,
    bool Unreadable = false, bool Cut = false)
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
        new(false, testPath, Path.GetFileName(testPath), extended, NoChange: false, string.Empty, Cut: true);
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
    /// <param name="sourcePath">Path of the file under test — decides where the tests go.</param>
    /// <param name="sourceCode">Selection if the editor has one, otherwise the whole file.</param>
    public static async Task<TestGenerationPlan> PlanAsync(
        IInferenceProvider client, string model, string sourcePath, string sourceCode,
        CancellationToken ct)
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
        if (File.Exists(testPath))
        {
            try { existing = await File.ReadAllTextAsync(testPath, ct); }
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
            : $"{TestGenerationPrompts.Instruction}\n\nSource file: {sourceName}\n\n{sourceCode}";

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

        var content = InlineEditResponse.Clean(result.TextContent);

        if (CodeActionSentinel.IsNoChange(content))
            return new TestGenerationPlan(false, testPath, testName, extend, NoChange: true, string.Empty);

        if (string.IsNullOrWhiteSpace(content))
            return TestGenerationPlan.Failed(testPath, extend);

        return new TestGenerationPlan(true, testPath, testName, extend, false, content);
    }
}
