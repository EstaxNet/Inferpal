using System.Globalization;
using System.Text.Json;
using static Inferpal.Services.JsonFields;

namespace Inferpal.Services.Documents;

/// <summary>
/// The requests a page made, read from its browser's DevTools events (<c>Network.*</c>) as they arrive: what failed — an
/// error status, a refused or broken connection — and what was slowest, each with its method, address, status and time.
/// </summary>
/// <remarks>
/// <para>
/// ⚠ The browser reaches nothing but the page's own origin (<see cref="PageCapture.ChromiumFlags"/>): a request elsewhere
/// fails by construction. It is counted apart, as not reached by this browser, never as the app's own failure.
/// </para>
/// <para>
/// An address keeps its path and query, what looks like a credential masked (<see cref="SecretRedactor"/>, best effort, as a
/// <c>.har</c> is read). Bodies are not read here: a caller asks the browser for them (<see cref="FailedWithBody"/>).
/// Fed on the connection's pump, read from a tool's call: every access holds the lock.
/// </para>
/// </remarks>
internal sealed class PageNetwork
{
    /// <summary>The events a page's connection feeds here.</summary>
    internal static readonly string[] Events =
        ["Network.requestWillBeSent", "Network.responseReceived", "Network.loadingFinished", "Network.loadingFailed"];

    private const int MaxRequests = 500, MaxFailed = 15, MaxSlowest = 5, UrlChars = 200;

    private sealed class Request(string id, string method, string url, double start)
    {
        public string Id { get; } = id;
        public string Method { get; } = method;
        /// <summary>The address the page asked: a redirect never takes it away (a same-origin /login sent elsewhere is the
        /// app's own behaviour).</summary>
        public string Url { get; } = url;
        public string? RedirectedTo { get; set; }
        public double Start { get; } = start;
        public double Bytes { get; set; }
        public bool Ok => Failure is null && Status is null or < 400;
        public double? End { get; set; }
        public int? Status { get; set; }
        public string StatusText { get; set; } = "";
        public string? Failure { get; set; }
        public bool Canceled { get; set; }
        public bool Done => End is not null;
    }

    /// <summary>Whether the browser was asked for its requests: a capture taken another way (Firefox, WebKit, Chromium's
    /// command line) reads none, which is not "none made".</summary>
    internal volatile bool Read;

    private readonly Dictionary<string, Request> _byId = new(StringComparer.Ordinal);
    private readonly List<Request> _order = [];
    private int _dropped, _forgottenOk;

    /// <summary>No event is recorded any more: what the capture does after its picture (the accessibility reading focuses
    /// elements, whose handlers may fetch) is not the page's load.</summary>
    internal volatile bool Frozen;

    /// <summary>One DevTools event of the page; any other is ignored. Never throws for a shape it does not expect.</summary>
    internal void OnEvent(string name, JsonElement p)
    {
        if (Frozen || p.ValueKind != JsonValueKind.Object || Str(p, "requestId") is not { Length: > 0 } id) return;
        lock (_order)
        {
            switch (name)
            {
                case "Network.requestWillBeSent":
                    if (!p.TryGetProperty("request", out var request)) return;
                    var url = Str(request, "url");
                    if (_byId.TryGetValue(id, out var redirected))
                    {
                        // A redirect keeps the request's id: the same request, followed to its new address.
                        redirected.RedirectedTo = url;
                        return;
                    }
                    // data: and blob: never travel: not a request of the app.
                    if (!url.StartsWith("http:", StringComparison.OrdinalIgnoreCase) && !url.StartsWith("https:", StringComparison.OrdinalIgnoreCase)) return;
                    if (_order.Count >= MaxRequests)
                    {
                        // ⚠ Full: the oldest request that ended well makes room (counted), never a new one — a dev server's
                        // hundreds of module loads come before the API call that fails, which is what this is read for.
                        var room = _order.FindIndex(r => r.Done && r.Ok);
                        if (room < 0) { _dropped++; return; }
                        _byId.Remove(_order[room].Id);
                        _order.RemoveAt(room);
                        _forgottenOk++;
                    }
                    var made = new Request(id, Str(request, "method"), url, Num(p, "timestamp"));
                    _byId[id] = made;
                    _order.Add(made);
                    return;
                case "Network.responseReceived" when _byId.TryGetValue(id, out var answered) && p.TryGetProperty("response", out var response):
                    answered.Status = response.TryGetProperty("status", out var s) && s.TryGetDouble(out var status) ? (int)status : null;
                    var text = Str(response, "statusText");
                    answered.StatusText = text.Length <= 60 ? text : text[..60] + "…";   // the server's words: bounded
                    return;
                case "Network.loadingFinished" when _byId.TryGetValue(id, out var finished):
                    finished.End = Num(p, "timestamp");
                    finished.Bytes = Num(p, "encodedDataLength");
                    return;
                case "Network.loadingFailed" when _byId.TryGetValue(id, out var failed):
                    failed.End = Num(p, "timestamp");
                    failed.Failure = Str(p, "errorText") is { Length: > 0 } why ? why : "failed";
                    failed.Canceled = p.TryGetProperty("canceled", out var c) && c.ValueKind == JsonValueKind.True;
                    if (Str(p, "blockedReason") is { Length: > 0 } blocked) failed.Failure += " (blocked: " + blocked + ")";
                    return;
            }
        }
    }

