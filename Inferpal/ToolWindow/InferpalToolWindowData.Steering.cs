using System.Collections.ObjectModel;
using System.Runtime.Serialization;
using Inferpal.Localization;
using Inferpal.Models;
using Microsoft.VisualStudio.Extensibility.UI;

namespace Inferpal.ToolWindow;

internal partial class InferpalToolWindowData
{
    #region Notes and the queue (what is typed while a turn runs)

    // During a turn, Enter is a NOTE for the run (RunSteering: read before its next step) and Ctrl+Enter a message sent
    // once the turn ends — never a Stop: Stop is the button. A note no step can read goes to the queue, never lost.

    /// <summary>The notes of the turn running now; null for a turn with no step to read one before (a code action, a
    /// slash command, a chat without tools). VM context.</summary>
    private RunSteering? _steering;
    private int _noteSeq;
    /// <summary>The running turn failed (the backend refused, a run that failed): its queue goes back to the input box.</summary>
    private bool _turnFailed;

    /// <summary>
    /// For the exception filter of a turn's owner (<c>catch (Exception ex) when (… FailsTheTurn())</c>): marks the turn
    /// failed while the exception goes on — it is never caught here —, before the owner's <c>finally</c> releases the turn.
    /// </summary>
    private bool FailsTheTurn()
    {
        _turnFailed = true;
        return false;
    }

    /// <summary>A message waiting for the turn to end: its words, and — a context-menu action — its model and code.</summary>
    private sealed record QueuedMessage(string Text, string? Model, List<AttachmentItem> Attachments);
    private readonly List<QueuedMessage> _queue = [];

    private bool _hasQueuedMessages;
    private string _queueTitle = string.Empty;
    private string _queueRemoveLabel = string.Empty;
    [DataMember] public ObservableCollection<QueuedMessageItem> QueuedMessages { get; } = [];
    [DataMember] public bool   HasQueuedMessages { get => _hasQueuedMessages; set => SetProperty(ref _hasQueuedMessages, value); }
    [DataMember] public string QueueTitle        { get => _queueTitle;        set => SetProperty(ref _queueTitle,        value); }
    [DataMember] public string QueueRemoveLabel  { get => _queueRemoveLabel;  set => SetProperty(ref _queueRemoveLabel,  value); }

    /// <summary>Ctrl+Enter: during a turn, the text waits for it to end; with none running, an ordinary send.</summary>
    [DataMember] public AsyncCommand QueueCommand { get; private set; } = null!;

    private void InitSteering() => QueueCommand = new AsyncCommand(QueueAsync);

    private async Task QueueAsync(object? _, CancellationToken ct)
    {
        var sendNow = false;
        await RunOnVMContextAsync(() =>
        {
            var text = Prompt.Trim();
            if (text.Length == 0) return;
            if (!IsLoading && !_sendStarting)
            {
                sendNow = true;
                return;
            }
            // The chips in the composer go with the message they were attached to.
            List<AttachmentItem> attachments = [..Attachments];
            Prompt = string.Empty;
            Attachments.Clear();
            HasAttachments = false;
            Enqueue(new QueuedMessage(text, null, attachments));
        });
        if (sendNow) await SendAsync(null, ct);
    }

    /// <summary>
    /// Enter during a turn with text: a note for the run's next step, drawn as waiting (<c>true</c>). A turn that will read
    /// none (a code action, a slash command, a chat without tools) queues it instead (<c>false</c>) — and a slash command
    /// is always queued: it is a command to run, never words for the model. VM context.
    /// </summary>
    private bool Steer(string text)
    {
        if (_steering is { } steering && !Services.SlashCommandRouter.LooksLikeCommand(text))
        {
            var id = "note-" + ++_noteSeq;
            var note = ChatMessageItem.NoteMsg(id, text);
            // Drawn before the run can read it, so the note precedes the step it changes.
            InsertThemed(note);
            ScrollToBottom();
            if (steering.TryPush(new UserNote(id, text))) return true;
            Messages.Remove(note);
        }
        Enqueue(new QueuedMessage(text, null, []));
        return false;
    }

    /// <summary>The run read a note: drawn as read, and saved as one. Called from the run's thread.</summary>
    private void OnNoteApplied(AppliedNote note) => Post(() =>
        Messages.FirstOrDefault(m => m.NoteId == note.Id)?.MarkNoteRead(note.AfterCalls));

    private void Enqueue(QueuedMessage message)
    {
        _queue.Add(message);
        RefreshQueue();
    }

    private void RefreshQueue()
    {
        // No SelectedItem on this list: rebuilt whole (the rule for bound selections does not apply).
        QueuedMessages.Clear();
        for (var i = 0; i < _queue.Count; i++)
        {
            var queued = _queue[i];
            QueuedMessages.Add(new QueuedMessageItem(queued.Text, () => Post(() =>
            {
                _queue.Remove(queued);
                RefreshQueue();
            })));
        }
        HasQueuedMessages = _queue.Count > 0;
    }

