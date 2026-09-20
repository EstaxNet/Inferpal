using System.Text.Json;
using System.Text.Json.Serialization;

namespace Inferpal.Services.Arena;

/// <summary>One completed arena battle: which two models fought and how the user voted.
/// <paramref name="Vote"/> is <c>"a"</c>, <c>"b"</c> or <c>"tie"</c>.</summary>
internal sealed record ArenaBattle(
    DateTime TimestampUtc, string Prompt, string ModelA, string ModelB, string Vote);

/// <summary>A battle whose answers were shown but not voted on yet. The blind A/B → model mapping
/// lives here so the reveal happens only at vote time.</summary>
internal sealed record ArenaPending(
    DateTime TimestampUtc, string Prompt, string ModelA, string ModelB);

/// <summary>Everything <c>/arena</c> persists: the cumulative battle log plus the pending vote.</summary>
internal sealed record ArenaSavedState(List<ArenaBattle> Battles, ArenaPending? Pending);

/// <summary>
/// Persists arena battles and the pending vote to <c>%AppData%\Inferpal\arena.json</c> so votes and
/// cumulative stats survive restarts and are shared by both front-ends. Same conventions as
/// <see cref="Bench.BenchStore"/>: best-effort I/O (a corrupt or missing file reads as "no state"),
/// path override for tests.
/// </summary>
internal static class ArenaStore
{
    // ⚠ A vote is not recomputable state. The class remark below said "same conventions as
    // BenchStore", and that is where the classification came from — but a bench run is exactly what
    // `/bench` reproduces, while nothing reproduces which answer the user preferred three weeks ago.
    // The cycle the preserve flag exists to stop (unreadable file, empty list, first write, the whole
    // history replaced by one entry) applies here and not there.
    private static readonly AppDataJsonFile<ArenaSavedState> _file =
        new("arena.json", "ArenaStore", preserveUnreadable: true,
            // The shape check is the file's own: a hand-edited or truncated document can deserialise
            // into a state whose list is null, and every caller enumerates it.
            accept: s => s.Battles is not null);

    /// <summary>Tests point this at a temp file so they never touch the real %AppData%.</summary>
    internal static string? _fileOverride
    {
        get => _file.PathOverride;
        set => _file.PathOverride = value;
    }

    /// <returns><c>false</c> when nothing was written.</returns>
    public static Task<bool> SaveAsync(ArenaSavedState state) => _file.SaveAsync(state);

    // `Battles: not null` is the shape check, not a formality: a hand-edited or truncated file can
    // deserialise into a state whose list is null, and every caller enumerates it.
    public static Task<ArenaSavedState> LoadAsync() =>
        _file.LoadAsync(new ArenaSavedState([], null));
}
