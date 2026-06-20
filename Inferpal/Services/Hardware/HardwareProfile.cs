using System.Text;
using Inferpal.Config;
using Inferpal.Localization;
using Inferpal.Models;

namespace Inferpal.Services.Hardware;

/// <summary>
/// A point-in-time snapshot of the Ollama host's VRAM situation: the configured budget, the
/// models currently loaded (from <c>/api/ps</c>), and the installed models with their on-disk
/// size (from <c>/api/tags</c>). Pure computation + markdown formatting — no I/O — so it is
/// unit-testable. The actual fetching and budget auto-seed live in the static helpers.
/// </summary>
internal sealed class HardwareProfile
{
    public double BudgetGb { get; }
    public IReadOnlyList<RunningModelInfo>  Running   { get; }
    public IReadOnlyList<InstalledModelInfo> Installed { get; }
    public ContextWindowAdvice? CtxAdvice { get; }

    public HardwareProfile(
        double budgetGb,
        IReadOnlyList<RunningModelInfo> running,
        IReadOnlyList<InstalledModelInfo> installed,
        ContextWindowAdvice? ctxAdvice = null)
    {
        BudgetGb  = budgetGb;
        Running   = running;
        Installed = installed;
        CtxAdvice = ctxAdvice;
    }

    /// <summary>Total VRAM currently held by loaded models, in bytes.</summary>
    public long LoadedVramBytes => Running.Sum(m => m.SizeVram);

    /// <summary>Loaded VRAM in GB.</summary>
    public double LoadedGb => LoadedVramBytes / ModelCatalog.BytesPerGb;

    /// <summary>Remaining VRAM (GB) under a known budget, else <c>null</c>.</summary>
    public double? HeadroomGb => BudgetGb > 0 ? BudgetGb - LoadedGb : null;

    /// <summary>True when at least one loaded model occupies VRAM (GPU offload), else false/unknown.</summary>
    public bool IsGpu => Running.Any(m => m.SizeVram > 0);

    // ── Budget auto-seed ────────────────────────────────────────────────────────

    /// <summary>
    /// When no VRAM budget is set and Ollama runs on loopback, tries to auto-detect the local
    /// GPU's VRAM and persists it. No-op when already set or when Ollama is remote.
    /// </summary>
    public static async Task EnsureBudgetAsync(InferpalConfig config, CancellationToken ct)
    {
        if (config.VramBudgetGb > 0) return;

        var bytes = await HardwareProbe.TryDetectLocalVramBytesAsync(config.BaseUrl, ct).ConfigureAwait(false);
        if (bytes is not { } b || b <= 0) return;

        config.VramBudgetGb = Math.Round(b / ModelCatalog.BytesPerGb, 1);
        config.Save();
    }

    // ── Report ──────────────────────────────────────────────────────────────────

    /// <summary>Renders the <c>/hardware</c> markdown report.</summary>
    public string FormatReport()
    {
        var sb = new StringBuilder(Strings.HardwareReportHeading + "\n\n");

        sb.AppendLine(BudgetGb > 0
            ? Strings.HardwareBudgetLine($"{BudgetGb:0.#}")
            : Strings.HardwareBudgetNotSet);

        if (Running.Count > 0)
        {
            var headroom = HeadroomGb is { } h ? Strings.HardwareHeadroom($"{h:0.#}") : "";
            var ofBudget = BudgetGb > 0 ? Strings.HardwareOfBudget($"{BudgetGb:0.#}") : "";
            sb.AppendLine(Strings.HardwareLoadedLine($"{LoadedGb:0.#}", ofBudget, headroom));
            sb.AppendLine(Strings.HardwareCompute(IsGpu ? "GPU" : "CPU"));
        }
        else
        {
            sb.AppendLine(Strings.HardwareLoadedNone);
        }

        if (Running.Count > 0)
        {
            sb.AppendLine("\n" + Strings.HardwareLoadedModelsTable);
            foreach (var m in Running)
                sb.AppendLine($"| `{m.Name}` | {m.SizeVram / ModelCatalog.BytesPerGb:0.#} GB |");
        }

        if (Installed.Count > 0)
        {
            var loaded = Running.Select(m => m.Name).ToHashSet();
            sb.AppendLine("\n" + Strings.HardwareInstalledModelsTable);
            foreach (var m in Installed.OrderByDescending(m => m.SizeBytes))
            {
                var marker  = loaded.Contains(m.Name) ? " 🟢" : "";
                var diskGb  = m.SizeBytes / ModelCatalog.BytesPerGb;
                var estGb   = ModelCatalog.EstimateVramBytes(m.SizeBytes) / ModelCatalog.BytesPerGb;
                sb.AppendLine($"| `{m.Name}`{marker} | {diskGb:0.#} GB | {estGb:0.#} GB |");
            }
            sb.AppendLine("\n" + Strings.HardwareInstalledNote);
        }

        if (CtxAdvice is { RecommendedMaxCtx: > 0 } adv)
        {
            sb.AppendLine("\n" + Strings.HardwareContextHeading + "\n");
            sb.AppendLine(Strings.HardwareConfiguredCtx(adv.ConfiguredCtx));
            var modelMax = adv.ModelMaxCtx > 0 ? Strings.HardwareModelMax(adv.ModelMaxCtx) : "";
            sb.AppendLine(Strings.HardwareRecommendedCtx(adv.Model, adv.RecommendedMaxCtx, modelMax));
            if (adv.ConfiguredCtx > adv.RecommendedMaxCtx)
                sb.AppendLine("\n" + Strings.HardwareCtxWarn(adv.ConfiguredCtx, adv.RecommendedMaxCtx));
        }

        return sb.ToString().TrimEnd();
    }
}

/// <summary>Advice for the active chat model's context window, derived from its KV-cache cost and
/// the VRAM budget. <see cref="RecommendedMaxCtx"/> is 0 when it cannot be computed (no budget or
/// missing architecture metadata), in which case the report omits the section.</summary>
internal sealed record ContextWindowAdvice(string Model, int ConfiguredCtx, int RecommendedMaxCtx, int ModelMaxCtx);
