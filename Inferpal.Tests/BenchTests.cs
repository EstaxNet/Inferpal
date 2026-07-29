using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Inferpal.Localization;
using Inferpal.Models;
using Inferpal.Services.Bench;
using Inferpal.Services.Commands;
using Xunit;

namespace Inferpal.Tests;

// /bench (ROADMAP 1.3.0 §5): frozen-task scorers, the runner's measurement/scoring loop
// (via FakeInferenceProvider) and the handler's model selection / formatting / persistence.
// All BenchStore-touching tests live in this single class: _fileOverride is a static global,
// and xUnit parallelises across classes, not within one.
public class BenchTests : IDisposable
{
    private readonly string _tempFile;

    public BenchTests()
    {
        _tempFile = Path.Combine(Path.GetTempPath(), $"inferpal-bench-{Guid.NewGuid():N}.json");
        BenchStore._fileOverride = _tempFile;
    }

    public void Dispose()
    {
        BenchStore._fileOverride = null;
        try { File.Delete(_tempFile); } catch { }
    }

    // ── Scorers ────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("for (int i = 0; i < items.Length; i++) sum += items[i];", true)]
    [InlineData("```csharp\nfor (int i = 0; i <= items.Length - 1; i++)\n```", true)]
    [InlineData("for (int i = 0; i <= items.Length; i++)", false)]
    [InlineData("You should check the bounds.", false)]
    public void CSharpFixScorer(string output, bool expected)
    {
        Assert.Equal(expected, BenchTasks.ChatTasks[0].Score(output));
    }

    [Theory]
    [InlineData("BANANA", true)]
    [InlineData("<think>hmm the user wants one word</think>BANANA.", true)]
    [InlineData("banana", false)]
    [InlineData("The word is BANANA", false)]
    public void InstructionScorer(string output, bool expected)
    {
        Assert.Equal(expected, BenchTasks.ChatTasks[1].Score(output));
    }

    [Fact]
    public void SummaryScorer_AcceptsOneSentence_RejectsEchoAndEmpty()
    {
        Assert.True(BenchTasks.ChatTasks[2].Score(
            "A village bakery from 1952 survives as a volunteer cooperative funding the community hall."));
        Assert.False(BenchTasks.ChatTasks[2].Score(""));
        Assert.False(BenchTasks.ChatTasks[2].Score(new string('x', 500)));   // longer than half the source
    }

    [Fact]
    public void ToolCallScorer_RequiresGetWeatherWithParis()
    {
        static ToolCallDto Call(string name, string argsJson) =>
            new(new ToolCallFunction(name, JsonDocument.Parse(argsJson).RootElement.Clone()));

        Assert.True(BenchTasks.ScoreToolCall(new("", [Call("get_weather", """{"city":"Paris"}""")], 0, 0)));
        Assert.True(BenchTasks.ScoreToolCall(new("", [Call("GET_WEATHER", """{"city":"paris, France"}""")], 0, 0)));
        Assert.False(BenchTasks.ScoreToolCall(new("", [Call("get_weather", """{"city":"Lyon"}""")], 0, 0)));
        Assert.False(BenchTasks.ScoreToolCall(new("", [Call("search_web", """{"q":"Paris weather"}""")], 0, 0)));
        Assert.False(BenchTasks.ScoreToolCall(new("It is sunny in Paris.", null, 0, 0)));
    }

    [Theory]
    [InlineData("+ b", true)]
    [InlineData("- b", false)]
    public void FimScorer(string completion, bool expected)
    {
        Assert.Equal(expected, BenchTasks.ScoreFim(completion));
    }

    // ── Runner ─────────────────────────────────────────────────────────────────

    /// <summary>Provider scripted to ace every task — asserts scoring, capability plumbing and
    /// that every chat call targets the benched model.</summary>
    private static FakeInferenceProvider PerfectProvider() => new()
    {
        OnFim   = (_, _) => "+ b",
        Running = [new RunningModelInfo("small:latest", 2_000_000_000, "")],
        OnChatRequest = (model, messages, tools, onToken) =>
        {
            var prompt = messages[^1].Content ?? "";
            if (prompt == BenchTasks.ToolPrompt)
            {
                var args = JsonDocument.Parse("""{"city":"Paris"}""").RootElement.Clone();
                return Task.FromResult(new ChatTurnResult(
                    "", [new ToolCallDto(new ToolCallFunction("get_weather", args))], 20, 10));
            }
            var answer = prompt.Contains("IndexOutOfRangeException") ? "for (int i = 0; i < items.Length; i++) sum += items[i];"
                       : prompt.Contains("BANANA")                   ? "BANANA"
                       : prompt.Contains("Summarize")                ? "A 1952 village bakery now runs as a volunteer cooperative funding the community hall."
                       : "OK";
            onToken?.Invoke(answer);
            return Task.FromResult(new ChatTurnResult(answer, null, 30, 10));
        },
    };

