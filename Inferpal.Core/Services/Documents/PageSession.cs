using System.Globalization;
using System.Text.Json;
using static Inferpal.Services.JsonFields;
using Inferpal.Services.Tools;

namespace Inferpal.Services.Documents;

/// <summary>One step the model asked to play in a page (<c>interact_page</c>).</summary>
internal sealed record PageStep(string Action, string? Selector = null, string? Text = null, string? Value = null,
                                string? Key = null, string? Url = null, int TimeoutMs = 5000);

/// <summary>What a step did: done, or why not — said in English, to the model.</summary>
internal sealed record PageStepResult(bool Done, string Detail);

/// <summary>A script run in the page threw: the page's own error, the browser is fine.</summary>
internal sealed class PageScriptException(string message) : Exception(message);

/// <summary>
/// A page of this machine open in a headless Chromium driven over its DevTools protocol (pari 126): steps played the way a
/// person plays them — a real mouse click at the element's centre, text typed into the focused field — then a capture.
/// </summary>
/// <remarks>
/// <para>
/// ⚠ The browser reaches nothing but the page's own origin (<see cref="PageCapture.ChromiumFlags"/>): a page of the Internet,
/// another port of this machine, a navigation away are cut by the browser itself, whatever a step or the page's script asks.
/// Its profile is new and removed with the session: nothing of the person's browser, no cookie kept beyond it, and a
/// download lands in the session's own folder, removed with it.
/// </para>
/// <para>
/// A page's <c>alert()</c>, <c>confirm()</c> and <c>prompt()</c> are answered OK as they open (the click that opened one
/// waits for the answer) and said with the step. A step the page's script or the browser refuses is a step not done; only
/// a browser that exits or stops answering ends the session.
/// </para>
/// </remarks>
internal sealed class PageSession : IDisposable
{
    internal const int Width = 1280, Height = 800;

    /// <summary>The keys <c>press</c> knows, with their Windows virtual key code.</summary>
    internal static readonly IReadOnlyDictionary<string, int> Keys = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
    {
        ["Enter"] = 13, ["Tab"] = 9, ["Escape"] = 27, ["Backspace"] = 8, ["Delete"] = 46, ["Space"] = 32,
        ["ArrowDown"] = 40, ["ArrowUp"] = 38, ["ArrowLeft"] = 37, ["ArrowRight"] = 39,
        ["Home"] = 36, ["End"] = 35, ["PageDown"] = 34, ["PageUp"] = 33,
    };

    /// <summary>How long the browser may take to open its DevTools port: a cold start on a slow disk is not a refusal.</summary>
    private static readonly TimeSpan PortWait = TimeSpan.FromSeconds(20);

    /// <summary>The event counted when the page's MAIN frame starts a load: the browser says it of every frame, an iframe's
    /// widget included, whose load the page's own load event never follows.</summary>
    private const string MainFrameLoading = "inferpal.mainFrameStartedLoading";

    private const int MaxConsole = 200, MaxLine = 300, MaxControls = 80;

    private readonly ChromiumBrowser _browser;
    private readonly string _work;
    private readonly PageEvents _events;

    private PageSession(ChromiumBrowser browser, string work, PageEvents events, string origin, string subject,
                        string browserName, string? runId, WorkspacePages? served, string? servedRoot)
    {
        _browser = browser;
        _work = work;
        _events = events;
        Origin = origin;
        Subject = subject;
        BrowserName = browserName;
        RunId = runId;
        Served = served;
        ServedRoot = servedRoot;
    }

    /// <summary>What the session reads and keeps of the page's events, filled on the connection's pump.</summary>
    private sealed class PageEvents
    {
        public readonly List<string> Console = [];
        public readonly List<string> Dialogs = [];
        public int Dropped;
        public readonly PageNetwork Network = new();
        public volatile string? MainFrame;
    }

    /// <summary>The page's origin, <c>scheme://host:port</c>: the only address its browser reaches.</summary>
    public string Origin { get; }

    /// <summary>What the person's permission rules read for this page: the same for every call that uses it.</summary>
    public string Subject { get; }

    public string BrowserName { get; }

    /// <summary>The run the session was opened in: a call of another run opens a fresh one.</summary>
    public string? RunId { get; }

    /// <summary>The server of the workspace's files, when the page is one of them.</summary>
    public WorkspacePages? Served { get; }

    /// <summary>The workspace root <see cref="Served"/> serves.</summary>
    public string? ServedRoot { get; }

    /// <summary>Whether the browser runs and its connection holds.</summary>
    public bool Alive => _browser.Running;

