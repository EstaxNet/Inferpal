using System.Globalization;
using Inferpal.Config;
using Inferpal.Localization;
using Inferpal.Models;
using Inferpal.Services.Hardware;

namespace Inferpal.Services.Presentation;

/// <summary>One model the server holds in memory, as the settings page shows it.</summary>
/// <param name="Name">The model, as the server names it — what an unload is asked for.</param>
/// <param name="Uses">What Inferpal uses it for ("Used for chat, agent"), or that the settings do not use it.</param>
/// <param name="Details">Its memory, context and when it unloads, as known: "14.2 GB of graphics memory · 32,768 tokens".</param>
internal sealed record LoadedModelRow(string Name, string Uses, string Details);

/// <summary>The "Loaded now" block of the models page.</summary>
/// <param name="Summary">How many models are loaded and what they occupy — or why it is not known.</param>
/// <param name="CanUnload">The server lets Inferpal unload a model (the buttons are offered).</param>
/// <param name="Message">What the last unload did, if one ran.</param>
internal sealed record LoadedModelsModel(string Summary, IReadOnlyList<LoadedModelRow> Rows, bool CanUnload, string? Message = null);

/// <summary>
/// Which models the server holds in memory, what Inferpal uses each one for, and the unload gestures — one reader for
/// both editors' settings pages.
/// </summary>
/// <remarks>
/// <para>
/// ⚠ <b>Unknown is not empty.</b> A server that does not answer, and one that cannot say (a generic OpenAI-compatible
/// server lists no loaded state), are not "no model is loaded": each has its own sentence.
/// </para>
/// <para>
/// ⚠ A memory figure is the server's: Ollama reports the graphics memory a model occupies (0 = it runs on the processor);
/// LM Studio does not, and its size ON DISK is shown instead, said to be one — never a "0 GB".
/// </para>
/// </remarks>
internal static class LoadedModelsCard
{
    /// <summary>Reads the server and builds the block.</summary>
    /// <param name="embeddingModel">The model the code index embeds with, if any (the index knows; the setting may be empty).</param>
    public static async Task<LoadedModelsModel> ReadAsync(
        IInferenceProvider client, InferpalConfig config, string? embeddingModel, CancellationToken ct, string? message = null)
    {
        var caps = client.Capabilities;
        if (!caps.VramMonitoring) return new(Strings.LoadedModelsUnknown, [], false, message);

        // ⚠ Not said is not empty: "No model is loaded" over loaded models is the answer of a server whose loaded list
        // cannot be read while the rest of it answers.
        var running = await client.ReadRunningModelsAsync(ct);
        if (running is not { Count: > 0 })
        {
            // An unreachable server says nothing either: asked once, on this branch only.
            var silent = await ModelCatalog.UnreachableBackendAsync(client, config, ct);
            return new(silent is not null ? Strings.LoadedModelsUnreachable
                     : running is null    ? Strings.LoadedModelsUnknown
                     :                      Strings.LoadedModelsNone, [], false, message);
        }

        var sizes = running.Any(m => !m.ReportsVram)
            ? (await client.ListInstalledModelsAsync(ct)).ToDictionary(m => m.Name, m => m.SizeBytes, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, long>();
        var contexts = new Dictionary<string, int?>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in running)
            contexts[m.Name] = await client.GetLoadedContextWindowAsync(m.Name, ct);

        return Build(running, caps.ModelManagement, config, embeddingModel, sizes, contexts, DateTime.UtcNow, message);
    }

    /// <summary>The block, from what the server answered — pure.</summary>
    internal static LoadedModelsModel Build(
        IReadOnlyList<RunningModelInfo> running, bool canUnload, InferpalConfig config, string? embeddingModel,
        IReadOnlyDictionary<string, long> diskSizes, IReadOnlyDictionary<string, int?> contexts, DateTime nowUtc,
        string? message = null)
    {
        if (running.Count == 0) return new(Strings.LoadedModelsNone, [], false, message);

        var rows = running.Select(m => new LoadedModelRow(
            m.Name,
            Uses(m.Name, config, embeddingModel),
            string.Join(" · ", new[]
            {
                Memory(m, diskSizes),
                contexts.TryGetValue(m.Name, out var ctx) && ctx is > 0
                    ? Strings.LoadedModelContext(ctx.Value.ToString("N0", CultureInfo.CurrentCulture)) : "",
                Expiry(m.ExpiresAt, nowUtc),
            }.Where(p => p.Length > 0)))).ToList();

        var summary = Strings.LoadedModelsCount(running.Count);
        if (running.All(m => m.ReportsVram))
            summary += " · " + Strings.LoadedModelVram(Gb(running.Sum(m => m.SizeVram)));
        return new(summary, rows, canUnload, message);
    }

