using System.IO;
using System.Text.Json.Serialization;

namespace Inferpal.Services.Signals;

/// <summary>What was dropped on Visual Studio's chat window, as the in-process half took it.</summary>
/// <param name="Paths">The files and folders, by their full path (the drop's <c>FileDrop</c>).</param>
/// <param name="Projects">The names of the Solution Explorer projects dropped: a project carries no path to attach.</param>
/// <param name="Ts">When, in Unix milliseconds.</param>
internal sealed record ChatDrop(
    [property: JsonPropertyName("paths")]    string[] Paths,
    [property: JsonPropertyName("projects")] string[] Projects,
    [property: JsonPropertyName("ts")]       long     Ts);

/// <summary>
/// File channel from the in-process half, the only one that sees a drop on the chat window (its XAML lives in devenv), to
/// the out-of-process window that turns it into chips — scoped to the declared Visual Studio instance.
/// </summary>
/// <remarks>Pushed, never asked: the window watches the file, takes it and deletes it. A drop is not state: one older than
/// <see cref="MaxAge"/> belongs to a window that was not there to take it, and is never attached later.</remarks>
internal static class ChatDropSignal
{
    internal static string FilePath => SignalFile.ScopedPathFor("chat_drop");

    /// <summary>A drop older than this is not attached: nobody was watching when it happened.</summary>
    internal static TimeSpan MaxAge { get; set; } = TimeSpan.FromSeconds(10);

    // ── In process (the drop) ───────────────────────────────────────────────────

    internal static bool Write(string[] paths, string[] projects) =>
        SignalFile.Write(FilePath,
            new ChatDrop(paths, projects, SignalFile.Now.ToUnixTimeMilliseconds()),
            "ChatDrop.Write");

    // ── Out of process (the window) ─────────────────────────────────────────────

    /// <summary>The drop waiting, removed from disk; <c>null</c> when there is none, or it is too old.</summary>
    internal static ChatDrop? Take()
    {
        var drop = SignalFile.TryRead<ChatDrop>(FilePath);
        if (drop is null) return null;
        SignalFile.Delete(FilePath);
        var age = SignalFile.Now.ToUnixTimeMilliseconds() - drop.Ts;
        return age > MaxAge.TotalMilliseconds ? null : drop with
        {
            Paths    = drop.Paths    ?? [],
            Projects = drop.Projects ?? [],
        };
    }

    /// <summary>Calls <paramref name="onDrop"/> (on a pool thread) for each drop written from now on; <c>null</c> when the
    /// watcher cannot start — traced in <c>/diagnostics</c>: devenv still takes the drop (it cannot know), and nothing
    /// is attached.</summary>
    internal static IDisposable? Watch(Action<ChatDrop> onDrop)
    {
        try
        {
            var dir = Path.GetDirectoryName(FilePath)!;
            Directory.CreateDirectory(dir);
            SignalFile.Delete(FilePath);   // a drop left by an earlier window is never attached to this one
            var watcher = new FileSystemWatcher(dir, Path.GetFileName(FilePath))
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
            };
            // SignalFile.Write renames a finished file into place: the drop arrives as a rename, a rewrite as a change.
            // ⚠ One take at a time: two events of one write, on two pool threads, would both read it before either deletes
            // it, and every file would be attached twice.
            var gate = new object();
            FileSystemEventHandler handler = (_, _) =>
            {
                ChatDrop? drop;
                try { lock (gate) drop = Take(); }
                catch (Exception ex) { Diagnostics.Swallow("ChatDrop.Take", ex); return; }
                if (drop is not null) onDrop(drop);
            };
            watcher.Created += handler;
            watcher.Changed += handler;
            watcher.Renamed += (s, e) => handler(s, e);
            watcher.EnableRaisingEvents = true;
            return watcher;
        }
        catch (Exception ex) { Diagnostics.Swallow("ChatDrop.Watch", ex); return null; }
    }
}
