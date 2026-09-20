using System.Text.Json;
using System.Text.Json.Serialization;

namespace Inferpal.Services.Persistence;

/// <summary>
/// One JSON document under <c>%AppData%\Inferpal\</c>, shared by both front-ends.
/// </summary>
/// <typeparam name="T">Shape persisted; must round-trip through <see cref="JsonSerializer"/>.</typeparam>
/// <remarks>
/// <para>
/// One implementation of the six pieces every store needs: the <c>%AppData%\Inferpal</c> path, a
/// <c>_fileOverride</c> so tests never touch the developer's real state, indented + null-skipping
/// serializer options, <c>CreateDirectory</c> before writing, an atomic write, and a read that
/// treats corruption as absence. Copied per store, they look interchangeable and differ where it
/// matters — a plain <c>Deserialize</c> with no guard throws on a truncated file where the others
/// return a default.
/// </para>
/// <para>
/// <b>Absence and corruption are the same answer on the READ side, deliberately.</b> Refusing to
/// start because a cache did not parse would trade a recoverable annoyance for a broken session, so
/// a document that will not deserialise is reported to <c>/diagnostics</c> and answered with the
/// caller fallback. Anything whose loss matters (sessions, plans) does not come through here.
/// </para>
/// <para>
/// <b>What is not symmetric is the WRITE.</b> "These files hold convenience state — past benchmark
/// runs, arena votes, saved snippets — never anything the user cannot recreate" is true of the
/// first two and false of the third: a snippet is a fragment of code the user <i>chose</i> to keep,
/// usually out of a conversation that is long gone, and the cycle is then a loss — unreadable file,
/// empty list, first addition, a hundred snippets replaced by one.
/// </para>
/// <para>
/// Hence <paramref name="preserveUnreadable"/>: the distinction lives in a <b>parameter</b>, not in
/// this paragraph. Disposable documents keep the cheap path; the ones that carry what the user
/// wrote are set aside before being overwritten.
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

    private readonly bool _preserveUnreadable;

    /// <summary>
    /// Shape check for a successfully parsed value — the SAME one on both halves.
    /// </summary>
    /// <remarks>
    /// ⚠ It used to be a parameter of <c>LoadAsync</c> alone, so the two halves disagreed on what
    /// "readable" means: a document that parses into an object with null collections is rejected on
    /// load (it reads as "no state") and counted as readable on save, hence never set aside. The
    /// shape a caller bothers to guard against is exactly the one a truncated write produces, so it
    /// is exactly the one whose bytes must be kept.
    /// </remarks>
    private readonly Func<T, bool>? _accept;

    /// <param name="fileName">Leaf name, e.g. <c>"bench.json"</c>.</param>
    /// <param name="diagnosticName">Prefix for <see cref="Diagnostics.Swallow"/> contexts.</param>
    /// <param name="preserveUnreadable">
    /// <c>true</c> when the document holds content the user authored: an existing file that will
    /// not parse is copied aside before being overwritten. Leave <c>false</c> for state the product
    /// recomputes on its own - archiving it would only clutter <c>%AppData%</c>.
    /// </param>
    /// <param name="accept">
    /// Optional shape check applied to a parsed value. A JSON document that deserialises into an
    /// object with null collections is syntactically valid and useless; callers that care say so
    /// here rather than defending against it at every use site.
    /// </param>
    public AppDataJsonFile(string fileName, string diagnosticName, bool preserveUnreadable = false,
                           Func<T, bool>? accept = null)
    {
        _preserveUnreadable = preserveUnreadable;
        _accept             = accept;
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
    public async Task<T> LoadAsync(T fallback, CancellationToken ct = default) =>
        (await ReadAsync(fallback, ct)).Value;

    /// <summary>
    /// The document, and whether reading it <b>failed</b> — which is not the same as it being absent.
    /// </summary>
    /// <remarks>
    /// ⚠ Both come back as <paramref name="fallback"/>, and a caller that renders that as "nothing
    /// saved yet" tells someone with a hundred entries that they have none — then offers them the
    /// gesture that overwrites the file. Absence is a fact about the user; a failed read is a fact
    /// about one file, and only the second has a remedy worth naming.
    /// </remarks>
    public async Task<(T Value, bool Unreadable)> ReadAsync(T fallback, CancellationToken ct = default)
    {
        try
        {
            if (!File.Exists(Path)) return (fallback, false);

            var value = JsonSerializer.Deserialize<T>(await File.ReadAllTextAsync(Path, ct), _opts);
            return value is not null && Readable(value) ? (value, false) : (fallback, true);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Diagnostics.Swallow($"{_diagnosticName}.Load", ex);
            return (fallback, true);
        }
    }

    /// <summary>Synchronous <see cref="LoadAsync"/>, for a caller that reads once while it is constructed.</summary>
    public T Load(T fallback)
    {
        try
        {
            if (!File.Exists(Path)) return fallback;
            var value = JsonSerializer.Deserialize<T>(File.ReadAllText(Path), _opts);
            return value is not null && Readable(value) ? value : fallback;
        }
        catch (Exception ex)
        {
            Diagnostics.Swallow($"{_diagnosticName}.Load", ex);
            return fallback;
        }
    }

    /// <summary>Synchronous <see cref="SaveAsync"/>: same set-aside of an unreadable file, same atomic write.</summary>
    public bool Save(T value)
    {
        try
        {
            PreserveIfUnreadable();
            AtomicFile.WriteAllText(Path, JsonSerializer.Serialize(value, _opts));
            return true;
        }
        catch (Exception ex) { Diagnostics.Swallow($"{_diagnosticName}.Save", ex); return false; }
    }

    /// <summary>Writes the document atomically. Best effort: a failure is traced, never thrown.</summary>
    /// <returns><c>false</c> when nothing was written — a caller that announces the save must check it.</returns>
    public async Task<bool> SaveAsync(T value, CancellationToken ct = default)
    {
        try
        {
            PreserveIfUnreadable();
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            await AtomicFile.WriteAllTextAsync(Path, JsonSerializer.Serialize(value, _opts), ct);
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { Diagnostics.Swallow($"{_diagnosticName}.Save", ex); return false; }
    }

    /// <summary>
    /// Sets aside an existing file that cannot be read, before the write overwrites it.
    /// </summary>
    /// <remarks>
    /// The state is judged <b>at the moment it matters</b>, by re-reading, rather than through a
    /// flag set at load time: a flag would be stale as soon as the user repairs the file by hand,
    /// and it would not cover a save coming from another path. The cost is one re-read per save, on
    /// documents that are written rarely.
    /// </remarks>
    /// <summary>What both halves mean by "this file can be used".</summary>
    private bool Readable(T value) => _accept is null || _accept(value);

    private void PreserveIfUnreadable()
    {
        if (!_preserveUnreadable || !File.Exists(Path)) return;

        try
        {
            var value = JsonSerializer.Deserialize<T>(File.ReadAllText(Path), _opts);
            if (value is not null && Readable(value)) return;
        }
        catch (Exception ex)
        {
            Diagnostics.Swallow($"{_diagnosticName}.PreserveCheck", ex);
        }

        var aside = AtomicFile.PreserveAside(Path);
        if (aside is not null)
            Diagnostics.Record($"{_diagnosticName}.Save",
                $"{Path} was unreadable: its bytes are kept in {aside} before being overwritten.");
    }
}
