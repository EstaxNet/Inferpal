using System.IO;
using Inferpal.Services.Persistence;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// The two notices the chat writes itself — the compaction of the conversation, the session recap — are no call of the
/// model. Live, Visual Studio draws them outside the run line; a reload that grouped every tool bubble into a run would
/// count them as steps, and show a run for a turn that ran nothing. Windows only: the window is Visual Studio's.
/// </summary>
[Collection(SignalCollection.Name)]   // the window writes the active solution's signal
public sealed class ChatNoticesAreNoStepsVsTests
{
    [Fact]
    public async Task AReloadedConversation_KeepsTheChatsNotices_OutOfTheRun()
    {
        var root = Directory.CreateTempSubdirectory("inferpal-vm").FullName;   // deleted by the window's harness
        await new ConversationStore(slotOwner: ConversationStore.VisualStudioSlot).AutoSaveAsync(
            [new SavedMessage("user", "the question"),
             new SavedMessage("tool", "compacted", ToolNoticeTitle.ContextCompact),
             new SavedMessage("tool", "a file", "read_file"),
             new SavedMessage("assistant", "the answer"),
             new SavedMessage("tool", "recap", ToolNoticeTitle.OodaRecap)],
            CancellationToken.None, root);

        using var vs = new VsWindowHarness(root: root);
        // The restore clears the list, adds the bubbles, groups the runs, then puts the anchors back: both seen = all done.
        await VsWindowHarness.UntilAsync(() => vs.Window.Messages.Any(m => m.Content == "the answer")
                                               && vs.Window.Messages.Any(m => m.Role == "anchor"));

        var run = Assert.Single(vs.Window.Messages, m => m.Role == "run");
        Assert.Equal("read_file", Assert.Single(run.Steps).ToolName);   // witness: the call is a step
        // Still shown: a fix that dropped them would leave the run as it should be.
        Assert.Contains(vs.Window.Messages, m => m.ToolName == ToolNoticeTitle.ContextCompact);
        Assert.Contains(vs.Window.Messages, m => m.ToolName == ToolNoticeTitle.OodaRecap);
    }
}
