using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Inferpal.Localization;
using Inferpal.Models;

namespace Inferpal.Services.Persistence;

/// <summary>
/// Persists and retrieves named chat sessions as JSON files under
/// <c>%AppData%/Inferpal/sessions/</c>.
/// </summary>
/// <remarks>
/// A session file contains the <b>display</b> transcript — role, text, tool name, timestamp — and
/// nothing else. ⚠ Not the API history: no <c>tool_calls</c>, no arguments, no call ids, so a
/// reloaded session does not resume exactly where it left off. What gets rebuilt on load, and how,
/// lives in <see cref="SessionManager.BuildRestoredHistory"/>.
/// The special name <c>"last_session"</c> is reserved for the auto-save slot.
/// </remarks>
internal class ConversationStore
{
    private static readonly string _defaultDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Inferpal", "sessions");

    /// <summary>
    /// Redirects the session folder in tests. Every other store here has one; without it the test
    /// suite reads and writes the developer's real sessions — the same mistake already paid for
    /// once with the configuration file.
    /// </summary>
    internal static string? OverrideDirForTests;

    private static string _dir => OverrideDirForTests ?? _defaultDir;

    private readonly string? _instanceDir;

    /// <param name="directory">
    /// Folder this instance reads and writes; <c>null</c> = the shared one.
    /// </param>
    /// <remarks>
    /// ⚠ <b>A test that needs a folder of its own uses THIS, never <see cref="OverrideDirForTests"/>.</b>
    /// That static belongs to the whole suite (<c>TestConfigIsolation</c> points it at one per-process
    /// folder), so repointing it from a class makes every other class read the wrong folder for as
    /// long as that class lives — and a <c>Dispose</c> that sets it back to <c>null</c> sends the
    /// rest of the suite at the developer's real <c>%AppData%</c>.
    /// </remarks>
    public ConversationStore(string? directory = null) => _instanceDir = directory;

    /// <summary>Where this instance works.</summary>
    private string Dir => _instanceDir ?? _dir;

