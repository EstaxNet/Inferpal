using System.IO;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// Visual Studio: "Open diff" on an approval card that cannot open its dialog says so in the conversation — the card is
/// still waiting for an answer, and approving it blind is what the diff was there to prevent.
/// </summary>
/// <remarks>
/// ⚠ The failure was swallowed: the click did nothing, and the user approved a change nobody had seen in full. VS Code
/// already said it. Remote UI types cannot be built outside Visual Studio: the rule reads the source.
/// </remarks>
public class ApprovalDiffNotOpenedTests
{
    [Fact]
    public void AnOpenDiffThatFails_IsSaidInTheConversation()
    {
        var chrome = ConventionCoverageTests.CodeOnly(Path.Combine(
            ConversationPersistenceSilenceTests.RepoRoot(), "Inferpal", "ToolWindow", "InferpalToolWindowData.Chrome.cs"));

        var open = chrome.IndexOf("private async Task OpenCardDiffAsync(", StringComparison.Ordinal);
        Assert.True(open > 0, "OpenCardDiffAsync moved — the rule reads nothing");   // WITNESS
        var body = chrome[open..chrome.IndexOf("private int InsertStep(", open, StringComparison.Ordinal)];

        var swallow = body.IndexOf("Diagnostics.Swallow(\"Chat.ApprovalOpenDiff\", ex);", StringComparison.Ordinal);
        var said    = body.IndexOf("ChatMessageItem.NoticeMsg(Strings.ApprovalDiffNotOpened(reason))", StringComparison.Ordinal);
        Assert.True(swallow > 0, "the failure is no longer caught here");
        Assert.True(said > swallow, "the failure is only traced, never said");
    }
}
