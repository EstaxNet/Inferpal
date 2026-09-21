using Inferpal.Localization;

namespace Inferpal.Services.Commands;

/// <summary>
/// Pure execution logic for <c>/models list|delete|running</c>, extracted from
/// <c>InferpalToolWindowData</c> so it is unit-testable with a <c>FakeInferenceProvider</c>.
/// </summary>
/// <remarks>
/// <c>/models pull</c> is deliberately NOT handled here: it owns a live status bubble (insert →
/// update-per-progress → remove) that is inherently VM/UI work, so it stays in the VM. This handler
/// covers the cases that reduce to "call the backend → format → message", and returns the markdown to
/// display. Capability gating (model management / VRAM monitoring) is enforced here for
/// <c>delete</c>/<c>running</c>; the VM enforces it for <c>pull</c>. Same pattern as
/// <see cref="SnippetsCommandHandler"/>.
/// </remarks>
internal static class ModelsCommandHandler
{
    /// <summary>Outcome of a <c>/models</c> invocation handled here.</summary>
    internal readonly record struct ModelsCommandResult(string Message);

    /// <summary>Handles <c>/models</c> (list), <c>/models delete &lt;name&gt;</c> and
    /// <c>/models running</c>. <paramref name="parts"/> is the whitespace-split command line.</summary>
    /// <param name="config">Read for the backend URL only — named when the backend does not answer.</param>
    public static async Task<ModelsCommandResult> HandleAsync(
        IInferenceProvider client, Config.InferpalConfig config, string[] parts, CancellationToken ct)
    {
        var sub = parts.Length >= 2 ? parts[1].ToLowerInvariant() : "list";

        // delete needs /api model management; running needs VRAM monitoring — Ollama-only.
        if ((sub == "delete"  && !client.Capabilities.ModelManagement) ||
            (sub == "running" && !client.Capabilities.VramMonitoring))
            return new(Strings.ModelsBackendUnsupported);

        if (sub == "delete")
        {
            if (parts.Length < 3) return new(Strings.ModelsDeleteUsage);
            var model = string.Join(" ", parts[2..]);
            var ok    = await client.DeleteModelAsync(model, ct);
            return new(ok ? Strings.ModelsDeleted(model) : Strings.ModelsDeleteFailed(model));
        }

        if (sub == "running")
        {
            var running = await client.GetRunningModelsAsync(ct);
            return new(running.Count == 0
                ? await EmptyMeans(client, config, Strings.ModelsNoneRunning, ct)
                : ModelCatalog.FormatRunningModels(running));
        }

        // /models (list) and any unknown sub-command.
        var models   = await client.ListModelsAsync(ct);
        var running2 = await client.GetRunningModelsAsync(ct);
        return new(models.Count == 0
            ? await EmptyMeans(client, config, Strings.ModelsNoneInstalled, ct)
            : ModelCatalog.FormatInstalledModels(models, running2));
    }

    // ⚠ The discriminator lives in `ModelCatalog`, not here: `/bench` renders the same empty list
    // and said "install one (/models pull …)" — a remedy that needs the backend that is down. The
    // ambiguity was already written down in THIS file for the other reader (`SwitchMessageAsync`:
    // "an empty list — backend unreachable, or nothing installed — cannot judge, so it adds
    // nothing"). It cannot judge; these can, by asking, and they must all ask the same way.
    private static Task<string> EmptyMeans(
        IInferenceProvider client, Config.InferpalConfig config, string nothingInstalled, CancellationToken ct) =>
        ModelCatalog.EmptyListMeansAsync(client, config, nothingInstalled, ct);

    private static readonly TimeSpan SwitchListBudget = TimeSpan.FromSeconds(3);

    /// <summary>
    /// What <c>/model &lt;name&gt;</c> answers once the name is set: the confirmation, plus a warning when
    /// the backend lists its models and this name is not among them.
    /// </summary>
    /// <remarks>
    /// A warning, never a refusal: some OpenAI-compatible servers (llama.cpp) list a single id and serve
    /// any name. An empty list — backend unreachable, or nothing installed — cannot judge, so it adds
    /// nothing. The listing is bounded so an unreachable backend does not hold the command for the
    /// client's own timeout.
    /// </remarks>
    public static async Task<string> SwitchMessageAsync(IInferenceProvider client, string model, CancellationToken ct)
    {
        var changed = Strings.SlashModelChanged(model);

        IReadOnlyList<string> listed;
        try
        {
            using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
            bounded.CancelAfter(SwitchListBudget);
            listed = await client.ListModelsAsync(bounded.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return changed; }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Diagnostics.Swallow("ModelsCommandHandler.SwitchMessage", ex);
            return changed;
        }

        return listed.Count == 0 || listed.Any(name => ModelCatalog.SameModelName(name, model))
            ? changed
            : changed + "\n\n" + Strings.SlashModelNotListed(model);
    }
}
