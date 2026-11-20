using System.Globalization;
using System.Text.Json;
using Inferpal.Services.Documents;

namespace Inferpal.Services.Tools;

/// <summary>
/// Agent tool: uses a page of the app being built the way a person does — clicks, typing, a choice in a list, a key — and
/// captures the state reached (pari 126): a cart with two items, a form filled in, the confirmation after an order, for a
/// README or to check what the source cannot show.
/// </summary>
/// <remarks>
/// <para>
/// ⚠ No card for the agent's own work — a page served by an app it started (a background job of <c>run_command</c>), a
/// file of the workspace: approving each path through a page would defeat the help it gives. Any other address of this
/// machine is another app of the person's (a notebook that runs what is typed in it, an admin page): asked once, its
/// "Always" for that address. The bounds hold either way: pages of this machine only, in a fresh Chromium that reaches
/// nothing but the page's own origin (<see cref="PageSession"/>); the person's <c>deny</c> rules are read at every call,
/// on the subject <c>view_page</c> and <c>http_request</c> give the same page. Each step is said with the result.
/// </para>
/// <para>
/// The page stays open for the next call of the same run — a call without <c>url</c> continues where the last one left it —
/// and closes when the run ends, when it stays unused <see cref="IdleClose"/>, or when it stops answering.
/// </para>
/// <para>
/// ⚠ The steps stop at the first one not done: the next ones were written for a state the page is not in (a click on
/// "Delete" meant for another row). The model continues from the state the result describes.
/// </para>
/// </remarks>
internal sealed class InteractPageTool : ITool, IDisposable
{
    private readonly IApprovalService _approval;
    private readonly Func<string> _getWorkspaceRoot;
    private readonly ToolImageInbox _images;
    private readonly Func<string?> _runId;
    private readonly Func<IReadOnlyList<CaptureBrowser>> _findBrowsers;
    private readonly Func<int, CancellationToken, Task<bool>> _servedByAgent;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Timer _watch;
    private PageSession? _session;
    /// <summary>The port of the agent's app the open page was opened on without a card; <c>null</c> for a page opened by
    /// the person's answer or a workspace file.</summary>
    private int? _agentPort;
    private DateTime _lastUsed;
    private volatile bool _disposed;

