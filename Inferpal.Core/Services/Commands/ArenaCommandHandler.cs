using System.Diagnostics;
using System.Globalization;
using Inferpal.Config;
using Inferpal.Localization;
using Inferpal.Models;
using Inferpal.Services.Arena;

namespace Inferpal.Services.Commands;

/// <summary>
/// Pure logic of the multi-model arena:
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
            {
                // ⚠ "No arena vote recorded yet" is a statement about the user; a file that did not
                // open is a fact about one file, and only the second has a remedy worth naming.
                var (state, unreadable) = await ArenaStore.ReadAsync();
                return new(unreadable
                    ? Strings.ArenaUnreadable(ArenaStore.FilePath)
                    : FormatStats(state.Battles));
            }
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
            (string Text, double Seconds, bool Cut) answerA, answerB;
            using (GpuScheduler.AcquireChatLease())
            {
                onProgress?.Invoke(Strings.ArenaRunning("A"));
                answerA = await AskAsync(client, modelA, prompt, ct);
                onProgress?.Invoke(Strings.ArenaRunning("B"));
                answerB = await AskAsync(client, modelB, prompt, ct);
            }

            var (state, unreadable) = await ArenaStore.ReadAsync();
            var pendingSaved = await ArenaStore.SaveAsync(state with
            {
                Pending = new ArenaPending(DateTime.UtcNow, prompt, modelA, modelB),
            });

            var sb = new System.Text.StringBuilder();
            sb.Append("### ").AppendLine(Strings.ArenaTitle).AppendLine();
            sb.Append("> ").AppendLine(prompt.Replace("\n", "\n> ")).AppendLine();
            AppendAnswer(sb, "A", answerA);
            AppendAnswer(sb, "B", answerB);
            sb.Append(Strings.ArenaVotePrompt);
            // Without the pending state on disk, the next vote would answer "no pending battle".
            if (!pendingSaved) sb.Append("\n\n").Append(Strings.ArenaPendingNotSaved);
            // ⚠ Two facts about two file states, each repaired elsewhere: the save above is where
            // the unreadable log was set aside, and this battle is the write that replaced it. The
            // standings printed from here on count one battle, and nothing else would say why.
            if (unreadable) sb.Append("\n\n").Append(Strings.ArenaUnreadable(ArenaStore.FilePath));
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
        var (state, unreadable) = await ArenaStore.ReadAsync();
        // ⚠ An unreadable file has no pending battle either, and "no battle awaiting a vote" is the
        // one sentence that must not stand in for "I could not read the file": it describes the
        // user's state instead of the file's, and the vote they just cast is gone without a word.
        if (unreadable)
            return Strings.ArenaVoteNotRead + "\n\n" + Strings.ArenaUnreadable(ArenaStore.FilePath);
        if (state.Pending is not { } pending) return Strings.ArenaNoPending;

        var battles = new List<ArenaBattle>(state.Battles)
        {
            new(pending.TimestampUtc, pending.Prompt, pending.ModelA, pending.ModelB, vote),
        };
        // A vote not written is not a vote: no reveal (the battle stays blind for a retry), no standings
        // that would count it.
        if (!await ArenaStore.SaveAsync(new ArenaSavedState(battles, Pending: null)))
            return Strings.ArenaVoteNotSaved;

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

        // Two installed model names up front ARE the explicit form, whatever follows: without a prompt it is
        // incomplete, and one model named twice is not a pair — neither may fall back to the automatic
        // battle with the model names pasted into its prompt.
        if (parts.Length >= 3
            && FindInstalled(installed, parts[1]) is { } m1
            && FindInstalled(installed, parts[2]) is { } m2)
        {
            return ModelCatalog.SameModelName(m1, m2)
                ? (null, null, string.Empty)
                : (m1, m2, string.Join(" ", parts[3..]));
        }

        var prompt = string.Join(" ", parts[1..]);
        var chat   = ModelRouter.Resolve(config, ModelRole.Chat);
        if (chat.Length == 0) return (null, null, prompt);

        var utility = ModelRouter.Resolve(config, ModelRole.Utility);
        if (!ModelCatalog.SameModelName(chat, utility)) return (chat, utility, prompt);

        var other = installed.FirstOrDefault(m => !ModelCatalog.SameModelName(m.Name, chat))?.Name;
        return (other is null ? null : chat, other, prompt);
    }

    /// <summary>One plain chat call (no tools), timed for the answer header.</summary>
    private static async Task<(string Text, double Seconds, bool Cut)> AskAsync(
        IInferenceProvider client, string model, string prompt, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var result = await client.SendChatAsync(
            model, [new("user", prompt)], EmptyToolRegistry.Instance, null, ct);
        sw.Stop();
        // Inline reasoning comes off per answer: left in the duel, an unclosed tag in the first answer reads as
        // reasoning up to the end of the message — the second answer included.
        return (MarkdownParser.WithoutLeadingReasoning(result.TextContent), sw.Elapsed.TotalSeconds, result.CutAtLimit);
    }

    // ⚠ A vote is a verdict: an answer that stopped at the length limit reads as a curt one, and the voter
    // compares a fragment with a whole without knowing it. The mark goes under the answer it qualifies.
    private static void AppendAnswer(System.Text.StringBuilder sb, string label, (string Text, double Seconds, bool Cut) answer)
    {
        sb.Append("#### ")
          .AppendLine(Strings.ArenaAnswerHeader(label, answer.Seconds.ToString("0.0", CultureInfo.CurrentUICulture)))
          .AppendLine()
          .AppendLine(string.IsNullOrWhiteSpace(answer.Text) ? "*(∅)*" : answer.Text.Trim())
          .AppendLine();
        if (answer.Cut) sb.AppendLine(Strings.ArenaAnswerCut).AppendLine();
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
            // One model is one row: automatic battles record the configured name, explicit ones the
            // installed name, and the two can differ by Ollama's implicit tag.
            void Bump(string model, bool won, bool tied)
            {
                var key = ModelCatalog.ModelKey(model);
                var s = stats.TryGetValue(key, out var cur) ? cur : (0, 0, 0);
                stats[key] = (s.Item1 + 1, s.Item2 + (won ? 1 : 0), s.Item3 + (tied ? 1 : 0));
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
        installed.FirstOrDefault(m => ModelCatalog.SameModelName(m.Name, token))?.Name;
}
