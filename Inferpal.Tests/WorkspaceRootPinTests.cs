using System.IO;
using System.Linq;
using Inferpal.Config;
using Inferpal.Services.Lsp;
using Inferpal.Services.Rag;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// The workspace root is not a RAG setting: under Visual Studio with RAG off, nothing pinned it.
/// </summary>
/// <remarks>
/// <c>ProjectIndexService.RootDir</c> is the root the file tools confine to, the approval service
/// reads <c>.inferpal/permissions.json</c> from and Smart Fix reads <c>validators.json</c> from.
/// The VS Code host pins it at <c>initialize</c>; the VS view model only set it by starting an
/// indexing pass, and both of its paths returned first on <c>!RagEnabled</c>. With RAG off the
/// root stayed empty for the whole session: writes were confined nowhere and the deny overlay was
/// never read — while <c>/permissions</c>, which resolves its own root, listed it as in force.
/// </remarks>
public class WorkspaceRootPinTests
{
    // ── The decision ──────────────────────────────────────────────────────────

    [Fact]
    public void WithRagOff_TheRootIsStillPinned() =>
        Assert.Equal((RootPinAction.Pin, "/src/app"),
            WorkspaceRootPin.Decide(ragEnabled: false, currentRoot: "", activeSolutionDir: null, reliableRoot: "/src/app"));

    /// <summary>Pinned at once even with RAG on: the startup pass indexes seconds later, and the
    /// tools must not run unconfined in between.</summary>
    [Fact]
    public void WithRagOn_AnEmptyRootIsPinnedWithoutWaitingForTheIndex() =>
        Assert.Equal((RootPinAction.Pin, "/src/app"),
            WorkspaceRootPin.Decide(ragEnabled: true, currentRoot: "", activeSolutionDir: "/src/app", reliableRoot: "/src/app"));

    [Fact]
    public void NoSolutionAnchoredRoot_PinsNothing() =>
        Assert.Equal((RootPinAction.None, (string?)null),
            WorkspaceRootPin.Decide(ragEnabled: false, currentRoot: null, activeSolutionDir: null, reliableRoot: null));

    [Fact]
    public void ASolutionSwitch_WithRagOff_RepointsTheRoot() =>
        Assert.Equal((RootPinAction.Pin, "/src/other"),
            WorkspaceRootPin.Decide(ragEnabled: false, currentRoot: "/src/app", activeSolutionDir: "/src/other", reliableRoot: null));

    [Fact]
    public void ASolutionSwitch_WithRagOn_Reindexes() =>
        Assert.Equal((RootPinAction.Index, "/src/other"),
            WorkspaceRootPin.Decide(ragEnabled: true, currentRoot: "/src/app", activeSolutionDir: "/src/other", reliableRoot: null));

    /// <summary>Reference arm: the same solution, whatever its casing, is not a switch — otherwise
    /// every heartbeat tick would restart indexing.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData(@"C:\SRC\App")]
    public void NoSwitch_DoesNothing(string? active) =>
        Assert.Equal((RootPinAction.None, (string?)null),
            WorkspaceRootPin.Decide(ragEnabled: true, currentRoot: @"C:\src\app", activeSolutionDir: active, reliableRoot: @"C:\src\app"));

    // ── What the pin turns on ─────────────────────────────────────────────────

    private sealed class AnsweringApproval(InferpalConfig config, Func<string?> root)
        : ApprovalServiceBase(config, root)
    {
        protected override Task<ApprovalDecision> PromptUserAsync(
            string message, DiffInfo? diff, CancellationToken ct) =>
            Task.FromResult(ApprovalDecision.Once);
    }

