using System.Text.Json;
using Inferpal.Models;
using Inferpal.Services;
using Inferpal.Services.Commands;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// Routing and rendering of <c>/task</c>, plus the tool surface a background run is allowed.
/// The queue is driven with a runner that never returns, so tasks sit in known states.
/// </summary>
public class TaskCommandTests
{
    private static BackgroundTaskQueue Queue() =>
        new((_, _, ct) => Task.Delay(System.Threading.Timeout.Infinite, ct)
                              .ContinueWith(_ => BackgroundTaskQueue.TaskRunOutcome.Of(""), ct));

    private static string Run(BackgroundTaskQueue queue, params string[] parts) =>
        TaskCommandHandler.Handle(queue, parts).Message;

    /// <summary>
    /// Waits for a task to settle in a terminal state.
    /// </summary>
    /// <remarks>
    /// <c>/task stop</c> finishes a <b>queued</b> task synchronously but only <i>signals</i> a
    /// <b>running</b> one — by design, the runner unwinds on its own token. Whether the single
    /// submitted task has been picked up by the worker yet is a race, so asserting straight after
    /// a stop is asserting on a coin flip: that is what made <c>Clear_ForgetsFinishedTasks</c>
    /// fail roughly one full-suite run in four.
    /// </remarks>
    private static void WaitSettled(BackgroundTaskQueue queue, string id)
    {
        var settled = SpinWait.SpinUntil(
            () => queue.Get(id) is { State: BackgroundTaskState.Succeeded
                                         or BackgroundTaskState.Failed
                                         or BackgroundTaskState.Cancelled },
            TimeSpan.FromSeconds(30));

        Assert.True(settled, $"task {id} never reached a terminal state");
    }

    // ── Routing ─────────────────────────────────────────────────────────────────

    [Fact]
    public void SlashRouter_SendsTaskToItsHandler()
    {
        var action = SlashCommandRouter.Route("/task audit the RAG layer", []);

        var delegated = Assert.IsType<SlashDelegatedAction>(action);
        Assert.Equal(SlashCommandId.Task, delegated.Id);
        Assert.Equal("audit", delegated.Parts[1]);
    }

    [Fact]
    public void Task_IsListedInTheSlashHints()
    {
        Assert.Contains(SlashCommandRouter.BuiltInCommands, c => c.Cmd == "/task");
    }

    [Fact]
    public void BareTask_ListsInsteadOfSubmitting()
    {
        using var queue = Queue();

        var message = Run(queue, "/task");

        Assert.Equal(0, queue.ActiveCount);           // nothing was submitted
        Assert.Contains("`/task", message);           // the empty-list hint
    }

    [Fact]
    public void Objective_IsSubmitted_AndReportedWithItsId()
    {
        using var queue = Queue();

        var message = Run(queue, "/task", "audit", "the", "RAG", "layer");

        Assert.Equal(1, queue.ActiveCount);
        Assert.Equal("audit the RAG layer", queue.Get("t1")!.Objective);
        Assert.Contains("t1", message);
        Assert.Contains("audit the RAG layer", message);
    }

    [Fact]
    public void AKnownId_ShowsThatTaskInsteadOfSubmittingIt()
    {
        using var queue = Queue();
        Run(queue, "/task", "first");

        var message = Run(queue, "/task", "t1");

        Assert.Equal(1, queue.ActiveCount);           // still one task: no second submission
        Assert.Contains("first", message);
    }

    [Fact]
    public void AnUnknownIdLikeArgument_IsTreatedAsAnObjective()
    {
        // "t9 is broken, investigate" must not be swallowed as a failed lookup.
        using var queue = Queue();

        var message = Run(queue, "/task", "t9");

        Assert.Equal(1, queue.ActiveCount);
        Assert.Equal("t9", queue.Get("t1")!.Objective);
        Assert.Contains("t1", message);
    }

    [Fact]
    public void Stop_CancelsTheTask_AndUnknownIdsSaySo()
    {
        using var queue = Queue();
        Run(queue, "/task", "long", "one");

        var stopped = Run(queue, "/task", "stop", "t1");
        Assert.Contains("t1", stopped);

        var unknown = Run(queue, "/task", "stop", "t42");
        Assert.Contains("t42", unknown);
    }

    [Fact]
    public void Stop_WithoutAnId_ShowsTheUsage()
    {
        using var queue = Queue();

        Assert.Contains("/task stop", Run(queue, "/task", "stop"));
    }

    [Fact]
    public void Clear_ForgetsFinishedTasks()
    {
        using var queue = Queue();
        Run(queue, "/task", "one");
        Run(queue, "/task", "stop", "t1");
        WaitSettled(queue, "t1");            // cancelling a *running* task is asynchronous

        var message = Run(queue, "/task", "clear");

        Assert.Contains("1", message);
        Assert.Null(queue.Get("t1"));
    }

    // ── Rendering ───────────────────────────────────────────────────────────────

