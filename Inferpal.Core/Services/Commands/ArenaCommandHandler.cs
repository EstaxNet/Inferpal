using System.Diagnostics;
using System.Globalization;
using Inferpal.Config;
using Inferpal.Localization;
using Inferpal.Models;
using Inferpal.Services.Arena;

namespace Inferpal.Services.Commands;

/// <summary>
/// Pure logic of the multi-model arena (ROADMAP 1.3.0 §6):
/// <c>/arena &lt;prompt&gt;</c> sends the same prompt to two models (sequentially — one GPU, the
/// scheduler imposes it anyway), shows both answers blind-labelled A/B, and stores the mapping;
/// <c>/arena a|b|tie</c> records the vote, reveals the models and updates the local standings;
/// <c>/arena stats</c> shows the cumulative table. V1 scope is deliberately narrow (chat without
/// tools, exactly 2 models, no parallel streaming). The pair is either explicit
/// (<c>/arena m1 m2 prompt</c>, both matching installed models) or automatic: chat model vs
/// utility model, falling back to the first other installed model.
/// </summary>
internal static class ArenaCommandHandler
{
    /// <summary>Outcome of an <c>/arena</c> invocation: the markdown to display.</summary>
    internal readonly record struct ArenaCommandResult(string Message);

    public static async Task<ArenaCommandResult> HandleAsync(
        IInferenceProvider client, InferpalConfig config, string[] parts,
        Action<string>? onProgress, CancellationToken ct, Func<bool>? swapOrder = null)
    {
        if (parts.Length < 2) return new(Strings.ArenaUsage);

        switch (parts[1].ToLowerInvariant())
        {
            case "a" or "b" or "tie" when parts.Length == 2:
                return new(await VoteAsync(parts[1].ToLowerInvariant()));
            case "stats" when parts.Length == 2:
                return new(FormatStats((await ArenaStore.LoadAsync()).Battles));
        }

        // ── New battle ──────────────────────────────────────────────────────────
        var (modelA, modelB, prompt) = await ResolvePairAsync(client, config, parts, ct);
        if (modelA is null || modelB is null) return new(Strings.ArenaNeedTwoModels);
        if (prompt.Length == 0) return new(Strings.ArenaUsage);

        // Blind shuffle: the stored mapping is the only place the identities live until the vote.
        if (swapOrder?.Invoke() ?? Random.Shared.Next(2) == 1)
            (modelA, modelB) = (modelB, modelA);

        try
        {
            string textA, textB;
            double secondsA, secondsB;
            using (GpuScheduler.AcquireChatLease())
            {
                onProgress?.Invoke(Strings.ArenaRunning("A"));
                (textA, secondsA) = await AskAsync(client, modelA, prompt, ct);
                onProgress?.Invoke(Strings.ArenaRunning("B"));
                (textB, secondsB) = await AskAsync(client, modelB, prompt, ct);
            }

            var state = await ArenaStore.LoadAsync();
            await ArenaStore.SaveAsync(state with
            {
                Pending = new ArenaPending(DateTime.UtcNow, prompt, modelA, modelB),
            });

            var sb = new System.Text.StringBuilder();
            sb.Append("### ").AppendLine(Strings.ArenaTitle).AppendLine();
            sb.Append("> ").AppendLine(prompt.Replace("\n", "\n> ")).AppendLine();
            AppendAnswer(sb, "A", textA, secondsA);
            AppendAnswer(sb, "B", textB, secondsB);
            sb.Append(Strings.ArenaVotePrompt);
            return new(sb.ToString());
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return new(Strings.ArenaFailed(ex.Message));
        }
    }

    /// <summary>Records the vote against the pending battle, reveals the mapping and shows the
    /// refreshed standings.</summary>
    private static async Task<string> VoteAsync(string vote)
    {
        var state = await ArenaStore.LoadAsync();
        if (state.Pending is not { } pending) return Strings.ArenaNoPending;

        var battles = new List<ArenaBattle>(state.Battles)
        {
            new(pending.TimestampUtc, pending.Prompt, pending.ModelA, pending.ModelB, vote),
        };
        await ArenaStore.SaveAsync(new ArenaSavedState(battles, Pending: null));

        var verdict = vote switch
        {
            "a" => Strings.ArenaVoteRecordedWin(pending.ModelA),
            "b" => Strings.ArenaVoteRecordedWin(pending.ModelB),
            _   => Strings.ArenaVoteRecordedTie,
        };
        return Strings.ArenaReveal(pending.ModelA, pending.ModelB) + " " + verdict
             + "\n\n" + FormatStats(battles);
    }