    [Fact]
    public async Task Runner_PerfectModel_ScoresFullQuality_AndMeasures()
    {
        var fake   = PerfectProvider();
        var result = await BenchRunner.RunModelAsync(fake, "small", CancellationToken.None);

        Assert.Null(result.Error);
        Assert.Equal(5, result.QualityMax);        // 3 chat + tool + FIM (Ollama caps)
        Assert.Equal(5, result.QualityScore);
        Assert.True(result.FimPassed);
        Assert.Equal(2_000_000_000, result.VramBytes);   // matched despite the ":latest" tag
        Assert.True(result.TtftMs >= 0);
        Assert.All(fake.ChatModels, m => Assert.Equal("small", m));
        Assert.Equal(5, fake.ChatModels.Count);    // warm-up + 3 chat tasks + tool task
    }

    [Fact]
    public async Task Runner_NoFimCapability_ScoresOutOfFour()
    {
        var fake = PerfectProvider();
        fake.Capabilities = ProviderCapabilities.OpenAiCompatible;

        var result = await BenchRunner.RunModelAsync(fake, "small", CancellationToken.None);

        Assert.Equal(4, result.QualityMax);
        Assert.Null(result.FimPassed);
        Assert.Equal(-1, result.VramBytes);        // no VRAM monitoring either
    }

    [Fact]
    public async Task Runner_BackendError_ReturnsErrorRow_NotThrow()
    {
        var fake = new FakeInferenceProvider
        {
            OnChat = (_, _) => Task.FromException<ChatTurnResult>(new InvalidOperationException("boom")),
        };
        var result = await BenchRunner.RunModelAsync(fake, "m", CancellationToken.None);
        Assert.Equal("boom", result.Error);
    }

    // ── Handler ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Handler_NoModelsAnywhere_SaysSo()
    {
        var result = await BenchCommandHandler.HandleAsync(
            new FakeInferenceProvider(), ["/bench"], null, CancellationToken.None);
        Assert.Equal(Strings.BenchNoModels, result.Message);
    }

    [Fact]
    public async Task Handler_DefaultList_CapsAtFive_AndUnloadsBetweenModels()
    {
        var fake = PerfectProvider();
        fake.Installed = [.. Enumerable.Range(1, 7).Select(i => new InstalledModelInfo($"m{i}", 1))];

        var progress = new List<string>();
        var result   = await BenchCommandHandler.HandleAsync(
            fake, ["/bench"], progress.Add, CancellationToken.None);

        Assert.Equal(5, progress.Count);                       // capped at MaxAutoModels
        Assert.Equal(["m1", "m2", "m3", "m4"], fake.Unloaded); // evicted between models, last stays warm
        Assert.Contains("`m5`", result.Message);
        Assert.Contains(Strings.BenchTitle, result.Message);
    }

    [Fact]
    public async Task Handler_ExplicitModels_WinOverInstalledList()
    {
        var fake = PerfectProvider();
        fake.Installed = [new InstalledModelInfo("ignored", 1)];

        var result = await BenchCommandHandler.HandleAsync(
            fake, ["/bench", "alpha", "beta"], null, CancellationToken.None);

        Assert.Contains("`alpha`", result.Message);
        Assert.Contains("`beta`", result.Message);
        Assert.DoesNotContain("ignored", result.Message);
    }

    [Fact]
    public async Task Handler_Last_ReplaysSavedRun_OrSaysNothingSaved()
    {
        var empty = await BenchCommandHandler.HandleAsync(
            new FakeInferenceProvider(), ["/bench", "last"], null, CancellationToken.None);
        Assert.Equal(Strings.BenchNoSaved, empty.Message);

        var fake = PerfectProvider();
        await BenchCommandHandler.HandleAsync(fake, ["/bench", "small"], null, CancellationToken.None);

        var last = await BenchCommandHandler.HandleAsync(
            new FakeInferenceProvider(), ["/bench", "last"], null, CancellationToken.None);
        Assert.Contains("`small`", last.Message);
        Assert.Contains("5/5", last.Message);
    }

    // ── Recommendation & formatting ────────────────────────────────────────────

    private static BenchModelResult Result(string model, double tokPerSec, int score, int max = 5,
        bool? fim = false, string? error = null) =>
        new(model, 100, tokPerSec, -1, score, max, fim, error);

    [Fact]
    public void Recommend_AgentByQuality_UtilityBySpeedAmongDecent_FimByPass()
    {
        var results = new[]
        {
            Result("big",   tokPerSec: 10, score: 5, fim: false),
            Result("small", tokPerSec: 80, score: 3, fim: true),
            Result("tiny",  tokPerSec: 120, score: 1, fim: true),   // fast but too dumb for utility
        };

        var (agent, utility, fim) = BenchCommandHandler.Recommend(results);

        Assert.Equal("big", agent);
        Assert.Equal("small", utility);   // tiny excluded: quality < max - 2
        Assert.Equal("tiny", fim);        // FIM only needs the FIM task passed
    }

    [Fact]
    public void Recommend_AllFailed_ReturnsNothing()
    {
        var (agent, utility, fim) = BenchCommandHandler.Recommend(
            [Result("m", 0, 0, max: 0, fim: null, error: "down")]);
        Assert.Null(agent); Assert.Null(utility); Assert.Null(fim);
    }

    [Fact]
    public void FormatReport_ErrorRow_ShowsTruncatedError_NotMetrics()
    {
        var md = BenchCommandHandler.FormatReport(
            [Result("dead", 0, 0, max: 0, fim: null, error: new string('e', 100))], null);
        Assert.Contains("⚠", md);
        Assert.Contains("…", md);
        Assert.DoesNotContain("0.0 GB", md);
    }
}
