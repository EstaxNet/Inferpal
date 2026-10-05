using System.Text.Json;
using Inferpal.Models;
using Inferpal.Services.CodeActions;
using Inferpal.Services.Execution;
using Inferpal.Services.Tasks;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// ⚠ A background <c>/task</c> that proposes edits is told, after each one, which file it was recorded for. The file was
/// read as "the last proposal made by this tool" — but a file proposed again is updated IN PLACE, so after A, B, A the
/// last entry is B's: the third edit was announced for B, "combined with your earlier proposal for this file". The model
/// then re-proposed B, or wrote a report about the wrong file. The recorder now says which proposal the request left.
/// </summary>
public class TaskProposalNamesTheRightFileTests
{
    private const string Disk = "a\nb\nc\nd\ne\n";

    /// <summary>A mutating tool that asks for the file it is given, with the diff it was scripted to compute.</summary>
    private sealed class ScriptedRegistry(IApprovalService approval, Queue<DiffInfo> diffs) : IToolRegistry
    {
        public IReadOnlyList<ToolDefinition> Definitions => [new("function", new ToolFunction("apply_diff", "", new { }))];

        public DiffInfo? ConsumeDiff() => null;

        public async Task<string> ExecuteAsync(string name, JsonElement args, CancellationToken ct)
        {
            var file = "/p/" + args.GetProperty("path").GetString();
            var diff = diffs.Dequeue();
            var ok   = await approval.RequestApprovalAsync(name, $"{name} {file}", ct, subject: file,
                                                           diff: new DiffInfo(diff.OldText, diff.NewText, file));
            return ok ? "written" : "Cancelled by the user.";
        }
    }

    private static JsonElement Args(string path) => JsonSerializer.SerializeToElement(new { path });

    [Fact]
    public async Task AFileProposedAgain_IsTheOneNamed_NotTheLastOneInTheList()
    {
        var recorder = new ProposalRecorder();
        var registry = new BackgroundTaskToolRegistry(new ScriptedRegistry(recorder, new Queue<DiffInfo>(
            [new DiffInfo(Disk, "a\nB\nc\nd\ne\n", ""), new DiffInfo(Disk, "A\nb\nc\nd\ne\n", ""),
             new DiffInfo(Disk, "a\nb\nc\nD\ne\n", "")])), recorder);

        await registry.ExecuteAsync("apply_diff", Args("a.cs"), default);
        await registry.ExecuteAsync("apply_diff", Args("b.cs"), default);
        var third = await registry.ExecuteAsync("apply_diff", Args("a.cs"), default);

        Assert.Equal(2, recorder.Count);                                     // WITNESS: A updated in place, B kept
        Assert.StartsWith("Recorded as a proposal for /p/a.cs ", third, StringComparison.Ordinal);
        Assert.Contains("combined with your earlier", third, StringComparison.Ordinal);
    }
}