    /// <summary>
    /// Resolves the two contestants. Explicit form first — <c>/arena m1 m2 prompt…</c> where both
    /// tokens match installed models (tag-tolerant) — otherwise chat model vs utility model, with
    /// the first other installed model as the fallback opponent. Returns nulls when no distinct
    /// pair exists.
    /// </summary>
    private static async Task<(string? A, string? B, string Prompt)> ResolvePairAsync(
        IInferenceProvider client, InferpalConfig config, string[] parts, CancellationToken ct)
    {
        IReadOnlyList<InstalledModelInfo> installed = [];
        try { installed = await client.ListInstalledModelsAsync(ct); }
        catch (Exception ex) { Diagnostics.Swallow("ArenaCommandHandler.ListInstalled", ex); }

        if (parts.Length >= 4
            && FindInstalled(installed, parts[1]) is { } m1
            && FindInstalled(installed, parts[2]) is { } m2
            && !SameModel(m1, m2))
            return (m1, m2, string.Join(" ", parts[3..]));

        var prompt = string.Join(" ", parts[1..]);
        var chat   = ModelRouter.Resolve(config, ModelRole.Chat);
        if (chat.Length == 0) return (null, null, prompt);

        var utility = ModelRouter.Resolve(config, ModelRole.Utility);
        if (!SameModel(chat, utility)) return (chat, utility, prompt);

        var other = installed.FirstOrDefault(m => !SameModel(m.Name, chat))?.Name;
        return (other is null ? null : chat, other, prompt);
    }

    /// <summary>One plain chat call (no tools), timed for the answer header.</summary>
    private static async Task<(string Text, double Seconds)> AskAsync(
        IInferenceProvider client, string model, string prompt, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var result = await client.SendChatAsync(
            model, [new("user", prompt)], EmptyToolRegistry.Instance, null, ct);
        sw.Stop();
        return (result.TextContent, sw.Elapsed.TotalSeconds);
    }

    private static void AppendAnswer(System.Text.StringBuilder sb, string label, string text, double seconds)
    {
        sb.Append("#### ")
          .AppendLine(Strings.ArenaAnswerHeader(label, seconds.ToString("0.0", CultureInfo.CurrentUICulture)))
          .AppendLine()
          .AppendLine(string.IsNullOrWhiteSpace(text) ? "*(∅)*" : text.Trim())
          .AppendLine();
    }

    /// <summary>Cumulative standings table. Pure — unit-tested directly.</summary>
    internal static string FormatStats(IReadOnlyList<ArenaBattle> battles)
    {
        if (battles.Count == 0) return Strings.ArenaNoStats;

        var stats = new Dictionary<string, (int Battles, int Wins, int Ties)>(StringComparer.OrdinalIgnoreCase);
        foreach (var b in battles)
        {
            Bump(b.ModelA, b.Vote == "a", b.Vote == "tie");
            Bump(b.ModelB, b.Vote == "b", b.Vote == "tie");
            void Bump(string model, bool won, bool tied)
            {
                var s = stats.TryGetValue(model, out var cur) ? cur : (0, 0, 0);
                stats[model] = (s.Item1 + 1, s.Item2 + (won ? 1 : 0), s.Item3 + (tied ? 1 : 0));
            }
        }

        var sb = new System.Text.StringBuilder();
        sb.Append("**").Append(Strings.ArenaStatsTitle).AppendLine("**").AppendLine();
        sb.Append("| ").Append(Strings.ArenaColModel)
          .Append(" | ").Append(Strings.ArenaColBattles)
          .Append(" | ").Append(Strings.ArenaColWins)
          .Append(" | ").Append(Strings.ArenaColTies)
          .Append(" | ").Append(Strings.ArenaColWinRate)
          .AppendLine(" |");
        sb.AppendLine("|---|---:|---:|---:|---:|");
        foreach (var (model, s) in stats.OrderByDescending(kv => kv.Value.Wins)
                                        .ThenByDescending(kv => kv.Value.Battles))
        {
            sb.Append("| `").Append(model).Append("` | ")
              .Append(s.Battles).Append(" | ")
              .Append(s.Wins).Append(" | ")
              .Append(s.Ties).Append(" | ")
              .Append((100.0 * s.Wins / s.Battles).ToString("0", CultureInfo.InvariantCulture))
              .AppendLine(" % |");
        }
        return sb.ToString().TrimEnd();
    }

    private static string? FindInstalled(IReadOnlyList<InstalledModelInfo> installed, string token) =>
        installed.FirstOrDefault(m => SameModel(m.Name, token))?.Name;

    /// <summary>Tag-tolerant model name comparison (<c>llama3.1</c> vs <c>llama3.1:latest</c>),
    /// same rule as the bench runner.</summary>
    private static bool SameModel(string x, string y) =>
        string.Equals(x, y, StringComparison.OrdinalIgnoreCase)
        || string.Equals(x.Split(':')[0], y.Split(':')[0], StringComparison.OrdinalIgnoreCase);
}
