using Inferpal.Config;
using Inferpal.Localization;

namespace Inferpal.Host.Acp;

/// <summary>
/// <c>Inferpal.Host --acp --setup</c>: the setup an ACP client runs in a terminal (its "terminal" authentication) —
/// the model server, the model, saved in the settings Visual Studio and VS Code share.
/// </summary>
/// <remarks>
/// <para>
/// Exit code 0 means set up: the client then reconnects and opens its session. Anything else — the input closed, no
/// server answered, no model installed — leaves the settings as they were, and says why.
/// </para>
/// <para>
/// ⚠ A server is accepted only once it ANSWERED (<see cref="ProviderProbe"/>), and the backend that answered is the one
/// saved — the same rule as the settings' Test button: a choice typed but never reached would leave a client that
/// opens sessions to a server that is not there.
/// </para>
/// </remarks>
internal static class AcpSetup
{
    private static readonly (string Code, string Url)[] Servers =
    [
        (InferenceProviderFactory.Ollama,           "http://localhost:11434"),
        (InferenceProviderFactory.LmStudio,         "http://localhost:1234"),
        (InferenceProviderFactory.OpenAiCompatible, string.Empty),
    ];

    /// <param name="probe">Test seam: which backend answers at an address (default: <see cref="ProviderProbe"/>).</param>
    /// <param name="listModels">Test seam: the models a server lists.</param>
    /// <param name="save">Test seam: where the settings go (default: <see cref="InferpalConfig.Save"/>).</param>
    public static async Task<int> RunAsync(
        TextReader input, TextWriter output, CancellationToken ct,
        Func<string, string?, CancellationToken, Task<(string? Provider, string? Refusal)>>? probe = null,
        Func<InferpalConfig, CancellationToken, Task<IReadOnlyList<string>>>? listModels = null,
        Func<InferpalConfig>? load = null, Action<InferpalConfig>? save = null)
    {
        probe      ??= ProviderProbe.DetectWithRefusalAsync;
        listModels ??= static (cfg, token) => InferenceProviderFactory.Create(cfg).ListModelsAsync(token);
        var config   = (load ?? InferpalConfig.Load)();
        save       ??= static cfg => cfg.Save();

        output.WriteLine(Strings.AcpSetupTitle);
        output.WriteLine();

        // ── The server ─────────────────────────────────────────────────────────
        var current = Array.FindIndex(Servers, s => s.Code == InferenceProviderFactory.Canonical(config.Provider));
        if (current < 0) current = 0;
        for (var i = 0; i < Servers.Length; i++)
            output.WriteLine($"  {i + 1}. {InferenceProviderFactory.DisplayName(Servers[i].Code)}");
        var choice = await AskChoiceAsync(input, output, Strings.AcpSetupServerQuestion(current + 1), Servers.Length, current, ct);
        if (choice is null) return Stopped(output);
        var server = Servers[choice.Value];

        string? found = null;
        string url = string.Empty, key = config.ApiKey;
        var suggested = server.Code == InferenceProviderFactory.Canonical(config.Provider) ? config.BaseUrl : server.Url;
        while (found is null)
        {
            var typed = await AskAsync(input, output, Strings.AcpSetupAddressQuestion(suggested), ct);
            if (typed is null) return Stopped(output);
            url = typed.Length == 0 ? suggested : typed.Trim();
            if (url.Length == 0) continue;
            if (server.Code == InferenceProviderFactory.OpenAiCompatible)
            {
                var typedKey = await AskAsync(input, output, Strings.AcpSetupKeyQuestion, ct);
                if (typedKey is null) return Stopped(output);
                key = typedKey.Trim();
            }

            output.WriteLine(Strings.AcpSetupChecking(url));
            var (provider, refusal) = await probe(url, string.IsNullOrEmpty(key) ? null : key, ct);
            if (provider is not null)
            {
                found = provider;
                output.WriteLine(Strings.AcpSetupReached(InferenceProviderFactory.DisplayName(provider), url));
            }
            else
            {
                output.WriteLine(refusal is null ? Strings.AcpSetupNotReached(url) : Strings.StatusRefused(refusal));
                suggested = url;
            }
        }

        var draft = new InferpalConfig { Provider = found, BaseUrl = url, ApiKey = key };

        // ── The model ──────────────────────────────────────────────────────────
        IReadOnlyList<string> listed;
        try { listed = await listModels(draft, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { listed = []; output.WriteLine(ex.Message); }
        var models = listed.Where(m => !ModelCatalog.IsEmbeddingModel(m)).ToList();
        if (models.Count == 0)
        {
            output.WriteLine(Strings.AcpSetupNoModel(InferenceProviderFactory.DisplayName(found)));
            return 1;
        }

        var preferred = models.Contains(config.DefaultModel, StringComparer.Ordinal) ? config.DefaultModel : ModelCatalog.PickBestChatModel(models);
        var at = Math.Max(0, models.IndexOf(preferred));
        output.WriteLine();
        for (var i = 0; i < models.Count; i++) output.WriteLine($"  {i + 1}. {models[i]}");
        var picked = await AskChoiceAsync(input, output, Strings.AcpSetupModelQuestion(at + 1), models.Count, at, ct);
        if (picked is null) return Stopped(output);

        config.Provider     = found;
        config.BaseUrl      = url;
        config.ApiKey       = key;
        config.DefaultModel = models[picked.Value];
        save(config);
        output.WriteLine();
        output.WriteLine(Strings.AcpSetupSaved(InferpalConfig.SettingsFilePath));
        return 0;
    }

    private static int Stopped(TextWriter output)
    {
        output.WriteLine();
        output.WriteLine(Strings.AcpSetupStopped);
        return 1;
    }

    /// <summary>A line typed by the user; <c>null</c> when the input closed (the terminal was shut).</summary>
    private static async Task<string?> AskAsync(TextReader input, TextWriter output, string question, CancellationToken ct)
    {
        output.Write(question);
        output.Flush();
        return await input.ReadLineAsync(ct);
    }

    /// <summary>A number from a list (1-based on screen), the default on an empty line; <c>null</c> when the input closed.</summary>
    private static async Task<int?> AskChoiceAsync(TextReader input, TextWriter output, string question, int count, int byDefault,
                                                  CancellationToken ct)
    {
        while (true)
        {
            var line = await AskAsync(input, output, question, ct);
            if (line is null) return null;
            if (line.Trim().Length == 0) return byDefault;
            if (int.TryParse(line.Trim(), out var n) && n >= 1 && n <= count) return n - 1;
            output.WriteLine(Strings.AcpSetupPickANumber(count));
        }
    }
}
