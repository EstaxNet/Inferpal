using System.IO;
using Microsoft.VisualStudio.Shell;

namespace Inferpal.GhostText;

/// <summary>
/// Answers the <c>@output</c> mention (<see cref="OutputPaneSignal"/>, asked by the out-of-process window) with the text
/// of the Output window's pane — the one shown, else Build — read here because only devenv's side can: a tool window's
/// text does not cross to the extension's process. One per devenv, started with the package.
/// </summary>
internal static class OutputPaneWatcher
{
    /// <summary>The Build pane, by its GUID: its name is translated ("Build", "Erstellen"…).</summary>
    private const string BuildPane = "{1BD8A850-02D1-11D1-BEE7-00A0C913D1F8}";

    private static readonly object _gate = new object();
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(10);
    private static FileSystemWatcher? _watcher;
    private static Timer? _retry;
    private static bool _stopped;
    private static string? _lastId;

    internal static void Start()
    {
        lock (_gate)
        {
            _stopped = false;
            Ensure();
        }
    }

    internal static void Stop()
    {
        lock (_gate)
        {
            _stopped = true;
            _retry?.Dispose();
            _retry = null;
            _watcher?.Dispose();
            _watcher = null;
        }
    }

    /// <summary>Under the lock: the watcher, or another try shortly — without it, a watcher that cannot start once leaves
    /// the mention waiting out its budget for the rest of the session.</summary>
    private static void Ensure()
    {
        if (_watcher is not null || _stopped) return;
        _watcher = Create();
        if (_watcher is null)
            (_retry ??= new Timer(_ => { lock (_gate) Ensure(); })).Change(RetryDelay, Timeout.InfiniteTimeSpan);
    }

    /// <summary>The watcher, or <c>null</c> when it cannot start.</summary>
    private static FileSystemWatcher? Create()
    {
        try
        {
            var path = OutputPaneSignal.RequestPath;
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
        catch (Exception ex) { Diagnostics.Swallow("Output.Watcher", ex); return null; }
    }

    /// <summary>A watcher that failed raises nothing more: started again, or the mention waits out its budget each time.</summary>
    private static void OnError(object sender, ErrorEventArgs e)
    {
        Diagnostics.Swallow("Output.Watcher", e.GetException());
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
        // Read outside the lock: the read touches the disk.
        var ask = OutputPaneSignal.TryTake(Volatile.Read(ref _lastId));
        if (ask is null) return;
        lock (_gate)
        {
            if (ask.Id == _lastId) return;
            _lastId = ask.Id;
        }
        _ = ServeAsync(ask);
    }

    private static async Task ServeAsync(OutputPaneAsk ask)
    {
        OutputPaneAnswer answer;
        try
        {
            // EnvDTE is UI-thread-only.
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            answer = Read(ask.Id);
        }
        catch (Exception ex)
        {
            Diagnostics.Swallow("Output.Read", ex);
            answer = new OutputPaneAnswer(ask.Id, null, null, false, null, $"{ex.GetType().Name}: {ex.Message}");
        }
        // Written off the UI thread: some 40,000 characters of JSON, a delete and a rename.
        await Task.Run(() => OutputPaneSignal.Answer(answer));
    }

    private static OutputPaneAnswer Read(string id)
    {
#if NETFRAMEWORK
        ThreadHelper.ThrowIfNotOnUIThread();
        // ⚠ The typed interop: through late binding, the panes come back with empty names, and none at all.
        if (!(Package.GetGlobalService(typeof(EnvDTE.DTE)) is EnvDTE80.DTE2 dte))
            return new OutputPaneAnswer(id, null, null, false, null, "DTE automation is unavailable");
        var window = dte.ToolWindows.OutputWindow;
        // The pane the person looks at; without one shown, the build's.
        EnvDTE.OutputWindowPane? pane = null;
        try { pane = window.ActivePane; } catch (Exception ex) { Diagnostics.Swallow("Output.ActivePane", ex); }
        if (pane is null)
            foreach (EnvDTE.OutputWindowPane p in window.OutputWindowPanes)
                if (string.Equals(p.Guid, BuildPane, StringComparison.OrdinalIgnoreCase)) pane = p;
        if (pane is null) return new OutputPaneAnswer(id, null, null, false, OutputPaneSignal.ReasonNone);

        var name = pane.Name;
        string text;
        bool cut;
        try
        {
            // Only the end is read: a detailed build log runs to megabytes, copied on this thread for 40,000 characters.
            // Moved back only from a pane longer than that: a short one is read from its start.
            var doc = pane.TextDocument;
            cut = doc.EndPoint.AbsoluteCharOffset > OutputPaneSignal.MaxChars;
            var start = cut ? doc.EndPoint.CreateEditPoint() : doc.StartPoint.CreateEditPoint();
            if (cut) start.CharLeft(OutputPaneSignal.MaxChars);
            text = start.GetText(doc.EndPoint);
        }
        catch (Exception ex)
        {
            // Debug, Source Control: panes Visual Studio does not give as a document.
            return new OutputPaneAnswer(id, name, null, false, OutputPaneSignal.ReasonUnreadable, ex.Message);
        }
        if (string.IsNullOrWhiteSpace(text)) return new OutputPaneAnswer(id, name, null, false, OutputPaneSignal.ReasonEmpty);
        return new OutputPaneAnswer(id, name, text, cut, null);
#else
        // EnvDTE lives in devenv; this leg exists for the tests and never runs there.
        return new OutputPaneAnswer(id, null, null, false, null, "the Output window is read in devenv only");
#endif
    }
}
