using System.Collections.Generic;
using System.IO;
using Inferpal.Services.CodeActions;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace Inferpal.GhostText;

/// <summary>
/// Answers the Error List's "Fix with Inferpal" (<see cref="ErrorListSelectionSignal"/>, asked by the out-of-process
/// command) with the lines chosen in the list — read here because only devenv's side can: the selection of a tool window
/// does not cross to the extension's process. One per devenv, started with the package.
/// </summary>
internal static class ErrorListSelectionWatcher
{
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

    /// <summary>Under the lock: the watcher, or another try shortly — without it, a watcher that cannot start once (a lock
    /// on the folder) leaves the command waiting out its budget for the rest of the session.</summary>
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
            var path = ErrorListSelectionSignal.RequestPath;
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
        catch (Exception ex) { Diagnostics.Swallow("ErrorList.Watcher", ex); return null; }
    }

    /// <summary>A watcher that failed raises nothing more: started again, or the command waits out its budget each time.</summary>
    private static void OnError(object sender, ErrorEventArgs e)
    {
        Diagnostics.Swallow("ErrorList.Watcher", e.GetException());
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
        var ask = ErrorListSelectionSignal.TryTake(Volatile.Read(ref _lastId));
        if (ask is null) return;
        lock (_gate)
        {
            if (ask.Id == _lastId) return;
            _lastId = ask.Id;
        }
        _ = ServeAsync(ask);
    }

    private static async Task ServeAsync(ErrorListAsk ask)
    {
        ErrorListAnswer answer;
        try
        {
            // The list's table is a WPF control.
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            answer = Read(ask.Id);
        }
        catch (Exception ex)
        {
            Diagnostics.Swallow("ErrorList.Selection", ex);
            answer = new ErrorListAnswer(ask.Id, new List<ProblemEntry>(), $"{ex.GetType().Name}: {ex.Message}");
        }
        // Written off the UI thread, as the Output pane's: a read, a delete and a rename.
        await Task.Run(() => ErrorListSelectionSignal.Answer(answer));
    }

    private static ErrorListAnswer Read(string id)
    {
#if NETFRAMEWORK
        ThreadHelper.ThrowIfNotOnUIThread();
        if (!(Package.GetGlobalService(typeof(SVsErrorList)) is IErrorList list))
            return new ErrorListAnswer(id, new List<ProblemEntry>(), "the Error List service is unavailable");
        if (list.TableControl is null)
            return new ErrorListAnswer(id, new List<ProblemEntry>(), "the Error List has no table");

        var problems = new List<ProblemEntry>();
        foreach (var entry in list.TableControl.SelectedEntries)
            problems.Add(ToProblem(key => entry.TryGetValue(key, out var value) ? value : null));
        return new ErrorListAnswer(id, problems);
#else
        // The table API is .NET Framework only; this leg exists for the tests and never runs in devenv.
        return new ErrorListAnswer(id, new List<ProblemEntry>(), "the Error List is read in devenv only");
#endif
    }

    // The values of StandardTableKeyNames (Microsoft.VisualStudio.Shell.Framework), whose .NET 8 build — the test leg's —
    // does not have the table API.
    internal const string KeyDocument = "documentname";
    internal const string KeyLine     = "line";
    internal const string KeyColumn   = "column";
    internal const string KeySeverity = "errorseverity";
    internal const string KeyCode     = "errorcode";
    internal const string KeyText     = "text";

    /// <summary>A line of the list, from its columns by key; <c>null</c> for a column the line lacks.</summary>
    internal static ProblemEntry ToProblem(Func<string, object?> column)
    {
        // The table's line and column are 0-based (the list shows them plus one); a problem with no line has none.
        var line = column(KeyLine) is int l && l >= 0 ? l + 1 : 0;
        var at   = line > 0 && column(KeyColumn) is int c && c >= 0 ? c + 1 : 0;
        // A severity missing, or boxed as a bare number by another table source, is never promoted to an error: errors
        // are listed first and fill the cap.
        var severity = column(KeySeverity) switch
        {
            __VSERRORCATEGORY category => (int)category,
            int number                 => number,
            _                          => -1,
        } switch
        {
            (int)__VSERRORCATEGORY.EC_ERROR   => "error",
            (int)__VSERRORCATEGORY.EC_WARNING => "warning",
            _                                 => "info",
        };
        return new ProblemEntry(
            File:     column(KeyDocument) is string file && file.Length > 0 ? file : null,
            Line:     line,
            Column:   at,
            Severity: severity,
            Code:     column(KeyCode) is string code && code.Length > 0 ? code : null,
            Text:     column(KeyText) as string ?? string.Empty);
    }
}
