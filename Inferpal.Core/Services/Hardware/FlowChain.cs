namespace Inferpal.Services.Hardware;

/// <summary>
/// What one async flow holds, newest first — the value of an <see cref="AsyncLocal{T}"/>: a flow started from it sees its
/// links and adds its own without touching them.
/// </summary>
internal sealed record FlowChain<T>(T Item, FlowChain<T>? Next) where T : class
{
    /// <summary>
    /// <paramref name="item"/> on top of the links of <paramref name="chain"/> still <paramref name="held"/>, in their order:
    /// a loop that takes one hold per step (a bench, an arena) would otherwise grow the chain without end.
    /// </summary>
    internal static FlowChain<T> Push(T item, FlowChain<T>? chain, Func<T, bool> held) => new(item, Held(chain, held));

    private static FlowChain<T>? Held(FlowChain<T>? link, Func<T, bool> held)
    {
        if (link is null) return null;
        var next = Held(link.Next, held);
        if (!held(link.Item)) return next;
        return ReferenceEquals(next, link.Next) ? link : link with { Next = next };
    }
}