    private static readonly JsonSerializerOptions _opts = new()
    {
        WriteIndented          = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// Saves a named session (UI messages + API history).
    /// <param name="parent">Session this one was forked from (<c>/branch</c>); null for a root session.</param>
    /// <param name="forkTurn">Turn the fork happened at, meaningful only with <paramref name="parent"/>.</param>
    /// <param name="workspaceRoot">Workspace the conversation belongs to; recorded for the auto-save slot.</param>
    /// <param name="currentName">The named session the conversation lives in; recorded for the auto-save slot.</param>
    /// <param name="templateSuffix">The <c>/template</c> mode the conversation is held in, restored with it.</param>
    public async Task SaveAsync(string sessionName, IEnumerable<SavedMessage> messages, CancellationToken ct,
                                string? parent = null, int? forkTurn = null, string? workspaceRoot = null,
                                string? currentName = null, string? templateSuffix = null)
    {
        var file = sessionName == AutoSaveSlot ? AutoSaveFile(workspaceRoot) : SessionPath(sessionName);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var payload = new SessionData(DateTime.UtcNow, messages.ToList(), parent, forkTurn,
                                      string.IsNullOrWhiteSpace(workspaceRoot) ? null : workspaceRoot,
                                      string.IsNullOrWhiteSpace(currentName) ? null : currentName,
                                      string.IsNullOrWhiteSpace(templateSuffix) ? null : templateSuffix);

        // Write-then-rename: a crash (or a full disk) mid-write must not leave a truncated
        // session behind. It matters more since /branch rewrites the parent file on every fork —
        // the interrupted save would be of the conversation the user is keeping. Via AtomicFile,
        // NOT a hand-rolled fixed ".tmp": both front-ends share %AppData% and auto-save
        // last_session.json every turn, and a fixed staging name turns those concurrent writers
        // into a collision.
        await AtomicFile.WriteAllTextAsync(file, JsonSerializer.Serialize(payload, _opts), ct);
    }

    /// Auto-saves the current session to "last_session.json".
    public Task AutoSaveAsync(IEnumerable<SavedMessage> messages, CancellationToken ct, string? workspaceRoot = null,
                              string? currentName = null, string? templateSuffix = null) =>
        SaveAsync("last_session", messages, ct, workspaceRoot: workspaceRoot, currentName: currentName,
                  templateSuffix: templateSuffix);

    /// <summary>
    /// Empties the auto-save slot when it holds THIS workspace's conversation — the one the user has just discarded
    /// (<c>/clear</c>, a new conversation). Another workspace's conversation in the slot is not this one's to empty.
    /// </summary>
    /// <remarks>
    /// ⚠ The slot is what comes back when the editor starts — and in VS Code whenever its host restarts (a setting, a
    /// crash), the blank screen reading as "nothing to keep". Left full, the conversation just discarded comes back
    /// there. It is archived under a name by the adapter before it is cleared.
    /// </remarks>
    /// <summary>
    /// <see cref="ForgetAutoSaveAsync"/>, or — when the slot could not be emptied — the notice that says so.
    /// </summary>
    /// <remarks>⚠ Swallowed, a slot left full brought the conversation just discarded back at the next start of either
    /// editor, with nothing said: the one gesture meant to get rid of it had silently failed.</remarks>
    public async Task<string?> ForgetAutoSaveOrSayAsync(string? workspaceRoot, CancellationToken ct)
    {
        try
        {
            await ForgetAutoSaveAsync(workspaceRoot, ct).ConfigureAwait(false);
            return null;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Diagnostics.Swallow("ConversationStore.ForgetAutoSave", ex);
            return Strings.AutoSaveNotForgotten(Diagnostics.RootMessage(ex));
        }
    }

    public async Task ForgetAutoSaveAsync(string? workspaceRoot, CancellationToken ct)
    {
        // Emptied here, the workspace's own slot also stops the shared one of older versions from being read for it.
        var slot = await LoadAutoSaveAsync(workspaceRoot, ct).ConfigureAwait(false);
        if (slot is null || slot.Messages.Count == 0) return;
        await AutoSaveAsync([], ct, workspaceRoot).ConfigureAwait(false);
    }

    /// <summary>The name the editors save and load the auto-save slot under: "the last conversation" of a workspace.</summary>
    internal const string AutoSaveSlot = "last_session";

    /// <summary>
    /// The last conversation of <paramref name="workspaceRoot"/>: its own slot, or — for a workspace that has none yet —
    /// the single slot older versions shared, when it is this workspace's.
    /// </summary>
    /// <remarks>
    /// ⚠ The slot is per workspace. Shared by every window of both editors, it held the conversation of whichever wrote
    /// last: two solutions open side by side, and the first one closed found nothing at its next start — its
    /// conversation overwritten by the other's, never archived.
    /// </remarks>
    /// <exception cref="UnreadableSessionException">The file is damaged: a copy is kept, and the message says where.</exception>
    public async Task<SessionData?> LoadAutoSaveAsync(string? workspaceRoot, CancellationToken ct)
    {
        var own = AutoSaveFile(workspaceRoot);
        if (File.Exists(own)) return await LoadFileAsync(own, AutoSaveSlot, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(workspaceRoot)) return null;

        var shared = await LoadAsync(AutoSaveSlot, ct).ConfigureAwait(false);
        return shared is not null && SessionManager.AutoSaveBelongsHere(shared, workspaceRoot) ? shared : null;
    }

    /// <summary>The auto-save slot of a workspace, in a folder of its own that the session list never reads; without a
    /// workspace, the shared slot older versions wrote.</summary>
    internal string AutoSaveFile(string? workspaceRoot)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot)) return SessionPath(AutoSaveSlot);
        var root = SessionManager.NormalizeRoot(workspaceRoot);
        // The key folds case where the file system does: one workspace, one slot (PathComparer).
        var key  = PathComparer.Comparison == StringComparison.Ordinal ? root : root.ToUpperInvariant();
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key)))[..16];
        var name = Sanitize(Path.GetFileName(root));
        return Path.Combine(Dir, "autosave", $"{(name.Length > 0 ? name + "-" : "")}{hash.ToLowerInvariant()}.json");
    }

    /// Loads a session by file name (without extension).
    /// <exception cref="UnreadableSessionException">The file is damaged: a copy is kept, and the message says where.</exception>
    public Task<SessionData?> LoadAsync(string sessionName, CancellationToken ct) =>
        LoadFileAsync(SessionPath(sessionName), sessionName, ct);

    private async Task<SessionData?> LoadFileAsync(string file, string sessionName, CancellationToken ct)
    {
        if (!File.Exists(file)) return null;
        string json;
        await using (var stream = OpenSessionForRead(file))
        using (var reader = new StreamReader(stream))
            json = await reader.ReadToEndAsync(ct);
        try
        {
            return JsonSerializer.Deserialize<SessionData>(json, _opts);
        }
        catch (JsonException ex)
        {
            // ⚠ Damaged, the file is still the conversation — and the auto-save slot is rewritten at the next turn of
            // either editor, the conversation then gone for good. A copy out of the session list keeps it.
            throw new UnreadableSessionException(sessionName, SetAside(file), ex);
        }
    }

    /// <summary>
    /// A copy of a session file that cannot be read, next to it under a name the session list does not read
    /// (<c>.unreadable-…</c>); the copy already made of the same bytes is reused. <c>null</c> when none could be made.
    /// </summary>
    private static string? SetAside(string file)
    {
        try
        {
            var bytes = File.ReadAllBytes(file);
            var dir   = Path.GetDirectoryName(file)!;
            var name  = Path.GetFileName(file);
            foreach (var kept in Directory.EnumerateFiles(dir, name + ".unreadable-*"))
                if (File.ReadAllBytes(kept).AsSpan().SequenceEqual(bytes)) return kept;
            var aside = file + ".unreadable-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss",
                                                                         System.Globalization.CultureInfo.InvariantCulture);
            AtomicFile.WriteAllBytes(aside, bytes);
            return aside;
        }
        catch (Exception ex)
        {
            Diagnostics.Swallow("ConversationStore.SetAside", ex);
            return null;
        }
    }

    /// <summary>The file a session name maps to in this instance's folder.</summary>
    private string SessionPath(string sessionName) =>
        Path.Combine(Dir, $"{Sanitize(sessionName)}.json");

    /// <summary>The only way a session file is opened for reading.</summary>
    /// <remarks>
    /// ⚠ ReadWrite | Delete sharing: both front-ends share this folder, and both the list and the search read
    /// all of its files. On Windows, a file opened without FileShare.Delete cannot be deleted — a read made
    /// deleting a session fail. Replacement by rename stays refused while the handle is open: AtomicFile
    /// retries.
    /// </remarks>
    internal static FileStream OpenSessionForRead(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: 4096, useAsync: true);

    /// Deletes a saved session. Returns true if the file existed.
    public bool Delete(string sessionName)
    {
        var file = SessionPath(sessionName);
        if (!File.Exists(file)) return false;
        File.Delete(file);
        return true;
    }

    /// Lists all saved sessions, most recent first.
    public IReadOnlyList<string> ListSessions()
    {
        if (!Directory.Exists(Dir)) return [];
        return Directory.GetFiles(Dir, "*.json")
                        // Explicit suffix check: Windows wildcard matching is looser than it
                        // looks, and SaveAsync stages through "<name>.json.tmp".
                        .Where(f => f.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                        .Select(f => Path.GetFileNameWithoutExtension(f)!)
                        .OrderByDescending(n => n)
                        .ToList();
    }

    /// <summary>
    /// Returns metadata for every named session (excluding <c>last_session</c>),
    /// most recent first.  Reads each session file exactly once.
    /// </summary>
    public async Task<SessionScan<SessionSummary>> ListWithPreviewAsync(CancellationToken ct)
    {
        var result     = new List<SessionSummary>();
        var unreadable = new List<string>();
        foreach (var name in ListSessions().Where(n => n != "last_session"))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var data = await LoadAsync(name, ct);
                // ⚠ `null` has two causes and only one is a failure: a file that yielded no session
                // is corrupt by another spelling, but a file DELETED between the listing and the read
                // — the other front-end, the user, `/branch` — simply is not there any more, and
                // reporting it would be an alarm about something that is fine.
                if (data is null) { if (File.Exists(SessionPath(name))) unreadable.Add(name); continue; }
                var preview = data.Messages.FirstOrDefault(SessionManager.IsQuestion)?.Content ?? string.Empty;
                if (preview.Length > 80) preview = preview[..80] + "…";
                result.Add(new SessionSummary(name, data.SavedAt, SessionManager.ConversationMessageCount(data.Messages),
                    preview.Replace('\n', ' '), data.Parent, data.ForkTurn));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // Traced AND returned. The ring buffer is not where the user is looking: dropped
                // here alone, `/history` answers "no saved sessions" to someone who has ten.
                unreadable.Add(name);
                Diagnostics.Swallow($"ConversationStore.ListWithPreview({name})", ex);
            }
        }
        return new SessionScan<SessionSummary>(result, unreadable);
    }

    /// <summary>
    /// Full-text search across all named sessions.
    /// Returns sessions that contain at least one message matching <paramref name="term"/>,
    /// with up to 3 surrounding snippets per session — and how many more messages match, said under them: three
    /// snippets read as "the word appears three times" otherwise.
    /// </summary>
    public async Task<SessionScan<SessionMatch>> SearchAsync(string term, CancellationToken ct)
    {
        const int MaxSnippets = 3;
        var results    = new List<SessionMatch>();
        var unreadable = new List<string>();
        foreach (var name in ListSessions().Where(n => n != "last_session"))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var data = await LoadAsync(name, ct);
                if (data is null) { if (File.Exists(SessionPath(name))) unreadable.Add(name); continue; }

                var matching = data.Messages
                    // What the chat shows: a word found only in the model's hidden reasoning is not a hit.
                    .Select(m => MarkdownParser.ShownText(m.Role, m.Content))
                    .Where(text => text.Contains(term, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                var snippets = matching.Take(MaxSnippets).Select(text => ExtractSnippet(text, term, 90)).ToList();

                if (snippets.Count > 0)
                    results.Add(new SessionMatch(name, data.SavedAt, snippets, matching.Count - snippets.Count));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // ⚠ A session that was not searched is the dangerous half: the answer "no results"
                // is the one the user acts on, and it reads as "the word is not in my history".
                unreadable.Add(name);
                Diagnostics.Swallow($"ConversationStore.Search({name})", ex);
            }
        }
        return new SessionScan<SessionMatch>(results, unreadable);
    }

    private static string ExtractSnippet(string content, string term, int maxLen)
    {
        var idx   = content.IndexOf(term, StringComparison.OrdinalIgnoreCase);
        var start = Math.Max(0, idx - 30);
        var end   = Math.Min(content.Length, idx + term.Length + 60);
        var snip  = content[start..end].Trim().Replace('\n', ' ');
        return (start > 0 ? "…" : string.Empty) + snip + (end < content.Length ? "…" : string.Empty);
    }

    private static readonly HashSet<char> _invalidChars = [..Path.GetInvalidFileNameChars()];

    /// <summary>The name a session is listed under once saved: the file <see cref="SessionPath"/> writes.</summary>
    /// <remarks>⚠ Whoever records "the session this conversation lives in" records THIS name: a typed name with a ':' or
    /// a '/' is listed nowhere, so /branch takes the conversation for an unsaved one and writes a new dated parent.</remarks>
    public static string StoredName(string name) => Sanitize(name);

    // Doctrine: flattening can make two names collide ("a/b" and "a_b" share the same file). Changing the encoding would
    // break addressing for already saved sessions; names come from the UI — a timestamped title on the VS side, and on the
    // VS Code side an InputBox that refuses these characters ("last_session" is reserved on top of that).
    private static string Sanitize(string name) =>
        string.Concat(name.Select(c => _invalidChars.Contains(c) ? '_' : c));
}

