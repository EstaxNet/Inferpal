using Inferpal.Localization;

namespace Inferpal.Services.Inference;

/// <summary>
/// How fast each server reads a prompt before its first token, per model — measured on the requests that went through,
/// the twin of the loaded window.
/// </summary>
/// <remarks>
/// ⚠ Measured, never assumed: a reading speed belongs to the machine, not to the model's family, so it is learned. Kept
/// in <c>%AppData%</c> because a sample costs a long read; shared by the editors, and recomputed, so an unreadable file
/// is replaced, not set aside. ⚠ Only a cold read says how slow a server is: one that does not say what it served from
/// its cache (LM Studio's <c>/v1</c>) makes a warm read look fast. Short reads are dropped (<see cref="MinTokens"/>,
/// <see cref="MinRead"/>), and the estimate follows the SLOWEST read of each size — a warm sample can only lengthen a
/// wait, never shorten one.
/// </remarks>
internal sealed class PrefillRates
{
    /// <summary>One read: the request's estimated size (<see cref="RequestSize"/>), the tokens the server counted, and the
    /// seconds from sending to the first useful chunk.</summary>
    internal sealed record Sample(int Estimated, int Tokens, double Seconds);

    /// <summary>What is known of one server and model.</summary>
    internal sealed class Entry
    {
        /// <summary>The reads that went through, oldest first.</summary>
        public List<Sample> Samples { get; set; } = [];

        /// <summary>A read that had no first token when its wait ran out: the server is slower than this.</summary>
        public Sample? Overrun { get; set; }

        /// <summary>The waits run out in a row since the last read that went through.</summary>
        public int Overruns { get; set; }
    }

    /// <summary>What a request is told before it is sent: the cold read its wait is made from (measured, or a bound an
    /// expiry left), the part of it that was MEASURED (what the waiting line may say), and the tokens to read.</summary>
    internal readonly record struct Reading(TimeSpan? ColdRead, TimeSpan? Measured, int Tokens);

    /// <summary>Under this many tokens a read is too short to say anything of a speed.</summary>
    internal const int MinTokens = 2_000;

    /// <summary>A read shorter than this is mostly the network and the first token, not reading.</summary>
    internal static readonly TimeSpan MinRead = TimeSpan.FromSeconds(5);

    private const int MaxSamples = 24;

    /// <summary>How many waits in a row an expiry may lengthen before the wait stops growing.</summary>
    internal const int MaxGrowth = 2;

    /// <summary>The one store of the process, on the file every editor of the machine shares.</summary>
    /// <remarks>⚠ Read on every request: an unreadable file is said ONCE (<c>ReportUnreadableOnce</c>), not once per
    /// question — a note per request fills the diagnostics ring and erases the failure looked for in it.</remarks>
    internal static PrefillRates Shared { get; } =
        new(new Persistence.AppDataJsonFile<Dictionary<string, Entry>>("prefill-rates.json", "PrefillRates")
        {
            ReportUnreadableOnce = true,
        });

    private readonly Persistence.AppDataJsonFile<Dictionary<string, Entry>> _file;
    private readonly object _lock = new();
    // The servers and models read once in this process (Record's mayIncludeLoad).
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _seen = new(StringComparer.Ordinal);

    internal PrefillRates(Persistence.AppDataJsonFile<Dictionary<string, Entry>> file) => _file = file;

    /// <summary>The file, for the tests that point it elsewhere.</summary>
    internal Persistence.AppDataJsonFile<Dictionary<string, Entry>> File => _file;

