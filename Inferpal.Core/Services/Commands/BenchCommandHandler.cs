using System.Globalization;
using Inferpal.Localization;
using Inferpal.Services.Bench;

namespace Inferpal.Services.Commands;

/// <summary>
/// Pure logic of <c>/bench [model…]</c> and <c>/bench last</c> — the local model test bench
/// (ROADMAP 1.3.0 §5). Runs the frozen <see cref="BenchTasks"/> suite on each requested model
/// (default: every installed model, capped), formats the comparative markdown table and derives a
/// per-role recommendation that feeds the Model Router. UI concerns (live status bubble) stay in
/// the callers; progress is reported through the <c>onProgress</c> callback, same convention as
/// <c>PullModelAsync</c>.
/// </summary>
internal static class BenchCommandHandler
{
    /// <summary>Outcome of a <c>/bench</c> invocation: the markdown to display.</summary>
    internal readonly record struct BenchCommandResult(string Message);

    /// <summary>Default cap on the number of models benched in one run — a full pass takes tens of
    /// seconds per model, and an explicit list always wins over the cap.</summary>
    internal const int MaxAutoModels = 5;

    public static async Task<BenchCommandResult> HandleAsync(
        IInferenceProvider client, string[] parts, Action<string>? onProgress, CancellationToken ct)
    {
        // /bench last → redisplay the persisted run, no inference.
        if (parts.Length >= 2 && parts[1].Equals("last", StringComparison.OrdinalIgnoreCase))
        {
            var saved = await BenchStore.LoadAsync();
            return new(saved is null || saved.Results.Count == 0
                ? Strings.BenchNoSaved
                : FormatReport(saved.Results, saved.TimestampUtc));
        }

        // Explicit model list wins; otherwise every installed model, capped.
        List<string> models;
        if (parts.Length >= 2)
        {
            models = [.. parts[1..]];
        }
        else
        {
            var installed = await client.ListInstalledModelsAsync(ct);
            models = [.. installed.Select(m => m.Name).Take(MaxAutoModels)];
        }
        if (models.Count == 0) return new(Strings.BenchNoModels);

        var results = new List<BenchModelResult>();
        for (int i = 0; i < models.Count; i++)
        {
            onProgress?.Invoke(Strings.BenchRunning(models[i], i + 1, models.Count));
            results.Add(await BenchRunner.RunModelAsync(client, models[i], ct));

            // Evict the benched model before loading the next one, so measurements don't degrade
            // as VRAM fills up. The last one stays warm — it is likely about to be used.
            if (i < models.Count - 1 && client.Capabilities.ModelManagement)
                await client.UnloadModelAsync(models[i], ct);
        }

        await BenchStore.SaveAsync(new BenchSavedRun(DateTime.UtcNow, results));
        return new(FormatReport(results, savedAtUtc: null));
    }

    /// <summary>Comparative table + per-role recommendation. Pure — unit-tested directly.</summary>
    internal static string FormatReport(IReadOnlyList<BenchModelResult> results, DateTime? savedAtUtc)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("### ").AppendLine(Strings.BenchTitle);
        if (savedAtUtc is { } ts)
            sb.AppendLine(Strings.BenchSavedAt(ts.ToLocalTime().ToString("g", CultureInfo.CurrentUICulture)));
        sb.AppendLine();
        sb.Append("| ").Append(Strings.BenchColModel)
          .Append(" | ").Append(Strings.BenchColTtft)
          .Append(" | ").Append(Strings.BenchColSpeed)
          .Append(" | ").Append(Strings.BenchColVram)
          .Append(" | ").Append(Strings.BenchColQuality)
          .AppendLine(" |");
        sb.AppendLine("|---|---:|---:|---:|---:|");

        foreach (var r in results)
        {
            if (r.Error is not null)
            {
                sb.Append("| `").Append(r.Model).Append("` | — | — | — | ⚠ ")
                  .Append(Truncate(r.Error, 60)).AppendLine(" |");
                continue;
            }
            sb.Append("| `").Append(r.Model).Append("` | ")
              .Append(r.TtftMs.ToString("0", CultureInfo.InvariantCulture)).Append(" ms | ")
              .Append(r.TokensPerSec.ToString("0.0", CultureInfo.InvariantCulture)).Append(" | ")
              .Append(r.VramBytes >= 0
                  ? (r.VramBytes / 1073741824.0).ToString("0.0", CultureInfo.InvariantCulture) + " GB"
                  : "—")
              .Append(" | ").Append(r.QualityScore).Append('/').Append(r.QualityMax)
              .AppendLine(" |");
        }

        var (agent, utility, fim) = Recommend(results);
        if (agent is not null || utility is not null || fim is not null)
        {
            sb.AppendLine().Append("**").Append(Strings.BenchRecoHeader).AppendLine("**");
            if (agent   is not null) sb.Append("- ").Append(Strings.BenchRecoAgent).Append(" : `").Append(agent).AppendLine("`");
            if (utility is not null) sb.Append("- ").Append(Strings.BenchRecoUtility).Append(" : `").Append(utility).AppendLine("`");
            if (fim     is not null) sb.Append("- ").Append(Strings.BenchRecoFim).Append(" : `").Append(fim).AppendLine("`");
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Per-role picks feeding the Model Router: <b>agent/chat</b> = best quality (throughput breaks
    /// ties) ; <b>utility</b> = fastest among the models within 2 points of a full quality score
    /// (these tasks need speed, not depth), else fastest overall ; <b>FIM</b> = fastest model that
    /// passed the FIM task.
    /// </summary>
    internal static (string? Agent, string? Utility, string? Fim) Recommend(
        IReadOnlyList<BenchModelResult> results)
    {
        var ok = results.Where(r => r.Error is null && r.QualityMax > 0).ToList();
        if (ok.Count == 0) return (null, null, null);

        var agent = ok.OrderByDescending(r => (double)r.QualityScore / r.QualityMax)
                      .ThenByDescending(r => r.TokensPerSec)
                      .First().Model;

        var decent  = ok.Where(r => r.QualityScore >= r.QualityMax - 2).ToList();
        var utility = (decent.Count > 0 ? decent : ok)
                      .OrderByDescending(r => r.TokensPerSec)
                      .First().Model;

        var fim = ok.Where(r => r.FimPassed == true)
                    .OrderByDescending(r => r.TokensPerSec)
                    .FirstOrDefault()?.Model;

        return (agent, utility, fim);
    }

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max] + "…";
}