    /// <param name="servedByAgent">Whether a port of this machine is served by an app the agent started (a background job
    /// of <c>run_command</c>): its pages need no card.</param>
    public InteractPageTool(IApprovalService approval, Func<string> getWorkspaceRoot, ToolImageInbox images, Func<string?> runId,
                            Func<int, CancellationToken, Task<bool>> servedByAgent, Func<IReadOnlyList<CaptureBrowser>>? findBrowsers = null)
    {
        _approval = approval;
        _getWorkspaceRoot = getWorkspaceRoot;
        _images = images;
        _runId = runId;
        _servedByAgent = servedByAgent;
        _findBrowsers = findBrowsers ?? (() => [.. PageCapture.FindAll()]);
        _watch = new Timer(_ => CloseWhenDone(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public string Name => "interact_page";

    /// <inheritdoc/>
    /// <remarks>A click may make the app change its data (a Delete button, a form sent): what it changes is beyond the call.</remarks>
    public ToolEffect EffectOf(JsonElement args) => ToolEffect.Network | ToolEffect.Runs;

    internal const int MaxSteps = 25, MaxWaitMs = 10_000, DefaultWaitMs = 5_000;

    /// <summary>The time one call's steps get, the browser's start included.</summary>
    internal static readonly TimeSpan CallBudget = TimeSpan.FromSeconds(60);

    /// <summary>The time the page's address, controls and picture get after the steps: never the steps' leftover.</summary>
    internal static readonly TimeSpan FinishBudget = TimeSpan.FromSeconds(15);

    /// <summary>How long an unused page stays open within its run.</summary>
    internal static readonly TimeSpan IdleClose = TimeSpan.FromMinutes(10);

    /// <summary>How often an open page is checked: its run ended, or it stayed unused too long.</summary>
    private static readonly TimeSpan WatchEvery = TimeSpan.FromSeconds(30);

    /// <summary>The most characters the list of controls takes in a result.</summary>
    internal const int MaxControlsChars = 6_000;

    /// <summary>The most console lines a result gives.</summary>
    internal const int MaxConsoleLines = 30;

    private static readonly string[] Actions = ["goto", "click", "type", "select", "press", "wait_for", "capture"];

    // ⚠ Short on purpose: every tool's definition goes with every request, and at an 8K window they fill most of it.
    public string Description =>
        "Use a page of the app you build as a person does, in a browser cut off from the network: open it, then click, type, " +
        "choose, press keys, wait — and capture the state reached (a cart with two items, a filled form). Returns each step " +
        "done or why not, the page's controls with a selector each, its console, and the capture. The page stays open for " +
        "your next call: without url, it continues from there.";

    public object Parameters => new
    {
        type = "object",
        properties = new
        {
            url   = new { type = "string", description = "The page to open first, served on this machine (http://localhost:5173/cart)." },
            path  = new { type = "string", description = "Or an HTML file of the workspace to open first." },
            steps = new
            {
                type = "array",
                items = new
                {
                    type = "object",
                    properties = new
                    {
                        action   = new { type = "string", @enum = Actions },
                        selector = new { type = "string", description = "click, type, select, wait_for: a CSS selector" },
                        text     = new { type = "string", description = "click, wait_for: a visible text (instead of a selector); type: the text to type" },
                        value    = new { type = "string", description = "select: the entry to choose, its text or value" },
                        key      = new { type = "string", description = "press: Enter, Tab, Escape, Backspace, ArrowDown…" },
                        url      = new { type = "string", description = "goto: a path of the page's app (/cart)" },
                        timeout_ms = new { type = "integer", description = $"wait_for: at most {MaxWaitMs}" },
                    },
                    required = new[] { "action" },
                },
            },
        },
        required = new[] { "steps" },
    };

    /// <summary>Where a call opens its page: an origin of this machine, or a file of the workspace served for it.</summary>
    private sealed record Target(Uri? Origin, string OpenAt, string? ServedRoot, string Subject);

    public async Task<string> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var url = args.Str("url");
        var path = args.Str("path");
        if (!string.IsNullOrWhiteSpace(url) && !string.IsNullOrWhiteSpace(path))
            return "Error: give either url (a page served on this machine) or path (an HTML file of the workspace), not both.";
        var (steps, notes, stepsError) = ReadSteps(args);
        if (stepsError is not null) return "Error: " + stepsError;

        Target? target = null;
        if (!string.IsNullOrWhiteSpace(url))
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                return $"Error: '{url}' is not an http or https URL. An HTML file of the workspace goes in path.";
            // ⚠ As view_page: localhost or a loopback address, never a name under .localhost the system may resolve elsewhere.
            if (!HttpRequestTool.IsLoopback(uri) || uri.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
                return $"Error: '{uri.Host}' is not this machine: interact_page uses pages served here (localhost, 127.0.0.1).";
            target = new Target(uri, uri.AbsoluteUri, null, HttpRequestTool.Subject("GET", uri));
        }
        else if (!string.IsNullOrWhiteSpace(path))
        {
            var root = _getWorkspaceRoot();
            string file;
            try
            {
                file = PathSanitizer.Sanitize(path, root);
                PathSanitizer.AssertUnderRoot(file, root);
            }
            catch (ArgumentException ex) { return $"Error: {ex.Message}"; }
            if (Directory.Exists(file)) return $"'{Path.GetFileName(file)}' is a folder: name an HTML file in it.";
            if (!File.Exists(file)) return ToolFailure.Said(Localization.Strings.ToolFileNotFound(file));
            if (Path.GetExtension(file).ToLowerInvariant() is not (".html" or ".htm" or ".xhtml" or ".svg"))
                return $"'{Path.GetFileName(file)}' is not a page (HTML or SVG): read it with read_file.";
            target = new Target(null, Path.GetRelativePath(root, file).Replace('\\', '/'), root, file);
        }

        await _gate.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var report = await PlayAsync(target, steps!, ct);
            return notes.Count == 0 ? report : string.Join("\n", notes) + "\n" + report;
        }
        finally
        {
            _lastUsed = DateTime.UtcNow;
            try { if (!_disposed) _watch.Change(WatchEvery, WatchEvery); }
            catch (ObjectDisposedException) { /* disposed while the call ran */ }
            _gate.Release();
        }
    }

    private async Task<string> PlayAsync(Target? target, List<PageStep> steps, CancellationToken ct)
    {
        var runId = _runId();
        if (_session is { } open)
        {
            var sameRun = runId is not null && open.RunId == runId;
            var samePage = target is null
                || (target.ServedRoot is not null ? open.ServedRoot == target.ServedRoot
                    : open.ServedRoot is null && open.Origin == PageSession.OriginOf(target.Origin!));
            if (!sameRun || !samePage) Close();
            else if (_agentPort is { } port && !await _servedByAgent(port, ct))
            {
                // Opened without a card because the agent's app served it: another app may serve it now.
                Close();
                if (target is null)
                    return $"Error: the app the agent started no longer serves {open.Origin}, and the page was closed: give url to " +
                           "open it again.";
            }
            else if (!await open.AnswersAsync(ct))
            {
                Close();
                if (target is null)
                    return "Error: the page the last call left stopped answering (its script may loop) and was closed: give url or " +
                           "path to open it again.";
            }
        }
        if (_session is null && target is null)
            return "Error: no page is open in this run: give url (a page served on this machine) or path (an HTML file of the " +
                   "workspace) to open one.";

        // The person's rules, at every call: a rule written, or a rules file broken, since the page opened holds now.
        if (_approval.Refuses(Name, target?.Subject ?? _session!.Subject)) return ToolFailure.CancelledByUser;
        // ⚠ An address no app of the agent's serves is another app of this machine — a notebook that runs what is typed in
        // it, an admin page: asked once, its "Always" for that address. The app the agent started, and the workspace's
        // files, are its own work: no card.
        int? agentPort = null;
        if (_session is null && target!.Origin is { } origin)
        {
            if (await _servedByAgent(origin.Port, ct)) agentPort = origin.Port;
            else
            {
                var where = $"{origin.Host.ToLowerInvariant()}:{origin.Port.ToString(CultureInfo.InvariantCulture)}";
                if (!await _approval.RequestScopedApprovalAsync(Name, Localization.Strings.InteractPageCard(where), target.Subject,
                                                               $"{Name} {PageSession.OriginOf(origin)}", ct))
                    return ToolFailure.CancelledByUser;
            }
        }

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(CallBudget);
        var lines = new List<string>();
        try
        {
            // ⚠ The session in a local: a disposal while this call runs closes the field, never the call's page under it.
            PageSession session;
            if (_session is { } kept)
            {
                session = kept;
                lines.Add(target is null ? "Continued in the page the last call left." : "The page of the last call, kept open.");
            }
            else
            {
                var (opened, why) = await OpenAsync(target!, runId, budget.Token);
                if (opened is null) return why!;
                _session = session = opened;
                _agentPort = agentPort;
                if (_disposed) { Close(); throw new ObjectDisposedException(nameof(InteractPageTool)); }
                lines.Add($"A fresh browser ({opened.BrowserName}, cut off from the network but for {opened.Origin}) opened the page.");
            }
            if (target is not null)
            {
                var at = session.Served is { } served ? served.UrlOf(target.OpenAt) : target.OpenAt;
                var opening = await session.PlayAsync(new PageStep("goto", Url: at), budget.Token);
                lines.Add(opening.Done ? Capitalized(opening.Detail) + "." : "Not opened: " + opening.Detail + ".");
                if (!opening.Done) return await FinishAsync(session, lines, null, ct);
            }

            byte[]? captured = null;
            for (var i = 0; i < steps.Count; i++)
            {
                var step = steps[i];
                PageStepResult result;
                try
                {
                    if (step.Action == "capture")
                    {
                        captured = await session.CaptureAsync(budget.Token);
                        result = new(captured is not null, captured is not null ? "captured the page as it is now" : "the browser returned no picture");
                    }
                    else result = await session.PlayAsync(step, budget.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    result = new(false, $"the call's {CallBudget.TotalSeconds:0} s ran out");
                }
                catch (DevToolsCallException ex)
                {
                    result = new(false, ex.Message);
                }
                lines.Add($"{i + 1}. {step.Action}: {(result.Done ? "done" : "NOT DONE")} — {result.Detail}");
                if (!result.Done)
                {
                    if (i + 1 < steps.Count)
                        lines.Add($"Steps {i + 2}-{steps.Count} not played: they were written for a state the page did not reach.");
                    break;
                }
            }
            if (!await session.AnswersAsync(ct))
            {
                // A page whose script loops answers nothing more: closed, so a next call does not spend its time on it.
                Close();
                lines.Add("The page stopped answering (its script may loop): it was closed; a next call opens it afresh.");
                return string.Join("\n", lines);
            }
            return await FinishAsync(session, lines, captured, ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // The call's own budget, never the person's Stop.
            if (_session is { } kept && await kept.AnswersAsync(ct))
                lines.Add($"The call ran out of time ({CallBudget.TotalSeconds:0} s): the page may still be loading. Call again " +
                          "without url to continue from it.");
            else
            {
                Close();
                lines.Add($"The call ran out of time ({CallBudget.TotalSeconds:0} s) before the page answered: nothing is kept open. " +
                          "Is the app running? Call again with url to open it.");
            }
            return string.Join("\n", lines);
        }
        catch (Exception ex) when (ChromiumDevTools.IsProtocolFailure(ex))
        {
            // The browser itself failed or ended: a next call opens a fresh one.
            Close();
            lines.Add("The browser failed: " + ex.Message + ". The next call opens a fresh one.");
            return string.Join("\n", lines);
        }
    }

    /// <summary>A fresh session in the first Chromium browser that starts: one that does not hands over to the next.</summary>
    private async Task<(PageSession? Session, string? Why)> OpenAsync(Target target, string? runId, CancellationToken ct)
    {
        var browsers = _findBrowsers().Where(b => b.Engine == CaptureEngine.Chromium).ToList();
        if (browsers.Count == 0)
            return (null, "No browser on this machine can be driven: interact_page needs Google Chrome, Microsoft Edge or Chromium. " +
                          "Install one, then call interact_page again (view_page can still capture a page as it loads).");
        var failed = new List<string>();
        foreach (var browser in browsers)
        {
            // Started per browser: a server the session takes, and disposes with it — or with a browser that does not start.
            var served = target.ServedRoot is null ? null : WorkspacePages.Start(target.ServedRoot);
            var at = served is not null ? new Uri(served.UrlOf("")) : target.Origin!;
            var (session, why) = await PageSession.OpenAsync(browser, at, target.Subject, runId, served, target.ServedRoot, ct);
            if (session is not null) return (session, null);
            failed.Add($"{browser.Name}: {why}");
        }
        return (null, "No browser could be driven: " + string.Join("; ", failed) + ".");
    }

    /// <summary>The page's address, its controls, its console and the capture: what the steps left. Each part the page
    /// refuses is said in its place; the others are still given.</summary>
    private async Task<string> FinishAsync(PageSession session, List<string> lines, byte[]? captured, CancellationToken ct)
    {
        using var finish = CancellationTokenSource.CreateLinkedTokenSource(ct);
        finish.CancelAfter(FinishBudget);

        // Each part said in its place when the page refuses it or does not answer within the finishing budget: never the
        // steps' own budget, never the person's Stop.
        bool Unanswered(Exception ex) => ex is PageScriptException or DevToolsCallException
                                         || (ex is OperationCanceledException && !ct.IsCancellationRequested);
        string Why(Exception ex) => ex is OperationCanceledException
            ? $"the page did not answer within {FinishBudget.TotalSeconds:0} s" : ex.Message;
        try
        {
            var (address, title) = await session.WhereAsync(finish.Token);
            lines.Add($"Address now: {address}" + (title.Length > 0 ? $" (\"{title}\")" : ""));
        }
        catch (Exception ex) when (Unanswered(ex)) { lines.Add("Address now: unknown — " + Why(ex)); }

        try
        {
            var (controls, more) = await session.ControlsAsync(finish.Token);
            if (controls.Count == 0) lines.Add("Controls: none visible.");
            else
            {
                lines.Add("Controls — role \"name\" selector [state]:");
                var used = 0;
                var shown = 0;
                foreach (var control in controls)
                {
                    if (used + control.Length > MaxControlsChars) break;
                    lines.Add("  " + control);
                    used += control.Length;
                    shown++;
                }
                var left = controls.Count - shown + more;
                if (left > 0) lines.Add($"  … {left} more not listed.");
            }
        }
        catch (Exception ex) when (Unanswered(ex)) { lines.Add("Controls: not read — " + Why(ex)); }

        var (console, dropped) = session.TakeConsole();
        if (console.Count > 0)
        {
            lines.Add("Console (errors first; the page's own text):");
            lines.AddRange(console.Take(MaxConsoleLines).Select(l => "  " + l));
            var notShown = console.Count - Math.Min(console.Count, MaxConsoleLines) + dropped;
            if (notShown > 0) lines.Add($"  … {notShown} more lines not shown.");
        }

        try { captured ??= await session.CaptureAsync(finish.Token); }
        catch (Exception ex) when (Unanswered(ex)) { lines.Add("No capture: " + Why(ex)); return string.Join("\n", lines); }
        if (captured is not null && ChatImages.Read(captured, "page.png").Image is { } image)
        {
            _images.Put(image);
            lines.Add(string.Create(CultureInfo.InvariantCulture,
                $"The capture ({PageSession.Width}×{PageSession.Height}) follows this result."));
        }
        else lines.Add("No capture: the browser returned no picture.");
        return string.Join("\n", lines);
    }

    /// <returns>The steps, the notes of a clamped wait (said above the report), or the error.</returns>
    private static (List<PageStep>? Steps, List<string> Notes, string? Error) ReadSteps(JsonElement args)
    {
        var notes = new List<string>();
        if (!args.Has("steps")) return ([], notes, null);
        if (!args.Array("steps", out var array)) return (null, notes, "'steps' must be an array of step objects.");
        var count = array.GetArrayLength();
        if (count > MaxSteps)
            return (null, notes, $"{count} steps: one call plays {MaxSteps} at most. Split them over several calls; the page stays open.");
        var steps = new List<PageStep>();
        var index = 0;
        foreach (var e in array.EnumerateArray())
        {
            index++;
            if (e.ValueKind != JsonValueKind.Object) return (null, notes, $"step {index} is not an object.");
            var action = e.Keyword("action");
            if (action is null || !Actions.Contains(action))
                return (null, notes, $"step {index}: unknown action '{e.Str("action")}' ({string.Join(", ", Actions)}).");
            var selector = Blank(e.Str("selector"));
            var text = e.Str("text");
            var value = e.Str("value");
            var key = Blank(e.Str("key"));
            var url = Blank(e.Str("url"));
            string? missing = action switch
            {
                "goto" when url is null => "a url",
                "click" or "wait_for" when selector is null && string.IsNullOrEmpty(text) => "a selector or a text",
                "type" when selector is null => "the field's selector",
                "type" when text is null => "the text to type",
                "select" when selector is null => "the list's selector",
                "select" when string.IsNullOrEmpty(value) => "the entry to choose (value)",
                "press" when key is null => "a key",
                _ => null,
            };
            if (missing is not null) return (null, notes, $"step {index} ({action}) needs {missing}.");
            if (action == "press" && !PageSession.Keys.ContainsKey(key!))
                return (null, notes, $"step {index}: unknown key '{key}' ({string.Join(", ", PageSession.Keys.Keys)}).");
            var (wait, note) = ClampedArgument.Read(e, "timeout_ms", DefaultWaitMs, 0, MaxWaitMs);
            if (note is not null) notes.Add($"Step {index}: {note}");
            steps.Add(new PageStep(action, selector, text, value, key, url, wait));
        }
        return (steps, notes, null);

        static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    }

    private static string Capitalized(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    /// <summary>Closes an open page whose run ended or that stayed unused <see cref="IdleClose"/>; never one a call uses.</summary>
    internal void CloseWhenDone()
    {
        if (!_gate.Wait(0)) return;
        try
        {
            if (_session is not { } open) { _watch.Change(Timeout.Infinite, Timeout.Infinite); return; }
            if (open.RunId != _runId() || DateTime.UtcNow - _lastUsed > IdleClose)
            {
                Close();
                _watch.Change(Timeout.Infinite, Timeout.Infinite);
            }
        }
        catch (ObjectDisposedException) { /* disposed meanwhile */ }
        finally { _gate.Release(); }
    }

    private void Close()
    {
        _agentPort = null;
        Interlocked.Exchange(ref _session, null)?.Dispose();
    }

    /// <remarks>Never waits for a call that runs: its browser killed, the call fails with the protocol and says so.</remarks>
    public void Dispose()
    {
        _disposed = true;
        _watch.Dispose();
        Close();
    }
}