    /// <summary>The origin of <paramref name="uri"/> as the browser compares it.</summary>
    internal static string OriginOf(Uri uri) =>
        string.Create(CultureInfo.InvariantCulture, $"{uri.Scheme}://{uri.Host}:{uri.Port}").ToLowerInvariant();

    /// <summary>Opens a blank page in a fresh <paramref name="browser"/> that reaches only <paramref name="origin"/>.</summary>
    /// <param name="subject">What the person's rules read for this page (<see cref="Subject"/>).</param>
    /// <param name="served">Handed to the session, disposed with it — or here, when the browser does not start.</param>
    internal static async Task<(PageSession? Session, string? Why)> OpenAsync(CaptureBrowser browser, Uri origin, string subject,
                                                                             string? runId, WorkspacePages? served, string? servedRoot,
                                                                             CancellationToken ct)
    {
        string? work = null;
        var events = new PageEvents();
        try
        {
            work = PageCapture.WorkFolder(browser);
            var profile = Directory.CreateDirectory(Path.Combine(work, "profile")).FullName;
            WriteDownloadPreferences(profile, Directory.CreateDirectory(Path.Combine(work, "downloads")).FullName);
            var flags = PageCapture.ChromiumFlags(profile, origin.Host, origin.Port.ToString(CultureInfo.InvariantCulture));
            var (driven, why, _) = await ChromiumDevTools.LaunchAsync(browser.Executable, flags, profile, Width, Height, PortWait,
                                                                      ["Page.loadEventFired"], (page, name, p) => OnEvent(page, name, p, events), ct);
            if (driven is null)
            {
                Remove(work);
                served?.Dispose();
                return (null, why);
            }
            ChromiumDevTools.PortOpened(browser.Executable);   // its protocol answers: a capture drives it again
            var session = new PageSession(driven, work, events, OriginOf(origin), subject, browser.Name, runId, served, servedRoot);
            try
            {
                foreach (var domain in new[] { "Page.enable", "Runtime.enable", "Log.enable", "Network.enable" })
                    await driven.Page.CallAsync(domain, new { }, ct);
                var tree = await driven.Page.CallAsync("Page.getFrameTree", new { }, ct);
                events.MainFrame = tree.GetProperty("frameTree").GetProperty("frame").GetProperty("id").GetString();
                // The profile's preferences already send a download to the session's folder; this refuses it outright.
                try { await driven.Page.CallAsync("Page.setDownloadBehavior", new { behavior = "deny" }, ct); }
                catch (DevToolsCallException) { /* a browser without it downloads into the session's folder */ }
            }
            catch
            {
                session.Dispose();
                throw;
            }
            return (session, null);
        }
        catch (Exception ex) when (ChromiumDevTools.IsProtocolFailure(ex) || ex is DevToolsCallException or OperationCanceledException)
        {
            if (work is not null) Remove(work);
            served?.Dispose();
            if (ex is OperationCanceledException) throw;
            return (null, ChromiumDevTools.ProtocolFailed + ex.Message);
        }
    }

    /// <summary>A download goes into <paramref name="downloads"/>, never the person's own Downloads folder, without asking.</summary>
    private static void WriteDownloadPreferences(string profile, string downloads)
    {
        var folder = Directory.CreateDirectory(Path.Combine(profile, "Default")).FullName;
        File.WriteAllText(Path.Combine(folder, "Preferences"), JsonSerializer.Serialize(new
        {
            download = new { default_directory = downloads, prompt_for_download = false, directory_upgrade = true },
            savefile = new { default_directory = downloads },
        }));
    }

