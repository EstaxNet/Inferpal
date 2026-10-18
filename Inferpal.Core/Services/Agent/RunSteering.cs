using System.Security.Cryptography;
using Inferpal.Models;

namespace Inferpal.Services.Agent;

/// <summary>A note the user typed while a run worked: the front-end's id for it, and its words.</summary>
internal sealed record UserNote(string Id, string Text);

/// <summary>A note the model read, and how many tool calls the run had made when it read it.</summary>
internal sealed record AppliedNote(string Id, string Text, int AfterCalls);

/// <summary>
/// The notes the user types while a run works, handed to the run at its next boundary — once the tool calls in progress
/// are done, before the next request to the model —, where they are the last text the model reads before it acts again.
/// </summary>
/// <remarks>
/// Owned by the front-end, one per turn that has boundaries (the orchestrator, the basic loop with tools). Pushed to from
/// another thread than the run's — an RPC, the composer —, so every member takes the one lock.
/// ⚠ <see cref="Close"/> is atomic: a note pushed after the run's last boundary, while it returns, comes back with the
/// notes no boundary took — a note is never lost, and the front-end sends it as the next message.
/// </remarks>
internal sealed class RunSteering
{
    private readonly object _gate = new();
    private readonly List<UserNote> _pending = [];
    private readonly List<AppliedNote> _applied = [];
    private bool _closed;

    /// <summary>Called, outside the lock, for each note the run hands the model: the line on screen that says it was read.</summary>
    public Action<AppliedNote>? OnApplied { get; init; }

    /// <summary>
    /// The run's token in the note's marker. A file or a web page the run reads cannot know it, so a text that imitates
    /// the marker does not pass for the user. The approval prompt stays the boundary: this only keeps the model's reading
    /// honest.
    /// </summary>
    public string Token { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(2)).ToLowerInvariant();

    /// <summary>Adds a note for the run's next boundary; <c>false</c> once the run has ended (<see cref="Close"/>).</summary>
    public bool TryPush(UserNote note)
    {
        lock (_gate)
        {
            if (_closed) return false;
            _pending.Add(note);
            return true;
        }
    }

    /// <summary>A note waits for the run's next boundary.</summary>
    public bool HasPending
    {
        get { lock (_gate) return _pending.Count > 0; }
    }

    /// <summary>The notes the model read, in the order it read them.</summary>
    public IReadOnlyList<AppliedNote> Applied
    {
        get { lock (_gate) return _applied.ToList(); }
    }

    /// <summary>
    /// Ends the run's steering: later pushes are refused, and the notes no boundary took are returned — for the
    /// front-end to send as the next message, or to give back to the composer after a Stop.
    /// </summary>
    public IReadOnlyList<UserNote> Close()
    {
        lock (_gate)
        {
            _closed = true;
            var rest = _pending.ToList();
            _pending.Clear();
            return rest;
        }
    }

    /// <summary>
    /// At a boundary: adds the waiting notes to <paramref name="messages"/> as one user message, the last the model reads,
    /// and records them as read after <paramref name="afterCalls"/> tool calls. Returns how many it added.
    /// </summary>
    /// <remarks>A plain user message: the clients fold it into the tool result before it
    /// (<c>CoalesceConsecutiveRoles</c>), which strict chat templates require — the marker is what says who wrote it.</remarks>
    internal int AddTo(List<ChatMessageDto> messages, int afterCalls)
    {
        List<AppliedNote> taken;
        lock (_gate)
        {
            if (_pending.Count == 0) return 0;
            taken = _pending.Select(n => new AppliedNote(n.Id, n.Text, afterCalls)).ToList();
            _applied.AddRange(taken);
            _pending.Clear();
        }
        messages.Add(new ChatMessageDto("user", Prompting.ModelPrompts.UserNote(Token, taken.Select(n => n.Text)))
        {
            IsUserNote = true,
        });
        foreach (var note in taken) OnApplied?.Invoke(note);
        return taken.Count;
    }
}
