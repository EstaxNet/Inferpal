using System.Text.Json;
using System.Text.Json.Serialization;

namespace Inferpal.Services.Persistence;

/// <summary>
/// One JSON document under <c>%AppData%\Inferpal\</c>, shared by both front-ends.
/// </summary>
/// <typeparam name="T">Shape persisted; must round-trip through <see cref="JsonSerializer"/>.</typeparam>
/// <remarks>
/// <para>
/// Three stores (<c>arena.json</c>, <c>bench.json</c>, <c>snippets.json</c>) had each grown the same
/// six pieces: the <c>%AppData%\Inferpal</c> path, a <c>_fileOverride</c> so tests never touch the
/// developer's real state, indented + null-skipping serializer options, <c>CreateDirectory</c>
/// before writing, an atomic write, and a read that treats corruption as absence. The copies were
/// close enough to look interchangeable and different enough to be wrong: one of the three read
/// through a plain <c>Deserialize</c> with no guard at all, so a truncated file threw where the
/// others returned a default.
/// </para>
/// <para>
/// <b>Absence and corruption are the same answer here, deliberately.</b> These files hold
/// convenience state — past benchmark runs, arena votes, saved snippets — never anything the user
/// cannot recreate. Refusing to start because a cache did not parse would trade a recoverable
/// annoyance for a broken session. Anything whose loss matters (sessions, plans) does not come
/// through here.
/// </para>
/// </remarks>
internal sealed class AppDataJsonFile<T>
{
    private static readonly JsonSerializerOptions _opts = new()
    {
        WriteIndented          = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _defaultPath;
    private readonly string _diagnosticName;

    /// <param name="fileName">Leaf name, e.g. <c>"bench.json"</c>.</param>
    /// <param name="diagnosticName">Prefix for <see cref="Diagnostics.Swallow"/> contexts.</param>
    public AppDataJsonFile(string fileName, string diagnosticName)
    {
        // Fully qualified: the Path property below shadows System.IO.Path inside this type.
        _defaultPath = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Inferpal", fileName);
        _diagnosticName = diagnosticName;
    }

    /// <summary>Tests point this at a temp file so they never touch the real %AppData%.</summary>
    internal string? PathOverride { get; set; }

    /// <summary>Where this document actually lives right now.</summary>
    public string Path => PathOverride ?? _defaultPath;

    /// <summary>
    /// Reads the document, or <paramref name="fallback"/> when it is absent, empty or unreadable.
    /// </summary>
    /// <param name="accept">
    /// Optional shape check applied to a successfully parsed value. A JSON document that
    /// deserialises into an object with null collections is syntactically valid and useless;
    /// callers that care say so here rather than defending against it at every use site.
    /// </param>
    public async Task<T> LoadAsync(T fallback, Func<T, bool>? accept = null, CancellationToken ct = default)
    {
        try
        {
            if (!File.Exists(Path)) return fallback;

            var value = JsonSerializer.Deserialize<T>(await File.ReadAllTextAsync(Path, ct), _opts);
            if (value is null) return fallback;
            return accept is null || accept(value) ? value : fallback;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Diagnostics.Swallow($"{_diagnosticName}.Load", ex);
            return fallback;
        }
    }

    /// <summary>Writes the document atomically. Best effort: a failure is traced, never thrown.</summary>
    public async Task SaveAsync(T value, CancellationToken ct = default)
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            await AtomicFile.WriteAllTextAsync(Path, JsonSerializer.Serialize(value, _opts), ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { Diagnostics.Swallow($"{_diagnosticName}.Save", ex); }
    }
}
