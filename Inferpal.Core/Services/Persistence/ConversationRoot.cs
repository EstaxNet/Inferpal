namespace Inferpal.Services.Persistence;

/// <summary>
/// The workspace a conversation belongs to: the one its auto-save slot, its archive and its branches are written for.
/// </summary>
/// <remarks>
/// ⚠ Not the workspace open at the time of writing. Visual Studio keeps the conversation on screen when another solution
/// is opened; written by the root of the moment, a conversation begun in solution A went into B's slot — B's own last
/// conversation overwritten, never archived — and <c>/clear</c> emptied B's slot, a conversation never shown in this
/// window, while the one just discarded came back at A's next start.
/// <para>A restored conversation belongs to the workspace it was restored in, a rootless one included (a window without
/// a solution). An empty conversation belongs nowhere yet: it is bound where its first save happens.</para>
/// </remarks>
public sealed class ConversationRoot
{
    /// <summary><c>null</c>: not bound yet. Empty: bound to no workspace.</summary>
    private string? _root;

    /// <summary>Whether the conversation belongs to a workspace yet — restored, or saved once. One that does not was
    /// never written to any slot: discarding it leaves every slot as it is.</summary>
    public bool IsBound => _root is not null;

    /// <summary>Whether the window ever held a conversation bound somewhere — restored, saved, or discarded since. A window
    /// that never did is the only one a late restore may fill: one emptied by <c>/clear</c> was emptied on purpose.</summary>
    public bool EverBound { get; private set; }

    /// <summary>The conversation restored into the window now belongs to <paramref name="workspaceRoot"/>.</summary>
    public void Restored(string? workspaceRoot)
    {
        _root = workspaceRoot ?? string.Empty;
        EverBound = true;
    }

    /// <summary>The conversation was discarded: the next one belongs where it is first saved.</summary>
    public void Discarded() => _root = null;

    /// <summary>
    /// The workspace to write the conversation for — <c>null</c> for none — binding a conversation not bound yet to
    /// <paramref name="current"/>, the workspace open now.
    /// </summary>
    public string? For(string? current)
    {
        _root ??= current ?? string.Empty;
        EverBound = true;
        return _root.Length == 0 ? null : _root;
    }
}
