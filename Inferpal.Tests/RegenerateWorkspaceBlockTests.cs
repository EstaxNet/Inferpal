using System.IO;
using Inferpal.Models;
using Inferpal.Services.Prompting;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// ⚠ The workspace block rides with the first question only, and Regenerate takes that question back: both front-ends
/// must then send the block again. The VS Code host is measured end to end
/// (<c>HostServerTests.Regenerating_TheFirstQuestion_SendsTheWorkspaceBlockAgain</c>); the Visual Studio view model,
/// which cannot be built without Visual Studio, is held by its source.
/// </summary>
public class RegenerateWorkspaceBlockTests
{
    [Fact]
    public void TheBlock_IsFoundInTheQuestionThatCarriesIt_AndNowhereElse()
    {
        var sent = WorkspaceContext.Compose("Solution : Shop.sln", null) + "\n\n## User request\n\nhi";
        Assert.True(WorkspaceContext.IsIn([new ChatMessageDto("system", "sys"), new ChatMessageDto("user", sent)]));

        // Reference arms: no question left, and the header quoted by the ASSISTANT, are not the block.
        Assert.False(WorkspaceContext.IsIn([new ChatMessageDto("system", "sys")]));
        Assert.False(WorkspaceContext.IsIn([new ChatMessageDto("assistant", WorkspaceContext.Header)]));
    }

    [Fact]
    public void VisualStudiosRegenerate_SendsTheBlockAgain_WhenItTookItBack()
    {
        var path = Path.Combine(ConversationPersistenceSilenceTests.RepoRoot(), "Inferpal", "ToolWindow",
                                "InferpalToolWindowData.ToolInvocation.cs");
        var code = ConventionCoverageTests.CodeOnly(path);
        var regenerate = code[code.IndexOf("private async Task RegenerateAsync()", StringComparison.Ordinal)..];

        var rollback = regenerate.IndexOf("_history.RemoveRange(i, _history.Count - i)", StringComparison.Ordinal);
        var reset    = regenerate.IndexOf("if (!Services.Prompting.WorkspaceContext.IsIn(_history)) _workspaceContextInjected = false;", StringComparison.Ordinal);
        var resend   = regenerate.IndexOf("await SendCoreAsync(", StringComparison.Ordinal);
        Assert.True(rollback >= 0 && resend > rollback, "the rollback moved: the rule reads nothing");   // WITNESS
        Assert.True(reset > rollback && reset < resend, "the block is not sent again with the question taken back");
    }
}