    /// <summary>One server, whichever of its addresses a caller holds: `…/v1` is the OpenAI-compatible client's, the
    /// configured one everybody else's.</summary>
    private static string Key(string server, string model)
    {
        var root = server.Trim().TrimEnd('/');
        if (root.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)) root = root[..^3].TrimEnd('/');
        return root + "|" + model;
    }

    /// <summary>Keeps a read that went through. A stopped or failed request is never a read: its callers do not come here.</summary>
    /// <param name="server">The server's address.</param>
    /// <param name="model">The model that read.</param>
    /// <param name="estimated">The request's estimated size.</param>
    /// <param name="tokens">The tokens the server counted, read anew.</param>
    /// <param name="read">From sending to the first useful chunk.</param>
    /// <param name="mayIncludeLoad">The client cannot tell a model's load from its read (an OpenAI-compatible server
    /// that loads on demand: LM Studio, llama-swap): the first read of a model in this process is no sample — a load
    /// of a minute recorded as reading would be the slowest read of its size, and the estimate follows the slowest.</param>
    /// <remarks>⚠ Only a read that says a speed replaces a bound an expiry left. A short or cached read that beats the bound
    /// scaled down to its size says nothing of a long cold read: cleared by it, a long run that timed out, then a quick
    /// question, then the run again, would time out again at the configured wait. A bound only lengthens the wait of a
    /// request that does not answer: a working server answers before it.</remarks>
    public void Record(string server, string model, int estimated, int tokens, TimeSpan read, bool mayIncludeLoad = false)
    {
        // The first read of a model is spent on this mark whatever its size: the load happens then, not later.
        var first = mayIncludeLoad && _seen.TryAdd(Key(server, model), 0);
        if (first || estimated <= 0 || tokens < MinTokens || read < MinRead) return;
        Update(server, model, entry =>
        {
            entry.Samples.Add(new Sample(estimated, tokens, read.TotalSeconds));
            if (entry.Samples.Count > MaxSamples) entry.Samples.RemoveRange(0, entry.Samples.Count - MaxSamples);
            // The server answered: what it is measured at now replaces "slower than".
            entry.Overrun  = null;
            entry.Overruns = 0;
        });
    }

    /// <summary>Keeps a read the server counted <paramref name="counted"/> tokens for, <paramref name="cached"/> of them served
    /// from its cache: the sample is the part it read anew, and the request's size scaled down to that part.</summary>
    /// <param name="server">The server's address.</param>
    /// <param name="model">The model that read.</param>
    /// <param name="estimated">The request's estimated size.</param>
    /// <param name="counted">The tokens the server counted for the request, cached ones included; 0 when it did not say.</param>
    /// <param name="cached">The tokens of <paramref name="counted"/> its cache served.</param>
    /// <param name="read">The time the server took to read the part it did not have in its cache.</param>
    /// <param name="mayIncludeLoad">As for <see cref="Record"/>.</param>
    /// <remarks>⚠ Kept at the whole request's size, a read of its new part says a speed as many times too fast as the
    /// cache served: the wait of the next cold read shrinks to the configured one, and a bound an expiry left is cleared.
    /// Scaled, the request's size and the server's count keep their ratio (<see cref="Tokens"/>).</remarks>
    public void RecordRead(string server, string model, int estimated, int counted, int cached, TimeSpan read,
                           bool mayIncludeLoad = false)
    {
        counted = counted > 0 ? counted : estimated;
        var fresh = counted - Math.Clamp(cached, 0, Math.Max(0, counted));
        Record(server, model, (int)Math.Round((double)estimated * fresh / Math.Max(1, counted)), fresh, read, mayIncludeLoad);
    }

    /// <summary>Keeps a request with no first token after <paramref name="waited"/>: the server reads slower than that.
    /// <c>false</c> when nothing was learned, so the next request does not wait longer.</summary>
    /// <remarks>
    /// ⚠ An expiry is a measure too: without it a first long prompt that runs out teaches nothing, and the next one runs
    /// out the same way. ⚠ But only a bound, and a bounded one. A request under <see cref="MinTokens"/> that runs out was
    /// queued or loading, not reading: it teaches nothing. A bound never stretches to a LARGER request (a cold load is not
    /// a speed: a small request that ran out would make every long one wait an hour). And after <see cref="MaxGrowth"/>
    /// in a row the wait stops growing: a server that accepts a request and never answers cannot be told from a slow one,
    /// and each question would otherwise wait twice as long as the last, restarts included.
    /// </remarks>
    public bool RecordOverrun(string server, string model, int estimated, TimeSpan waited)
    {
        if (estimated < MinTokens) return false;
        var grew = false;
        Update(server, model, entry =>
        {
            if (entry.Overruns >= MaxGrowth) return;
            entry.Overruns++;
            var bound = new Sample(estimated, 0, waited.TotalSeconds);
            if (entry.Overrun is not { } prior || bound.Seconds > Bound(prior, estimated))
                entry.Overrun = bound;
            grew = true;
        });
        return grew;
    }

    /// <summary>The time a COLD read of a request estimated at <paramref name="estimated"/> tokens takes, measured; <c>null</c>
    /// when nothing is — never a guess.</summary>
    public TimeSpan? Estimate(string server, string model, int estimated) => Describe(server, model, estimated).ColdRead;

    /// <summary>The tokens the server counts for a request estimated at <paramref name="estimated"/>: its own tokenizer's
    /// count once one read is known, the estimate otherwise.</summary>
    public int Tokens(string server, string model, int estimated) => Describe(server, model, estimated).Tokens;

    /// <summary>All a request is told before it is sent, from one read of the file.</summary>
    /// <remarks>⚠ The waiting line says the MEASURED read only, and only when no expiry says the server is slower: a bound
    /// is "more than", never "up to".</remarks>
    public Reading Describe(string server, string model, int estimated)
    {
        if (estimated <= 0 || Read(server, model) is not { } entry) return new(null, null, Math.Max(0, estimated));
        var fit   = Fit(entry.Samples, estimated);
        var bound = entry.Overrun is { Estimated: > 0 } overrun ? Bound(overrun, estimated) : (double?)null;
        var cold  = Math.Max(fit ?? 0, bound ?? 0);
        var measured = fit is > 0 && (bound is null || fit >= bound) ? TimeSpan.FromSeconds(fit.Value) : (TimeSpan?)null;
        var tokens = estimated;
        if (entry.Samples is { Count: > 0 } samples)
        {
            var ratios = samples.Select(s => (double)s.Tokens / s.Estimated).Order().ToList();
            tokens = (int)Math.Round(estimated * ratios[ratios.Count / 2]);
        }
        return new(cold > 0 ? TimeSpan.FromSeconds(cold) : null, measured, tokens);
    }

    /// <summary>What an expiry says of a request of <paramref name="estimated"/> tokens: as long, or proportionally less for
    /// a smaller one — never more for a larger one.</summary>
    private static double Bound(Sample overrun, int estimated) =>
        overrun.Seconds * Math.Min(1.0, (double)estimated / overrun.Estimated);

    /// <summary>The seconds a cold read of <paramref name="estimated"/> tokens takes, from <paramref name="samples"/>.</summary>
    /// <remarks>
    /// The slowest read of each size (sizes in powers of two), then the first of these that holds, every coefficient
    /// positive: <c>c + a·N + b·N²</c> (three sizes), <c>a·N + b·N²</c>, <c>c + a·N</c> — a server reads slower deep in a long
    /// prompt — and the slowest speed seen otherwise.
    /// </remarks>
    internal static double? Fit(IReadOnlyList<Sample> samples, int estimated)
    {
        if (samples.Count == 0) return null;
        var points = samples.Where(s => s.Estimated > 0)
                            .GroupBy(s => (int)Math.Log2(Math.Max(1, s.Estimated / 1024.0)))
                            .Select(g => g.MaxBy(s => s.Seconds / s.Estimated)!)
                            .Select(s => (X: s.Estimated / 1000.0, Y: s.Seconds))
                            .OrderBy(p => p.X)
                            .ToList();
        if (points.Count == 0) return null;
        var x = estimated / 1000.0;
        if (points.Count >= 3 && Quadratic(points) is { } q) return q.C + q.A * x + q.B * x * x;
        if (points.Count >= 2 && ThroughZero(points) is { } z) return z.A * x + z.B * x * x;
        if (points.Count >= 2 && Linear(points) is { } l) return l.C + l.A * x;
        return x * points.Max(p => p.Y / p.X);
    }

    /// <summary>The least-squares <c>c + a·x + b·x²</c> through <paramref name="points"/>, or <c>null</c> when a coefficient comes out
    /// negative (a speed that would grow with the prompt, or a read that would take less than nothing).</summary>
    private static (double C, double A, double B)? Quadratic(IReadOnlyList<(double X, double Y)> points)
    {
        double s0 = points.Count, s1 = 0, s2 = 0, s3 = 0, s4 = 0, t0 = 0, t1 = 0, t2 = 0;
        foreach (var (x, y) in points)
        {
            var x2 = x * x;
            s1 += x; s2 += x2; s3 += x2 * x; s4 += x2 * x2;
            t0 += y; t1 += x * y; t2 += x2 * y;
        }
        // Normal equations, by Cramer's rule.
        var det = Det(s0, s1, s2, s1, s2, s3, s2, s3, s4);
        if (Math.Abs(det) < 1e-12) return null;
        var c = Det(t0, s1, s2, t1, s2, s3, t2, s3, s4) / det;
        var a = Det(s0, t0, s2, s1, t1, s3, s2, t2, s4) / det;
        var b = Det(s0, s1, t0, s1, s2, t1, s2, s3, t2) / det;
        return c >= 0 && a >= 0 && b >= 0 ? (c, a, b) : null;
    }

    /// <summary>The least-squares <c>a·x + b·x²</c> through <paramref name="points"/>, or <c>null</c> when a coefficient comes out negative.</summary>
    private static (double A, double B)? ThroughZero(IReadOnlyList<(double X, double Y)> points)
    {
        double s2 = 0, s3 = 0, s4 = 0, t1 = 0, t2 = 0;
        foreach (var (x, y) in points)
        {
            var x2 = x * x;
            s2 += x2; s3 += x2 * x; s4 += x2 * x2;
            t1 += x * y; t2 += x2 * y;
        }
        var det = s2 * s4 - s3 * s3;
        if (Math.Abs(det) < 1e-12) return null;
        var a = (t1 * s4 - t2 * s3) / det;
        var b = (s2 * t2 - s3 * t1) / det;
        return a >= 0 && b >= 0 ? (a, b) : null;
    }

    /// <summary>The least-squares <c>c + a·x</c> through <paramref name="points"/>, or <c>null</c> when a coefficient comes out negative.</summary>
    private static (double C, double A)? Linear(IReadOnlyList<(double X, double Y)> points)
    {
        double n = points.Count, sx = 0, sy = 0, sxx = 0, sxy = 0;
        foreach (var (x, y) in points) { sx += x; sy += y; sxx += x * x; sxy += x * y; }
        var det = n * sxx - sx * sx;
        if (Math.Abs(det) < 1e-12) return null;
        var a = (n * sxy - sx * sy) / det;
        var c = (sy - a * sx) / n;
        return c >= 0 && a >= 0 ? (c, a) : null;
    }

    private static double Det(double a, double b, double c, double d, double e, double f, double g, double h, double i) =>
        a * (e * i - f * h) - b * (d * i - f * g) + c * (d * h - e * g);

    private Entry? Read(string server, string model)
    {
        lock (_lock)
            return _file.Read(new Dictionary<string, Entry>()).Value.GetValueOrDefault(Key(server, model));
    }

    private void Update(string server, string model, Action<Entry> change)
    {
        lock (_lock)
        {
            // Read again: the other editor of the machine may have written since. ⚠ A read that fails is read again
            // before anything is written: a write made from the empty fallback would erase every other model's reads.
            // Still unreadable, it is replaced.
            var (all, _) = _file.ReadSettled(new Dictionary<string, Entry>());
            var key   = Key(server, model);
            var entry = all.GetValueOrDefault(key) ?? new Entry();
            change(entry);
            all[key] = entry;
            _file.Save(all);
        }
    }
}

