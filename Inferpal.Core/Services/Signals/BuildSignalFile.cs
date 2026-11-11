using System.IO;
using System.Text.Json;
using System.Threading;

namespace Inferpal.Services.Signals;

/// <summary>
/// Lightweight file-based IPC channel between the in-process <c>GhostTextPackage</c>
/// (which runs inside the VS process and can subscribe to COM build events via
/// <c>IVsUpdateSolutionEvents</c>) and the out-of-process <c>VsBuildMonitor</c>
/// (which runs in the VisX extension host and cannot access VS COM services directly).
///
/// <para>Protocol:</para>
/// <list type="bullet">
///   <item>In-process: calls <see cref="Write"/> when a VS build fails, optionally passing
///         errors already collected from the VS Error List so the OOP monitor does not need
///         to run a second <c>dotnet build</c>.</item>
///   <item>Out-of-process: detects the file via <see cref="System.IO.FileSystemWatcher"/>,
///         takes it (<see cref="Take"/>): read, then removed.</item>
/// </list>
/// </summary>
internal static class BuildSignalFile
{


    /// <summary>Full path of the signal file, scoped to the declared VS instance.</summary>
    internal static string FilePath => SignalFile.ScopedPathFor("build_signal");

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
    /// <remarks>
    /// ⚠ Through <see cref="SignalFile.Write{T}"/> — stage then rename — and not for uniformity:
    /// this is the channel where a torn read does not degrade the signal, it <b>destroys</b> it.
    /// <c>VsBuildMonitor.OnSignalFileEvent</c> takes the signal (<see cref="Take"/>): a read that landed on a
    /// truncated file would find it malformed and remove it, deleting the real payload. The failure is then
    /// mute and total — no "build failed" banner, hence no "Fix with AI" entry into
    /// <c>/fix-build</c>, on a compilation that did fail. This is exactly the distinction
    /// <see cref="SignalFile.Write{T}"/> documents between a <i>hint</i> channel (re-read next
    /// turn) and the debugger's <i>command</i> channel.
    /// </remarks>
    /// <param name="errorCount">How many errors the build had — the lines are capped, the count is not. Negative when
    /// unknown: the reader then counts the lines.</param>
    /// <param name="succeeded">The build SUCCEEDED: the "last build failed" banner of an earlier build goes away.</param>
    internal static void Write(string solutionPath, IReadOnlyList<string>? errorLines = null, int errorCount = -1,
                               bool succeeded = false) =>
        SignalFile.Write(FilePath, new
        {
            solutionPath,
            ts     = SignalFile.Now.ToUnixTimeMilliseconds(),
            errors = (errorLines != null && errorLines.Count > 0)
                         ? errorLines
                         : Array.Empty<string>(),
            errorCount,
            succeeded,
        }, "BuildSignal.Write");

    // ── Out-of-process side (VsBuildMonitor) ──────────────────────────────────

    /// <summary>
    /// Payload returned by <see cref="TryRead"/>.
    /// </summary>
    /// <param name="ErrorCount">The build's error count — ⚠ never the number of <paramref name="ErrorLines"/>, which are
    /// capped: 200 errors read as 30. 0 when nobody could count them (the Error List was not filled in time).</param>
    /// <param name="Succeeded">The build succeeded.</param>
    internal readonly record struct SignalPayload(string? SolutionPath, string[] ErrorLines, int ErrorCount = 0,
                                                  bool Succeeded = false);

    /// <summary>
    /// Reads the signal file and returns its payload if the signal is recent (≤ 30 s).
    /// Returns a default value (null solution path, empty errors) if the file is absent,
    /// unreadable, or stale.
    /// </summary>
    internal static SignalPayload TryRead() => Read().Payload;

    /// <summary>How long <see cref="Take"/> waits before its second read of a signal it could not read.</summary>
    internal static TimeSpan SecondReadDelay { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// The signal, taken: read, then removed so it is not dispatched twice. A signal that exists and cannot be read is
    /// read once more after <see cref="SecondReadDelay"/>, and left in place if it still cannot be.
    /// </summary>
    /// <remarks>
    /// ⚠ Removed unread, a failed build lost its banner — and the way into <c>/fix-build</c> — without a word: a read
    /// that fails is not "no signal". A sleep, not a delay: the watcher's handlers run one at a time on a pool
    /// thread, and two awaiting at once would both read the newer signal. An absent or stale one is cleared as before.
    /// </remarks>
    internal static SignalPayload Take()
    {
        for (var attempt = 1; ; attempt++)
        {
            var (payload, unreadable) = Read();
            if (!unreadable)
            {
                Clear();
                return payload;
            }
            if (attempt == 2)
            {
                Diagnostics.Record("BuildSignal", "The build signal could not be read; left in place for the next read.");
                return default;
            }
            Thread.Sleep(SecondReadDelay);
        }
    }

    /// <summary>The signal, and whether reading it FAILED — which is not the same as there being none (absent, stale).</summary>
    private static (SignalPayload Payload, bool Unreadable) Read()
    {
        try
        {
            if (!File.Exists(FilePath)) return default;
            var obj = JsonSerializer.Deserialize<JsonElement>(SignalFile.ReadShared(FilePath));
            var ts  = obj.GetProperty("ts").GetInt64();
            // One clock shared by both sides: this channel held the only pair that read
            // DateTimeOffset.UtcNow directly, so its 30 s rule was the only untestable one.
            var age = SignalFile.Now.ToUnixTimeMilliseconds() - ts;
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

            // A writer that predates the count (or could not count) leaves -1 or nothing: the lines are then all we know.
            var count = obj.TryGetProperty("errorCount", out var countEl) && countEl.TryGetInt32(out var c) && c >= 0
                ? Math.Max(c, errors.Length)
                : errors.Length;
            var succeeded = obj.TryGetProperty("succeeded", out var okEl) && okEl.ValueKind == JsonValueKind.True;

            return (new SignalPayload(path, errors, count, succeeded), false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Diagnostics.Swallow("BuildSignal.Read", ex);
            return (default, true);
        }
        catch { return default; }   // malformed: it will never read, cleared like a stale one
    }

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
        try { Directory.CreateDirectory(SignalFile.Dir); }
        catch (Exception ex) { Services.Diagnostics.Swallow("BuildSignal.EnsureDir", ex); }
    }
}
