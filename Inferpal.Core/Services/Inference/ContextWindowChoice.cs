using System.Globalization;
using Inferpal.Config;
using Inferpal.Localization;
using Inferpal.Models;

namespace Inferpal.Services.Inference;

/// <summary>What the first run, <c>/setup</c> and "Suggest" decided about the chat model's context window.</summary>
/// <param name="Tokens">The window to write — the factory one included, when nothing worth writing fits; <c>null</c> =
/// leave the window as it is (another server type, a model without a known shape, an unknown card).</param>
/// <param name="Sentence">What the person reads about it; <c>null</c> = nothing worth saying.</param>
internal sealed record WindowAdvice(int? Tokens, string? Sentence);

/// <summary>
/// The context window the first run and <c>/setup</c> give the chat model on Ollama, and "Suggest" proposes: the largest
/// the card holds beside the models that live with it, at most what the battery measures — written only when it reaches
/// <see cref="SmallestWorthwhile"/>, the factory window otherwise.
/// </summary>
/// <remarks>
/// <para>⚠ Ollama only: the window is sized from the model's shape (<c>/api/show</c>), which no other server publishes,
/// and LM Studio loads a model at its own size whatever the client asks.</para>
/// <para>⚠ Only an AUTOMATIC window is resized (<see cref="IsAutomatic(InferpalConfig)"/>): the factory value, or the
/// one the first run or <c>/setup</c> last wrote (<see cref="InferpalConfig.ContextWindowSizeAuto"/>), judged on the
/// FILE. Any other value is the person's — a window "Suggest" proposed and the person saved included. The file is
/// written whole at its first save, so a window typed at 8 192 cannot be told from the factory one.</para>
/// </remarks>
internal static class ContextWindowChoice
{
    /// <summary>The factory window.</summary>
    public static int Factory => new InferpalConfig().ContextWindowSize;

    /// <summary>The largest window Inferpal's battery measures; above it, nothing is measured.</summary>
    public const int MeasuredCeiling = 32768;

    /// <summary>The smallest window worth writing. Below it, the tools and the files a question about a real project
    /// reads still do not fit: a turn overflows or loops as at the factory window, only slower — so the factory one
    /// stays.</summary>
    public const int SmallestWorthwhile = 16384;

    /// <summary>
    /// The largest window whose cache fits a card of <paramref name="budgetGb"/> after the chat model's
    /// <paramref name="chatWeightsBytes"/> and <paramref name="besideBytes"/> of other models — what the memory holds,
    /// uncapped (<c>/hardware</c>'s figure); 0 when the card is unknown or nothing fits.
    /// </summary>
    /// <remarks>⚠ The cache is sized as full attention on every layer (<see cref="ModelCatalog.KvCacheBytesPerToken"/>):
    /// an upper bound, so the window always fits, but a sliding-window or hybrid model (Gemma 4, Qwen3.5 and later)
    /// gets less than its card holds.</remarks>
    public static int FitsMemory(double budgetGb, long chatWeightsBytes, ModelArchInfo arch, long besideBytes) =>
        budgetGb <= 0 || chatWeightsBytes <= 0
            ? 0
            : ModelCatalog.MaxSafeNumCtx(budgetGb - besideBytes / ModelCatalog.BytesPerGb, chatWeightsBytes, arch);

    /// <summary><see cref="FitsMemory"/> at most <see cref="MeasuredCeiling"/>: the window this class writes.</summary>
    public static int Fits(double budgetGb, long chatWeightsBytes, ModelArchInfo arch, long besideBytes) =>
        Math.Min(FitsMemory(budgetGb, chatWeightsBytes, arch, besideBytes), MeasuredCeiling);

    /// <summary>
    /// What lives beside <paramref name="chatModel"/> on its server, by <paramref name="config"/>'s switches: the search
    /// model while the index is on, and a completion model of its own while completions are on — the chat's own model
    /// completes in the chat's runner (<c>OllamaClient.FimNumCtx</c>). A <c>null</c> or empty name is nothing beside.
    /// </summary>
    public static IReadOnlyList<string> Beside(InferpalConfig config, string chatModel, string? searchModel, string? completionModel)
    {
        var beside = new List<string>();
        if (config.RagEnabled && RoleServers.OnChatServer(config, ServerRole.Search) && !string.IsNullOrWhiteSpace(searchModel))
            beside.Add(searchModel);
        if (config.InlineCompletionEnabled && RoleServers.OnChatServer(config, ServerRole.Completion)
            && !string.IsNullOrWhiteSpace(completionModel) && !ModelCatalog.SameModelName(completionModel, chatModel))
            beside.Add(completionModel);
        return beside;
    }

