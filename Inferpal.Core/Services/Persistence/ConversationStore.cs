using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
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

    public static string SessionsDirectory => _dir;

    /// Saves a named session (UI messages + API history).
    /// <param name="parent">Session this one was forked from (<c>/branch</c>); null for a root session.</param>
    /// <param name="forkTurn">Turn the fork happened at, meaningful only with <paramref name="parent"/>.</param>
    /// <param name="workspaceRoot">Workspace the conversation belongs to; recorded for the auto-save slot.</param>
    public async Task SaveAsync(string sessionName, IEnumerable<SavedMessage> messages, CancellationToken ct,
                                string? parent = null, int? forkTurn = null, string? workspaceRoot = null)
    {
        Directory.CreateDirectory(Dir);
        var file = SessionPath(sessionName);
        var payload = new SessionData(DateTime.UtcNow, messages.ToList(), parent, forkTurn,
                                      string.IsNullOrWhiteSpace(workspaceRoot) ? null : workspaceRoot);

        // Write-then-rename: a crash (or a full disk) mid-write must not leave a truncated
        // session behind. It matters more since /branch rewrites the parent file on every fork —
        // the interrupted save would be of the conversation the user is keeping. Via AtomicFile,
        // NOT a hand-rolled fixed ".tmp": both front-ends share %AppData% and auto-save
        // last_session.json every turn, and a fixed staging name turns those concurrent writers
        // into a collision.
        await AtomicFile.WriteAllTextAsync(file, JsonSerializer.Serialize(payload, _opts), ct);
    }

    /// Auto-saves the current session to "last_session.json".
    public Task AutoSaveAsync(IEnumerable<SavedMessage> messages, CancellationToken ct, string? workspaceRoot = null) =>
        SaveAsync("last_session", messages, ct, workspaceRoot: workspaceRoot);

    /// Loads a session by file name (without extension).
    public async Task<SessionData?> LoadAsync(string sessionName, CancellationToken ct)
    {
        var file = SessionPath(sessionName);
        if (!File.Exists(file)) return null;
        await using var stream = OpenSessionForRead(file);
        using var reader = new StreamReader(stream);
        var json = await reader.ReadToEndAsync(ct);
        return JsonSerializer.Deserialize<SessionData>(json, _opts);
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

    /// Loads the last auto-saved session.
    public Task<SessionData?> LoadLastAsync(CancellationToken ct) =>
        LoadAsync("last_session", ct);

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
                var preview = data.Messages.FirstOrDefault(m => m.Role == "user")?.Content ?? string.Empty;
                if (preview.Length > 80) preview = preview[..80] + "…";
                result.Add(new SessionSummary(name, data.SavedAt, data.Messages.Count,
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
    /// with up to 3 surrounding snippets per session.
    /// </summary>
    public async Task<SessionScan<SessionMatch>> SearchAsync(string term, CancellationToken ct)
    {
        var results    = new List<SessionMatch>();
        var unreadable = new List<string>();
        foreach (var name in ListSessions().Where(n => n != "last_session"))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var data = await LoadAsync(name, ct);
                if (data is null) { if (File.Exists(SessionPath(name))) unreadable.Add(name); continue; }

                var snippets = data.Messages
                    // What the chat shows: a word found only in the model's hidden reasoning is not a hit.
                    .Select(m => MarkdownParser.ShownText(m.Role, m.Content))
                    .Where(text => text.Contains(term, StringComparison.OrdinalIgnoreCase))
                    .Take(3)
                    .Select(text => ExtractSnippet(text, term, 90))
                    .ToList();

                if (snippets.Count > 0)
                    results.Add(new SessionMatch(name, data.SavedAt, snippets));
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

    // Doctrine: flattening can make two names collide ("a/b" and "a_b" share
    // the same file). Changing the encoding would break addressing for already saved sessions; and
    // names come from the UI (timestamped title on the VS side, filtered InputBox on the VS Code
    // side, where "last_session" is reserved on top of that), which makes the case marginal.
    private static string Sanitize(string name) =>
        string.Concat(name.Select(c => _invalidChars.Contains(c) ? '_' : c));
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
    [property: JsonPropertyName("workspace_root")] string? WorkspaceRoot = null);

internal record SavedMessage(
    [property: JsonPropertyName("role")]      string  Role,
    [property: JsonPropertyName("content")]   string  Content,
    [property: JsonPropertyName("toolName")]  string? ToolName  = null,
    [property: JsonPropertyName("timestamp")] string? Timestamp = null);

/// <summary>Lightweight session descriptor returned by <see cref="ConversationStore.ListWithPreviewAsync"/>.
/// <paramref name="Parent"/>/<paramref name="ForkTurn"/> are non-null for branches.</summary>
internal record SessionSummary(string Name, DateTime SavedAt, int MessageCount, string FirstUserPreview,
                               string? Parent = null, int? ForkTurn = null);

/// <summary>Search hit returned by <see cref="ConversationStore.SearchAsync"/>.</summary>
internal record SessionMatch(string Name, DateTime SavedAt, List<string> Snippets);

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
