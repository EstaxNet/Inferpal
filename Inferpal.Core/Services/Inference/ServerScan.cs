using Inferpal.Config;
using Inferpal.Localization;

namespace Inferpal.Services.Inference;

/// <summary>What one place answered when the first run looked for a model server.</summary>
internal enum ServerPlaceOutcome
{
    /// <summary>A model server answers (its type in <see cref="ServerPlace.Provider"/>).</summary>
    Answers,
    /// <summary>Nothing listens there.</summary>
    NothingListening,
    /// <summary>Something answers, but none of the model servers' endpoints: a web page, another service.</summary>
    NotAModelServer,
    /// <summary>A server answers 401 or 403: it wants a key.</summary>
    NeedsKey,
}

/// <summary>One place the first run looked at, and what it found there.</summary>
/// <param name="Url">The address probed.</param>
/// <param name="Expected">The server that usually listens there (a provider code), <c>null</c> for an address the person
/// gave.</param>
/// <param name="Provider">The type of server that answers, when one does.</param>
/// <param name="Detail">The refusal the server answered with, when there is one.</param>
/// <param name="ChatModels">How many models there answer questions.</param>
/// <param name="SearchModels">How many models there serve code search.</param>
/// <param name="ListFailure">Why its models could not be listed: the counts are then unknown, not zero.</param>
internal sealed record ServerPlace(
    string Url, string? Expected, ServerPlaceOutcome Outcome, string? Provider, string? Detail,
    int ChatModels = 0, int SearchModels = 0, string? ListFailure = null)
{
    /// <summary>The name to show: the server's type when one answers or usually listens there, else the address.</summary>
    public string Name => (Provider ?? Expected) is { } code ? InferenceProviderFactory.DisplayName(code) : Url;

    /// <summary>What it answered, in the user's language: one sentence for VS Code's card, the ACP setup and Visual
    /// Studio.</summary>
    public string Status => Outcome switch
    {
        ServerPlaceOutcome.Answers when ListFailure is not null => Strings.SetupPlaceAnswersUnlisted(ListFailure),
        ServerPlaceOutcome.Answers          => Strings.SetupPlaceAnswers(ChatModels),
        ServerPlaceOutcome.NothingListening => Strings.SetupPlaceNothingListening,
        ServerPlaceOutcome.NotAModelServer  => Strings.SetupPlaceNotAModelServer,
        _                                   => Strings.SetupPlaceNeedsKey,
    };

    /// <summary>The place and its answer on one line, as a terminal and a chat notice list them.</summary>
    public string Line => $"{Name} · {Url} — {Status}";
}

/// <summary>
/// Looks for a model server where one usually runs, and says what each place answered: the first run's one search, for
/// VS Code, the ACP agent and Visual Studio.
/// </summary>
/// <remarks>
/// ⚠ The configured key goes to the configured address only, never to the other places: a key belongs to its server.
/// A place that wants one says so (<see cref="ServerPlaceOutcome.NeedsKey"/>), and the person gives it.
/// </remarks>
internal static class ServerScan
{
    /// <summary>Where each server listens by default: Ollama, LM Studio, llama-server (9931; 8080 in older builds and
    /// its Docker image), vLLM.</summary>
    internal static readonly IReadOnlyList<(string Provider, int Port)> DefaultPlaces =
    [
        (InferenceProviderFactory.Ollama,           11434),
        (InferenceProviderFactory.LmStudio,         1234),
        (InferenceProviderFactory.OpenAiCompatible, 9931),
        (InferenceProviderFactory.OpenAiCompatible, 8080),
        (InferenceProviderFactory.OpenAiCompatible, 8000),
    ];

    /// <summary>The places looked at, in order: the configured address first when it is none of the usual ones.</summary>
    internal static IReadOnlyList<(string Url, string? Expected)> Places(string? configured)
    {
        var mine = ProviderProbe.RootOf(configured);
        // A usual place the configured address names otherwise (127.0.0.1 for localhost) keeps the person's spelling:
        // taken, the server stays the address they typed.
        var places = DefaultPlaces
            .Select(p => $"http://localhost:{p.Port}" is var url && mine.Length > 0 && SameAddress(url, mine)
                ? (mine, (string?)p.Provider)
                : (url, (string?)p.Provider))
            .ToList();
        if (mine.Length > 0 && !places.Any(p => SameAddress(p.Item1, mine)))
            places.Insert(0, (mine, null));
        return places;
    }

    /// <summary>
    /// Nothing was chosen yet: a first run, the default address, no key, the code's default model. ⚠ All four — the
    /// first-run flag stays set for anyone set up outside Visual Studio's first run, and a server or a model the person
    /// chose is theirs, down or not: looked past, it was replaced in the settings every editor shares.
    /// </summary>
    public static bool NothingChosen(InferpalConfig config)
    {
        var factory = new InferpalConfig();
        // The default address as written: 127.0.0.1 typed in place of localhost is a choice made, not the factory value.
        return config.IsFirstRun && string.IsNullOrEmpty(config.ApiKey)
            && string.Equals(ProviderProbe.RootOf(config.BaseUrl), ProviderProbe.RootOf(factory.BaseUrl), StringComparison.OrdinalIgnoreCase)
            && ModelCatalog.SameModelName(config.DefaultModel, factory.DefaultModel);
    }

