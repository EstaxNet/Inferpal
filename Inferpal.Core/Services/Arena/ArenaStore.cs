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
    private static readonly AppDataJsonFile<ArenaSavedState> _file = new("arena.json", "ArenaStore");

    /// <summary>Tests point this at a temp file so they never touch the real %AppData%.</summary>
    internal static string? _fileOverride
    {
        get => _file.PathOverride;
        set => _file.PathOverride = value;
    }

    public static Task SaveAsync(ArenaSavedState state) => _file.SaveAsync(state);

    // `Battles: not null` is the shape check, not a formality: a hand-edited or truncated file can
    // deserialise into a state whose list is null, and every caller enumerates it.
    public static Task<ArenaSavedState> LoadAsync() =>
        _file.LoadAsync(new ArenaSavedState([], null), accept: s => s.Battles is not null);
}
