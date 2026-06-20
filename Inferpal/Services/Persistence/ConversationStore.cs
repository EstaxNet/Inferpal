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
/// Each session file contains both the display messages and the raw API history,
/// allowing a conversation to be resumed exactly where it left off.
/// The special name <c>"last_session"</c> is reserved for the auto-save slot.
/// </remarks>
internal class ConversationStore
{
    private static readonly string _dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Inferpal", "sessions");

    private static readonly JsonSerializerOptions _opts = new()
    {
        WriteIndented          = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string SessionsDirectory => _dir;

    /// Saves a named session (UI messages + API history).
    public async Task SaveAsync(string sessionName, IEnumerable<SavedMessage> messages, CancellationToken ct)
    {
        Directory.CreateDirectory(_dir);
        var file = Path.Combine(_dir, $"{Sanitize(sessionName)}.json");
        var payload = new SessionData(DateTime.UtcNow, messages.ToList());
        await File.WriteAllTextAsync(file, JsonSerializer.Serialize(payload, _opts), ct);
    }

    /// Auto-saves the current session to "last_session.json".
    public Task AutoSaveAsync(IEnumerable<SavedMessage> messages, CancellationToken ct) =>
        SaveAsync("last_session", messages, ct);

    /// Loads a session by file name (without extension).
    public async Task<SessionData?> LoadAsync(string sessionName, CancellationToken ct)
    {
        var file = Path.Combine(_dir, $"{Sanitize(sessionName)}.json");
        if (!File.Exists(file)) return null;
        var json = await File.ReadAllTextAsync(file, ct);
        return JsonSerializer.Deserialize<SessionData>(json, _opts);
    }

    /// Loads the last auto-saved session.
    public Task<SessionData?> LoadLastAsync(CancellationToken ct) =>
        LoadAsync("last_session", ct);

    /// Deletes a saved session. Returns true if the file existed.
    public bool Delete(string sessionName)
    {
        var file = Path.Combine(_dir, $"{Sanitize(sessionName)}.json");
        if (!File.Exists(file)) return false;
        File.Delete(file);
        return true;
    }

    /// Lists all saved sessions, most recent first.
    public IReadOnlyList<string> ListSessions()
    {
        if (!Directory.Exists(_dir)) return [];
        return Directory.GetFiles(_dir, "*.json")
                        .Select(f => Path.GetFileNameWithoutExtension(f)!)
                        .OrderByDescending(n => n)
                        .ToList();
    }

    /// <summary>
    /// Returns metadata for every named session (excluding <c>last_session</c>),
    /// most recent first.  Reads each session file exactly once.
    /// </summary>
    public async Task<List<SessionSummary>> ListWithPreviewAsync(CancellationToken ct)
    {
        var result = new List<SessionSummary>();
        foreach (var name in ListSessions().Where(n => n != "last_session"))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var data = await LoadAsync(name, ct);
                if (data is null) continue;
                var preview = data.Messages.FirstOrDefault(m => m.Role == "user")?.Content ?? string.Empty;
                if (preview.Length > 80) preview = preview[..80] + "…";
                result.Add(new SessionSummary(name, data.SavedAt, data.Messages.Count,
                    preview.Replace('\n', ' ')));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { Diagnostics.Swallow($"ConversationStore.ListWithPreview({name})", ex); }
        }
        return result;
    }

    /// <summary>
    /// Full-text search across all named sessions.
    /// Returns sessions that contain at least one message matching <paramref name="term"/>,
    /// with up to 3 surrounding snippets per session.
    /// </summary>
    public async Task<List<SessionMatch>> SearchAsync(string term, CancellationToken ct)
    {
        var results = new List<SessionMatch>();
        foreach (var name in ListSessions().Where(n => n != "last_session"))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var data = await LoadAsync(name, ct);
                if (data is null) continue;

                var snippets = data.Messages
                    .Where(m => m.Content.Contains(term, StringComparison.OrdinalIgnoreCase))
                    .Take(3)
                    .Select(m => ExtractSnippet(m.Content, term, 90))
                    .ToList();

                if (snippets.Count > 0)
                    results.Add(new SessionMatch(name, data.SavedAt, snippets));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { Diagnostics.Swallow($"ConversationStore.Search({name})", ex); }
        }
        return results;
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

    private static string Sanitize(string name) =>
        string.Concat(name.Select(c => _invalidChars.Contains(c) ? '_' : c));
}

internal record SessionData(
    [property: JsonPropertyName("saved_at")] DateTime SavedAt,
    [property: JsonPropertyName("messages")]  List<SavedMessage> Messages);

internal record SavedMessage(
    [property: JsonPropertyName("role")]      string  Role,
    [property: JsonPropertyName("content")]   string  Content,
    [property: JsonPropertyName("toolName")]  string? ToolName  = null,
    [property: JsonPropertyName("timestamp")] string? Timestamp = null);

/// <summary>Lightweight session descriptor returned by <see cref="ConversationStore.ListWithPreviewAsync"/>.</summary>
internal record SessionSummary(string Name, DateTime SavedAt, int MessageCount, string FirstUserPreview);

/// <summary>Search hit returned by <see cref="ConversationStore.SearchAsync"/>.</summary>
internal record SessionMatch(string Name, DateTime SavedAt, List<string> Snippets);
