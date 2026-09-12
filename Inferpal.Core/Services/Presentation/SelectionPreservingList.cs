namespace Inferpal.Services.Presentation;

/// <summary>
/// Updates <b>in place</b> the collection of a dropdown whose selection is bound, without ever
/// removing a value a bound property holds.
/// </summary>
/// <remarks>
/// ⚠ A <c>Selector</c> writes <c>null</c> into its bound property as soon as the selected item
/// leaves the collection — through <c>Clear()</c> or through the last <c>RemoveAt</c> alike. Under
/// Remote UI that write crosses the boundary and comes back <b>after</b> the pass that caused it:
/// removing a value and adding it back in the same pass does not protect it, the <c>null</c>
/// arrives once it is back and overwrites it. The only guarantee is that it never leaves.
/// </remarks>
internal static class SelectionPreservingList
{
    /// <param name="items">The collection bound to <c>ItemsSource</c>.</param>
    /// <param name="listed">What the backend lists, in its order.</param>
    /// <param name="held">The values of the properties bound to this collection; empty ones are ignored.</param>
    /// <param name="leadingEmpty">The list starts with an empty entry, which is never removed.</param>
    public static void Sync(IList<string> items, IReadOnlyCollection<string> listed,
                            IEnumerable<string?> held, bool leadingEmpty = false)
    {
        var kept = held.Where(h => !string.IsNullOrEmpty(h)).Select(h => h!)
                       .Distinct(StringComparer.Ordinal).ToList();
        var keep = new HashSet<string>(listed, StringComparer.Ordinal);
        keep.UnionWith(kept);

        if (leadingEmpty && (items.Count == 0 || items[0] != string.Empty))
            items.Insert(0, string.Empty);

        var first = leadingEmpty ? 1 : 0;
        for (var i = items.Count - 1; i >= first; i--)
            if (!keep.Contains(items[i]))
                items.RemoveAt(i);

        foreach (var model in listed)
            if (!items.Contains(model)) items.Add(model);

        // A configured model the backend does not list (unloaded, backend unreachable, another
        // server) stays visible: otherwise the list has nothing to select and the next save no
        // longer knows what was configured.
        foreach (var value in kept)
            if (!items.Contains(value)) items.Add(value);
    }
}