    /// <summary>What lives beside <paramref name="chatModel"/> under <paramref name="config"/> as it stands: the search model
    /// the index uses (the best one <paramref name="installed"/> when left automatic) and the completion model.</summary>
    public static IReadOnlyList<string> Beside(InferpalConfig config, string chatModel, IReadOnlyList<InstalledModelInfo> installed) =>
        Beside(config, chatModel,
               string.IsNullOrWhiteSpace(config.RagEmbeddingModel)
                   ? EmbeddingModels.PreferredOf(ModelsByKind.Of(installed).Embedding) : config.RagEmbeddingModel,
               RoleServers.CompletionModel(config, null));

    /// <summary>The memory <paramref name="beside"/> takes, by their sizes in <paramref name="installed"/> — the one
    /// estimate <c>/setup</c> and <c>/hardware</c> share.</summary>
    public static long BesideBytes(IEnumerable<string> beside, IReadOnlyList<InstalledModelInfo> installed) =>
        beside.Sum(m => ModelCatalog.EstimateVramBytes(SizeOf(m, installed)));

    private static long SizeOf(string name, IReadOnlyList<InstalledModelInfo> installed) =>
        installed.FirstOrDefault(m => ModelCatalog.SameModelName(m.Name, name))?.SizeBytes ?? 0;

    /// <summary>
    /// The window for <paramref name="chatModel"/> on a server of <paramref name="provider"/>, with the sentence that says
    /// it. <paramref name="installed"/> is what that server lists (the sizes); <paramref name="beside"/> the models that
    /// live beside it (<see cref="Beside(InferpalConfig, string, string?, string?)"/>); <paramref name="budgetGb"/> the
    /// card's memory, 0 when unknown.
    /// </summary>
    /// <remarks>The installed list is asked for by the caller, and only on Ollama (<see cref="Applies"/>): another server
    /// gets no window, and a server that hangs costs its listing for nothing.</remarks>
    public static async Task<WindowAdvice> AdviseAsync(
        IInferenceProvider client, string provider, string chatModel, IReadOnlyList<string> beside,
        IReadOnlyList<InstalledModelInfo> installed, double budgetGb, CancellationToken ct)
    {
        if (!Applies(provider)) return new(null, null);
        if (budgetGb <= 0) return new(null, Strings.WindowUnknownCard(Factory));

        var weights = SizeOf(chatModel, installed);
        if (weights <= 0) return new(null, null);
        var arch = await client.ShowModelAsync(chatModel, ct).ConfigureAwait(false);
        if (arch is null) return new(null, null);

        // ⚠ The models themselves must fit first, by the estimate the card's own fit line uses: a window "sized for the
        // card" beside models that already overflow it would hide that they do.
        var besideBytes = BesideBytes(beside, installed);
        if (ModelCatalog.EstimateVramBytes(weights) + besideBytes > budgetGb * ModelCatalog.BytesPerGb) return new(Factory, null);

        var fits = Fits(budgetGb, weights, arch, besideBytes);
        // Too little to change a turn's outcome: the factory window, unsaid.
        return fits < SmallestWorthwhile
            ? new(Factory, null)
            : new(fits, Strings.WindowChosen(fits, budgetGb.ToString("0.#", CultureInfo.CurrentCulture)));
    }

    /// <summary>The window is sized here only for Ollama.</summary>
    public static bool Applies(string provider) =>
        InferenceProviderFactory.Canonical(provider) == InferenceProviderFactory.Ollama;

    /// <summary>
    /// The window is still the product's to resize: the factory one, or the one the first run or <c>/setup</c> last
    /// wrote — judged on the FILE, so a window the person set in the other editor since this copy was read stands.
    /// Anything else is the person's, and nothing here writes or proposes over it.
    /// </summary>
    public static bool IsAutomatic(InferpalConfig config)
    {
        var onDisk = config.ReadFileNow() ?? config;
        return IsAutomatic(onDisk.ContextWindowSize, onDisk.ContextWindowSizeAuto);
    }

    /// <summary>A window of <paramref name="window"/> is automatic when the product last wrote <paramref name="written"/>.</summary>
    public static bool IsAutomatic(int window, int written) => window == Factory || (written > 0 && window == written);

    /// <summary>The window the product last wrote, as the FILE holds it (this copy's when the file cannot be read).</summary>
    public static int WrittenAuto(InferpalConfig config) =>
        (config.ReadFileNow() ?? config).ContextWindowSizeAuto;

    /// <summary>Writes <paramref name="tokens"/> when the window is automatic (<see cref="IsAutomatic(InferpalConfig)"/>),
    /// and remembers it as the product's. <c>true</c> when it was written.</summary>
    public static bool Apply(InferpalConfig config, int tokens)
    {
        if (!IsAutomatic(config)) return false;
        config.ContextWindowSize     = tokens;
        config.ContextWindowSizeAuto = tokens == Factory ? 0 : tokens;
        return true;
    }
}
