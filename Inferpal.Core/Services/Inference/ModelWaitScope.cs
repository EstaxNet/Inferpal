namespace Inferpal.Services.Inference;

/// <summary>
/// Where a model call says its wait (<see cref="PrefillWait"/>) when its caller names no place: the scope a front-end
/// opens for a turn or a command, so that every call made under it — a compaction, a review, a summary — is said where
/// the person is waiting, without each call site wiring it.
/// </summary>
/// <remarks>
/// ⚠ A caller that passes its own <c>onWaiting</c> keeps it: the scope is only the default. ⚠ Work that outlives the
/// gesture (a task started from it and left running) carries the scope with it — a closed scope says nothing more, so a
/// late line never lands in a turn that has ended.
/// </remarks>
internal static class ModelWaitScope
{
    private static readonly AsyncLocal<Scope?> _current = new();

    /// <summary>Where a call made now says its wait, or <c>null</c> (no scope, or a closed one).</summary>
    public static Action<string?>? Current => _current.Value is { IsOpen: true } scope ? scope.Say : null;

    /// <summary>
    /// Opens a scope for the calls made from here, in this flow and the tasks it starts, until it is disposed. Opened
    /// in a synchronous method, it holds for its async caller until that caller returns.
    /// </summary>
    /// <param name="onWaiting">The line, or <c>null</c> when it gives its place back; called under
    /// <see cref="PrefillWait"/>'s lock, so it must not block.</param>
    public static IDisposable Open(Action<string?> onWaiting) => _current.Value = new Scope(onWaiting, _current.Value);

    /// <remarks>⚠ Work that outlives the turn keeps the scope object in its captured context: once closed it lets go of
    /// its line (the front-end behind it) and of the scopes it covered, or an ended session could never be collected.
    /// ⚠ Closing and saying share a lock: once <see cref="Dispose"/> returns, no line is said.</remarks>
    private sealed class Scope(Action<string?> onWaiting, Scope? previous) : IDisposable
    {
        private readonly object _gate = new();
        private Action<string?>? _onWaiting = onWaiting;
        private Scope? _previous = previous;

        public bool IsOpen => Volatile.Read(ref _onWaiting) is not null;

        public void Say(string? line)
        {
            lock (_gate) _onWaiting?.Invoke(line);
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (ReferenceEquals(_current.Value, this)) _current.Value = _previous;
                _onWaiting = null;
                _previous = null;
            }
        }
    }
}
