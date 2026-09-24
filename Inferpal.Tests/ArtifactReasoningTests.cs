using System.IO;
using Inferpal.Config;
using Inferpal.Models;
using Inferpal.Services;
using Inferpal.Services.CodeActions;
using Inferpal.Services.Commands;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A reply turned into an ARTIFACT carries no reasoning. A server that does not separate reasoning (vLLM without
/// <c>--reasoning-parser</c>) sends it inline, at the head of the content — and every site that writes the reply
/// wrote the chain of thought with it: into the source file (in-place actions), the test file (<c>/test</c>),
/// <c>.inferpal/context.md</c> (<c>/onboard context</c>, the system prompt of every following session), the
/// findings of <c>/check</c>, and the answers of <c>/arena</c>.
/// </summary>
public sealed class ArtifactReasoningTests : IDisposable
{
    private const string Reasoning = "<think>\nThe user wants the value changed. I'll answer with the code only.\n</think>\n\n";

    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "inferpal-tests", $"artifact-reasoning-{Guid.NewGuid():N}");

    public ArtifactReasoningTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static CodeActionRun Finish(string reply, string original = "int x = 1;") =>
        CodeActionPipeline.Finish(reply, original, original, reindent: false, "m", "http://x", cutAtLimit: false);

    // ── In-place code actions ────────────────────────────────────────────────

    [Fact]
    public void AnInPlaceEdit_AppliesTheCode_NotTheReasoningAboveIt()
    {
        var run = Finish(Reasoning + "```csharp\nint x = 2;\n```");

        Assert.Equal(CodeActionOutcome.Edited, run.Outcome);
        Assert.Equal("int x = 2;", run.EditedCode);
    }

    [Fact]
    public void TheNoChangeSentinel_AfterReasoning_StillMeansNoChange()
    {
        var run = Finish(Reasoning + CodeActionSentinel.Token);

        Assert.Equal(CodeActionOutcome.NoChangeNeeded, run.Outcome);
    }

    [Fact]
    public void AReplyThatIsOnlyReasoning_IsNeverApplied()
    {
        var run = Finish("<think>\nStill weighing whether x should be 2 or 3");

        Assert.NotEqual(CodeActionOutcome.Edited, run.Outcome);
    }

    [Fact]
    public void CodeThatContainsTheTags_IsRewrittenWhole()
    {
        // Reference arm: the tags are the CODE's here — stripping them anywhere would delete the lines between them.
        const string code = "const string Open = \"<think>\";\nconst string Close = \"</think>\";";

        var run = Finish(code, original: "const string Open = \"<x>\";");

        Assert.Equal(CodeActionOutcome.Edited, run.Outcome);
        Assert.Equal(code, run.EditedCode);
    }

    // ── /test ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ANewTestFile_IsTheCode_NotTheReasoning()
    {
        var source = Path.Combine(_dir, "Widget.cs");
        File.WriteAllText(source, "public class Widget { }");
        var client = new FakeInferenceProvider
        {
            ChatResult = new ChatTurnResult(Reasoning + "public class WidgetTests { }", null, 0, 0),
        };

        var plan = await TestGenerationPlanner.PlanAsync(client, "m", source, "public class Widget { }", CancellationToken.None);

        Assert.True(plan.Ok);
        Assert.Equal("public class WidgetTests { }", plan.Content);
    }

    // ── /onboard context ─────────────────────────────────────────────────────

    [Fact]
    public async Task TheProjectContext_IsTheDraft_NotTheReasoning()
    {
        var client = new FakeInferenceProvider
        {
            ChatResult = new ChatTurnResult(Reasoning + "# Project\n\nA fixture.", null, 0, 0),
        };
        GitRunner noGit = (_, _) => Task.FromResult((string.Empty, 0));

        var result = await OnboardCommandHandler.HandleAsync(
            client, new InferpalConfig(), _dir, ["/onboard", "context"], noGit, null, CancellationToken.None);

        Assert.Equal("# Project\n\nA fixture.", result.Write!.Content);
    }

    // ── /check ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task AFindingDraftedWhileReasoning_IsNotAFinding()
    {
        Directory.CreateDirectory(Path.Combine(_dir, ".inferpal", "checks"));
        File.WriteAllText(Path.Combine(_dir, ".inferpal", "checks", "secrets.md"),
            "---\ndescription: no secrets\n---\nNo secret may be committed.");
        const string diff = "diff --git a/src/Alpha.cs b/src/Alpha.cs\n--- a/src/Alpha.cs\n+++ b/src/Alpha.cs\n"
                          + "@@ -8,3 +10,4 @@\n+    const string Key = \"sk-live-1234\";\n";
        var client = new FakeInferenceProvider
        {
            OnChat = (_, _) => Task.FromResult(new ChatTurnResult(
                "<think>\n- [minor] src/Alpha.cs:10 — maybe a naming issue, on second thought it is fine\n</think>\n"
                + "- [blocker] src/Alpha.cs:10 — API key in clear text", [], 0, 0)),
        };

        var result = await CheckCommandHandler.HandleAsync(
            client, new InferpalConfig(), _dir, ["/check"],
            git: (args, _) => Task.FromResult((args == "diff --staged" ? diff : "", 0)),
            onProgress: null, CancellationToken.None);

        Assert.Contains("API key in clear text", result.Message);                  // witness: the review is there
        Assert.DoesNotContain("on second thought", result.Message);
    }

    // /arena lives in ArenaTests (ADuel_ShowsBothAnswers_EvenWhenOneNeverLeftItsReasoning): every test that
    // touches ArenaStore's static file override must share that one class.
}