    /// <summary>The server a first run takes when the configured one does not answer (<see cref="Offered"/>), or null
    /// unless <see cref="NothingChosen"/>.</summary>
    public static ServerPlace? ToAdopt(InferpalConfig config, IReadOnlyList<ServerPlace> places) =>
        NothingChosen(config) ? Offered(places, config.BaseUrl) : null;

    /// <summary>Looks at every place at once.</summary>
    /// <param name="configured">The configured address and its key: the key is sent there only.</param>
    /// <param name="createClient">Builds a client for a server that answers, to count its models.</param>
    public static Task<IReadOnlyList<ServerPlace>> ScanAsync(
        string? configured, string? apiKey, Func<InferpalConfig, IInferenceProvider> createClient, CancellationToken ct) =>
        ScanAsync(Places(configured), configured, apiKey, createClient, ct);

    /// <summary>The same, over the given places (tests never probe this machine's real ports).</summary>
    internal static async Task<IReadOnlyList<ServerPlace>> ScanAsync(
        IReadOnlyList<(string Url, string? Expected)> places, string? configured, string? apiKey,
        Func<InferpalConfig, IInferenceProvider> createClient, CancellationToken ct)
    {
        var mine = ProviderProbe.RootOf(configured);
        var looks = places.Select(p => ExamineAsync(
            p.Url, p.Expected, SameAddress(p.Url, mine) ? apiKey : null, createClient, ct));
        return await Task.WhenAll(looks).ConfigureAwait(false);
    }

    /// <summary>What one address answers, with its models counted when a model server is there.</summary>
    public static async Task<ServerPlace> ExamineAsync(
        string url, string? expected, string? apiKey, Func<InferpalConfig, IInferenceProvider> createClient,
        CancellationToken ct)
    {
        // A usual place is only a guess (expected set): asked once when nothing answers. An address given is asked fully.
        var answer = await ProviderProbe.ExamineAsync(url, apiKey, ct, askOnce: expected is not null).ConfigureAwait(false);
        if (answer.Provider is null)
            return new(url, expected,
                answer.NeedsKey ? ServerPlaceOutcome.NeedsKey
                : answer.Answered ? ServerPlaceOutcome.NotAModelServer
                : ServerPlaceOutcome.NothingListening,
                null, answer.Refusal);

        try
        {
            var client = createClient(new InferpalConfig { Provider = answer.Provider, BaseUrl = url, ApiKey = apiKey ?? "" });
            var models = await client.ListModelsByKindAsync(ct, url).ConfigureAwait(false);
            return new(url, expected, ServerPlaceOutcome.Answers, answer.Provider, null, models.Chat.Count, models.Embedding.Count);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            // ⚠ Unknown is not empty: "0 chat models" would send the person to download a model the server may have.
            Diagnostics.Swallow("ServerScan.Models", ex);
            return new(url, expected, ServerPlaceOutcome.Answers, answer.Provider, null, ListFailure: Diagnostics.RootMessage(ex));
        }
    }

    /// <summary>
    /// The server to offer among those that answer: the configured address when it answers — the person's choice — else
    /// the first with a chat model, else the first that answers; null when none does. ⚠ One choice for VS Code's card, the
    /// ACP setup and Visual Studio's first run: two front-ends offering different servers for one scan contradict each
    /// other.
    /// </summary>
    public static ServerPlace? Offered(IReadOnlyList<ServerPlace> places, string? configured)
    {
        var answering = places.Where(p => p.Outcome == ServerPlaceOutcome.Answers && p.Provider is not null).ToList();
        var mine = ProviderProbe.RootOf(configured);
        return answering.FirstOrDefault(p => mine.Length > 0 && SameAddress(p.Url, mine))
            ?? answering.FirstOrDefault(p => p.ChatModels > 0)
            ?? answering.FirstOrDefault();
    }

    /// <summary>Two addresses of one server: same root, <c>/v1</c> and case aside — and <c>localhost</c>,
    /// <c>127.0.0.1</c> and <c>[::1]</c> are one machine, so a port there is one server however it is spelled.</summary>
    internal static bool SameAddress(string? a, string? b)
    {
        string? Loopback(string? url) =>
            Hardware.HardwareProbe.IsLoopback(url) && Uri.TryCreate(url, UriKind.Absolute, out var u) ? $"{u.Scheme}://loopback:{u.Port}" : null;
        if (Loopback(a) is { } la && Loopback(b) is { } lb) return la == lb;
        return string.Equals(ProviderProbe.RootOf(a), ProviderProbe.RootOf(b), StringComparison.OrdinalIgnoreCase);
    }
}