    [Fact]
    public void List_RendersOneRowPerTask()
    {
        using var queue = Queue();
        Run(queue, "/task", "alpha");
        Run(queue, "/task", "beta");

        var message = Run(queue, "/task", "list");

        Assert.Contains("`t1`", message);
        Assert.Contains("`t2`", message);
        Assert.Contains("alpha", message);
        Assert.Contains("beta", message);
    }

    [Fact]
    public void APipeInTheObjective_CannotBreakTheTableRow()
    {
        using var queue = Queue();
        Run(queue, "/task", "compare", "a", "|", "b");

        var row = Run(queue, "/task", "list")
            .Split('\n').First(l => l.Contains("`t1`"));

        // Three columns → exactly four unescaped pipes; the objective's own pipe must not count.
        var separators = System.Text.RegularExpressions.Regex.Matches(row, @"(?<!\\)\|").Count;
        Assert.Equal(4, separators);
        Assert.Contains("\\|", row);
    }

    [Fact]
    public void Report_ShowsObjectiveStateAndSteps()
    {
        var release = new TaskCompletionSource<string>();
        using var queue = new BackgroundTaskQueue((_, onStep, _) =>
        {
            onStep("read RagDatabase.cs");
            return release.Task.ContinueWith(t => BackgroundTaskQueue.TaskRunOutcome.Of(t.Result));
        });

        queue.Submit("audit the RAG layer");
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (queue.Get("t1")!.Steps.Count == 0 && DateTime.UtcNow < deadline) Thread.Sleep(10);

        var report = TaskCommandHandler.RenderReport(queue.Get("t1")!);

        Assert.Contains("audit the RAG layer", report);
        Assert.Contains("read RagDatabase.cs", report);
        release.SetResult("done");
    }

    [Fact]
    public void FormatDuration_ReadsNaturally_BelowAndAboveAMinute()
    {
        // The decimal separator follows the UI culture (it is user-facing text), so the
        // expectation is pinned to a known culture rather than to the dev machine's.
        var previous = System.Globalization.CultureInfo.CurrentCulture;
        System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
        try
        {
            Assert.Equal("4.2s",  TaskCommandHandler.FormatDuration(TimeSpan.FromSeconds(4.23)));
            Assert.Equal("2m 5s", TaskCommandHandler.FormatDuration(TimeSpan.FromSeconds(125)));
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = previous; }
    }

    // ── Tool surface ────────────────────────────────────────────────────────────

    private sealed class SpyRegistry : IToolRegistry
    {
        public readonly List<string> Executed = [];
        public IReadOnlyList<ToolDefinition> Definitions { get; init; } = [];
        public DiffInfo? ConsumeDiff() => null;
        public Task<string> ExecuteAsync(string name, JsonElement args, CancellationToken ct)
        {
            Executed.Add(name);
            return Task.FromResult("inner ran");
        }
    }

    [Theory]
    [InlineData("read_file",       true)]
    [InlineData("search_codebase", true)]
    [InlineData("analyze_code",    true)]
    [InlineData("write_file",      false)]
    [InlineData("apply_diff",      false)]
    [InlineData("delete_file",     false)]
    [InlineData("run_command",     false)]
    [InlineData("run_tests",       false)]
    public void BackgroundRegistry_AllowsOnlyReadOnlyTools(string tool, bool allowed)
    {
        Assert.Equal(allowed, BackgroundTaskToolRegistry.IsAllowed(tool));
    }

    [Theory]
    [InlineData("fetch_url")]
    [InlineData("web_search")]
    public void BackgroundRegistry_IsStricterThanPlanMode_AboutApprovalGatedTools(string tool)
    {
        // Plan mode runs in front of the user and can afford these prompts; a background task
        // must never pop a modal while the user is typing.
        Assert.True(PlanModeToolRegistry.IsAllowed(tool));
        Assert.False(BackgroundTaskToolRegistry.IsAllowed(tool));
    }

    [Fact]
    public async Task BackgroundRegistry_RefusesAWriteWithoutTouchingTheInnerRegistry()
    {
        var inner    = new SpyRegistry();
        var registry = new BackgroundTaskToolRegistry(inner);

        var result = await registry.ExecuteAsync("write_file", default, CancellationToken.None);

        Assert.Empty(inner.Executed);
        Assert.Contains("not available", result);
    }

    [Fact]
    public async Task BackgroundRegistry_ForwardsAllowedTools()
    {
        var inner    = new SpyRegistry();
        var registry = new BackgroundTaskToolRegistry(inner);

        var result = await registry.ExecuteAsync("read_file", default, CancellationToken.None);

        Assert.Equal(["read_file"], inner.Executed);
        Assert.Equal("inner ran", result);
    }

    [Fact]
    public void BackgroundRegistry_HidesForbiddenToolsFromTheModel()
    {
        var inner = new SpyRegistry
        {
            Definitions =
            [
                new ToolDefinition("function", new ToolFunction("read_file",  "", new { })),
                new ToolDefinition("function", new ToolFunction("write_file", "", new { })),
            ],
        };

        var names = new BackgroundTaskToolRegistry(inner).Definitions.Select(d => d.Function.Name);

        Assert.Equal(["read_file"], names);
    }
}
