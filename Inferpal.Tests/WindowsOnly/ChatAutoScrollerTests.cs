using Inferpal.GhostText;
using Xunit;

namespace Inferpal.Tests;

public class ChatAutoScrollerTests
{
    // IsPinnedToBottom(scrollableHeight, verticalOffset) — re-evaluated on a settled
    // (pure) scroll to decide whether streaming should keep following.

    [Fact]
    public void UnscrollableContent_IsPinned()
    {
        // Content fits the viewport (nothing to scroll): always considered at the bottom.
        Assert.True(ChatAutoScroller.IsPinnedToBottom(0, 0));
    }

    [Fact]
    public void ExactlyAtBottom_IsPinned()
    {
        Assert.True(ChatAutoScroller.IsPinnedToBottom(1000, 1000));
    }

    [Fact]
    public void WithinThreshold_IsPinned()
    {
        // 30 px above the bottom — still counts as pinned (50 px threshold).
        Assert.True(ChatAutoScroller.IsPinnedToBottom(1000, 970));
    }

    [Fact]
    public void ScrolledUp_IsNotPinned()
    {
        // 300 px above the bottom: the user is reading history.
        Assert.False(ChatAutoScroller.IsPinnedToBottom(1000, 700));
    }
}
