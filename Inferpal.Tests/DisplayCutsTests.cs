using System.IO;
using System.Text;
using Inferpal.Services.Commands;
using Inferpal.Services.Execution;
using Inferpal.Services.Presentation;
using Inferpal.Services.Tasks;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A text cut for display keeps whole characters (<c>SafeTruncate</c>'s rule, as <c>ChatTurnPolicy.OneLinePreview</c>
/// applies it): an emoji across the cut stays whole or goes, never half of it — a lone surrogate that the editors draw as a
/// replacement box and that no UTF-8 writer can encode.
/// </summary>
public class DisplayCutsTests
{
    private static readonly string Emoji = char.ConvertFromUtf32(0x1F600);

    private static void AssertWhole(string shown) =>
        Assert.Equal(shown, Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(shown)));

    [Fact]
    public void TheRewindCardsQuestion_IsCutOnAWholeCharacter() =>
        AssertWhole(RewindCard.Build(new RewindPlan(3, new string('a', 79) + Emoji + " and more", [], [])).Title);

    [Fact]
    public void AFailedTasksError_IsCutOnAWholeCharacter()
    {
        var now = DateTimeOffset.UtcNow;
        var task = new BackgroundTaskSnapshot("t1", "objective", BackgroundTaskState.Failed, now, now, now, null,
                                              new string('e', 158) + Emoji + " and more", [], 0);

        AssertWhole(TaskCommandHandler.FinishedNotice(task));
    }

    [Fact]
    public void AnApprovalCardsFileNames_AreCutOnAWholeCharacter()
    {
        // A batch's subject names every file: the cut falls inside the emoji of the second name.
        var root   = Path.GetTempPath();
        var first  = new string('a', ApprovalCard.SubjectChars - 7) + ".cs";
        var second = "b" + Emoji + ".cs";
        var subject = Path.Combine(root, first) + "\n" + Path.Combine(root, second);

        AssertWhole(ApprovalCard.Build(new ApprovalPrompt("apply_edits", "edits", subject, null, "Change?"), root).Subject);
    }
}