    /// <summary>The page's console, its dialogs answered as they open, and the main frame's loads.</summary>
    private static void OnEvent(DevToolsConnection page, string name, JsonElement p, PageEvents events)
    {
        string? line = null;
        if (name.StartsWith("Network.", StringComparison.Ordinal))
        {
            events.Network.OnEvent(name, p);
            return;
        }
        switch (name)
        {
            case "Page.frameStartedLoading":
                if (Str(p, "frameId") is { Length: > 0 } frame && frame == events.MainFrame) page.Record(MainFrameLoading);
                return;
            case "Runtime.consoleAPICalled":
                var args = p.TryGetProperty("args", out var a) && a.ValueKind == JsonValueKind.Array
                    ? string.Join(" ", a.EnumerateArray().Select(Shown))
                    : "";
                line = $"{Str(p, "type")}: {args}";
                break;
            case "Runtime.exceptionThrown":
                var details = p.GetProperty("exceptionDetails");
                var description = details.TryGetProperty("exception", out var e) ? Str(e, "description") : "";
                line = "error: uncaught " + (description.Length > 0 ? description.Split('\n')[0] : Str(details, "text"));
                break;
            case "Log.entryAdded":
                var entry = p.GetProperty("entry");
                var url = Str(entry, "url");
                line = $"{Str(entry, "level")}: {Str(entry, "text")}"
                     + (Uri.TryCreate(url, UriKind.Absolute, out var at) ? $" ({at.PathAndQuery})" : "");
                break;
            case "Page.javascriptDialogOpening":
                var kind = Str(p, "type");
                lock (events) events.Dialogs.Add($"the page showed a {kind} \"{Cut(Str(p, "message"))}\": answered OK");
                page.Post("Page.handleJavaScriptDialog", new { accept = true, promptText = Str(p, "defaultPrompt") });
                return;
        }
        if (line is null) return;
        line = Cut(line);
        lock (events)
        {
            if (events.Console.Count < MaxConsole) { events.Console.Add(line); return; }
            // Full: an error takes the place of the oldest line that is not one, and whatever is left out is counted.
            events.Dropped++;
            if (IsError(line) && events.Console.FindIndex(l => !IsError(l)) is var other and >= 0)
            {
                events.Console.RemoveAt(other);
                events.Console.Add(line);
            }
        }

        static string Shown(JsonElement v) =>
            v.TryGetProperty("value", out var value) ? (value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.GetRawText())
            : v.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "";
    }

    private static bool IsError(string line) => line.StartsWith("error", StringComparison.Ordinal);

    private static string Cut(string s)
    {
        s = OneLine.Flat(s);
        return s.Length <= MaxLine ? s : s[..MaxLine] + "…";
    }

    /// <summary>The page's requests ended since the last call (<see cref="PageNetwork.Lines"/>); forgets them.</summary>
    internal IReadOnlyList<string> TakeNetwork() => _events.Network.Lines(Origin);

    /// <summary>The start of each answer that carried an error status, read from the browser while it still holds it, what
    /// looks like a credential masked (best effort); an answer it no longer holds is said so.</summary>
    internal async Task<IReadOnlyList<string>> FailedBodiesAsync(CancellationToken ct)
    {
        var bodies = new List<string>();
        var failed = _events.Network.FailedWithBody(Origin);
        foreach (var (id, label, bytes) in failed.Take(MaxBodies))
        {
            // ⚠ The browser sends a body whole in one message, copied several times on the way: a large one is named, never
            // asked for, only to keep its first few hundred characters.
            if (bytes > MaxBodyBytes)
            {
                bodies.Add(string.Create(CultureInfo.InvariantCulture, $"  {label}: ({bytes:0} bytes, not read)"));
                continue;
            }
            string shown;
            try
            {
                var body = await _browser.Page.CallAsync("Network.getResponseBody", new { requestId = id }, ct);
                var text = body.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";
                shown = body.TryGetProperty("base64Encoded", out var encoded) && encoded.ValueKind == JsonValueKind.True
                    ? "(encoded content, not shown)"
                    : text.Length == 0 ? "(empty)" : Clip(text);
            }
            catch (DevToolsCallException) { shown = "(the browser no longer holds it)"; }
            catch (Exception ex) when (ex is OperationCanceledException || ChromiumDevTools.IsProtocolFailure(ex))
            {
                // What was read is kept: the rest is said not read, with why.
                bodies.Add("  … the other bodies were not read: " + (ex is OperationCanceledException ? "the page did not answer in time" : ex.Message));
                return bodies;
            }
            bodies.Add($"  {label}: {shown}");
        }
        if (failed.Count > MaxBodies) bodies.Add($"  … {failed.Count - MaxBodies} more error answers, their bodies not read.");
        return bodies;

        // Masked on a generous slice BEFORE the cut: a credential cut short is no longer one the redactor recognises.
        static string Clip(string text) => OneLine.Masked(text, BodyChars);
    }

    private const int BodyChars = 300, MaxBodies = 15, MaxBodyBytes = 256 * 1024;

    /// <summary>The console's lines since the last call, errors first, and how many were left out; empties it.</summary>
    internal (IReadOnlyList<string> Lines, int Dropped) TakeConsole()
    {
        lock (_events)
        {
            var lines = _events.Console.OrderByDescending(IsError).ToList();
            var dropped = _events.Dropped;
            _events.Console.Clear();
            _events.Dropped = 0;
            return (lines, dropped);
        }
    }