/// <summary>How long a request waits for its first token.</summary>
internal static class PrefillDeadline
{
    /// <summary>How much longer than its measured cold read a request waits before it is given up.</summary>
    internal const double Margin = 2.0;

    /// <summary>Never less than <paramref name="floor"/> (the configured wait), and twice a cold read of the request when
    /// it is measured: a slow server reading a long prompt sends nothing, and is not a dead one.</summary>
    /// <remarks>⚠ And never before <see cref="AfterConnect"/>: Ollama sends nothing, not even its headers, before its first
    /// token, so a wait that ran out before the connection deadline could not tell a slow read from a server never
    /// reached — with a configured wait under it, nothing was ever learned and every expiry counted against the
    /// server.</remarks>
    public static TimeSpan For(TimeSpan floor, TimeSpan? coldRead)
    {
        var wait = coldRead is { } read && read * Margin > floor ? read * Margin : floor;
        return wait > AfterConnect ? wait : AfterConnect;
    }

    /// <summary>The shortest first-token wait: the connection deadline has had its say.</summary>
    internal static readonly TimeSpan AfterConnect = ConnectDeadline.Timeout + TimeSpan.FromSeconds(5);
}

/// <summary>
/// What a request says while the server reads its prompt and nothing has come back: from <see cref="FirstSaid"/> on, then
/// every <see cref="Beat"/>, the tokens to read, the measured cold read when there is one, and the time waited.
/// </summary>
/// <remarks>
/// ⚠ When the first token comes, the line gives its place back (<c>null</c>): the caller shows the step it covered.
/// Nothing is said on a machine that answers within <see cref="FirstSaid"/>.
/// </remarks>
internal sealed class PrefillWait : IDisposable
{
    /// <summary>The silence after which the wait is said.</summary>
    internal static TimeSpan FirstSaid { get; set; } = TimeSpan.FromSeconds(8);

