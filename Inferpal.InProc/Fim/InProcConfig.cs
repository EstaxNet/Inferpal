using System.IO;
using System.Text.Json;

namespace Inferpal.GhostText;

/// <summary>
/// The three inline-completion settings, read straight from <c>%AppData%/Inferpal/config.json</c>.
/// </summary>
/// <remarks>
/// <para>
/// The in-process assembly is net472 and does not reference the Core: <c>InferpalConfig</c>
/// (net8, DPAPI, ~90 properties) cannot be loaded there. Yet the only thing ghost text needs to
/// know before it opens its mouth is whether it is enabled — and that question comes up on
/// <b>every keystroke</b>. Sending it all the way to the sidecar would be absurd: we read the same
/// file, three properties, cached and invalidated by the write timestamp.
/// </para>
/// <para>
/// ⚠ The three property names are copied here from <c>InferpalConfig</c> — a silent rename would
/// disable ghost text without a single message. That is exactly the kind of drift this repository
/// pays for elsewhere, so it is locked by a test: <c>InProcConfigContractTests</c> checks that the
/// three names still exist on <c>InferpalConfig</c>, with the right type and the right default.
/// </para>
/// </remarks>
internal static class InProcConfig
{
    internal const string KeyEnabled = "InlineCompletionEnabled";
    internal const string KeyMode    = "InlineCompletionMode";
    internal const string KeyModel   = "InlineCompletionModel";

    /// <summary>Defaults, identical to those of <c>InferpalConfig</c>.</summary>
    internal sealed class Snapshot
    {
        internal bool    Enabled { get; set; } = true;
        internal string  Mode    { get; set; } = "Default";
        internal string? Model   { get; set; }

        /// <summary>Write timestamp of the file that was read; used both to decide on a re-read
        /// AND to recycle the sidecar when the backend changed under its feet.</summary>
        internal long Stamp { get; set; }
    }

    /// <summary>Test seam — production reads the real path.</summary>
    internal static string? PathOverride;

    internal static string ConfigPath => PathOverride ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Inferpal", "config.json");

    private static readonly object _gate = new object();
    private static Snapshot _cached = new Snapshot { Stamp = -1 };
    private static long _cachedStamp = -1;

    /// <summary>
    /// The current settings. Re-reads the file only when it changed; never throws — a missing or
    /// unreadable file yields the defaults, just like <c>InferpalConfig.Load</c>.
    /// </summary>
    internal static Snapshot Current()
    {
        long stamp;
        try
        {
            var info = new FileInfo(ConfigPath);
            stamp = info.Exists ? info.LastWriteTimeUtc.Ticks ^ info.Length : 0;
        }
        catch (Exception ex) { Diagnostics.Swallow("InProcConfig.Stat", ex); return _cached; }

        lock (_gate)
        {
            if (stamp == _cachedStamp) return _cached;
            _cached      = Read(stamp);
            _cachedStamp = stamp;
            return _cached;
        }
    }

    /// <summary>
    /// Re-reads the file. A <b>missing</b> file returns the defaults; an <b>unreadable</b> file
    /// returns the last values successfully read.
    /// </summary>
    /// <remarks>
    /// The distinction is not cosmetic: <c>Enabled</c> defaults to <c>true</c>, so a truncated
    /// <c>config.json</c> — a concurrent write, a full disk — turned inline completion back on for
    /// someone who had turned it off, and put the sidecar back to consuming GPU without a word. A
    /// preference one can no longer read is not a preference that has just changed.
    /// </remarks>
    private static Snapshot Read(long stamp)
    {
        var snap = new Snapshot { Stamp = stamp };
        try
        {
            if (!File.Exists(ConfigPath)) return snap;
            using var doc = JsonDocument.Parse(File.ReadAllText(ConfigPath));
            var root = doc.RootElement;

            if (root.TryGetProperty(KeyEnabled, out var enabled) &&
                (enabled.ValueKind == JsonValueKind.True || enabled.ValueKind == JsonValueKind.False))
                snap.Enabled = enabled.GetBoolean();

            if (root.TryGetProperty(KeyMode, out var mode) && mode.ValueKind == JsonValueKind.String)
                snap.Mode = mode.GetString() ?? "Default";

            if (root.TryGetProperty(KeyModel, out var model) && model.ValueKind == JsonValueKind.String)
            {
                var value = model.GetString();
                snap.Model = string.IsNullOrEmpty(value) ? null : value;
            }
        }
        catch (Exception ex)
        {
            Diagnostics.Swallow("InProcConfig.Read", ex);
            // The last values READ, at the new stamp so as not to re-read on every keystroke a file
            // that will not be read. Nothing is lost: the next valid write changes the stamp and
            // comes back through here.
            return new Snapshot
            {
                Enabled = _cached.Enabled,
                Mode    = _cached.Mode,
                Model   = _cached.Model,
                Stamp   = stamp,
            };
        }
        return snap;
    }
}