    /// <summary>
    /// What the requests ended since the last call say, for <paramref name="origin"/> (<c>scheme://host:port</c>): the
    /// failed ones, the ones elsewhere this browser does not reach, the slowest; empty when the page made none. Requests
    /// still under way stay for the next call. <paramref name="take"/> forgets what it read.
    /// </summary>
    internal IReadOnlyList<string> Lines(string origin, bool take = true)
    {
        lock (_order)
        {
            var ended = _order.Where(r => r.Done).ToList();
            var pending = _order.Count - ended.Count;
            var dropped = _dropped;
            var forgotten = _forgottenOk;
            if (take)
            {
                foreach (var r in ended) _byId.Remove(r.Id);
                _order.RemoveAll(r => r.Done);
                _dropped = _forgottenOk = 0;
            }
            if (ended.Count == 0 && pending == 0 && dropped == 0 && forgotten == 0) return [];

            // Elsewhere = where the request ENDED: a same-origin address redirected away fails at the proxy too.
            var outside = ended.Where(r => r.Failure is not null && !r.Canceled && !SameOrigin(r.RedirectedTo ?? r.Url, origin)).ToList();
            var failed = ended.Where(r => !outside.Contains(r) && (r.Status >= 400 || r.Failure is not null && !r.Canceled)).ToList();
            var lines = new List<string>();
            var total = ended.Count + forgotten;
            var counts = $"{total} request{(total == 1 ? "" : "s")}, {failed.Count} failed";
            if (pending > 0) counts += $", {pending} still under way";
            if (dropped > 0) counts += $", {dropped} more not recorded";
            if (forgotten > 0) counts += $", {forgotten} that ended well not kept to make room";
            lines.Add($"Network ({counts}; addresses masked where they look like credentials):");
            foreach (var r in failed.Take(MaxFailed)) lines.Add("  " + Shown(r, origin) + " → " + Outcome(r) + Duration(r));
            if (failed.Count > MaxFailed) lines.Add($"  [{failed.Count - MaxFailed} more failed]");
            if (outside.Count > 0)
            {
                var groups = outside.GroupBy(r => OriginOf(r.RedirectedTo ?? r.Url)).OrderByDescending(g => g.Count()).ToList();
                lines.Add("  Not reached — this browser reaches only the page's own address, so this says nothing of the app: "
                        + string.Join(", ", groups.Take(8).Select(g => $"{g.Key} ({g.Count()})"))
                        + (groups.Count > 8 ? $", and {groups.Count - 8} more addresses" : ""));
                // A request of the page's own address sent away by a redirect is the app's behaviour: said as such.
                foreach (var r in outside.Where(r => r.RedirectedTo is not null && SameOrigin(r.Url, origin)).Take(MaxFailed))
                    lines.Add($"  {Shown(r, origin)} redirected to {OriginOf(r.RedirectedTo!)}, which this browser does not reach");
            }
            var slowest = ended.Where(r => r.Failure is null && !failed.Contains(r) && r.End > r.Start)
                               .OrderByDescending(r => r.End - r.Start).Take(MaxSlowest).ToList();
            if (slowest.Count > 0)
                lines.Add("  Slowest: " + string.Join("; ", slowest.Select(r => $"{Shown(r, origin)} {Outcome(r)}{Duration(r)}")));
            return lines;
        }
    }

    /// <summary>The ended requests whose answer was an error status, for a caller that reads their bodies: id, label and
    /// the size the answer travelled with (0 when unknown).</summary>
    internal IReadOnlyList<(string Id, string Label, double Bytes)> FailedWithBody(string origin)
    {
        lock (_order)
            return [.. _order.Where(r => r.Done && r.Status >= 400 && SameOrigin(r.Url, origin))
                             .Select(r => (r.Id, $"{Shown(r, origin)} → {Outcome(r)}", r.Bytes))];
    }

    private static string Shown(Request r, string origin)
    {
        var url = SameOrigin(r.Url, origin) && Uri.TryCreate(r.Url, UriKind.Absolute, out var u) ? u.PathAndQuery : r.Url;
        url = SecretRedactor.Redact(url);
        return $"{r.Method} {(url.Length <= UrlChars ? url : url[..UrlChars] + "…")}";
    }

    private static string Outcome(Request r) =>
        r.Failure is not null ? "failed: " + r.Failure
        : r.Status is { } status ? $"{status.ToString(CultureInfo.InvariantCulture)}{(r.StatusText.Length > 0 ? " " + r.StatusText : "")}"
        : "no status";

    private static string Duration(Request r) =>
        r.End is { } end && end > r.Start
            ? (end - r.Start) >= 1 ? $" ({(end - r.Start).ToString("0.0", CultureInfo.InvariantCulture)} s)"
                                   : $" ({Math.Round((end - r.Start) * 1000).ToString(CultureInfo.InvariantCulture)} ms)"
            : "";

    private static bool SameOrigin(string url, string origin) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && string.Equals(PageSession.OriginOf(u), origin, StringComparison.OrdinalIgnoreCase);

    private static string OriginOf(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) ? $"{u.Scheme}://{u.Authority}" : url;

    private static double Num(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.TryGetDouble(out var d) ? d : 0;
}