    /// <summary>What the settings route to <paramref name="model"/>: each role whose model resolves to it.</summary>
    internal static string Uses(string model, InferpalConfig config, string? embeddingModel)
    {
        var roles = new List<string>();
        void Role(ModelRole role, string label)
        {
            if (ModelCatalog.SameModelName(ModelRouter.Resolve(config, role), model) && !roles.Contains(label)) roles.Add(label);
        }
        Role(ModelRole.Chat, Strings.ModelRoleChat);
        Role(ModelRole.Agent, Strings.ModelRoleAgent);
        Role(ModelRole.CodeActions, Strings.ModelRoleCodeActions);
        Role(ModelRole.InlineEdit, Strings.ModelRoleInlineEdit);
        if (config.InlineCompletionEnabled) Role(ModelRole.Fim, Strings.ModelRoleAutocomplete);
        Role(ModelRole.Utility, Strings.ModelRoleUtility);
        if ((EmbeddingModels.Configured(config) ?? embeddingModel) is { } embedding
            && ModelCatalog.SameModelName(embedding, model))
            roles.Add(Strings.ModelRoleCodeSearch);
        return roles.Count == 0 ? Strings.LoadedModelUnused : Strings.LoadedModelUses(string.Join(", ", roles));
    }

    private static string Memory(RunningModelInfo m, IReadOnlyDictionary<string, long> diskSizes) =>
        m.SizeVram > 0  ? Strings.LoadedModelVram(Gb(m.SizeVram))
        : m.SizeVram == 0 ? Strings.LoadedModelCpu
        : diskSizes.TryGetValue(m.Name, out var disk) && disk > 0 ? Strings.LoadedModelDiskSize(Gb(disk))
        : "";

    /// <summary>When the server unloads the model on its own: Ollama's <c>expires_at</c>; empty when it does not say.</summary>
    internal static string Expiry(string expiresAt, DateTime nowUtc)
    {
        if (string.IsNullOrWhiteSpace(expiresAt)
            || !DateTime.TryParse(expiresAt, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var at))
            return "";
        var left = at - nowUtc;
        // keep_alive -1 is answered with a date centuries away: the model stays.
        if (left > TimeSpan.FromDays(1)) return Strings.LoadedModelStays;
        return left <= TimeSpan.Zero ? "" : Strings.LoadedModelUnloadsIn(Math.Max(1, (int)Math.Ceiling(left.TotalMinutes)));
    }

    private static string Gb(long bytes) =>
        (bytes / ModelCatalog.BytesPerGb).ToString("0.0", CultureInfo.CurrentCulture);
}

/// <summary>Unloads models from the server, and says what it really did.</summary>
/// <remarks>
/// ⚠ An unload is a request the server may refuse or ignore: the result is read back from the server's own list,
/// never assumed. And it is refused while a question is being answered — the model would be pulled from under it.
/// ⚠ An EMPTY list is also what a server that did not answer gives back (both clients turn a failed listing into
/// none): before the unload it read "nothing was loaded" about the models the card had just shown, after it
/// "unloaded" about a model still in memory. Empty is checked against the server, on those branches only.
/// </remarks>
internal static class ModelUnloader
{
    /// <param name="only">The models to unload; <c>null</c> = every loaded one.</param>
    /// <param name="isAnswering">Whether a question is being answered; <c>null</c> = <see cref="GpuScheduler.IsChatActive"/>
    /// (process-wide state, which a test replaces).</param>
    /// <returns>The sentence that says what happened.</returns>
    public static async Task<string> UnloadAsync(
        IInferenceProvider client, InferpalConfig config, IReadOnlyList<string>? only, CancellationToken ct,
        Func<bool>? isAnswering = null)
    {
        if (!client.Capabilities.ModelManagement) return Strings.UnloadNotSupported;
        if ((isAnswering ?? (() => GpuScheduler.IsChatActive))()) return Strings.UnloadWhileAnswering;

        var before  = await client.GetRunningModelsAsync(ct);
        var targets = before.Select(m => m.Name)
            .Where(n => only is null || only.Any(o => ModelCatalog.SameModelName(o, n)))
            .ToList();
        if (before.Count == 0) return await ModelCatalog.EmptyListMeansAsync(client, config, Strings.UnloadNothing, ct);
        if (targets.Count == 0) return Strings.UnloadNothing;

        foreach (var name in targets)
            await client.UnloadModelAsync(name, ct);

        var after = await client.GetRunningModelsAsync(ct);
        if (after.Count == 0 && await ModelCatalog.UnreachableBackendAsync(client, config, ct) is not null)
            return Strings.UnloadUnverified(string.Join(", ", targets));
        var kept  = targets.Where(t => after.Any(a => ModelCatalog.SameModelName(a.Name, t))).ToList();
        var done  = targets.Except(kept).ToList();
        var parts = new List<string>();
        if (done.Count > 0) parts.Add(Strings.UnloadDone(string.Join(", ", done)));
        if (kept.Count > 0) parts.Add(Strings.UnloadKept(string.Join(", ", kept)));
        return string.Join(" ", parts);
    }
}
