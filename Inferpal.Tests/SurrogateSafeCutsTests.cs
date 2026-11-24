using Inferpal.Services;
using Inferpal.Services.Agent;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A text cut to a length never keeps half of an emoji (or any character outside the BMP): a lone surrogate in a prompt is
/// not valid text — some JSON serializers replace it, others throw. Every cut goes through <see cref="SafeTruncate"/>.
/// </summary>
public class SurrogateSafeCutsTests
{
    private static readonly string Emoji = char.ConvertFromUtf32(0x1F600);

    private static bool Whole(string s)
    {
        for (var i = 0; i < s.Length; i++)
        {
            if (char.IsHighSurrogate(s[i]) && (i + 1 >= s.Length || !char.IsLowSurrogate(s[i + 1]))) return false;
            if (char.IsLowSurrogate(s[i]) && (i == 0 || !char.IsHighSurrogate(s[i - 1]))) return false;
        }
        return true;
    }

    [Fact]
    public void ATailCut_StartsOnAWholeCharacter() =>
        Assert.Equal(new string('a', 3), SafeTruncate.Tail(Emoji + new string('a', 3), 4));

    [Fact]
    public void AnAttachmentCutOnAnEmoji_KeepsItWholeOrNotAtAll()
    {
        var capped = AttachmentCap.Of(new string('a', 99) + Emoji + new string('b', 50), 100);
        Assert.True(Whole(capped.Text));
        Assert.Equal(99, capped.Shown);
    }

    [Fact]
    public void TheTaskSnippetOfALongRequest_StartsOnAWholeCharacter() =>
        Assert.True(Whole(AgentOrchestrator.TaskSnippet(Emoji + new string('a', 399))));

    [Fact]
    public void ToolPreviews_CutOnAWholeCharacter()
    {
        Assert.True(Whole(ChatTurnPolicy.BuildToolPreview(new string('a', 499) + Emoji + "x")));
        Assert.True(Whole(ChatTurnPolicy.OneLinePreview(new string('a', 9) + Emoji + "x", 10)));
    }

    [Fact]
    public void ACommitDiffCutOnAnEmoji_CountsWhatItKept()
    {
        var capped = GitCommitPolicy.CapDiff(new string('a', GitCommitPolicy.MaxDiffChars - 1) + Emoji + "x");
        Assert.True(Whole(capped.Text));
        Assert.Equal(GitCommitPolicy.MaxDiffChars - 1, capped.Kept);
    }

    [Fact]
    public void ATemplatesHint_CutsOnAWholeCharacter() =>
        Assert.True(Whole(SlashTemplates.HintOf(new UserSlashTemplate("t", new string('a', 49) + Emoji + "zz"))));
}
