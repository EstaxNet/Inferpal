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
/// <para>⚠ Only an AUTOMATIC window is resized (<see cref="IsAutomatic(InferpalConfig)"/>): the one the product last
/// wrote (<see cref="InferpalConfig.ContextWindowSizeAuto"/>), or the factory value when it never wrote one, judged on
/// the FILE. Any other value is the person's — a window "Suggest" proposed and the person saved included, and the
/// factory one set back by hand over a window the product wrote. The file is written whole at its first save, so a
/// window typed at 8 192 before the product ever sized one cannot be told from the factory one.</para>
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
    /// model while the code index is on or <c>@Docs</c> has sources, and a completion model of its own while completions
    /// are on — the chat's own model completes in the chat's runner (<c>OllamaClient.FimNumCtx</c>). A <c>null</c> or
    /// empty name is nothing beside.
    /// </summary>
    public static IReadOnlyList<string> Beside(InferpalConfig config, string chatModel, IReadOnlyList<string> searchModels,
                                               string? completionModel)
    {
        var beside = new List<string>();
        if ((config.RagEnabled || RoleServers.HasDocSources(config)) && RoleServers.OnChatServer(config, ServerRole.Search))
            beside.AddRange(searchModels.Where(m => !string.IsNullOrWhiteSpace(m)));
        if (config.InlineCompletionEnabled && RoleServers.OnChatServer(config, ServerRole.Completion)
            && !string.IsNullOrWhiteSpace(completionModel) && !ModelCatalog.SameModelName(completionModel, chatModel))
            beside.Add(completionModel);
        return beside;
    }

    /// <summary>
    /// The search models that get loaded beside the chat under <paramref name="config"/>: <paramref name="chosen"/> when the
    /// field names one (both indexes use it); left automatic, the code index's pick while it is on AND <c>@Docs</c>' while
    /// it has sources — each as it picks it, so possibly two (<see cref="EmbeddingModels.PreferredOf"/>: the code index
    /// passes over a family that searches code below keywords, <c>@Docs</c> does not).
    /// </summary>
    public static IReadOnlyList<string> SearchModelsBeside(InferpalConfig config, string? chosen, IReadOnlyList<string> installedEmbeddings)
    {
        if (!string.IsNullOrWhiteSpace(chosen)) return [chosen];
        var models = new List<string>();
        if (config.RagEnabled && EmbeddingModels.PreferredOf(installedEmbeddings) is { } code) models.Add(code);
        if (RoleServers.HasDocSources(config) && EmbeddingModels.PreferredOf(installedEmbeddings, forCode: false) is { } docs
            && !models.Any(m => ModelCatalog.SameModelName(m, docs)))
            models.Add(docs);
        return models;
    }

    /// <summary>What lives beside <paramref name="chatModel"/> under <paramref name="config"/> as it stands: the search models
    /// that get loaded (<see cref="SearchModelsBeside"/>) and the completion model.</summary>
    public static IReadOnlyList<string> Beside(InferpalConfig config, string chatModel, IReadOnlyList<InstalledModelInfo> installed) =>
        Beside(config, chatModel,
               SearchModelsBeside(config, config.RagEmbeddingModel, ModelsByKind.Of(installed).Embedding),
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
    /// live beside it (<see cref="Beside(InferpalConfig, string, IReadOnlyList{string}, string?)"/>); <paramref name="budgetGb"/> the
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
    /// The window is still the product's to resize: the one the product last wrote, or the factory one when it never
    /// wrote one — judged on the FILE, so a window the person set in the other editor since this copy was read stands.
    /// Anything else is the person's, and nothing here writes or proposes over it.
    /// </summary>
    public static bool IsAutomatic(InferpalConfig config)
    {
        var onDisk = config.ReadFileNow() ?? config;
        return IsAutomatic(onDisk.ContextWindowSize, onDisk.ContextWindowSizeAuto);
    }

    /// <summary>A window of <paramref name="window"/> is automatic when the product last wrote <paramref name="written"/>.</summary>
    /// <remarks>⚠ The product writes the factory window only with no mark (<see cref="Apply"/>): the factory window under a
    /// mark is the person's, set back by hand after the product sized it — never resized over again.</remarks>
    public static bool IsAutomatic(int window, int written) => written > 0 ? window == written : window == Factory;

    /// <summary>The window the product last wrote, as the FILE holds it (this copy's when the file cannot be read).</summary>
    public static int WrittenAuto(InferpalConfig config) =>
        (config.ReadFileNow() ?? config).ContextWindowSizeAuto;

    /// <summary>Writes <paramref name="tokens"/> when the window is automatic (<see cref="IsAutomatic(InferpalConfig)"/>),
    /// and remembers it as the product's. <c>true</c> when it was written.</summary>
    public static bool Apply(InferpalConfig config, int tokens)
    {
        if (!IsAutomatic(config)) return false;
        // Decided on the file: a size equal to what this copy read is still a change from what the file holds now.
        config.TakeFromFile(nameof(InferpalConfig.ContextWindowSize), nameof(InferpalConfig.ContextWindowSizeAuto));
        config.ContextWindowSize     = tokens;
        config.ContextWindowSizeAuto = tokens == Factory ? 0 : tokens;
        return true;
    }

    /// <summary>The FILE holds the factory window and no mark: the window was never sized — an install from before the
    /// first run sized it, or one whose card held nothing worth writing then.</summary>
    private static bool NeverSized(InferpalConfig config)
    {
        var onDisk = config.ReadFileNow() ?? config;
        return onDisk.ContextWindowSize == Factory && onDisk.ContextWindowSizeAuto == 0;
    }

    /// <summary>
    /// At an editor's start: a window never sized (<see cref="NeverSized"/>) is sized for the card and
    /// <paramref name="chatModel"/> as the first run does, saved, and the sentence that says it — once, since the window
    /// written is no longer the factory one. <c>null</c>: nothing written (another server, an unknown card or model,
    /// nothing above the factory window), nothing to say.
    /// </summary>
    /// <remarks>Never throws but for a cancellation: it runs at a start, where a failure has nobody to tell — traced. A
    /// start that kept the factory window is not asked again in this process for the same server, model and card
    /// (<see cref="Settled"/>): an 8 GB card stays at the factory window for good, and every reconnection and every ACP
    /// session would otherwise ask the server again. A start that learnt nothing (the server down) is asked again.</remarks>
    public static async Task<string?> CatchUpAsync(InferpalConfig config, IInferenceProvider client, string chatModel, CancellationToken ct)
    {
        try
        {
            if (!Asks(config, client, chatModel) || !NeverSized(config) || Settled.ContainsKey(SettledKey(config, chatModel)))
                return null;
            var advice = await AdviseForAsync(config, client, chatModel, ct).ConfigureAwait(false);
            // No size: nothing was known (a server down lists nothing, a model it does not show) — asked again at the next
            // start, the reconnection included. Only a decision is settled.
            if (advice.Tokens is not { } tokens) return null;
            if (tokens <= Factory)
            {
                // Keyed on the card as it is now: the advice may have just detected it.
                Settled.TryAdd(SettledKey(config, chatModel), 0);
                return null;
            }
            // Read again: the other editor may have sized it, or the person set it, while the server was asked.
            if (!NeverSized(config) || !Apply(config, tokens)) return null;
            config.Save();
            return Strings.WindowCaughtUp(tokens, Factory, CardGb(config), Strings.SettingsPageServer, Strings.LabelContextWindowSize);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Diagnostics.Swallow("ContextWindowChoice.CatchUp", ex);
            return null;
        }
    }

    /// <summary>The starts that decided nothing, by server, model and card (<see cref="CatchUpAsync"/>).</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> Settled = new(StringComparer.Ordinal);

    private static string SettledKey(InferpalConfig config, string chatModel) =>
        FormattableString.Invariant($"{config.BaseUrl}|{chatModel}|{config.VramBudgetGb}");

    /// <summary>
    /// After the chat model changed to <paramref name="chatModel"/>: an automatic window is sized for it — larger, smaller,
    /// or back to the factory one —, saved, and said. <c>null</c>: the person's window, another server, an unknown card or
    /// model, or the same size.
    /// </summary>
    /// <remarks>Never throws but for a cancellation: the model switch it follows is already saved, and its own sentence
    /// and effects must not be lost to a failure here — traced.</remarks>
    public static async Task<string?> FollowAsync(InferpalConfig config, IInferenceProvider client, string chatModel, CancellationToken ct)
    {
        try
        {
            if (!Asks(config, client, chatModel) || !IsAutomatic(config)) return null;
            var advice = await AdviseForAsync(config, client, chatModel, ct).ConfigureAwait(false);
            if (advice.Tokens is not { } tokens || tokens == (config.ReadFileNow() ?? config).ContextWindowSize
                || !Apply(config, tokens))
                return null;
            config.Save();
            // The factory window comes back unsaid by the advice: the model leaves the card no room, or none worth
            // writing — never "sized for the card", which a model that overflows it is not.
            return advice.Sentence ?? Strings.WindowBackToFactory(tokens, chatModel);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Diagnostics.Swallow("ContextWindowChoice.Follow", ex);
            return null;
        }
    }

    private static string CardGb(InferpalConfig config) => config.VramBudgetGb.ToString("0.#", CultureInfo.CurrentCulture);

    /// <summary>An Ollama server to ask, through a client of its type: a backend switched since the client was built
    /// (the host's, until it restarts) would answer through the other protocol.</summary>
    private static bool Asks(InferpalConfig config, IInferenceProvider client, string chatModel) =>
        Applies(config.Provider) && !string.IsNullOrWhiteSpace(chatModel)
        && InferenceProviderFactory.PendingSwitch(client, config.Provider) is null;

    /// <summary><see cref="AdviseAsync"/> for <paramref name="chatModel"/> under <paramref name="config"/> as it stands, the
    /// card's memory made known first as the first run does. A card still unknown decides nothing, and the server is not
    /// listed for it: these run at every start and every model switch.</summary>
    private static async Task<WindowAdvice> AdviseForAsync(InferpalConfig config, IInferenceProvider client, string chatModel,
                                                           CancellationToken ct)
    {
        await HardwareProfile.EnsureBudgetAsync(config, ct).ConfigureAwait(false);
        if (config.VramBudgetGb <= 0) return new(null, null);
        var installed = await client.ListInstalledModelsAsync(ct).ConfigureAwait(false);
        return await AdviseAsync(client, config.Provider, chatModel, Beside(config, chatModel, installed), installed,
                                 config.VramBudgetGb, ct).ConfigureAwait(false);
    }
}
