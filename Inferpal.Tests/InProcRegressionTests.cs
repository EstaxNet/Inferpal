using System.IO;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// In-process defects (ghost text, the <c>/tdd</c> debugger driver) that each have one subject. That
/// code runs inside devenv against EnvDTE and the WPF editor, so these guards read its source; the
/// FIM sidecar's lifecycle is exercised for real in <c>FimSidecarLifecycleTests</c>.
/// </summary>
public class InProcRegressionTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "README.md")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string CodeOnly(string relativePath)
    {
        var path = Path.Combine(RepoRoot(), relativePath);
        Assert.True(File.Exists(path), $"{path} is gone — this guard checks nothing any more.");
        return ConventionCoverageTests.CodeOnly(path);
    }

    private static SyntaxNode Root(string relativePath) =>
        CSharpSyntaxTree.ParseText(CodeOnly(relativePath)).GetRoot();

    private static MethodDeclarationSyntax Method(string relativePath, string name)
    {
        var method = Root(relativePath).DescendantNodes().OfType<MethodDeclarationSyntax>()
            .FirstOrDefault(m => m.Identifier.Text == name);
        Assert.True(method is not null, $"{name} not found in {relativePath}.");
        return method!;
    }

    private static bool Calls(SyntaxNode node, string name) =>
        node.DescendantNodes().OfType<InvocationExpressionSyntax>().Any(i => i.Expression switch
        {
            IdentifierNameSyntax id        => id.Identifier.Text == name,
            MemberAccessExpressionSyntax m => m.Name.Identifier.Text == name,
            _                              => false,
        });

    /// <summary>
    /// The driver serves one request at a time: while it waited for a stop that never came, the
    /// host's next request — stop, typically — was never claimed, and every debugger tool went dead
    /// until the program ended.
    /// </summary>
    [Fact]
    public void TheDebugDriver_NeverWaitsForAStopLongerThanTheHost()
    {
        const string driverFile = "Inferpal.InProc/GhostText/VsDebugDriver.cs";

        var loops = Method(driverFile, "ResumeAndWaitAsync").DescendantNodes().OfType<WhileStatementSyntax>().ToList();
        Assert.NotEmpty(loops);
        Assert.All(loops, loop => Assert.True(
            loop.Condition.ToString().Contains("deadline", StringComparison.OrdinalIgnoreCase),
            $"ResumeAndWaitAsync waits without a deadline: while ({loop.Condition})"));

        var driver = CodeOnly(driverFile);
        Assert.Contains("DebugOps.StartBudget", driver);
        Assert.Contains("DebugOps.ResumeBudget", driver);

        // The host's side of the same budgets: one number, read by both ends of the wire.
        var host = Root("Inferpal.Core/Services/Debugging/SignalDebugSession.cs")
            .DescendantNodes().OfType<PropertyDeclarationSyntax>().ToList();
        string Initializer(string name) =>
            host.FirstOrDefault(p => p.Identifier.Text == name)?.Initializer?.ToString() ?? string.Empty;
        Assert.Contains("DebugOps.StartBudget", Initializer("StartTimeout"));
        Assert.Contains("DebugOps.ResumeBudget", Initializer("ResumeTimeout"));
    }

    /// <summary>
    /// A completion computed for an older snapshot stayed pending: the next one was appended to it,
    /// and Tab inserted that stale, doubled text though nothing was displayed.
    /// </summary>
    [Fact]
    public void AStaleGhostCompletion_IsNeitherAppendedToNorAccepted()
    {
        var root = Root("Inferpal.InProc/GhostText/GhostTextAdornment.cs");

        var appends = root.DescendantNodes().OfType<BinaryExpressionSyntax>()
            .Where(b => b.IsKind(SyntaxKind.AddExpression) && b.ToString().Contains("_pending", StringComparison.Ordinal))
            .Select(b => b.ToString())
            .ToList();
        Assert.True(appends.Count == 0, "GhostTextAdornment appends to the pending completion: " + string.Join(" | ", appends));

        var pending = root.DescendantNodes().OfType<PropertyDeclarationSyntax>()
            .FirstOrDefault(p => p.Identifier.Text == "PendingCompletion");
        Assert.True(pending is not null, "GhostTextAdornment.PendingCompletion not found — this guard checks nothing.");
        Assert.True(pending!.ToString().Contains("CurrentSnapshot", StringComparison.Ordinal),
            "PendingCompletion hands Tab a completion without checking the snapshot it was computed for.");
    }

    /// <summary>
    /// The capture's cleanup stopped the debugger even when the attach had failed — by then possibly
    /// the user's own F5 session.
    /// </summary>
    [Fact]
    public void TheTestCapture_StopsTheDebuggerOnlyWhenItAttached()
    {
        var stops = Method("Inferpal.InProc/GhostText/VsDebugDriver.cs", "CaptureTestAsync")
            .DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(i => i.Expression is MemberAccessExpressionSyntax { Name.Identifier.Text: "Stop" })
            .ToList();
        Assert.NotEmpty(stops);
        Assert.All(stops, stop => Assert.True(
            stop.Ancestors().OfType<IfStatementSyntax>().Any(s => s.Condition.ToString().Contains("attached", StringComparison.Ordinal)),
            "CaptureTestAsync stops the debugger without checking that it attached."));
    }

    /// <summary>The package's teardown was documented as killing the sidecar, and never did.</summary>
    [Fact]
    public void ThePackage_ShutsTheFimSidecarDown()
    {
        var dispose = Method("Inferpal.InProc/GhostText/GhostTextPackage.cs", "Dispose");
        Assert.True(Calls(dispose, "Shutdown"), "GhostTextPackage.Dispose never calls FimSidecar.Shutdown.");
    }

    /// <summary>
    /// A recycled sidecar's reader could finish after the next sidecar had started, and released the
    /// new one's requests as if they had failed.
    /// </summary>
    [Fact]
    public void TheSidecarReader_ReleasesOnlyItsOwnRequests()
    {
        var read = Method("Inferpal.InProc/Fim/FimSidecar.cs", "ReadLoop").ToString().Replace(" ", string.Empty);
        Assert.Contains("ReferenceEquals(_process", read);
    }
}