/// <summary>A session file that is there but cannot be read; its message, in the user's language, says where the copy
/// kept of it is.</summary>
internal sealed class UnreadableSessionException(string sessionName, string? copy, Exception inner)
    : Exception(copy is null ? Strings.SessionLoadFailed(sessionName) : Strings.SessionUnreadableKept(sessionName, copy), inner)
{
    public string? Copy { get; } = copy;
}

/// <summary>A session file. <c>Parent</c>/<c>ForkTurn</c> are set only on a branch
/// (<c>/branch</c>); older files simply have neither, which keeps the format backward compatible.
/// <c>WorkspaceRoot</c> is recorded on the auto-save slot only — see
/// <see cref="SessionManager.AutoSaveBelongsHere"/>.</summary>
internal record SessionData(
    [property: JsonPropertyName("saved_at")]       DateTime SavedAt,
    [property: JsonPropertyName("messages")]       List<SavedMessage> Messages,
    [property: JsonPropertyName("parent")]         string? Parent        = null,
    [property: JsonPropertyName("fork_turn")]      int?    ForkTurn      = null,
    [property: JsonPropertyName("workspace_root")] string? WorkspaceRoot = null,
    // The named session the auto-saved conversation lives in (last_session only): restored with it, so /branch keeps
    // writing to that session after a restart instead of starting a new dated copy.
    [property: JsonPropertyName("current_name")]   string? CurrentName   = null,
    // ⚠ The /template mode the conversation was held in: its greeting ("Code Review mode active") is in the messages, so
    // a load that drops the mode shows a mode no answer follows — a restart of VS Code's host reloads every time.
    [property: JsonPropertyName("template_suffix")] string? TemplateSuffix = null);

