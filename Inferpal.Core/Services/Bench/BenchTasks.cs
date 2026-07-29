using System.Text.Json;
using Inferpal.Models;

namespace Inferpal.Services.Bench;

/// <summary>
/// The fixed micro-evaluation suite of <c>/bench</c>: tiny frozen tasks scored by programmatic
/// assertions — deliberately no LLM judge, so scores are reproducible and 100 % local. Each scorer
/// is lenient about formatting (think tags, code fences, stray punctuation) but strict about the
/// substance, because small models fail on substance, not on markdown.
/// </summary>
internal static class BenchTasks
{
    /// <summary>A frozen chat task and its pass/fail assertion on the model's reply.</summary>
    internal sealed record ChatTask(string Id, string Prompt, Func<string, bool> Score);

    private const string SummarySource =
        "The village bakery opened in 1952 and was run by the same family for three generations. " +
        "When the last baker retired in 2019, the residents formed a cooperative to keep it alive. " +
        "Today twelve volunteers bake bread twice a week, the profits fund the community hall, " +
        "and the old wood-fired oven is now listed as a protected historical monument.";

    /// <summary>The three plain chat tasks (the tool-call and FIM tasks have dedicated entry points).</summary>
    public static readonly IReadOnlyList<ChatTask> ChatTasks =
    [
        new("csharp-fix",
            "This C# loop sums an array but throws IndexOutOfRangeException:\n\n" +
            "for (int i = 0; i <= items.Length; i++) sum += items[i];\n\n" +
            "Reply with the corrected for statement only.",
            output =>
            {
                var flat = Normalize(output).Replace(" ", "").ToLowerInvariant();
                return flat.Contains("i<items.length") || flat.Contains("i<=items.length-1");
            }),

        new("instruction",
            "Reply with exactly the single word BANANA in uppercase. No punctuation, no explanation.",
            output => Normalize(output).Trim('.', '!', '"', '\'', ' ') == "BANANA"),

        new("summary",
            $"Summarize the following text in one sentence of at most 20 words:\n\n{SummarySource}",
            output =>
            {
                var text  = Normalize(output);
                var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
                return words is >= 3 and <= 35 && text.Length < SummarySource.Length / 2;
            }),
    ];

    /// <summary>Prompt of the tool-call task (sent with <see cref="ToolRegistry"/> exposed).</summary>
    public const string ToolPrompt = "What is the weather in Paris right now? Use the get_weather tool.";

    /// <summary>
    /// Scores the tool-call task on the raw turn result: the model must have emitted a
    /// <c>get_weather</c> call whose arguments mention Paris. Inline (plain-text) tool calls
    /// recovered by the client's fallback parser count as a pass too — the wire shape matters
    /// less than the model knowing it should call the tool.
    /// </summary>
    public static bool ScoreToolCall(ChatTurnResult result)
    {
        if (result.ToolCalls is not { Count: > 0 } calls) return false;
        foreach (var call in calls)
        {
            if (!string.Equals(call.Function.Name, "get_weather", StringComparison.OrdinalIgnoreCase))
                continue;
            try
            {
                if (call.Function.Arguments.GetRawText().Contains("paris", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            catch (InvalidOperationException) { }   // default(JsonElement) — malformed call, no pass
        }
        return false;
    }

    /// <summary>FIM task: completing <c>return a _ b;</c> in an Add method must produce a <c>+</c>.</summary>
    public const string FimPrefix = "int Add(int a, int b)\n{\n    return a ";
    /// <inheritdoc cref="FimPrefix"/>
    public const string FimSuffix = ";\n}";

    /// <inheritdoc cref="FimPrefix"/>
    public static bool ScoreFim(string completion) => completion.Contains('+');

    /// <summary>Registry exposing the single fake tool of the tool-call task (never executed —
    /// <c>/bench</c> only checks whether the model <em>requests</em> the call).</summary>
    internal sealed class ToolRegistry : IToolRegistry
    {
        public static readonly ToolRegistry Instance = new();

        public IReadOnlyList<ToolDefinition> Definitions { get; } =
        [
            new ToolDefinition("function", new ToolFunction(
                "get_weather",
                "Gets the current weather for a city.",
                new
                {
                    type       = "object",
                    properties = new { city = new { type = "string", description = "City name" } },
                    required   = new[] { "city" },
                })),
        ];

        public Task<string> ExecuteAsync(string name, JsonElement args, CancellationToken ct)
            => Task.FromResult("(bench tool — not executed)");

        public DiffInfo? ConsumeDiff() => null;
    }

    /// <summary>Strips think tags and code fences so scorers judge the substance only.</summary>
    private static string Normalize(string output)
    {
        var text = MarkdownParser.StripThinkTags(output ?? string.Empty).Trim();
        if (text.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNewline = text.IndexOf('\n');
            var lastFence    = text.LastIndexOf("```", StringComparison.Ordinal);
            if (firstNewline >= 0 && lastFence > firstNewline)
                text = text[(firstNewline + 1)..lastFence].Trim();
        }
        return text.Replace('\n', ' ').Replace('\r', ' ').Trim();
    }
}
