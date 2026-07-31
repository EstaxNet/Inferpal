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
    private static readonly string _defaultFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Inferpal", "bench.json");

    /// <summary>Tests point this at a temp file so they never touch the real %AppData%.</summary>
    internal static string? _fileOverride;

    private static string FilePath => _fileOverride ?? _defaultFile;

    private static readonly JsonSerializerOptions _opts = new()
    {
        WriteIndented          = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static async Task SaveAsync(BenchSavedRun run)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            await AtomicFile.WriteAllTextAsync(FilePath, JsonSerializer.Serialize(run, _opts));
        }
        catch (Exception ex) { Diagnostics.Swallow("BenchStore.Save", ex); }
    }

    public static async Task<BenchSavedRun?> LoadAsync()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            return JsonSerializer.Deserialize<BenchSavedRun>(await File.ReadAllTextAsync(FilePath), _opts);
        }
        catch (Exception ex)
        {
            Diagnostics.Swallow("BenchStore.Load", ex);
            return null;
        }
    }
}