    [Fact]
    public async Task APinnedRoot_EnforcesTheWorkspaceDenyRules_WithoutAnyIndexing()
    {
        var root = Path.Combine(Path.GetTempPath(), "inferpal-pin-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, ".inferpal"));
        try
        {
            File.WriteAllText(Path.Combine(root, ".inferpal", "permissions.json"),
                "{ \"rules\": [\"deny write_file secret\\\\.txt$\"] }");

            var config = new InferpalConfig();
            using var lsp   = new LspSemanticProvider();
            using var index = new ProjectIndexService(new FakeInferenceProvider(), config, lsp);
            var approval = new AnsweringApproval(config, () => index.RootDir);
            var subject  = Path.Combine(root, "secret.txt");

            // Witness: nothing pinned — what VS had all session with RAG off — and the rule does not exist.
            Assert.True(await approval.RequestApprovalAsync("write_file", subject, CancellationToken.None, subject));

            index.SetRoot(root);

            await Assert.ThrowsAsync<PermissionDeniedException>(() =>
                approval.RequestApprovalAsync("write_file", subject, CancellationToken.None, subject));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    // ── The view model goes through it ────────────────────────────────────────
    // The view model cannot be instantiated outside Visual Studio, so this half reads its source.

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "README.md")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static MethodDeclarationSyntax Method(string file, string name)
    {
        var path = Path.Combine(RepoRoot(), "Inferpal", "ToolWindow", file);
        Assert.True(File.Exists(path), $"{path} is gone — this rule checks nothing any more.");
        var method = CSharpSyntaxTree.ParseText(ConventionCoverageTests.CodeOnly(path)).GetRoot()
            .DescendantNodes().OfType<MethodDeclarationSyntax>()
            .FirstOrDefault(m => m.Identifier.Text == name);
        Assert.True(method is not null, $"{name} not found in {file}.");
        return method!;
    }

    private static bool Calls(SyntaxNode node, string name) =>
        node.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Any(i => i.Expression is IdentifierNameSyntax id && id.Identifier.Text == name
                   || i.Expression is MemberAccessExpressionSyntax m && m.Name.Identifier.Text == name);

    /// <summary>No early return: the one that sat on <c>!RagEnabled</c> is the defect.</summary>
    [Fact]
    public void ThePin_DecidesBeforeAnythingCanReturn()
    {
        var pin = Method("InferpalToolWindowData.Rag.cs", "PinWorkspaceRoot");

        Assert.True(Calls(pin, "Decide"), "PinWorkspaceRoot must go through WorkspaceRootPin.Decide.");
        Assert.True(Calls(pin, "SetRoot"), "PinWorkspaceRoot must be able to pin without indexing.");
        Assert.Empty(pin.DescendantNodes().OfType<ReturnStatementSyntax>());
    }

    /// <summary>
    /// Whatever moves the root takes a solution-anchored one, never <c>FindProjectRoot()</c>: that one
    /// always answers, falling back to the host's working directory — which under Visual Studio is
    /// never the project. <c>/index rebuild</c> passed it, so its "cannot locate solution root" refusal
    /// could not happen, and with no solution found it indexed that directory and moved the file tools'
    /// confinement and the deny overlay there.
    /// </summary>
    [Fact]
    public void NothingThatMovesTheRoot_TakesTheWorkingDirectoryFallback()
    {
        var dir = Path.Combine(RepoRoot(), "Inferpal", "ToolWindow");
        Assert.True(Directory.Exists(dir), $"{dir} is gone — this rule checks nothing any more.");

        var movers = Directory.EnumerateFiles(dir, "*.cs")
            .SelectMany(f => CSharpSyntaxTree.ParseText(ConventionCoverageTests.CodeOnly(f)).GetRoot()
                .DescendantNodes().OfType<InvocationExpressionSyntax>()
                .Where(i => i.Expression is MemberAccessExpressionSyntax m
                         && (m.Name.Identifier.Text is "StartIndexing" or "SetRoot"
                             || m.Name.Identifier.Text == "Handle" && m.Expression.ToString().EndsWith("IndexCommandHandler")))
                .Select(i => (File: Path.GetFileName(f), Call: i)))
            .ToList();

        // Witness: the startup pass, the pin (both actions) and /index.
        Assert.True(movers.Count >= 4, $"Only {movers.Count} root-moving call(s) found — the scan no longer sees them.");

        var offenders = movers
            .Where(m => Calls(m.Call.ArgumentList, "FindProjectRoot"))
            .Select(m => $"{m.File}: {m.Call}")
            .ToList();
        Assert.True(offenders.Count == 0,
            "These calls can move the workspace root to the host's working directory:\n" + string.Join("\n", offenders));
    }

    [Theory]
    [InlineData("InferpalToolWindowData.Connection.cs", "StartHeartbeatAsync")]
    [InlineData("InferpalToolWindowData.ChatTurn.cs",   "SendCoreAsync")]
    [InlineData("InferpalToolWindowData.ChatTurn.cs",   "SendAsync")]
    public void EveryEntryPoint_PinsTheRoot(string file, string method) =>
        Assert.True(Calls(Method(file, method), "PinWorkspaceRoot"),
            $"{method} no longer pins the workspace root.");
}
