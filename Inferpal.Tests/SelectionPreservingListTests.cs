using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Inferpal.Services.Presentation;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A selected value <b>never</b> leaves the collection of its dropdown, not even for the duration of
/// a pass.
/// </summary>
/// <remarks>
/// ⚠ These tests observe the collection's <b>events</b>, not its final state. Removing the value
/// and adding it back yields an identical final state — which is exactly what the model refresh
/// did — and yet it is the removal that makes the Selector write <c>null</c>. A test on the final
/// contents would be green on the defect.
/// </remarks>
public class SelectionPreservingListTests
{
    private static List<string> Removed(ObservableCollection<string> items)
    {
        var removed = new List<string>();
        items.CollectionChanged += (_, e) =>
        {
            if (e.Action is NotifyCollectionChangedAction.Remove or NotifyCollectionChangedAction.Reset
                && e.OldItems is not null)
                removed.AddRange(e.OldItems.Cast<string>());
            if (e.Action == NotifyCollectionChangedAction.Reset) removed.Add("<reset>");
        };
        return removed;
    }

    [Fact]
    public void AConfiguredModelTheBackendDoesNotList_NeverLeavesTheCollection()
    {
        // The reported state: an Ollama-style model name configured, an LM Studio backend that does
        // not list it.
        var items = new ObservableCollection<string> { "", "qwen3-coder:latest", "old-model" };
        var removed = Removed(items);

        SelectionPreservingList.Sync(items, ["qwen/qwen3-coder-30b"],
                                     held: ["qwen3-coder:latest", null, ""], leadingEmpty: true);

        Assert.DoesNotContain("qwen3-coder:latest", removed);
        // Witness: the pass does remove what is neither listed nor held. Without it, "nothing held
        // was removed" would also be true of a pass that removes nothing at all.
        Assert.Contains("old-model", removed);
        Assert.Equal(["", "qwen/qwen3-coder-30b", "qwen3-coder:latest"], items);
    }

    [Fact]
    public void AnUnreachableBackend_EmptiesNothingThatIsHeld()
    {
        var items = new ObservableCollection<string> { "llama3", "mistral" };
        var removed = Removed(items);

        SelectionPreservingList.Sync(items, [], held: ["llama3"]);

        Assert.DoesNotContain("llama3", removed);
        Assert.Equal(["llama3"], items);
    }

    [Fact]
    public void TheLeadingEmptyEntry_IsInsertedOnce_AndNeverRemoved()
    {
        var items = new ObservableCollection<string>();
        SelectionPreservingList.Sync(items, ["a"], held: [], leadingEmpty: true);
        SelectionPreservingList.Sync(items, ["a", "b"], held: [], leadingEmpty: true);

        Assert.Equal(["", "a", "b"], items);
    }

    [Fact]
    public void AValueHeldByTwoProperties_IsAddedOnce()
    {
        var items = new ObservableCollection<string>();
        SelectionPreservingList.Sync(items, ["a"], held: ["x", "x"]);

        Assert.Equal(["a", "x"], items);
    }

    [Fact]
    public void ANewEntry_IsInsertedAtItsListedPosition_NotAppended()
    {
        // Sessions: newest first. A session archived while another one is selected must arrive AT
        // THE TOP, without the selected one leaving the list.
        var items = new ObservableCollection<string> { "2026-09-11_1200_b", "2026-09-10_0900_a" };
        var removed = Removed(items);

        SelectionPreservingList.Sync(items, ["2026-09-12_1800_c", "2026-09-11_1200_b", "2026-09-10_0900_a"],
                                     held: ["2026-09-10_0900_a"]);

        Assert.Empty(removed);
        Assert.Equal(["2026-09-12_1800_c", "2026-09-11_1200_b", "2026-09-10_0900_a"], items);
    }
}
