using System.Diagnostics;
using Inferpal.Models;

namespace Inferpal.Services.Bench;

/// <summary>Measured outcome of one model's <c>/bench</c> pass. Serialized as-is by
/// <see cref="BenchStore"/>, so keep it primitives-only.</summary>
/// <param name="Model">Model name as benched.</param>
/// <param name="TtftMs">Average warm time-to-first-token over the chat tasks, in ms.</param>
/// <param name="TokensPerSec">Aggregate generation throughput over the chat tasks.</param>
/// <param name="VramBytes">VRAM occupied right after the run, or <c>-1</c> when the backend can't report it.</param>
/// <param name="QualityScore">Micro-eval tasks passed.</param>
/// <param name="QualityMax">Micro-eval tasks attempted (5 with FIM support, 4 without).</param>
/// <param name="FimPassed"><c>null</c> when the backend has no FIM.</param>
/// <param name="Error">Non-null when the run aborted (network/timeout) — other fields are then meaningless.</param>
internal sealed record BenchModelResult(
    string  Model,
    double  TtftMs,
    double  TokensPerSec,
    long    VramBytes,
    int     QualityScore,
    int     QualityMax,
    bool?   FimPassed,
    string? Error);

/// <summary>
/// Runs the frozen <see cref="BenchTasks"/> suite against one model and measures it with a client-side
/// stopwatch — the only approach that works uniformly across Ollama, LM Studio and generic
/// OpenAI-compatible backends (none of which agree on server-side timing fields).
/// </summary>
/// <remarks>
/// GPU discipline: the chat tasks run under a single <see cref="GpuScheduler.AcquireChatLease"/> (the
/// ghost-text FIM yields, as during a normal chat turn), but the lease is released <em>before</em> the
/// FIM sub-test — <c>StreamFimAsync</c> silently returns when <c>ChatBusySignal</c> is up, so keeping
/// the lease would score every model's FIM as an empty fail.
/// </remarks>
internal static class BenchRunner
{
    public static async Task<BenchModelResult> RunModelAsync(
        IInferenceProvider client, string model, CancellationToken ct)
    {
        var ttfts        = new List<double>();
        double genTokens = 0, genSeconds = 0;
        int passed = 0, attempted = 0;

        try
        {
            using (GpuScheduler.AcquireChatLease())
            {
                // Warm-up (not scored, not timed): absorbs the VRAM load so TTFT below is warm latency.
                await client.SendChatAsync(model, [new("user", "Reply with the word OK.")],
                    EmptyToolRegistry.Instance, null, ct, TaskComplexity.Quick);

                foreach (var task in BenchTasks.ChatTasks)
                {
                    var (result, ttftMs, tokens, seconds) = await TimedChatAsync(
                        client, model, task.Prompt, EmptyToolRegistry.Instance, ct);
                    ttfts.Add(ttftMs);
                    genTokens  += tokens;
                    genSeconds += seconds;
                    attempted++;
                    if (task.Score(result.TextContent)) passed++;
                }

                // Tool-call task — same timing, but the fake tool is exposed and the score reads the calls.
                var (toolResult, toolTtft, toolTokens, toolSeconds) = await TimedChatAsync(
                    client, model, BenchTasks.ToolPrompt, BenchTasks.ToolRegistry.Instance, ct);
                ttfts.Add(toolTtft);
                genTokens  += toolTokens;
                genSeconds += toolSeconds;
                attempted++;
                if (BenchTasks.ScoreToolCall(toolResult)) passed++;
            }

            // FIM sub-test, outside the chat lease (see remarks).
            bool? fimPassed = null;
            if (client.Capabilities.Fim)
            {
                var sb = new System.Text.StringBuilder();
                await client.StreamFimAsync(BenchTasks.FimPrefix, BenchTasks.FimSuffix,
                    maxTokens: 16, temperature: 0, token => sb.Append(token), ct, model);
                fimPassed = BenchTasks.ScoreFim(sb.ToString());
                attempted++;
                if (fimPassed == true) passed++;
            }

            // VRAM pressure right after the run (best-effort; [] on backends without /api/ps).
            long vram = -1;
            if (client.Capabilities.VramMonitoring)
            {
                foreach (var running in await client.GetRunningModelsAsync(ct))
                    if (SameModel(running.Name, model)) { vram = running.SizeVram; break; }
            }

            return new BenchModelResult(
                Model:        model,
                TtftMs:       ttfts.Count > 0 ? ttfts.Average() : 0,
                TokensPerSec: genSeconds > 0 ? genTokens / genSeconds : 0,
                VramBytes:    vram,
                QualityScore: passed,
                QualityMax:   attempted,
                FimPassed:    fimPassed,
                Error:        null);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return new BenchModelResult(model, 0, 0, -1, 0, 0, null, ex.Message);
        }
    }

    /// <summary>One measured chat call: TTFT = first streamed token (thinking counts — it is the
    /// user-perceived latency), throughput counted from that first token to completion.</summary>
    private static async Task<(ChatTurnResult Result, double TtftMs, double Tokens, double Seconds)>
        TimedChatAsync(IInferenceProvider client, string model, string prompt, IToolRegistry tools,
                       CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        long firstTokenTicks = -1;
        int  chunkCount      = 0;

        void OnFirst() { if (firstTokenTicks < 0) firstTokenTicks = sw.ElapsedTicks; }

        var result = await client.SendChatAsync(
            model, [new("user", prompt)], tools,
            onToken:    t => { OnFirst(); chunkCount++; },
            ct:         ct,
            complexity: TaskComplexity.Quick,
            onThinking: _ => OnFirst());
        sw.Stop();

        var ttftMs = firstTokenTicks >= 0
            ? firstTokenTicks * 1000.0 / Stopwatch.Frequency
            : sw.Elapsed.TotalMilliseconds;   // no streaming (tool-call-only reply) → whole call

        // Prefer the backend's generated-token count; fall back to streamed chunks (≈ tokens).
        var tokens  = Math.Max(result.TokensUsed - result.PromptTokens, chunkCount);
        var seconds = Math.Max((sw.Elapsed.TotalMilliseconds - ttftMs) / 1000.0, 0);
        return (result, ttftMs, tokens, seconds);
    }

    /// <summary>Matches <c>/api/ps</c> names against the benched name, tolerant of a missing tag
    /// (<c>llama3.1</c> vs <c>llama3.1:latest</c>).</summary>
    private static bool SameModel(string running, string benched) =>
        string.Equals(running, benched, StringComparison.OrdinalIgnoreCase)
        || string.Equals(running.Split(':')[0], benched.Split(':')[0], StringComparison.OrdinalIgnoreCase);
}