    /// <summary>How often the line is said again.</summary>
    internal static TimeSpan Beat { get; set; } = TimeSpan.FromSeconds(5);

    private readonly Action<string?>? _onWaiting;
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private readonly CancellationTokenSource _stop = new();
    private readonly object _gate = new();
    private bool _stopped, _said, _disposed;

    /// <param name="onWaiting">The line, or <c>null</c> when it gives its place back; <c>null</c> to say nothing. ⚠ It must
    /// not block (the editors post it): it is called under the lock that keeps a line from following its withdrawal.</param>
    /// <param name="tokens">The tokens to read, as the server counts them.</param>
    /// <param name="coldRead">The measured cold read of a request this size, said with the line; <c>null</c> when not
    /// measured.</param>
    /// <param name="firstSaid">The silence before the first line; <see cref="FirstSaid"/> when <c>null</c> (tests).</param>
    public PrefillWait(Action<string?>? onWaiting, int tokens, TimeSpan? coldRead, TimeSpan? firstSaid = null)
    {
        _onWaiting = onWaiting;
        if (onWaiting is not null) _ = SayAsync(tokens, coldRead, firstSaid ?? FirstSaid, _stop.Token);
    }

    /// <summary>The time since the request was sent.</summary>
    public TimeSpan Elapsed => _clock.Elapsed;

