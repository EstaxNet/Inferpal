using System.IO;
using System.Text.Json;

namespace Inferpal.Services;

/// <summary>
/// Lightweight file-based IPC channel between the in-process <see cref="GhostText.GhostTextPackage"/>
/// (which runs inside the VS process and can subscribe to COM build events via
/// <c>IVsUpdateSolutionEvents</c>) and the out-of-process <see cref="VsBuildMonitor"/>
/// (which runs in the VisX extension host and cannot access VS COM services directly).
///
/// <para>Protocol:</para>
/// <list type="bullet">
///   <item>In-process: calls <see cref="Write"/> when a VS build fails, optionally passing
///         errors already collected from the VS Error List so the OOP monitor does not need
///         to run a second <c>dotnet build</c>.</item>
///   <item>Out-of-process: detects the file via <see cref="System.IO.FileSystemWatcher"/>,
///         calls <see cref="TryRead"/> then <see cref="Clear"/>.</item>
/// </list>
/// </summary>
internal static class BuildSignalFile
{
    private static readonly string TempDir = Path.Combine(Path.GetTempPath(), "Inferpal");

    /// <summary>Full path of the signal file.</summary>
    internal static string FilePath { get; } = Path.Combine(TempDir, "build_signal.json");

    // ── In-process side (GhostTextPackage) ────────────────────────────────────

    /// <summary>
    /// Writes (or overwrites) the signal file with the current timestamp, solution path, and
    /// optional error lines already collected from the VS Error List.
    /// Called from a background thread inside the in-process package.
    /// </summary>
    /// <param name="solutionPath">Full path of the failing .sln file.</param>
    /// <param name="errorLines">
    /// Error messages collected in-process from <c>IVsTaskList</c>.
    /// When non-empty the OOP monitor uses them directly and skips the second
    /// <c>dotnet build</c> pass.
    /// </param>
    internal static void Write(string solutionPath, IReadOnlyList<string>? errorLines = null)
    {
        try
        {
            Directory.CreateDirectory(TempDir);
            var json = JsonSerializer.Serialize(new
            {
                solutionPath,
                ts     = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                errors = (errorLines != null && errorLines.Count > 0)
                             ? errorLines
                             : Array.Empty<string>(),
            });
            File.WriteAllText(FilePath, json);
        }
        catch { /* non-critical */ }
    }

    // ── Out-of-process side (VsBuildMonitor) ──────────────────────────────────

    /// <summary>
    /// Payload returned by <see cref="TryRead"/>.
    /// </summary>
    internal readonly record struct SignalPayload(string? SolutionPath, string[] ErrorLines);

    /// <summary>
    /// Reads the signal file and returns its payload if the signal is recent (≤ 30 s).
    /// Returns a default value (null solution path, empty errors) if the file is absent,
    /// unreadable, or stale.
    /// </summary>
    internal static SignalPayload TryRead()
    {
        try
        {
            if (!File.Exists(FilePath)) return default;
            var obj = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(FilePath));
            var ts  = obj.GetProperty("ts").GetInt64();
            var age = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - ts;
            if (age > 30_000) return default;   // stale — ignore (previous VS session)

            var path = obj.GetProperty("solutionPath").GetString();

            string[] errors = Array.Empty<string>();
            if (obj.TryGetProperty("errors", out var errEl) && errEl.ValueKind == JsonValueKind.Array)
            {
                errors = errEl.EnumerateArray()
                              .Select(e => e.GetString() ?? string.Empty)
                              .Where(s => s.Length > 0)
                              .ToArray();
            }

            return new SignalPayload(path, errors);
        }
        catch { return default; }
    }

    /// <summary>
    /// Reads the signal file and returns only the solution path (backward-compatible helper).
    /// </summary>
    internal static string? TryReadSolutionPath() => TryRead().SolutionPath;

    /// <summary>Deletes the signal file (safe to call when it doesn't exist).</summary>
    internal static void Clear()
    {
        try { File.Delete(FilePath); }
        catch { }
    }

    /// <summary>
    /// Creates the temp directory.  Must be called before creating a
    /// <see cref="System.IO.FileSystemWatcher"/> on <see cref="FilePath"/>.
    /// </summary>
    internal static void EnsureDir()
    {
        try { Directory.CreateDirectory(TempDir); }
        catch { }
    }
}
