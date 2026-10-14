using Inferpal.Services.Execution;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A call blocked by a permission rule (<c>deny * \.env$</c>) was told "The arguments are not the cause … continue
/// without this tool": false twice — the path IS the cause, and a model told so gave up writing for the rest of its
/// task. The refusal names the rule and keeps the tool's other calls open.
/// </summary>
public class PermissionDeniedAdviceTests
{
    [Fact]
    public void ACallBlockedByARule_IsAboutThatCall_NotTheTool()
    {
        var said = ToolFailure.Describe("write_file",
            new PermissionDeniedException(@"Blocked by a permission rule (deny): C:\ws\.env"));

        Assert.Contains("permission rule", said);
        Assert.DoesNotContain("continue without this tool", said);
        Assert.DoesNotContain("The arguments are not the cause", said);
        Assert.Contains("Other calls of this tool are not affected", said);
    }

    [Fact]
    public void AnotherFailure_KeepsItsAdvice()
    {
        // Reference arm.
        Assert.Contains("continue without this tool",
            ToolFailure.Describe("search_codebase", new InvalidOperationException("the index is gone")));
    }
}