internal record SavedMessage(
    [property: JsonPropertyName("role")]      string  Role,
    [property: JsonPropertyName("content")]   string  Content,
    [property: JsonPropertyName("toolName")]  string? ToolName  = null,
    [property: JsonPropertyName("timestamp")] string? Timestamp = null);

/// <summary>Lightweight session descriptor returned by <see cref="ConversationStore.ListWithPreviewAsync"/>;
/// <paramref name="MessageCount"/> counts its questions and answers (<see cref="SessionManager.ConversationMessageCount"/>).
/// <paramref name="Parent"/>/<paramref name="ForkTurn"/> are non-null for branches.</summary>
internal record SessionSummary(string Name, DateTime SavedAt, int MessageCount, string FirstUserPreview,
                               string? Parent = null, int? ForkTurn = null);

/// <summary>Search hit returned by <see cref="ConversationStore.SearchAsync"/>.</summary>
/// <param name="MoreMatches">Matching messages of the session beyond the snippets shown.</param>
internal record SessionMatch(string Name, DateTime SavedAt, List<string> Snippets, int MoreMatches = 0);

/// <summary>
/// What a pass over the session folder found, <b>and what it could not read</b>.
/// </summary>
/// <remarks>
/// ⚠ A pass that answers with the list alone and traces the rest to <c>/diagnostics</c> reports to
/// nobody: <c>/history</c> then says "no saved sessions" to someone who has ten, or shows five of
/// eight with nothing to say so. Same rule as <c>.inferpal/checks</c> one folder away
/// (<c>ChecksService.Load(dir, out var unreadable)</c>) — there a review criterion, here the user's
/// own conversation.
/// </remarks>
/// <param name="Items">What could be read.</param>
/// <param name="Unreadable">Session names that could not be, in listing order.</param>
internal readonly record struct SessionScan<T>(List<T> Items, IReadOnlyList<string> Unreadable);