    /// <summary>
    /// The end of a turn's notes, before the turn is released: those the model read join the question in the history
    /// (<c>ChatTurnPolicy.WithUserNotes</c>) — the next question, a reload and Regenerate see the correction the run
    /// followed —; those no step read leave the turn, the next message after a turn that ended normally, back in the input
    /// box otherwise, said. VM context.
    /// </summary>
    private void SettleNotes(RunSteering? steering, bool normal)
    {
        if (steering is null) return;
        var unread = steering.Close();
        if (ReferenceEquals(_steering, steering)) _steering = null;
        var read = steering.Applied;
        foreach (var n in read)
            Messages.FirstOrDefault(m => m.NoteId == n.Id)?.MarkNoteRead(n.AfterCalls);
        if (read.Count > 0)
        {
            var at = _history.FindLastIndex(m => m.Role == "user" && !m.IsScaffolding);
            // A question at the very start of the history (one restored without its system prompt) is a question too.
            if (at >= 0)
            {
                _history[at] = _history[at] with
                {
                    Content = Services.Agent.ChatTurnPolicy.WithUserNotes(_history[at].Content ?? string.Empty, read.Select(n => n.Text)),
                };
                _lastPromptTokens = Services.Agent.AgentOrchestrator.EstimateTokens(_history);
            }
        }
        if (unread.Count == 0) return;
        foreach (var n in unread)
            if (Messages.FirstOrDefault(m => m.NoteId == n.Id) is { } bubble) Messages.Remove(bubble);
        if (normal)
        {
            _queue.InsertRange(0, unread.Select(n => new QueuedMessage(n.Text, null, [])));
            RefreshQueue();
            return;
        }
        GiveBack(unread.Select(n => n.Text));
        InsertThemed(ChatMessageItem.NoticeMsg(Strings.NoteNotRead));
    }

    /// <summary>Text back in the input box, in front of what is typed there: the draft is the user's too. VM context.</summary>
    private void GiveBack(IEnumerable<string> texts)
    {
        var back = string.Join("\n\n", texts);
        Prompt = string.IsNullOrWhiteSpace(Prompt) ? back : back + "\n\n" + Prompt;
    }

    /// <summary>
    /// The messages typed for the queue go back to the input box with their chips — the chips went with them, and given
    /// back alone their words asked about code they no longer carry. An editor action (its model and code taken at the
    /// click) stays in the queue: as words in the input box it would become an ordinary agent turn without its code.
    /// Returns whether any editor action stayed. VM context.
    /// </summary>
    private bool GiveQueueBack(IReadOnlyList<QueuedMessage> messages)
    {
        var typed = messages.Where(q => q.Model is null).ToList();
        GiveBack(typed.Select(q => q.Text));
        foreach (var chip in typed.SelectMany(q => q.Attachments))
            if (!Attachments.Contains(chip)) Attachments.Add(chip);
        HasAttachments = Attachments.Count > 0;
        var actions = messages.Where(q => q.Model is not null).ToList();
        _queue.RemoveAll(q => typed.Contains(q));
        foreach (var action in actions.Where(a => !_queue.Contains(a)).Reverse()) _queue.Insert(0, action);
        RefreshQueue();
        return actions.Count > 0;
    }

    /// <summary>
    /// A turn was released: the next queued message goes now when it ended normally. After a Stop or a failure nothing is
    /// sent on its own — the user stopped for a reason: the queue goes back to the input box (its editor actions stay in
    /// it), and that is said. VM context.
    /// </summary>
    private void AfterTurn(bool normal)
    {
        if (_queue.Count == 0) return;
        if (!normal)
        {
            var actionsKept = GiveQueueBack([.._queue]);
            InsertThemed(ChatMessageItem.NoticeMsg(actionsKept ? Strings.QueueActionsKept : Strings.QueueGivenBack));
            return;
        }
        var next = _queue[0];
        _queue.RemoveAt(0);
        RefreshQueue();
        _ = SendQueuedAsync(next);
    }

    /// <summary>The claim of the send being started: a send's <c>finally</c> releases <c>_sendStarting</c> only while
    /// the claim is still its own — the turn it ended can have started a queued send that claimed after it.</summary>
    private int _sendClaim;

    /// <summary>Sends a queued message as its own turn — claimed like a typed send, and put back first if one started.</summary>
    private async Task SendQueuedAsync(QueuedMessage message)
    {
        var claim = 0;
        await RunOnVMContextAsync(() =>
        {
            if (_sendStarting || IsLoading)
            {
                _queue.Insert(0, message);
                RefreshQueue();
                return;
            }
            // ⚠ Refused before its turn (the backend unreachable): back to the input box, never dropped — removed from the
            // queue already, it was lost with the refusal.
            if (!Services.SlashCommandRouter.LooksLikeCommand(message.Text) && !_isBackendReachable)
            {
                GiveQueueBack([message]);
                InsertThemed(ChatMessageItem.NoticeMsg(UnreachableText()));
                ScrollToBottom();
                return;
            }
            _sendStarting = true;
            claim = ++_sendClaim;
        });
        if (claim == 0) return;
        try
        {
            if (Services.SlashCommandRouter.LooksLikeCommand(message.Text))
            {
                PinWorkspaceRoot();
                await HandleSlashCommandAsync(message.Text, CancellationToken.None);
            }
            else
            {
                await SendCoreAsync(message.Text, message.Model, message.Attachments, CancellationToken.None, clearPrompt: false);
            }
        }
        finally
        {
            await RunOnVMContextAsync(() => { if (_sendClaim == claim) _sendStarting = false; });
        }
    }

    #endregion
}

/// <summary>A message waiting for the turn to end, as the queue under the composer shows it: its words, and Remove.</summary>
[DataContract]
internal sealed class QueuedMessageItem(string text, Action remove)
{
    [DataMember] public string Text { get; } = text;
    [DataMember] public AsyncCommand RemoveCommand { get; } = new((_, _) => { remove(); return Task.CompletedTask; });
}
