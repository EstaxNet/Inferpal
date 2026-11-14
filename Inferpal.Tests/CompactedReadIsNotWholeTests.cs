using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Inferpal.Models;
using Inferpal.Services.Agent;
using Inferpal.Services.Execution;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A read_file result the run's compaction cut or elided is no longer "read whole": the model has its head and end, or
/// nothing, and write_file must not let it replace the whole file from that — unless a later call of the run wrote the
/// file, whose content is then in that call's arguments, never elided.
/// </summary>
public sealed class CompactedReadIsNotWholeTests
{
    private sealed class Forgetting : IToolRegistry
    {
        public List<string> Forgotten { get; } = [];
        public IReadOnlyList<ToolDefinition> Definitions => [];
        public Task<string> ExecuteAsync(string name, JsonElement args, CancellationToken ct) => Task.FromResult("");
        public DiffInfo? ConsumeDiff() => null;
        public void ForgetWholeRead(string path) => Forgotten.Add(path);
    }

    private static ChatMessageDto Call(string tool, string path) =>
        new("assistant", "", [new ToolCallDto(new ToolCallFunction(tool, JsonSerializer.SerializeToElement(new { path })))]);

    private static List<ChatMessageDto> Head() => [new("system", new string('s', 5027)), new("user", new string('q', 1308))];

    [Fact]
    public void AReadTheCompactionCut_OrElided_IsNoLongerReadWhole()
    {
        var run = Head();
        run.Add(Call("read_file", "src/old.py"));     // elided: older
        run.Add(new("tool", new string('a', 8000)));
        run.Add(Call("read_file", "src/big.py"));     // cut: the last batch
        run.Add(new("tool", new string('b', 8000)));
        var tools = new Forgetting();

        var shrunk = AgentOrchestrator.CompactRunContext(run, anchorCount: 2, budget: 8192, toolTokens: 4453);
        AgentOrchestrator.ForgetShrunkReads(run, shrunk, tools);

        Assert.Contains("truncated", run[^1].Content);                          // witness: the last read was cut
        Assert.Equal(["src/old.py", "src/big.py"], tools.Forgotten);
    }

    /// <summary>Reference arm: a file the run wrote after reading it is not forgotten — the model has what it wrote.</summary>
    [Fact]
    public void AFileWrittenAfterItsRead_IsNotForgotten()
    {
        var run = Head();
        run.Add(Call("read_file", "src/a.py"));
        run.Add(new("tool", new string('a', 8000)));
        run.Add(Call("write_file", "src/a.py"));
        run.Add(new("tool", "written"));
        run.Add(Call("read_file", "src/b.py"));
        run.Add(new("tool", new string('b', 8000)));
        var tools = new Forgetting();

        AgentOrchestrator.ForgetShrunkReads(run, AgentOrchestrator.CompactRunContext(run, 2, 8192, 4453), tools);

        Assert.DoesNotContain("src/a.py", tools.Forgotten);
        Assert.Contains("src/b.py", tools.Forgotten);
    }

    /// <summary>A later call that merely names the file (an apply_diff, a search) does not give the model the file whole: the
    /// cut read is forgotten. A later read of it that came back untouched does.</summary>
    [Fact]
    public void OnlyAWriteOrAnUntouchedRereadKeepsTheFileWhole()
    {
        var run = Head();
        run.Add(Call("read_file", "src/a.py"));
        run.Add(new("tool", new string('a', 8000)));
        run.Add(Call("apply_diff", "src/a.py"));
        run.Add(new("tool", "applied"));
        run.Add(Call("read_file", "src/b.py"));
        run.Add(new("tool", new string('b', 8000)));
        run.Add(Call("read_file", "src/b.py"));
        run.Add(new("tool", "short and whole"));
        var tools = new Forgetting();

        // Indices of the two large results, as the compaction would report them.
        AgentOrchestrator.ForgetShrunkReads(run, [3, 7], tools);

        Assert.Contains("src/a.py", tools.Forgotten);        // apply_diff named it, the model still lacks it whole
        Assert.DoesNotContain("src/b.py", tools.Forgotten);  // re-read, untouched
    }

    private sealed class Summarizer : IChatTurnClient
    {
        public Task<ChatTurnResult> SendChatAsync(string model, List<ChatMessageDto> messages, IToolRegistry tools,
            Action<string>? onToken, CancellationToken ct, TaskComplexity complexity = TaskComplexity.Normal,
            string? toolChoice = null, Action<string>? onThinking = null) =>
            Task.FromResult(new ChatTurnResult("SUMMARY", null, 0, 0));
    }

    /// <summary>The run's summary paraphrases what it replaces: the reads in that range are no longer whole either.</summary>
    [Fact]
    public async Task AReadTheRunsSummaryReplaced_IsNoLongerReadWhole()
    {
        var run = new List<ChatMessageDto> { new("system", new string('s', 4000)), new("user", "go") };
        for (var i = 0; i < 6; i++)
        {
            run.Add(Call("read_file", $"src/f{i}.py"));
            run.Add(new("tool", new string('x', 8000)));
        }
        var tools = new Forgetting();
        var orch = new AgentOrchestrator(new Summarizer(),
            new Inferpal.Config.InferpalConfig { ContextWindowSize = 8192, CompactionEnabled = true });

        await orch.CompactRunContextAsync(run, anchorCount: 2, model: "m", alreadySummarized: false, onStep: _ => { },
                                          ct: CancellationToken.None, tools: tools);

        Assert.Contains(run, m => m.Content is { } c && c.Contains("SUMMARY"));   // witness: the summary ran
        Assert.Contains("src/f0.py", tools.Forgotten);
    }

    /// <summary>End to end against the run's own record: forgotten, the file is no longer read for the write guard.</summary>
    [Fact]
    public void TheRunsRecord_SaysNotRead_OnceForgotten()
    {
        var history = new FileHistoryService();
        history.BeginRun();
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "inferpal-forget", "big.py");
        history.NoteRead(path);
        Assert.True(history.WasRead(path));

        history.ForgetRead(path);

        Assert.False(history.WasRead(path));
        history.EndRun();
    }
}
