using System.IO;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// <c>/tdd</c> under VS Code keeps what Visual Studio keeps: each test run and each fix explanation.
/// </summary>
/// <remarks>
/// Visual Studio turns every round's test output into a <c>run_tests</c> bubble and every fix summary
/// into an assistant bubble. The host sent the test output as a <c>chat/step</c> status line — which the
/// next step overwrites — and passed no fix-result callback at all: a three-round run left only the
/// final verdict, with neither the failing output nor the model's explanations. Read from the source:
/// the loop runs real test processes and has no harness here.
/// </remarks>
public class HostTddReportTests
{
    private static InvocationExpressionSyntax TddCall()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "README.md")))
            dir = dir.Parent;
        Assert.NotNull(dir);

        var path = Path.Combine(dir!.FullName, "Inferpal.Host", "HostSlashCommands.cs");
        Assert.True(File.Exists(path), $"{path} is gone — this guard checks nothing any more.");

        return Assert.Single(
            CSharpSyntaxTree.ParseText(ConventionCoverageTests.CodeOnly(path)).GetRoot()
                .DescendantNodes().OfType<InvocationExpressionSyntax>(),
            i => i.Expression.ToString().EndsWith("TddCommandHandler.HandleAsync", StringComparison.Ordinal));
    }

    private static string Argument(InvocationExpressionSyntax call, string name) =>
        Assert.Single(call.ArgumentList.Arguments, a => a.NameColon?.Name.Identifier.Text == name)
              .Expression.ToString();

    [Fact]
    public void EachTestRun_BecomesAToolBubble_NotAStatusLine()
    {
        var report = Argument(TddCall(), "onTestReport");
        Assert.Contains("\"chat/tool\"", report, StringComparison.Ordinal);
        Assert.DoesNotContain("\"chat/step\"", report, StringComparison.Ordinal);
    }

    [Fact]
    public void TheFixExplanations_AreKept()
    {
        Assert.NotEqual("null", Argument(TddCall(), "onFixResult"));
    }
}
