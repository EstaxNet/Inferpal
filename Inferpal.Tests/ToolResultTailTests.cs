using System.Text;
using Inferpal.Services.Agent;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// ⚠ Every tool result enters the context cut to <see cref="AgentOrchestrator.MaxToolResultCharsInContext"/>
/// characters, and the cut kept the HEAD. A command's verdict is at its END: <c>ShellSession</c> appends
/// <c>[stderr]</c> and <c>[exit code N]</c> after the output — "a silent failure must not read as
/// success", its own comment says — so a chatty build that failed reached the model as eight
/// thousand characters of progress lines, with neither the error nor the exit code.
/// </summary>
public class ToolResultTailTests
{
    private const int Max = AgentOrchestrator.MaxToolResultCharsInContext;

    /// <summary>What <c>ShellSession.RunAsync</c> returns for a long run that fails.</summary>
    private static string FailedChattyCommand()
    {
        var sb = new StringBuilder("Restore complete (1.2s)\n");
        for (var i = 0; i < 400; i++) sb.Append($"  Compiling module {i:D4} of the solution ...\n");
        sb.Append("\n[stderr]\nsrc/App.cs(42,9): error CS0103: The name 'foo' does not exist in the current context");
        sb.Append("\n[exit code 1]");
        return sb.ToString();
    }

    [Fact]
    public void ALongFailedCommand_KeepsItsVerdict_InContext()
    {
        var result = FailedChattyCommand();
        Assert.True(result.Length > Max);                         // witness: the cap does bite

        var capped = AgentOrchestrator.CapForContext(result);

        Assert.Contains("[exit code 1]", capped);
        Assert.Contains("error CS0103", capped);
        Assert.StartsWith("Restore complete", capped);            // the head is still there too
        Assert.True(capped.Length <= Max + 200);
    }

    [Fact]
    public void TheCut_SaysHowMuchWasCut_AndWhere()
    {
        var result = FailedChattyCommand();
        var capped = AgentOrchestrator.CapForContext(result);

        Assert.Contains("truncated", capped);
        Assert.Contains(result.Length.ToString(), capped);
        Assert.Contains("middle", capped);
    }
}
