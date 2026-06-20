using System.Globalization;
using Inferpal.Config;
using Inferpal.Localization;
using Inferpal.Models;

namespace Inferpal.Services.Commands;

/// <summary>
/// Pure execution logic for <c>/hardware</c> — set the VRAM budget (<c>/hardware &lt;gb&gt;</c>) or
/// render the GPU/VRAM profile report. Extracted from <c>InferpalToolWindowData</c> so it is
/// unit-testable with a <c>FakeInferenceProvider</c>.
/// </summary>
/// <remarks>
/// The handler never persists the config itself: a successful <c>/hardware &lt;gb&gt;</c> returns the
/// new budget in <see cref="HardwareCommandResult.SetBudgetGb"/> and the VM applies + saves it (so
/// tests never touch the real <c>%APPDATA%</c> config). The report path calls
/// <see cref="HardwareProfile.EnsureBudgetAsync"/> (a no-op once a budget is set) and builds the
/// profile from live backend data. Same pattern as <see cref="SnippetsCommandHandler"/>.
/// </remarks>
internal static class HardwareCommandHandler
{
    /// <summary>Outcome of a <c>/hardware</c> invocation.</summary>
    /// <param name="Message">Markdown message to show the user.</param>
    /// <param name="SetBudgetGb">When non-null, the VM must set the VRAM budget to this value and save.</param>
    internal readonly record struct HardwareCommandResult(string Message, double? SetBudgetGb = null);

    /// <summary>Handles <c>/hardware</c> (profile report) and <c>/hardware &lt;gb&gt;</c> (set budget).</summary>
    public static async Task<HardwareCommandResult> HandleAsync(
        InferpalConfig config, IInferenceProvider client, string[] parts, CancellationToken ct)
    {
        // /hardware <gb> — set the VRAM budget manually (Ollama can't report it; may be remote).
        if (parts.Length >= 2 &&
            double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var gb))
        {
            return gb <= 0
                ? new(Strings.HardwareUsage)
                : new(Strings.HardwareBudgetSet($"{gb:0.#}"), SetBudgetGb: gb);
        }

        // /hardware — auto-seed the budget when local, then show the profile.
        await HardwareProfile.EnsureBudgetAsync(config, ct);

        // OpenAI-compatible backends expose no live per-model VRAM / architecture; the manual budget
        // and fit-checks still apply, so report that rather than printing an empty live section.
        if (!client.Capabilities.VramMonitoring)
            return new(Strings.HardwareNoVramBackend);

        // Independent backend round-trips → run concurrently (Task.WhenAll observes both on fault).
        var runningTask   = client.GetRunningModelsAsync(ct);
        var installedTask = client.ListInstalledModelsAsync(ct);
        await Task.WhenAll(runningTask, installedTask);
        var running   = await runningTask;
        var installed = await installedTask;
        var ctxAdvice = await BuildContextWindowAdviceAsync(client, config, installed, ct);
        var profile   = new HardwareProfile(config.VramBudgetGb, running, installed, ctxAdvice);

        return new(profile.FormatReport());
    }

    /// <summary>
    /// Computes the recommended max <c>num_ctx</c> for the active chat model from its KV-cache cost
    /// (<c>/api/show</c> architecture) and the VRAM budget. Returns <c>null</c> when the budget is
    /// unknown or the architecture metadata is unavailable, so the report simply omits the section.
    /// </summary>
    private static async Task<ContextWindowAdvice?> BuildContextWindowAdviceAsync(
        IInferenceProvider client, InferpalConfig config,
        IReadOnlyList<InstalledModelInfo> installed, CancellationToken ct)
    {
        var model = config.DefaultModel;
        if (config.VramBudgetGb <= 0 || string.IsNullOrEmpty(model)) return null;

        var arch = await client.ShowModelAsync(model, ct);
        if (arch is null) return null;

        var weights     = installed.FirstOrDefault(m => m.Name == model)?.SizeBytes ?? 0;
        var recommended = ModelCatalog.MaxSafeNumCtx(config.VramBudgetGb, weights, arch);

        return new ContextWindowAdvice(model, config.ContextWindowSize, recommended, arch.ContextLength);
    }
}
