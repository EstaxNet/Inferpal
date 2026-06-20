using Inferpal.Services;
using Xunit;

namespace Inferpal.Tests;

// The ↑/↓ navigation arithmetic (most-recent-first, draft stash/restore, clamping) is exactly the
// off-by-one-prone logic that lived untested inside the tool-window VM. These pin its behaviour.
public class PromptHistoryNavigatorTests
{
    private static PromptHistoryNavigator Seeded(params string[] entries)
    {
        var nav = new PromptHistoryNavigator(max: 50);
        nav.Load(entries);
        return nav;
    }

    [Fact]
    public void Up_WalksFromMostRecentToOldest_ThenClamps()
    {
        var nav = Seeded("first", "second", "third"); // chronological; "third" is newest

        Assert.Equal("third",  nav.Up("draft"));
        Assert.Equal("second", nav.Up("draft"));
        Assert.Equal("first",  nav.Up("draft"));
        Assert.Equal("first",  nav.Up("draft")); // clamps at the oldest
    }

    [Fact]
    public void Down_RestoresDraft_AfterSteppingPastNewest()
    {
        var nav = Seeded("a", "b");

        Assert.Equal("b", nav.Up("my draft")); // stashes "my draft"
        Assert.Equal("a", nav.Up("ignored"));  // draft only stashed on the FIRST step
        Assert.Equal("b", nav.Down("x"));
        Assert.Equal("my draft", nav.Down("x")); // back to the live draft
        Assert.False(nav.CanDown);               // no longer navigating
    }

    [Fact]
    public void Down_WhenNotNavigating_IsNoOp()
    {
        var nav = Seeded("a");
        Assert.Equal("current", nav.Down("current"));
        Assert.False(nav.IsNavigating);
    }

    [Fact]
    public void Up_OnEmptyHistory_ReturnsCurrentTextAndDoesNotNavigate()
    {
        var nav = new PromptHistoryNavigator();
        Assert.Equal("typed", nav.Up("typed"));
        Assert.False(nav.CanUp);
        Assert.False(nav.IsNavigating);
    }

    [Fact]
    public void CanUpAndCanDown_ReflectNavigationState()
    {
        var nav = Seeded("a", "b");
        Assert.True(nav.CanUp);
        Assert.False(nav.CanDown);    // not navigating yet

        nav.Up("d");
        Assert.True(nav.CanUp);
        Assert.True(nav.CanDown);     // now navigating
        Assert.True(nav.IsNavigating);
    }

    [Fact]
    public void Append_DedupsConsecutiveDuplicates_AndReportsChange()
    {
        var nav = new PromptHistoryNavigator();
        Assert.True(nav.Append("hello"));
        Assert.False(nav.Append("hello")); // identical top entry → no change, no persist
        Assert.True(nav.Append("world"));
        Assert.Equal(new[] { "hello", "world" }, nav.Entries);
    }

    [Fact]
    public void Append_ResetsNavigation()
    {
        var nav = Seeded("a", "b");
        nav.Up("d");
        Assert.True(nav.IsNavigating);

        nav.Append("c");
        Assert.False(nav.IsNavigating);
    }

    [Fact]
    public void Append_EvictsOldestPastMax()
    {
        var nav = new PromptHistoryNavigator(max: 2);
        nav.Append("a");
        nav.Append("b");
        nav.Append("c");
        Assert.Equal(new[] { "b", "c" }, nav.Entries);
    }

    [Fact]
    public void Load_SkipsBlankEntries()
    {
        var nav = new PromptHistoryNavigator();
        nav.Load(new[] { "a", "  ", "", "b" });
        Assert.Equal(new[] { "a", "b" }, nav.Entries);
    }

    [Fact]
    public void Load_KeepsOnlyTheNewestMaxEntries()
    {
        var nav = new PromptHistoryNavigator(max: 2);
        nav.Load(new[] { "x", "y", "z" }); // only the last 2 survive
        Assert.Equal(new[] { "y", "z" }, nav.Entries);
    }
}
