using System.Text.Json;
using System.Text.Json.Serialization;

namespace Inferpal.Services.Bench;

/// <summary>A persisted <c>/bench</c> run: when it ran and what it measured.</summary>
internal sealed record BenchSavedRun(DateTime TimestampUtc, List<BenchModelResult> Results);

/// <summary>
/// Persists the latest <c>/bench</c> run to <c>%AppData%\Inferpal\bench.json</c> so
/// <c>/bench last</c> can redisplay it without re-running minutes of inference.
/// Same conventions as <see cref="Persistence.SnippetStore"/>: best-effort I/O (a corrupt or
/// missing file reads as "no saved run"), path override for tests.
/// </summary>
internal static class BenchStore
{
    // ⚠ Deliberately NOT preserved, unlike arena.json next door: a bench run is what `/bench`
    // reproduces. Keeping a copy of a corrupt one would only clutter %AppData% for state the
    // product can rebuild on demand — the very distinction the flag exists to carry.
    private static readonly AppDataJsonFile<BenchSavedRun?> _file =
        new("bench.json", "BenchStore", accept: r => r?.Results is not null);

    /// <summary>Tests point this at a temp file so they never touch the real %AppData%.</summary>
    internal static string? _fileOverride
    {
        get => _file.PathOverride;
        set => _file.PathOverride = value;
    }

    public static Task SaveAsync(BenchSavedRun run) => _file.SaveAsync(run);

    // Same shape check as the arena state, and for the same reason: `/bench last` enumerates it.
    public static Task<BenchSavedRun?> LoadAsync() =>
        _file.LoadAsync(null);
}
