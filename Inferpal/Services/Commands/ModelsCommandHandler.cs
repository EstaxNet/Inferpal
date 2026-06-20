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
    public static async Task<ModelsCommandResult> HandleAsync(
        IInferenceProvider client, string[] parts, CancellationToken ct)
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
                ? Strings.ModelsNoneRunning
                : ModelCatalog.FormatRunningModels(running));
        }

        // /models (list) and any unknown sub-command.
        var models   = await client.ListModelsAsync(ct);
        var running2 = await client.GetRunningModelsAsync(ct);
        return new(models.Count == 0
            ? Strings.ModelsNoneInstalled
            : ModelCatalog.FormatInstalledModels(models, running2));
    }
}