    /// <summary>How long the page has to answer a script before it is taken for one whose script loops.</summary>
    /// <remarks>⚠ Seconds, not a few: a busy machine (a model answering on the same processor, a build) runs a script late,
    /// and a page wrongly taken for looping is closed with the state the calls brought it to. A page that loops never
    /// answers: a longer wait only delays its close.</remarks>
    internal static readonly TimeSpan AnswerBudget = TimeSpan.FromSeconds(10);

    /// <summary>Whether the page answers a script within <see cref="AnswerBudget"/>: a page whose script loops never does.</summary>
    internal async Task<bool> AnswersAsync(CancellationToken ct)
    {
        if (!Alive) return false;
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
        wait.CancelAfter(AnswerBudget);
        try
        {
            await _browser.Page.CallAsync("Runtime.evaluate", new { expression = "1", returnByValue = true }, wait.Token);
            return true;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return false; }
        catch (Exception ex) when (ChromiumDevTools.IsProtocolFailure(ex) || ex is DevToolsCallException) { return false; }
    }

    /// <summary>Plays <paramref name="step"/>: what the page or the browser refuses is a step not done; a browser that
    /// exits throws (<see cref="ChromiumDevTools.IsProtocolFailure"/>).</summary>
    internal async Task<PageStepResult> PlayAsync(PageStep step, CancellationToken ct)
    {
        int before;
        lock (_events) before = _events.Dialogs.Count;
        PageStepResult result;
        try
        {
            result = step.Action switch
            {
                "goto" => await GotoAsync(step.Url!, ct),
                "click" => await ClickAsync(step, ct),
                "type" => await TypeAsync(step, ct),
                "select" => await SelectAsync(step, ct),
                "press" => await PressAsync(step.Key!, ct),
                "wait_for" => await WaitForAsync(step, ct),
                _ => new PageStepResult(false, $"unknown action {step.Action}"),
            };
        }
        catch (Exception ex) when (ex is PageScriptException or DevToolsCallException)
        {
            result = new(false, ex.Message);
        }
        lock (_events)
            if (_events.Dialogs.Count > before)
                result = result with { Detail = result.Detail + "; " + string.Join("; ", _events.Dialogs.Skip(before)) };
        return result;
    }

    /// <summary>The page's address, relative to its origin, and its title.</summary>
    internal async Task<(string Address, string Title)> WhereAsync(CancellationToken ct)
    {
        var where = await EvalAsync("[location.href, document.title]", ct);
        var href = where[0].GetString() ?? "";
        return (Relative(href), where[1].GetString() ?? "");
    }

    /// <summary>The page's address without its origin, as the model wrote it.</summary>
    private string Relative(string href) =>
        Uri.TryCreate(href, UriKind.Absolute, out var uri) && OriginOf(uri) == Origin ? uri.PathAndQuery + uri.Fragment : href;

    /// <summary>A PNG of the page's viewport, <see cref="Width"/> × <see cref="Height"/>.</summary>
    internal async Task<byte[]?> CaptureAsync(CancellationToken ct)
    {
        var shot = await _browser.Page.CallAsync("Page.captureScreenshot", new { format = "png" }, ct);
        return shot.TryGetProperty("data", out var data) && data.GetString() is { } base64 ? Convert.FromBase64String(base64) : null;
    }

