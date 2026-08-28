using System.Windows;
using System.Windows.Controls;

namespace Inferpal.GhostText;

/// <summary>
/// In-process auto-scroll for the chat tool window.
///
/// <para>
/// The tool window is Remote UI: its XAML is instantiated inside devenv.exe where our
/// assembly types are unreachable, and WPF itself never scrolls a <see cref="ListBox"/>
/// when <c>SelectedItem</c> changes through a binding (no <c>BringIntoView</c> code path
/// exists in <c>Selector</c>/<c>ListBox</c>/<c>ListBoxItem</c>). Only in-process code can
/// therefore perform the actual scrolling. This class registers two global WPF class
/// handlers, both filtered to the chat list via its XAML <c>Tag</c>:
/// </para>
/// <list type="bullet">
///   <item><c>ListBoxItem.Selected</c> → <c>BringIntoView()</c> — makes the ViewModel's
///         anchor-alternation (<c>ScrollTarget</c>) an explicit "scroll to bottom" command
///         (fires on send / session load) and re-arms following.</item>
///   <item><c>ScrollViewer.ScrollChanged</c> → follow content growth during streaming
///         (the bubble grows token by token), unless the user has scrolled up.</item>
/// </list>
///
/// <para>
/// <b>Why a sticky <c>_following</c> flag, and why only an <i>upward</i> scroll clears it:</b>
/// <c>ScrollToEnd()</c> is asynchronous. During fast streaming the next token grows the
/// extent before the previous <c>ScrollToEnd</c> has committed, so by the time that scroll's
/// own <c>ScrollChanged</c> event fires the offset already lags the (now larger) extent — an
/// <c>offset >= bottom - threshold</c> check reads false even though we are faithfully
/// following. Re-evaluating the flag on <i>any</i> pure scroll therefore lets our own
/// catch-up scroll switch following off, which is precisely why auto-scroll died "after a
/// few lines". The key invariant: our auto-scroll <b>only ever moves the view down</b>, so
/// the only gesture that should stop following is the user scrolling <b>up</b>. A downward
/// pure scroll (our <c>ScrollToEnd</c>, or the user dragging back to the bottom) may only
/// <i>re-arm</i> following, never clear it — which makes the streaming race harmless.
/// </para>
/// </summary>
internal static class ChatAutoScroller
{
    /// <summary>Must match the literal <c>Tag</c> set on the chat ListBox in
    /// <c>InferpalToolWindowContent.xaml</c>.</summary>
    internal const string ChatListTag = "InferpalChatList";

    /// <summary>How close to the bottom (px) still counts as "pinned to bottom".</summary>
    private const double BottomThresholdPx = 50;

    private static bool _registered;

    /// <summary>
    /// Whether the view should track new content. Sticky: set false only when the user
    /// scrolls away from the bottom, set true again when they scroll back (or on send /
    /// session load). Starts true so a fresh window follows the first response.
    /// </summary>
    private static bool _following = true;

    /// <summary>Registers the class handlers once. Must be called on the VS UI thread.</summary>
    internal static void Initialize()
    {
        if (_registered) return;
        _registered = true;

        EventManager.RegisterClassHandler(typeof(ListBoxItem), ListBoxItem.SelectedEvent,
            new RoutedEventHandler(OnItemSelected), handledEventsToo: true);
        EventManager.RegisterClassHandler(typeof(ScrollViewer), ScrollViewer.ScrollChangedEvent,
            new ScrollChangedEventHandler(OnScrollChanged), handledEventsToo: true);
    }

    private static void OnItemSelected(object sender, RoutedEventArgs e)
    {
        if (sender is not ListBoxItem item) return;
        if (ItemsControl.ItemsControlFromItemContainer(item) is not ListBox list) return;
        if (!IsChatList(list)) return;

        // An explicit "scroll to bottom" command (send / session load): re-arm following.
        _following = true;
        item.BringIntoView();
    }

    private static void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        // Cheap bail-out before the type/Tag filters: most events neither grow the content
        // nor move the view (this handler fires for every ScrollViewer in devenv).
        var grew = e.ExtentHeightChange > 0;
        if (!grew && e.VerticalChange == 0) return;

        if (sender is not ScrollViewer viewer) return;
        if (viewer.TemplatedParent is not ListBox list || !IsChatList(list)) return;

        if (grew)
        {
            // Content grew (a streaming token, a new bubble). Follow purely from the sticky
            // flag — never from the lagging offset, which is unreliable mid-stream.
            if (_following) viewer.ScrollToEnd();
            return;
        }

        // Pure scroll. Our auto-scroll only ever moves the view *down*, so a downward change
        // is either our own catch-up ScrollToEnd (whose offset may lag a still-growing extent
        // mid-stream) or the user dragging back toward the bottom — neither should ever clear
        // following; it may only re-arm it. Only an *upward* scroll means the user deliberately
        // left the bottom to read history, and that is the sole signal that stops following.
        if (e.VerticalChange < 0)
            _following = IsPinnedToBottom(viewer.ScrollableHeight, viewer.VerticalOffset);
        else if (IsPinnedToBottom(viewer.ScrollableHeight, viewer.VerticalOffset))
            _following = true;
    }

    /// <summary>
    /// True when the settled view sits at (or within the threshold of) the bottom.
    /// Used to re-evaluate the sticky follow flag on a pure scroll event.
    /// </summary>
    internal static bool IsPinnedToBottom(double scrollableHeight, double verticalOffset)
        => scrollableHeight - verticalOffset <= BottomThresholdPx;

    private static bool IsChatList(ListBox list) => Equals(list.Tag, ChatListTag);
}
