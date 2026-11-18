namespace Inferpal.Services.Inference;

/// <summary>
/// How fast each model wrote during one chat turn, as a scope its front-end opens: the clients record what the server
/// counted for each call (<see cref="Generated"/>), and the answer's header shows the figure of the model that answered
/// (<see cref="Scope.RateOf"/>).
/// </summary>
/// <remarks>
/// ⚠ The figure is the SERVER's count — Ollama's <c>eval_count</c> over its <c>eval_duration</c>, an OpenAI-compatible
/// server's <c>usage.completion_tokens</c> over the time from the first useful chunk to the end of the stream — never the
/// chunks counted instead: a call without a count is no sample, and a turn without one shows no figure.
/// <para>
/// ⚠ Opened in an async method, the scope holds for every call that method and the tasks it starts make; one closed
/// stays closed for the work that outlives it (a title written after the turn). Without a scope nothing is counted.
/// </para>
/// </remarks>
internal static class GenerationMeter
{
    private static readonly AsyncLocal<Scope?> _current = new();

    /// <summary>Opens a scope for the calls made from here until it is disposed.</summary>
    public static Scope Open() => _current.Value = new Scope(_current.Value);

    /// <summary>Records what <paramref name="model"/> wrote in one call: the tokens the server counted, its reasoning
    /// included, and the time spent writing them. A count or a time the server did not give is no sample.</summary>
    public static void Generated(string model, int tokens, TimeSpan time)
    {
        if (tokens > 0 && time > TimeSpan.Zero && _current.Value is { } scope) scope.Add(model, tokens, time);
    }

    /// <summary>One turn's samples. Thread-safe: work the turn starts may record from another thread.</summary>
    internal sealed class Scope : IDisposable
    {
        private readonly object _gate = new();
        private readonly List<(string Model, long Tokens, TimeSpan Time)> _samples = [];
        private Scope? _previous;
        private bool _open = true;

        internal Scope(Scope? previous) => _previous = previous;

        /// <summary>How fast <paramref name="model"/> wrote in this turn, over every call it answered; <c>null</c> when
        /// no call gave a count and a time. Another model's calls (a summary, a title) do not enter.</summary>
        public GenerationRate? RateOf(string model)
        {
            lock (_gate)
            {
                long tokens = 0;
                var time = TimeSpan.Zero;
                foreach (var s in _samples.Where(s => ModelCatalog.SameModelName(s.Model, model)))
                {
                    tokens += s.Tokens;
                    time   += s.Time;
                }
                return tokens > 0 && time > TimeSpan.Zero ? new GenerationRate(tokens, time) : null;
            }
        }

        internal void Add(string model, long tokens, TimeSpan time)
        {
            lock (_gate)
                if (_open) _samples.Add((model, tokens, time));
        }

        /// <summary>Closes the scope: calls made after it — work that outlives the turn — are not counted.</summary>
        public void Dispose()
        {
            lock (_gate)
            {
                if (ReferenceEquals(_current.Value, this)) _current.Value = _previous;
                _open = false;
                _previous = null;
            }
        }
    }
}

/// <summary>What a model wrote over some calls: tokens generated, and the time spent writing them.</summary>
internal readonly record struct GenerationRate(long Tokens, TimeSpan Time)
{
    /// <summary>Tokens per second.</summary>
    public double PerSecond => Tokens / Time.TotalSeconds;
}