    private async Task SayAsync(int tokens, TimeSpan? coldRead, TimeSpan firstSaid, CancellationToken ct)
    {
        try
        {
            await Task.Delay(firstSaid, ct).ConfigureAwait(false);
            while (!ct.IsCancellationRequested)
            {
                lock (_gate)
                {
                    if (_stopped) return;
                    _said = true;
                    _onWaiting!(Strings.StatusReadingPrompt(tokens, coldRead, _clock.Elapsed));
                }
                await Task.Delay(Beat, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Diagnostics.Swallow("PrefillWait", ex); }
    }

    /// <summary>The first token came: the line stops, and gives its place back if it was said.</summary>
    public void Arrived() => Stop();

    /// <summary>The request ended otherwise (Stop, failure, a retry that starts its own line, a stream with nothing in
    /// it): the line stops, and gives its place back too.</summary>
    /// <remarks>⚠ Without it a retry that answers within <see cref="FirstSaid"/> leaves the first request's
    /// "Reading the prompt · 9 s" on screen for the whole answer: the retry's own line was never said, so it has nothing
    /// to give back.</remarks>
    public void Dispose()
    {
        Stop();
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        _stop.Dispose();
    }

    private void Stop()
    {
        lock (_gate)
        {
            if (_stopped) return;
            _stopped = true;
            if (_said)
                try { _onWaiting?.Invoke(null); }
                catch (Exception ex) { Diagnostics.Swallow("PrefillWait", ex); }
        }
        _stop.Cancel();
    }
}
