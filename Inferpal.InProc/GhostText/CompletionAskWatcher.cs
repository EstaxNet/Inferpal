using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace Inferpal.GhostText;

/// <summary>
/// Hands the key of inline completion (<see cref="CompletionAskSignal"/>, written by the out-of-process command) to the
/// controller of the editor that has the focus, and says in the status bar what the key could not do. One per devenv,
/// started with the first editor.
/// </summary>
/// <remarks>A watcher, not a poll: the request is a keystroke, and the inline diff's half-second poll would be felt.</remarks>
internal static class CompletionAskWatcher
{
    private static readonly object _gate = new object();
    private static readonly List<GhostTextController> _controllers = new List<GhostTextController>();
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(10);
    private static FileSystemWatcher? _watcher;
    private static Timer? _retry;
    private static string? _lastId;

    internal static void Add(GhostTextController controller)
    {
        lock (_gate)
        {
            _controllers.Add(controller);
            Ensure();
        }
    }

    /// <summary>Under the lock: the watcher, or another try shortly — without it, a watcher that cannot start (a lock on
    /// the folder, a failed restart) leaves the key dead until another editor opens, which may be never.</summary>
    private static void Ensure()
    {
        if (_watcher is not null) return;
        _watcher = Start();
        if (_watcher is null)
            (_retry ??= new Timer(_ => { lock (_gate) Ensure(); })).Change(RetryDelay, Timeout.InfiniteTimeSpan);
    }

    internal static void Remove(GhostTextController controller)
    {
        lock (_gate) _controllers.Remove(controller);
    }

    /// <summary>Says <paramref name="text"/> in Visual Studio's status bar, from any thread.</summary>
    internal static void Say(string text) => _ = SayAsync(text);

    private static async Task SayAsync(string text)
    {
        try
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            (Package.GetGlobalService(typeof(SVsStatusbar)) as IVsStatusbar)?.SetText(text);
        }
        catch (Exception ex) { Diagnostics.Swallow("GhostText.StatusBar", ex); }
    }

    /// <summary>The watcher, or <c>null</c> when it cannot start — the next editor tries again.</summary>
    private static FileSystemWatcher? Start()
    {
        try
        {
            var path = CompletionAskSignal.RequestPath;
            var dir  = Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(dir);
            var watcher = new FileSystemWatcher(dir, Path.GetFileName(path))
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
            };
            // The writer renames a finished file into place: the request arrives as a rename, a rewrite as a change.
            watcher.Created += OnSignal;
            watcher.Changed += OnSignal;
            watcher.Renamed += OnSignal;
            watcher.Error   += OnError;
            watcher.EnableRaisingEvents = true;
            return watcher;
        }
        catch (Exception ex) { Diagnostics.Swallow("GhostText.AskWatcher", ex); return null; }
    }

    /// <summary>A watcher that failed (its buffer overflowed, its folder was deleted) raises nothing more: started again,
    /// or the key stays dead for the rest of the session.</summary>
    private static void OnError(object sender, ErrorEventArgs e)
    {
        Diagnostics.Swallow("GhostText.AskWatcher", e.GetException());
        lock (_gate)
        {
            if (!ReferenceEquals(sender, _watcher)) return;
            _watcher.Dispose();
            _watcher = null;
            Ensure();
        }
    }

    private static void OnSignal(object sender, FileSystemEventArgs e)
    {
        // Read outside the lock: editors opening and closing on the UI thread take it, and the read touches the disk.
        var ask = CompletionAskSignal.TryTake(Volatile.Read(ref _lastId));
        if (ask is null) return;
        lock (_gate)
        {
            if (ask.Id == _lastId) return;
            _lastId = ask.Id;
        }
        _ = ServeAsync(ask);
    }

    private static async Task ServeAsync(CompletionAsk ask)
    {
        try
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            GhostTextController[] controllers;
            lock (_gate) controllers = _controllers.ToArray();
            if (controllers.FirstOrDefault(c => c.HasFocus) is { } focused) focused.Ask(ask);
            // The key pressed where no code editor has the focus: said, never dropped.
            else await SayAsync(ask.Refusal ?? ask.NoEditor);
        }
        catch (Exception ex) { Diagnostics.Swallow("GhostText.Ask", ex); }
    }
}
