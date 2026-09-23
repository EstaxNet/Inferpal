using System.Text.Json;
using Inferpal.Models;
using Inferpal.Services.CodeActions;
using Inferpal.Services.Tasks;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// ⚠ A background task in proposal mode applies nothing, so every edit it makes is computed from the
/// file ON DISK — not from its own earlier proposal. Two <c>apply_diff</c> calls on two regions of one
/// file are two intentions, and "the last word wins" kept only the second: the model was told both were
/// recorded, its report described both, and the user was shown — and could apply — one.
/// </summary>
public class TaskProposalMergeTests
{
    private const string Disk = "a\nb\nc\nd\ne\n";

    private static JsonElement Args(object o) =>
        JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(o));

    /// <summary>A mutating tool that asks with the diff it was scripted to compute, and writes nothing.</summary>
    private sealed class ScriptedRegistry(IApprovalService approval, Queue<DiffInfo> diffs) : IToolRegistry
    {
        public IReadOnlyList<ToolDefinition> Definitions =>
            [new("function", new ToolFunction("apply_diff", "", new { })),
             new("function", new ToolFunction("write_file", "", new { }))];

        public DiffInfo? ConsumeDiff() => null;

        public async Task<string> ExecuteAsync(string name, JsonElement args, CancellationToken ct)
        {
            var ok = await approval.RequestApprovalAsync(name, $"{name} a.cs", ct, subject: "/p/a.cs", diff: diffs.Dequeue());
            return ok ? "written" : "Cancelled by the user.";
        }
    }

    private static async Task<(ProposalRecorder Recorder, string Second)> TwoCalls(
        string firstTool, string firstNew, string secondTool, string secondNew, string? secondOld = null)
    {
        var recorder = new ProposalRecorder();
        var registry = new BackgroundTaskToolRegistry(
            new ScriptedRegistry(recorder, new Queue<DiffInfo>(
                [new DiffInfo(Disk, firstNew, "/p/a.cs"), new DiffInfo(secondOld ?? Disk, secondNew, "/p/a.cs")])),
            recorder);

        await registry.ExecuteAsync(firstTool, Args(new { path = "a.cs" }), default);
        var second = await registry.ExecuteAsync(secondTool, Args(new { path = "a.cs" }), default);
        return (recorder, second);
    }

    [Fact]
    public async Task TwoEditsToDifferentLines_OfOneFile_AreBothInTheProposal()
    {
        var (recorder, second) = await TwoCalls("apply_diff", "a\nB\nc\nd\ne\n", "apply_diff", "a\nb\nc\nD\ne\n");

        var proposal = Assert.Single(recorder.Proposals);
        Assert.Equal(Disk, proposal.Diff!.OldText);              // still applicable to the file as it is
        Assert.Equal("a\nB\nc\nD\ne\n", proposal.Diff.NewText);  // both intentions
        Assert.Contains("Recorded as a proposal", second);
        Assert.Contains("combined with your earlier", second);
    }

    [Fact]
    public async Task AnInsertionAtTheEnd_IsCombinedWithAnEditAboveIt()
    {
        var (recorder, _) = await TwoCalls("apply_diff", "a\nB\nc\nd\ne\n", "apply_diff", "a\nb\nc\nd\ne\nf\n");

        Assert.Equal("a\nB\nc\nd\ne\nf\n", Assert.Single(recorder.Proposals).Diff!.NewText);
    }

    [Fact]
    public async Task AnEditToTheSameLines_IsACorrection_AndTheModelIsToldItReplaces()
    {
        // Overlapping regions cannot be told apart from a second attempt at the same change: the last
        // word wins, as before — but the model now reads that its earlier proposal is gone.
        var (recorder, second) = await TwoCalls("apply_diff", "a\nB\nc\nd\ne\n", "apply_diff", "a\nBB\nc\nd\ne\n");

        Assert.Equal("a\nBB\nc\nd\ne\n", Assert.Single(recorder.Proposals).Diff!.NewText);
        Assert.Contains("REPLACES your earlier proposal", second);
    }

    [Fact]
    public async Task AWholeFileWrite_AfterAnEdit_Replaces_AndSaysSo()
    {
        // write_file states the complete content: it is never merged into.
        var (recorder, second) = await TwoCalls("apply_diff", "a\nB\nc\nd\ne\n", "write_file", "a\nb\nc\nD\ne\n");

        Assert.Equal("a\nb\nc\nD\ne\n", Assert.Single(recorder.Proposals).Diff!.NewText);
        Assert.Contains("REPLACES your earlier proposal", second);
    }

    [Fact]
    public async Task TwoEditsFromDifferentVersionsOfTheFile_AreNotMerged_AndSaySo()
    {
        // The file changed on disk between the two calls: the earlier diff's base is gone.
        var (recorder, second) = await TwoCalls(
            "apply_diff", "a\nB\nc\nd\ne\n", "apply_diff", "a\nb\nc\nD\ne\nz\n", secondOld: "a\nb\nc\nd\ne\nz\n");

        Assert.Equal("a\nb\nc\nD\ne\nz\n", Assert.Single(recorder.Proposals).Diff!.NewText);
        Assert.Contains("REPLACES your earlier proposal", second);
    }

    [Fact]
    public async Task TheSameEditTwice_SaysNothingMore()
    {
        // Reference arm: a repeated identical proposal loses nothing, and a warning there is noise.
        var (recorder, second) = await TwoCalls("apply_diff", "a\nB\nc\nd\ne\n", "apply_diff", "a\nB\nc\nd\ne\n");

        Assert.Equal("a\nB\nc\nd\ne\n", Assert.Single(recorder.Proposals).Diff!.NewText);
        Assert.DoesNotContain("REPLACES", second);
        Assert.DoesNotContain("combined", second);
    }

    [Theory]
    [InlineData("a\nB\nc\nd\ne\n", "a\nb\nc\nD\ne\n", "a\nB\nc\nD\ne\n")]  // two disjoint edits
    [InlineData("X\na\nb\nc\nd\ne\n", "a\nb\nc\nd\ne\nY\n", "X\na\nb\nc\nd\ne\nY\n")] // head and tail inserts
    [InlineData("a\nc\nd\ne\n", "a\nb\nc\nd\n", "a\nc\nd\n")]  // two deletions
    [InlineData("a\nB\nc\nd\ne\n", "a\nBB\nc\nd\ne\n", null)]  // same line: no merge
    [InlineData("a\nb\nX\nc\nd\ne\n", "a\nb\nY\nc\nd\ne\n", null)]  // two inserts at one point: ambiguous
    public void TheMerge_CombinesDisjointRegionsOnly(string first, string second, string? expected) =>
        Assert.Equal(expected, ProposalMerge.TryMerge(Disk, first, second));
}