    /// <summary>
    /// The page's visible controls — role, name, a CSS selector that finds that control alone, state — at most
    /// <see cref="MaxControls"/>, and how many more were not listed.
    /// </summary>
    internal async Task<(IReadOnlyList<string> Controls, int More)> ControlsAsync(CancellationToken ct)
    {
        var listed = await EvalAsync(ControlsScript.Replace("__MAX__", MaxControls.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
                                                   .Replace("__SELECTOR__", PageAccessibility.SelectorFunction, StringComparison.Ordinal), ct);
        var controls = listed.GetProperty("controls").EnumerateArray().Select(c => c.GetString() ?? "").ToList();
        return (controls, listed.GetProperty("more").GetInt32());
    }

    private async Task<PageStepResult> GotoAsync(string url, CancellationToken ct)
    {
        var current = await EvalAsync("location.href", ct);
        var baseUri = Uri.TryCreate(current.GetString(), UriKind.Absolute, out var b) && b.Scheme.StartsWith("http", StringComparison.Ordinal)
            ? b : new Uri(Origin + "/");
        if (!Uri.TryCreate(baseUri, url, out var target))
            return new(false, $"'{url}' is not an address");
        if (OriginOf(target) != Origin)
            return new(false, $"refused: {target.GetLeftPart(UriPartial.Path)} is not this page's address ({Origin}); open it with interact_page url=…");
        var mark = _browser.Page.Mark;
        var navigated = await _browser.Page.CallAsync("Page.navigate", new { url = target.AbsoluteUri }, ct);
        if (navigated.TryGetProperty("errorText", out var error) && error.GetString() is { Length: > 0 } why)
            return new(false, $"{Relative(target.AbsoluteUri)} did not load ({why}): is the app running?");
        // A move within the document (#section) loads nothing: no load event follows it.
        if (navigated.TryGetProperty("loaderId", out var loader) && loader.GetString() is { Length: > 0 }) await LoadedAsync(mark, ct);
        else await Task.Delay(200, ct);
        return new(true, "opened " + Relative(target.AbsoluteUri));
    }

    private async Task<PageStepResult> ClickAsync(PageStep step, CancellationToken ct)
    {
        var found = await FindAsync(step.Selector, step.Selector is null ? step.Text : null, ct);
        if (Unusable(found, step) is { } refusal) return refusal;
        var name = found.GetProperty("name").GetString();
        var also = Also(found, step);
        var mark = _browser.Page.Mark;
        if (found.GetProperty("option").GetBoolean())
        {
            // A list's entry is chosen, not clicked: a headless browser draws no drop-down to click in.
            var chosen = await EvalAsync("""
                (() => { const o = window.__inferpalTarget, s = o.closest("select"); s.value = o.value;
                  s.dispatchEvent(new Event("input", { bubbles: true })); s.dispatchEvent(new Event("change", { bubbles: true }));
                  return [o.text.trim(), s.tagName.toLowerCase() + (s.id ? "#" + s.id : "")]; })()
                """, ct);
            await SettleAsync(mark, ct);
            return new(true, $"chose \"{chosen[0].GetString()}\" in {chosen[1].GetString()}{also}");
        }
        // ⚠ Never clicked when covered: the click would land on what covers it — a dialog's button, a banner's — that no
        // step named.
        if (found.TryGetProperty("covered", out var covered) && covered.GetString() is { } cover)
            return new(false, $"{name} is covered by {cover}: a click would land on it, so none was made");
        await MouseClickAsync(found, ct);
        await SettleAsync(mark, ct);
        return new(true, $"clicked {name}{also}");
    }

    private async Task<PageStepResult> TypeAsync(PageStep step, CancellationToken ct)
    {
        var found = await FindAsync(step.Selector, null, ct);
        if (Unusable(found, step) is { } refusal) return refusal;
        var name = found.GetProperty("name").GetString();
        if (!found.GetProperty("editable").GetBoolean())
            return new(false, $"{name} is not a field to type in" + (name!.StartsWith("select", StringComparison.Ordinal) ? ": use select" : ""));
        if (found.TryGetProperty("covered", out var covered) && covered.GetString() is { } cover)
            return new(false, $"{name} is covered by {cover}: a click to focus it would land on it, so none was made");
        var mark = _browser.Page.Mark;
        await MouseClickAsync(found, ct);
        var before = await EvalAsync("""
            (() => { const e = window.__inferpalTarget; if (document.activeElement !== e) e.focus();
              if (e.select) e.select(); else document.execCommand("selectAll");
              return { focused: document.activeElement === e, value: e.isContentEditable ? e.innerText : e.value }; })()
            """, ct);
        if (!before.GetProperty("focused").GetBoolean()) return new(false, $"{name} did not take the focus");
        var text = step.Text ?? "";
        await _browser.Page.CallAsync("Input.insertText", new { text }, ct);
        await SettleAsync(mark, ct);
        var now = (await EvalAsync("(() => { const e = window.__inferpalTarget; return e.isContentEditable ? e.innerText : e.value; })()", ct))
                  .GetString() ?? "";
        var password = found.GetProperty("password").GetBoolean();
        var shown = password ? $"{text.Length} characters" : $"\"{Cut(text)}\"";
        // A field the browser cannot type into (a date, a colour) or that the page resets keeps what it had: not typed.
        if (text.Length > 0 && now != text && now == before.GetProperty("value").GetString())
            return new(false, $"{name} did not take the text: it still shows " + (password ? "what it had" : $"\"{Cut(now)}\""));
        return new(true, $"typed {shown} into {name}" + (password || now == text ? "" : $" — it now shows \"{Cut(now)}\""));
    }

    private async Task<PageStepResult> SelectAsync(PageStep step, CancellationToken ct)
    {
        var mark = _browser.Page.Mark;
        var chosen = await EvalAsync($$"""
            (() => { let s; try { s = document.querySelector({{Json(step.Selector)}}); }
              catch (x) { return { error: {{Json(step.Selector)}} + " is not a valid CSS selector: " + x.message }; }
              const want = {{Json(step.Value)}};
              if (!s) return { error: "no element matches " + {{Json(step.Selector)}} };
              const name = s.tagName.toLowerCase() + (s.id ? "#" + s.id : "");
              if (s.tagName !== "SELECT") return { error: name + " is not a list to choose in: click its entry" };
              if (s.disabled) return { error: name + " is disabled" };
              const all = [...s.options], t = (o) => o.text.trim();
              const o = all.find((o) => o.value === want) ?? all.find((o) => t(o) === want)
                     ?? all.find((o) => t(o).toLowerCase() === want.toLowerCase());
              if (!o) return { error: name + " has no entry " + JSON.stringify(want) + "; its entries: "
                                       + all.slice(0, 20).map((o) => JSON.stringify(t(o))).join(", ") + (all.length > 20 ? ", …" : "") };
              s.value = o.value; s.dispatchEvent(new Event("input", { bubbles: true })); s.dispatchEvent(new Event("change", { bubbles: true }));
              return { done: "chose " + JSON.stringify(t(o)) + " in " + name }; })()
            """, ct);
        if (chosen.TryGetProperty("error", out var error)) return new(false, error.GetString() ?? "");
        await SettleAsync(mark, ct);
        return new(true, chosen.GetProperty("done").GetString() ?? "");
    }

    private async Task<PageStepResult> PressAsync(string key, CancellationToken ct)
    {
        var code = Keys[key];
        var named = Keys.Keys.First(k => k.Equals(key, StringComparison.OrdinalIgnoreCase));
        var domKey = named == "Space" ? " " : named;
        var text = named switch { "Enter" => "\r", "Space" => " ", _ => null };
        var mark = _browser.Page.Mark;
        await _browser.Page.CallAsync("Input.dispatchKeyEvent",
            new { type = text is null ? "rawKeyDown" : "keyDown", key = domKey, code = named, windowsVirtualKeyCode = code, text }, ct);
        await _browser.Page.CallAsync("Input.dispatchKeyEvent",
            new { type = "keyUp", key = domKey, code = named, windowsVirtualKeyCode = code }, ct);
        await SettleAsync(mark, ct);
        return new(true, $"pressed {named}");
    }

    private async Task<PageStepResult> WaitForAsync(PageStep step, CancellationToken ct)
    {
        var what = step.Selector is not null ? step.Selector : $"\"{step.Text}\"";
        var test = step.Selector is not null
            ? $"(() => {{ try {{ return !!document.querySelector({Json(step.Selector)}); }} catch (x) {{ return \"invalid: \" + x.message; }} }})()"
            : $"!!document.body && document.body.innerText.includes({Json(step.Text)})";
        var until = DateTime.UtcNow.AddMilliseconds(step.TimeoutMs);
        while (true)
        {
            var seen = await EvalAsync(test, ct);
            if (seen.ValueKind == JsonValueKind.String) return new(false, $"{what} is not a valid CSS selector: {seen.GetString()![9..]}");
            if (seen.ValueKind == JsonValueKind.True) return new(true, $"{what} appeared");
            if (DateTime.UtcNow >= until)
                return new(false, $"{what} did not appear within {(step.TimeoutMs / 1000.0).ToString("0.#", CultureInfo.InvariantCulture)} s");
            await Task.Delay(100, ct);
        }
    }

    /// <summary>Finds the element a step names, scrolled into view, kept as <c>window.__inferpalTarget</c>.</summary>
    private Task<JsonElement> FindAsync(string? selector, string? text, CancellationToken ct) =>
        EvalAsync(FindScript.Replace("__SELECTOR__", Json(selector), StringComparison.Ordinal)
                            .Replace("__TEXT__", Json(text), StringComparison.Ordinal), ct);

    /// <summary>Why the found element cannot take the step; <c>null</c> when it can.</summary>
    private static PageStepResult? Unusable(JsonElement found, PageStep step)
    {
        var what = step.Selector ?? $"\"{step.Text}\"";
        if (found.TryGetProperty("invalid", out var invalid)) return new(false, $"{what} is not a valid CSS selector: {invalid.GetString()}");
        if (!found.GetProperty("found").GetBoolean()) return new(false, $"no element matches {what}");
        var name = found.GetProperty("name").GetString();
        if (!found.GetProperty("visible").GetBoolean()) return new(false, $"{name} is not visible");
        if (found.GetProperty("disabled").GetBoolean()) return new(false, $"{name} is disabled");
        return null;
    }

    /// <summary>" (the first of 3 matching …)" when the step's text or selector found several elements.</summary>
    private static string Also(JsonElement found, PageStep step)
    {
        var count = found.GetProperty("count").GetInt32();
        return count > 1 ? $" (the first of {count} matching {step.Selector ?? $"\"{step.Text}\""}: name a selector for another one)" : "";
    }

