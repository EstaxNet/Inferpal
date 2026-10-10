using Inferpal.Config;

namespace Inferpal.Host.Acp;

/// <summary>
/// Whether Inferpal can answer in an ACP client without being set up first — the question behind <c>auth_required</c>.
/// </summary>
/// <remarks>
/// ⚠ "Set up" is not "a settings file exists" alone: with none, the factory settings point at Ollama on this machine,
/// and an installed Ollama with a model answers. Refusing that user would ask them to configure what already works. So:
/// a settings file, or the default server answering.
/// </remarks>
internal static class AcpReadiness
{
    /// <summary>The id of the setup method (terminal authentication).</summary>
    internal const string SetupMethod = "setup";

    /// <summary>The page that says how to use Inferpal from an ACP client.</summary>
    internal const string DocsUrl = "https://github.com/EstaxNet/Inferpal/blob/master/docs/acp.md";

    /// <summary>The server the factory settings use.</summary>
    internal static string DefaultServer => new InferpalConfig().BaseUrl;

    /// <summary>How long the default server is given to answer before it is taken for absent.</summary>
    internal static readonly TimeSpan ProbeBudget = TimeSpan.FromSeconds(3);

    public static async Task<bool> IsReadyAsync(CancellationToken ct)
    {
        if (InferpalConfig.SettingsFileExists) return true;
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(ProbeBudget);
        try
        {
            var (detected, _) = await ProviderProbe.DetectWithRefusalAsync(DefaultServer, apiKey: null, budget.Token);
            return detected is not null;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return false; }
    }
}
