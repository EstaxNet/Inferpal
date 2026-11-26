using Inferpal.ToolWindow;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// Visual Studio draws an answer as Markdown while it streams, as VS Code does: raw text until the end showed "**bold**"
/// and "`code`" marks the whole time an answer was written. A parse crosses to Visual Studio's process, so it runs at
/// most every <see cref="ChatMessageItem.StreamParseEvery"/>, and the blocks already shown are kept.
/// </summary>
public class StreamingMarkdownTests
{
    [Fact]
    public async Task AStreamingAnswer_IsDrawnAsMarkdown_AndKeepsTheBlocksAlreadyShown()
    {
        var item = new ChatMessageItem { Role = "assistant", IsStreaming = true };

        // Not in the frame the bubble is inserted in: Remote UI drops a block added in that frame (an empty bubble).
        item.Content = "Order 1194";
        Assert.False(item.HasBlocks);

        await Task.Delay(ChatMessageItem.StreamParseEvery + TimeSpan.FromMilliseconds(60));
        item.Content = "Order 1194 is still **awaiting payment**.";
        Assert.True(item.HasBlocks);                                     // drawn as it streams, not at the end
        var first = Assert.Single(item.Blocks);
        Assert.DoesNotContain("**", first.Text, StringComparison.Ordinal);

        await Task.Delay(ChatMessageItem.StreamParseEvery + TimeSpan.FromMilliseconds(60));
        item.Content += "\n\nThe total is now computed for every order.";
        Assert.Equal(2, item.Blocks.Count);
        Assert.Same(first, item.Blocks[0]);                              // the block shown is kept, not sent again

        // Within the interval, a chunk waits for the next parse (or the end).
        item.Content += "\n\n- one more line";
        Assert.Equal(2, item.Blocks.Count);
        item.IsStreaming = false;                                        // the end parses whatever is left
        Assert.Equal(3, item.Blocks.Count);
        // ...and sends every block again: one Visual Studio dropped on the way would otherwise stay missing.
        Assert.NotSame(first, item.Blocks[0]);
        Assert.Equal(first.Text, item.Blocks[0].Text);
    }

    [Fact]
    public void AShortAnswerInOneChunk_IsDrawnAtTheEnd()
    {
        var item = new ChatMessageItem { Role = "assistant", IsStreaming = true };
        item.Content = "\n\n42";
        Assert.False(item.HasBlocks);
        item.IsStreaming = false;
        Assert.Equal("42", Assert.Single(item.Blocks).Text);
    }

    [Fact]
    public void ANoticeOrAQuestion_IsNotParsedWhileItIsWritten()
    {
        // REFERENCE ARM: only a streaming answer is parsed as it grows.
        var user = new ChatMessageItem { Role = "user", IsStreaming = true };
        user.Content = "**not parsed**";
        Assert.False(user.HasBlocks);
    }
}
