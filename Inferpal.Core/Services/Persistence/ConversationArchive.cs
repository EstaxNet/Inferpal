using System.IO;
using Inferpal.Config;

namespace Inferpal.Services.Persistence;

/// <summary>
/// The archive of a conversation being discarded (<c>/clear</c>, a new conversation, <c>/template</c>): on disk at once,
/// under its first question's words, then renamed after the title the model gives it.
/// </summary>
/// <remarks>
/// ⚠ The title costs a model call (up to 15 s, <see cref="SessionTitleGenerator"/>), and the auto-save slot was emptied
/// before it came back: an editor closed, or a host stopped, in those seconds lost the conversation — in no slot, under no
/// name. Written first under a name that needs no model, the archive exists before the slot is emptied; the title only
/// renames it.
/// </remarks>
internal static class ConversationArchive
{
    /// <summary>The name an archive is first written under: the date and the first question's words, free among the
    /// saved sessions.</summary>
    public static string FirstName(ConversationStore store, DateTime localNow, string firstQuestion) =>
        SessionManager.UniqueSessionName(
            SessionManager.SessionFileName(localNow, SessionManager.MakeSnippet(firstQuestion)), store.ListSessions());

    /// <summary>The first question of <paramref name="messages"/>, or nothing.</summary>
    public static string FirstQuestion(IEnumerable<SavedMessage> messages) =>
        messages.FirstOrDefault(SessionManager.IsQuestion)?.Content ?? string.Empty;

    /// <summary>
    /// Renames the archive <paramref name="name"/>, written at <paramref name="localNow"/>, after <paramref name="title"/>;
    /// its name — the first one when the title changes nothing or the rename fails (the archive stays where it is).
    /// </summary>
    public static string Retitle(ConversationStore store, string name, DateTime localNow, string title)
    {
        var wanted = SessionManager.SessionFileName(localNow, title);
        if (string.Equals(ConversationStore.StoredName(wanted), ConversationStore.StoredName(name), StringComparison.OrdinalIgnoreCase))
            return name;
        var target = SessionManager.UniqueSessionName(wanted, store.ListSessions());
        try
        {
            store.Rename(name, target);
            return target;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Diagnostics.Swallow("ConversationArchive.Retitle", ex);
            return name;
        }
    }

    /// <summary>
    /// Before a conversation that never read <paramref name="workspaceRoot"/>'s auto-save slot writes it for the first
    /// time: the conversation kept there is archived under its first question, as <c>/clear</c> archives, never written
    /// over. Returns the archive's name, or <c>null</c> when the slot holds no question.
    /// </summary>
    /// <remarks>⚠ Visual Studio builds its window before the solution is known: nothing is restored then, and the first
    /// question's save, bound to the solution that opened meanwhile, replaced that solution's conversation — no archive, no
    /// word. Every failure THROWS: a damaged slot as <see cref="UnreadableSessionException"/>, whose copy the store kept
    /// and whose message names it (the caller says it, then may write); any other (the file held, the disk full) for the
    /// caller to write nothing over the slot, and try again.</remarks>
    public static async Task<ArchivedSlot?> KeepUnreadSlotAsync(ConversationStore store, string? workspaceRoot, CancellationToken ct)
    {
        // Only the file the save is about to write: an older shared slot it falls back to is left as it is, not replaced.
        if (!File.Exists(store.AutoSaveFile(workspaceRoot))) return null;
        var kept = await store.LoadAutoSaveAsync(workspaceRoot, ct).ConfigureAwait(false);
        if (kept is null || !kept.Messages.Any(SessionManager.IsQuestion)) return null;
        var at = kept.SavedAt.ToLocalTime();
        var first = FirstQuestion(kept.Messages);
        var name = FirstName(store, at, first);
        await store.SaveAsync(name, kept.Messages, ct, workspaceRoot, state: kept.State with { CurrentName = null })
                   .ConfigureAwait(false);
        return new ArchivedSlot(name, at, first);
    }

    /// <summary>Asks the model for the title, then renames: what both editors run once the archive is on disk.</summary>
    public static async Task<string> RetitleAsync(ConversationStore store, string name, DateTime localNow, string firstQuestion,
                                                  IInferenceProvider client, InferpalConfig config)
    {
        var title = await SessionTitleGenerator.GenerateAsync(client, config, firstQuestion, CancellationToken.None)
                                               .ConfigureAwait(false);
        return Retitle(store, name, localNow, title);
    }
}

/// <summary>A conversation archived from an auto-save slot (<see cref="ConversationArchive.KeepUnreadSlotAsync"/>): its
/// name, when it was last saved, and its first question — what retitling it needs.</summary>
internal sealed record ArchivedSlot(string Name, DateTime SavedAtLocal, string FirstQuestion);