    private async Task MouseClickAsync(JsonElement found, CancellationToken ct)
    {
        var x = found.GetProperty("x").GetDouble();
        var y = found.GetProperty("y").GetDouble();
        await _browser.Page.CallAsync("Input.dispatchMouseEvent", new { type = "mouseMoved", x, y }, ct);
        await _browser.Page.CallAsync("Input.dispatchMouseEvent", new { type = "mousePressed", x, y, button = "left", clickCount = 1 }, ct);
        await _browser.Page.CallAsync("Input.dispatchMouseEvent", new { type = "mouseReleased", x, y, button = "left", clickCount = 1 }, ct);
    }

    /// <summary>Waits for a load of the main frame the last gesture started, then for the page to be quiet.</summary>
    private async Task SettleAsync(int mark, CancellationToken ct)
    {
        await Task.Delay(300, ct);
        if (_browser.Page.SeenSince(mark, MainFrameLoading)) await LoadedAsync(mark, ct);
    }

    /// <summary>Waits, 15 s at most, for the page's load event after <paramref name="mark"/>.</summary>
    private async Task LoadedAsync(int mark, CancellationToken ct)
    {
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
        wait.CancelAfter(TimeSpan.FromSeconds(15));
        try { await _browser.Page.WaitForAsync("Page.loadEventFired", mark, wait.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { /* a page still loading is captured as it is */ }
        await Task.Delay(200, ct);
    }

    /// <summary>The value of a script run in the page; run once more when a navigation replaced the page under it.</summary>
    /// <exception cref="PageScriptException">The script threw.</exception>
    /// <exception cref="DevToolsCallException">The browser refused the call twice.</exception>
    private async Task<JsonElement> EvalAsync(string expression, CancellationToken ct)
    {
        JsonElement evaluated;
        try { evaluated = await EvaluateOnceAsync(expression, ct); }
        catch (DevToolsCallException ex) when (ex.Message.Contains("context", StringComparison.OrdinalIgnoreCase)
                                               || ex.Message.Contains("navigated", StringComparison.OrdinalIgnoreCase))
        {
            // The page the script ran in was replaced by a load it started: once more, in the page that replaced it.
            await Task.Delay(500, ct);
            evaluated = await EvaluateOnceAsync(expression, ct);
        }
        if (evaluated.TryGetProperty("exceptionDetails", out var thrown))
            throw new PageScriptException("the page's script failed: "
                + (thrown.TryGetProperty("exception", out var e) ? Str(e, "description").Split('\n')[0] : Str(thrown, "text")));
        return evaluated.GetProperty("result").TryGetProperty("value", out var value) ? value.Clone() : default;
    }

    private Task<JsonElement> EvaluateOnceAsync(string expression, CancellationToken ct) =>
        _browser.Page.CallAsync("Runtime.evaluate", new { expression, returnByValue = true, awaitPromise = true }, ct);

    private static string Json(string? s) => JsonSerializer.Serialize(s);

    /// <summary>Finds a step's element: by CSS selector, or by the visible text of a control (exact first, then a part);
    /// a visible match before a hidden one (a page's mobile copy of a link).</summary>
    private const string FindScript = """
        (() => {
          const sel = __SELECTOR__, text = __TEXT__;
          const visible = (e) => { const r = e.getBoundingClientRect(), s = getComputedStyle(e);
            return r.width > 0 && r.height > 0 && s.visibility !== "hidden" && s.display !== "none"; };
          const listed = (e) => e.tagName === "OPTION" && !!e.closest("select");
          const shown = (e) => listed(e) ? visible(e.closest("select")) : visible(e);
          let all;
          if (sel) { try { all = [...document.querySelectorAll(sel)]; } catch (x) { return { invalid: String(x.message) }; } }
          else {
            const label = (e) => (e.tagName === "OPTION" ? e.text : (e.innerText || e.value || e.getAttribute("aria-label")
                                  || e.getAttribute("title") || "")).trim().replace(/\s+/g, " ");
            const pool = [...document.querySelectorAll("button, a, input, select, textarea, label, summary, option, [onclick], "
              + "[role=button], [role=link], [role=tab], [role=menuitem], [role=option], [role=checkbox], [role=radio], [role=switch]")]
              .filter((e) => e.tagName !== "OPTION" || listed(e));
            all = pool.filter((e) => label(e) === text);
            if (all.length === 0) all = pool.filter((e) => label(e).toLowerCase().includes(text.toLowerCase()));
          }
          all.sort((a, b) => shown(b) - shown(a));
          const el = all[0];
          if (!el) return { found: false };
          const option = listed(el);
          const box = option ? el.closest("select") : el;
          box.scrollIntoView({ block: "center", inline: "center", behavior: "instant" });
          const r = box.getBoundingClientRect(), x = r.left + r.width / 2, y = r.top + r.height / 2;
          const at = document.elementFromPoint(x, y);
          const name = (e) => e.tagName.toLowerCase() + (e.id ? "#" + e.id : "")
            + (typeof e.className === "string" && e.className.trim() ? "." + e.className.trim().split(/\s+/).join(".") : "");
          // An element's own label over it (a styled checkbox) passes the click on to it: not a cover.
          const ownLabel = at && el.labels && [...el.labels].some((l) => l === at || l.contains(at));
          window.__inferpalTarget = el;
          const editable = !el.readOnly && (el.isContentEditable || el.tagName === "TEXTAREA"
            || (el.tagName === "INPUT" && !["button", "submit", "reset", "checkbox", "radio", "file", "image", "range", "color"].includes(el.type)));
          return { found: true, count: all.length, option, visible: shown(el),
                   disabled: !!(el.disabled || box.disabled), x, y, name: name(el), editable, password: el.type === "password",
                   covered: !option && at && at !== el && !el.contains(at) && !at.contains(el) && !ownLabel ? name(at) : null };
        })()
        """;

    /// <summary>The page's visible controls, each with a selector that finds it alone.</summary>
    private const string ControlsScript = """
        (() => {
          const q = "a[href], button, input, select, textarea, summary, [contenteditable=true], [onclick], [role=button], "
                  + "[role=link], [role=tab], [role=menuitem], [role=option], [role=checkbox], [role=radio], [role=switch]";
          const selectorOf = __SELECTOR__;
          const controls = []; let more = 0;
          for (const e of document.querySelectorAll(q)) {
            const r = e.getBoundingClientRect(), st = getComputedStyle(e);
            if (r.width === 0 || r.height === 0 || st.visibility === "hidden" || st.display === "none") continue;
            if (controls.length >= __MAX__) { more++; continue; }
            const tag = e.tagName.toLowerCase();
            const role = e.getAttribute("role") || (tag === "a" ? "link" : tag === "input" ? "input:" + (e.type || "text") : tag);
            const name = (e.getAttribute("aria-label") || (e.labels && e.labels[0] ? e.labels[0].innerText : "")
              || (tag === "select" ? "" : e.innerText) || e.getAttribute("placeholder") || e.getAttribute("title")
              || (["button", "submit"].includes(e.type) ? e.value : "") || "").trim().replace(/\s+/g, " ").slice(0, 60);
            const state = [];
            if (e.disabled) state.push("disabled");
            if (e.readOnly) state.push("read-only");
            if (e.checked) state.push("checked");
            if (tag === "select") state.push("chosen: " + JSON.stringify(e.selectedOptions[0] ? e.selectedOptions[0].text.trim() : ""));
            else if ((tag === "input" || tag === "textarea") && e.value && e.type !== "password"
                     && !["checkbox", "radio", "button", "submit", "reset"].includes(e.type))
              state.push("value: " + JSON.stringify(e.value.slice(0, 60)));
            if (r.bottom < 0 || r.top > innerHeight || r.right < 0 || r.left > innerWidth) state.push("off-screen");
            else {
              const at = document.elementFromPoint(r.left + r.width / 2, r.top + r.height / 2);
              const own = at && e.labels && [...e.labels].some((l) => l === at || l.contains(at));
              if (at && at !== e && !e.contains(at) && !at.contains(e) && !own)
                state.push("covered by " + at.tagName.toLowerCase() + (at.id ? "#" + at.id : ""));
            }
            controls.push(role + " " + JSON.stringify(name) + " " + selectorOf(e) + (state.length ? " [" + state.join(", ") + "]" : ""));
          }
          return { controls, more };
        })()
        """;

    private int _disposed;

    /// <remarks>Once: a call whose browser failed closes it while the tool's own disposal may too.</remarks>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        _browser.Dispose();
        Served?.Dispose();
        Remove(_work);
    }

    /// <summary>Removes a browser's folder, in the background: its crash reporter may hold it a moment after it exits.</summary>
    private static void Remove(string work) =>
        _ = Task.Run(async () =>
        {
            for (var attempt = 0; attempt < 5 && Directory.Exists(work); attempt++)
            {
                try { Directory.Delete(work, recursive: true); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { await Task.Delay(400); }
            }
        });
}
