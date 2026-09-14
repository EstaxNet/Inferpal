using System.IO;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// Visual Studio adapter defects that each have a single subject. The view model and the editor
/// commands cannot be instantiated outside Visual Studio, so these guards read their source, like
/// <see cref="WorkspaceRootPinTests"/>. Properties with more than one subject live in
/// <see cref="ConventionCoverageTests"/> (rules 29 and 30).
/// </summary>
public class VsAdapterRegressionTests
{
    private const string Vm = "Inferpal/ToolWindow/";

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "README.md")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static MethodDeclarationSyntax Method(string relativePath, string name)
    {
        var path = Path.Combine(RepoRoot(), relativePath);
        Assert.True(File.Exists(path), $"{path} is gone — this guard checks nothing any more.");
        var method = CSharpSyntaxTree.ParseText(ConventionCoverageTests.CodeOnly(path)).GetRoot()
            .DescendantNodes().OfType<MethodDeclarationSyntax>()
            .FirstOrDefault(m => m.Identifier.Text == name);
        Assert.True(method is not null, $"{name} not found in {relativePath}.");
        return method!;
    }

    private static bool Calls(SyntaxNode node, string name) =>
        node.DescendantNodes().OfType<InvocationExpressionSyntax>().Any(i => i.Expression switch
        {
            IdentifierNameSyntax id         => id.Identifier.Text == name,
            MemberAccessExpressionSyntax m  => m.Name.Identifier.Text == name,
            MemberBindingExpressionSyntax b => b.Name.Identifier.Text == name,
            _                               => false,
        });

    /// <summary>Alt+M posts "/map" as a pending prompt: it must reach the slash router, not the model.</summary>
    [Fact]
    public void APendingSlashCommand_IsRouted_NotSentToTheModel()
    {
        var run = Method(Vm + "InferpalToolWindowData.PendingPrompt.cs", "RunPendingTurnAsync");
        Assert.True(Calls(run, "HandleSlashCommandAsync"),
            "RunPendingTurnAsync sends every pending prompt to the model — \"/map\" (Alt+M) arrives as chat text.");
    }

    /// <summary>
    /// /fix-build writes through the real registry: without a run of its own, its edits belong to no
    /// run and /undo-run cannot revert them.
    /// </summary>
    [Fact]
    public void FixBuild_WritesInsideARun()
    {
        var fix = Method(Vm + "InferpalToolWindowData.PromptHistory.cs", "HandleFixBuildCommandAsync");

        Assert.True(Calls(fix, "BeginRun") || Calls(fix, "BeginRunScope"),
            "HandleFixBuildCommandAsync opens no change-tracking run: /undo-run cannot revert its fixes.");
        var closes = Calls(fix, "BeginRunScope")
            || fix.DescendantNodes().OfType<FinallyClauseSyntax>().Any(f => Calls(f, "EndRun"));
        Assert.True(closes, "HandleFixBuildCommandAsync opens a run it never closes.");
    }

    /// <summary>
    /// Regenerate removes the last question before resending it; with the backend down, the resend
    /// stops at its pre-flight and the question was gone from the screen and the history.
    /// </summary>
    [Fact]
    public void Regenerate_ChecksTheBackend_BeforeTruncatingTheConversation()
    {
        var regen = Method(Vm + "InferpalToolWindowData.ToolInvocation.cs", "RegenerateAsync");

        var firstRemoval = regen.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(i => i.Expression is MemberAccessExpressionSyntax { Name.Identifier.Text: "RemoveAt" or "RemoveRange" })
            .Select(i => i.SpanStart)
            .DefaultIfEmpty(-1)
            .Min();
        Assert.True(firstRemoval >= 0, "RegenerateAsync no longer truncates anything — this guard checks nothing.");

        var guard = regen.DescendantNodes().OfType<IdentifierNameSyntax>()
            .Where(id => id.Identifier.Text == "_isBackendReachable")
            .Select(id => id.SpanStart)
            .DefaultIfEmpty(int.MaxValue)
            .Min();
        Assert.True(guard < firstRemoval,
            "RegenerateAsync truncates the conversation before checking that the backend is reachable.");
    }

    /// <summary>Commands that call the model for a long time take the turn: Stop cancels them, and
    /// no chat turn can start underneath them.</summary>
    [Theory]
    [InlineData("Check")]
    [InlineData("Onboard")]
    [InlineData("Bench")]
    [InlineData("Arena")]
    [InlineData("Models")]
    [InlineData("Setup")]
    public void ALongCommand_TakesTheTurn(string command)
    {
        var dispatch = Method(Vm + "InferpalToolWindowData.SlashCommands.cs", "RunDelegatedCommandAsync");

        var section = dispatch.DescendantNodes().OfType<SwitchSectionSyntax>()
            .FirstOrDefault(s => s.Labels.OfType<CaseSwitchLabelSyntax>()
                .Any(l => l.Value.ToString() == "SlashCommandId." + command));
        Assert.True(section is not null, $"No dispatch case for SlashCommandId.{command} — this guard checks nothing.");
        Assert.True(Calls(section!, "RunOwnedCommandAsync"),
            $"/{command.ToLowerInvariant()} runs without taking the turn: Stop cannot cancel it and a chat turn can start underneath it.");
    }

    /// <summary>
    /// /setup can only take the turn if Stop reaches it: its discovery made every backend call with
    /// <c>CancellationToken.None</c>, so a Stop button would have shown and done nothing.
    /// </summary>
    [Fact]
    public void SetupDiscovery_HonoursTheTurnsCancellation()
    {
        var discovery = Method(Vm + "InferpalToolWindowData.Rag.cs", "RunSetupDiscoveryAsync");

        Assert.Contains(discovery.ParameterList.Parameters, p => p.Type?.ToString() == "CancellationToken");
        Assert.DoesNotContain("CancellationToken.None", discovery.ToString(), StringComparison.Ordinal);
    }

    /// <summary>AsyncCommand handlers run off the view-model context; the prompt box and the history
    /// navigator are view-model state.</summary>
    [Theory]
    [InlineData("HistoryUpAsync")]
    [InlineData("HistoryDownAsync")]
    public void PromptHistoryNavigation_WritesThePromptOnTheVmContext(string handler)
    {
        var method = Method(Vm + "InferpalToolWindowData.PromptHistory.cs", handler);

        var writes = method.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Where(a => a.Left is IdentifierNameSyntax { Identifier.Text: "Prompt" })
            .ToList();
        Assert.NotEmpty(writes);

        Assert.All(writes, write => Assert.True(
            write.Ancestors().OfType<LambdaExpressionSyntax>().Any(l =>
                l.Parent is ArgumentSyntax { Parent: ArgumentListSyntax { Parent: InvocationExpressionSyntax inv } }
                && inv.Expression.ToString() == "RunOnVMContextAsync"),
            $"{handler} writes Prompt off the view-model context."));
    }

    /// <summary>The settings window is opened and closed repeatedly; each instance subscribed to the
    /// VS theme and never let go.</summary>
    [Fact]
    public void SettingsWindow_ReleasesItsThemeSubscription()
    {
        var detach = Method(Vm + "InferpalSettingsData.cs", "Detach");
        var text   = detach.ToString();

        Assert.True(text.Contains("_themeSubscription", StringComparison.Ordinal) && Calls(detach, "Dispose"),
            "InferpalSettingsData.Detach leaves the VS theme subscription alive.");
    }

    /// <summary>The message list always holds the two scroll anchors: counting it archived an empty
    /// conversation — a titled, empty session — on every /clear and /template.</summary>
    [Fact]
    public void Clear_ArchivesOnlyAConversationWithContent()
    {
        var clear = Method(Vm + "InferpalToolWindowData.Connection.cs", "ClearAsync");

        var decision = clear.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Where(a => a.Left is IdentifierNameSyntax { Identifier.Text: "hasMessages" })
            .ToList();
        Assert.NotEmpty(decision);
        Assert.All(decision, a => Assert.DoesNotContain("Messages.Count", a.Right.ToString()));
    }

    /// <summary>Closing the spinner is the only way to say "stop" to an editor code action: the edit
    /// used to land anyway once the model answered.</summary>
    [Theory]
    [InlineData("Inferpal/Commands/InlineEditSelectionCommand.cs")]
    [InlineData("Inferpal/Commands/InPlaceCodeEdit.cs")]
    [InlineData("Inferpal/Commands/TestGenerationEdit.cs")]
    public void ClosingTheSpinner_CancelsTheGeneration(string file)
    {
        var path = Path.Combine(RepoRoot(), file);
        Assert.True(File.Exists(path), $"{path} is gone — this guard checks nothing any more.");
        Assert.Contains("CancelledByUser", ConventionCoverageTests.CodeOnly(path));
    }

    /// <summary>Ctrl+Shift+I swallowed a model failure and an edit failure without a word: the user saw
    /// the spinner vanish and nothing change.</summary>
    [Fact]
    public void InlineEdit_NeverFailsInSilence()
    {
        var run = Method("Inferpal/Commands/InlineEditSelectionCommand.cs", "ExecuteCommandAsync");

        var catches = run.DescendantNodes().OfType<CatchClauseSyntax>().ToList();
        Assert.NotEmpty(catches);

        var mute = catches
            .Where(c => c.Declaration?.Type.ToString() != "OperationCanceledException" && !Calls(c.Block, "Swallow"))
            .Select(c => c.ToString().ReplaceLineEndings(" "))
            .ToList();
        Assert.True(mute.Count == 0,
            "InlineEditSelectionCommand swallows these failures without tracing them:" + Environment.NewLine
            + string.Join(Environment.NewLine, mute));
    }

    /// <summary>
    /// "Use a separate model per role" unchecked promises the chat model everywhere — its own hint
    /// says so. It only folded the pickers away: the role overrides stayed in the configuration, the
    /// router kept using them, and the box came back checked at the next opening (issue #8).
    /// </summary>
    [Fact]
    public void SavingWithSeparateRoleModelsOff_UsesTheChatModelEverywhere()
    {
        var save = Method(Vm + "InferpalSettingsData.cs", "SaveCoreAsync");
        Assert.True(Calls(save, "UseChatModelEverywhere"),
            "SaveCoreAsync keeps the per-role models when \"Use a separate model per role\" is unchecked.");
    }

    /// <summary>
    /// A code action that opens the chat window (Explain, Alt+M…) queued its turn before the last
    /// session was restored. The restore had already waited for the current turn — there was none yet
    /// — so RestoreConversation then replaced the conversation under the running action: its question
    /// and history gone while the answer streamed in. The pending turn waits for that startup load.
    /// </summary>
    [Fact]
    public void APendingPrompt_WaitsForTheStartupSessionLoad()
    {
        var run = Method(Vm + "InferpalToolWindowData.PendingPrompt.cs", "RunPendingTurnAsync");
        Assert.True(run.DescendantNodes().OfType<AwaitExpressionSyntax>()
                       .Any(a => a.Expression.ToString().Contains("_startupSessionLoad", StringComparison.Ordinal)),
            "RunPendingTurnAsync starts its turn while the startup session load can still replace the conversation.");

        var construction = ConventionCoverageTests.CodeOnly(
            Path.Combine(RepoRoot(), Vm + "InferpalToolWindowData.Construction.cs"));
        Assert.Matches(@"_startupSessionLoad\s*=\s*LoadSessionAsync\(", construction);
    }
}
