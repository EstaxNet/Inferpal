using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace Inferpal.GhostText;

/// <summary>
/// The four inline-completion settings, read straight from <c>%AppData%/Inferpal/config.json</c>.
/// </summary>
/// <remarks>
/// <para>
/// The in-process assembly is net472 and does not reference the Core: <c>InferpalConfig</c>
/// (net8, DPAPI, ~90 properties) cannot be loaded there. Yet the only thing ghost text needs to
/// know before it opens its mouth is whether it is enabled — and that question comes up on
/// <b>every keystroke</b>. Sending it all the way to the sidecar would be absurd: we read the same
/// file, four properties, cached and invalidated by the write timestamp.
/// </para>
/// <para>
/// ⚠ The four keys are the JSON names <c>config.json</c> is written with — <c>InferpalConfig</c>'s
/// <c>[JsonPropertyName]</c>, camelCase — and the lookup is case-sensitive. Copied with the C# property names
/// instead, they never matched: ghost text ignored its on/off switch, its model and its mode, without a message.
/// Locked by <c>InProcConfigKeyTests</c>, which compares them with the attributes, not with the property names.
/// </para>
/// </remarks>
internal static class InProcConfig
{
    internal const string KeyEnabled = "inlineCompletionEnabled";
    internal const string KeyMode    = "inlineCompletionMode";
    internal const string KeyModel   = "inlineCompletionModel";
    internal const string KeyTrigger = "inlineCompletionTrigger";

    /// <summary>The value of <see cref="KeyTrigger"/> under which only the key asks (<c>AskedCompletion.OnDemand</c>).</summary>
    internal const string OnDemandValue = "onDemand";

    /// <summary>Defaults, identical to those of <c>InferpalConfig</c>.</summary>
    internal sealed class Snapshot
    {
        internal bool    Enabled  { get; set; } = true;
        internal string  Mode     { get; set; } = "Default";
        internal string? Model    { get; set; }
        internal bool    OnDemand { get; set; }

        /// <summary>Whether a pause in typing asks for a suggestion: on, and not only on demand.</summary>
        internal bool AsksOnPause => Enabled && !OnDemand;

        /// <summary>Write timestamp of the file that was read; used both to decide on a re-read
        /// AND to recycle the sidecar when the backend changed under its feet.</summary>
        internal long Stamp { get; set; }
    }

    /// <summary>Test seam — production reads the real path.</summary>
    internal static string? PathOverride;

    internal static string ConfigPath => PathOverride ?? Path.Combine(
        UserFolders.Of(Environment.SpecialFolder.ApplicationData), "Inferpal", "config.json");

    private static readonly object _gate = new object();
    // ⚠ Off until a read says otherwise: the factory values for a file that exists and cannot be read at VS's start
    // would turn completion back on for someone who has turned it off. An absent file reads as the factory values.
    private static Snapshot _cached = Unknown();

    /// <summary>What is known before any read: nothing, so no suggestion.</summary>
    private static Snapshot Unknown() => new Snapshot { Stamp = -1, Enabled = false };
    private static long _cachedStamp = -1;

    // A read that failed is tried again from this time on (Stopwatch timestamp: a clock set back must not hold it off),
    // never at every keystroke; long.MaxValue once read.
    private static long _retryAt;

    /// <summary>How long a read that failed is trusted to stay so before it is tried again (tests set it to zero).</summary>
    internal static TimeSpan RetryUnread { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Forgets what was read (tests: the state is the process's).</summary>
    internal static void ResetForTests()
    {
        lock (_gate)
        {
            _cached      = Unknown();
            _cachedStamp = -1;
            _retryAt     = 0;
        }
    }

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

        Snapshot kept;
        lock (_gate)
        {
            if (stamp == _cachedStamp && Stopwatch.GetTimestamp() < _retryAt) return _cached;
            kept = _cached;
        }
        // Read outside the lock: a file held a moment is read again (off the UI thread), and a keystroke on the UI thread
        // must not wait behind it.
        var (snapshot, failed) = Read(stamp, kept);
        lock (_gate)
        {
            _cached      = snapshot;
            _cachedStamp = stamp;
            // ⚠ A failed read is not cached under the file's date: a file read while it is replaced — the person has just
            // turned completion off — would keep the old values under the new date, and never be read again.
            _retryAt     = failed
                ? Stopwatch.GetTimestamp() + (long)(RetryUnread.TotalSeconds * Stopwatch.Frequency)
                : long.MaxValue;
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
    /// preference one can no longer read is not a preference that has just changed. ⚠ The values kept carry the stamp
    /// they were read at: a new stamp recycles the sidecar, whose own read of the unreadable file would give it the
    /// factory backend and model for its life.
    /// </remarks>
    private static (Snapshot Snapshot, bool Failed) Read(long stamp, Snapshot kept)
    {
        var snap = new Snapshot { Stamp = stamp };
        try
        {
            if (!File.Exists(ConfigPath)) return (snap, false);
            Parse(Inferpal.Services.Signals.SignalFile.ReadShared(ConfigPath), snap);
        }
        catch (Exception ex)
        {
            // Once per state of the file: tried again every few seconds while typing, it would flush the ring.
            Diagnostics.RecordOnce("InProcConfig.Read", $"{ex.GetType().Name}: {ex.Message}", stamp.ToString(System.Globalization.CultureInfo.InvariantCulture));
            return (new Snapshot
            {
                Enabled  = kept.Enabled,
                Mode     = kept.Mode,
                Model    = kept.Model,
                OnDemand = kept.OnDemand,
                Stamp    = kept.Stamp,
            }, true);
        }
        return (snap, false);
    }

    /// <summary>Fills <paramref name="snap"/> from the text of <c>config.json</c>; a key absent keeps its default. Throws
    /// on text that is not JSON (the caller keeps the last values read).</summary>
    internal static Snapshot Parse(string json, Snapshot? snap = null)
    {
        snap ??= new Snapshot();
        using var doc = JsonDocument.Parse(json);
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

        if (root.TryGetProperty(KeyTrigger, out var trigger) && trigger.ValueKind == JsonValueKind.String)
            snap.OnDemand = string.Equals(trigger.GetString()?.Trim(), OnDemandValue, StringComparison.OrdinalIgnoreCase);
        return snap;
    }
}
