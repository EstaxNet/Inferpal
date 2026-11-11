using System.IO;
using System.Text.Json;
using Inferpal.Services.Agent;
using Inferpal.Services.Execution;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

/// <summary>The copy guard where the model meets it (pari 69): <c>apply_diff</c> and <c>apply_edits</c> refuse before the
/// approval card and write nothing; the same edit sent again in a run is written, under the one pass it shares with the
/// syntax guard.</summary>
[Collection("SyntaxGuard")]
public sealed class CopiedContextToolsTests : IDisposable
{
    private readonly string _ws = Directory.CreateTempSubdirectory("inferpal-copied-tools").FullName;
    private const string Page = "@page \"/counter\"\n@rendermode InteractiveServer\n\n<PageTitle>Counter</PageTitle>\n\n"
                              + "<h1>Counter</h1>\n\n<p role=\"status\">Current count: @currentCount</p>\n\n"
                              + "<button class=\"btn btn-primary\" @onclick=\"IncrementCount\">Click me</button>\n";
    private const string Fragment = "<button class=\"btn btn-primary\" @onclick=\"IncrementCount\">Click me</button>";
    private static readonly string WholePage = Page.TrimEnd('\n') + "\n<button class=\"btn\" @onclick=\"Reset\">Reset</button>";

    public CopiedContextToolsTests() => SyntaxGuard.ResetForTests();

    public void Dispose()
    {
        try { Directory.Delete(_ws, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private sealed class CountingApproval : IApprovalService
    {
        public int Asked;
        public Task<bool> RequestApprovalAsync(string toolName, string details, CancellationToken ct, string? subject = null,
                                               Inferpal.Services.CodeActions.DiffInfo? diff = null, bool forcePrompt = false)
        {
            Asked++;
            return Task.FromResult(true);
        }
    }

    private static JsonElement Args(object o) => JsonSerializer.SerializeToElement(o);

    private string FileWith(string name, string content)
    {
        var path = Path.Combine(_ws, name);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public async Task ApplyDiff_RefusesTheWholePageForAFragment_BeforeTheCard_AndWritesNothing()
    {
        var path = FileWith("Counter.razor", Page);
        var approval = new CountingApproval();
        var tool = new ApplyDiffTool(approval, new FileHistoryService(), () => _ws);

        var answer = await tool.ExecuteAsync(Args(new { path, old_content = Fragment, new_content = WholePage }), CancellationToken.None);

        Assert.Contains(CopiedContext.RefusalMarker + "Counter.razor: new_content starts with", answer, StringComparison.Ordinal);
        Assert.DoesNotContain(CopiedContext.PassOffered, answer, StringComparison.Ordinal);   // outside a run: no pass
        Assert.Equal(0, approval.Asked);
        Assert.Equal(Page, File.ReadAllText(path));
    }

    [Fact]
    public async Task ApplyDiff_AFragmentForAFragment_IsWritten()
    {
        var path = FileWith("Counter.razor", Page);
        var approval = new CountingApproval();
        var tool = new ApplyDiffTool(approval, new FileHistoryService(), () => _ws);

        await tool.ExecuteAsync(Args(new { path, old_content = Fragment,
            new_content = Fragment + "\n<button class=\"btn\" @onclick=\"Reset\">Reset</button>" }), CancellationToken.None);

        Assert.Equal(1, approval.Asked);
        Assert.Contains("Reset</button>", File.ReadAllText(path), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ApplyEdits_RefusesTheWholeBatch_AndWritesNothing()
    {
        var path = FileWith("Counter.razor", Page);
        var other = FileWith("B.txt", "one\n");
        var approval = new CountingApproval();
        var tool = new ApplyEditsTool(approval, new FileHistoryService(), () => _ws);

        var answer = await tool.ExecuteAsync(Args(new
        {
            edits = new object[]
            {
                new { path = other, old_content = "one", new_content = "two" },
                new { path, old_content = Fragment, new_content = WholePage },
            },
        }), CancellationToken.None);

        Assert.Contains(CopiedContext.RefusalMarker, answer, StringComparison.Ordinal);
        Assert.Equal(0, approval.Asked);
        Assert.Equal(Page, File.ReadAllText(path));
        Assert.Equal("one\n", File.ReadAllText(other));   // all or nothing
    }

    [Fact]
    public async Task InARun_TheSameEditSentAgain_IsWritten_AndTheLoopForgivesTheRepeat()
    {
        var path = FileWith("Counter.razor", Page);
        var approval = new CountingApproval();
        var history = new FileHistoryService();
        history.BeginRun();
        var tool = new ApplyDiffTool(approval, history, () => _ws);
        var edit = Args(new { path, old_content = Fragment, new_content = WholePage });

        var first = await tool.ExecuteAsync(edit, CancellationToken.None);
        Assert.Contains(CopiedContext.PassOffered, first, StringComparison.Ordinal);
        Assert.True(SyntaxGuard.OffersPass(first));
        Assert.Equal(0, approval.Asked);

        await tool.ExecuteAsync(edit, CancellationToken.None);
        Assert.Equal(1, approval.Asked);
    }

    /// <summary>An edit both guards refuse: both said at once, and the one resend writes it — two passes, one per guard,
    /// would refuse it by turns forever.</summary>
    [Fact]
    public async Task AnEditBothGuardsRefuse_IsWrittenAtTheFirstResend()
    {
        var path = FileWith("A.cs", "class A\n{\n    void M() { }\n}\n");
        var approval = new CountingApproval();
        var history = new FileHistoryService();
        history.BeginRun();
        var tool = new ApplyDiffTool(approval, history, () => _ws);
        var edit = Args(new { path, old_content = "    void M() { }", new_content = "class A\n{\n    void M() { }\n    void N() {\n}" });

        var first = await tool.ExecuteAsync(edit, CancellationToken.None);
        Assert.Contains(CopiedContext.RefusalMarker, first, StringComparison.Ordinal);
        Assert.Contains(SyntaxGuard.RefusalMarker, first, StringComparison.Ordinal);
        Assert.Equal(0, approval.Asked);

        await tool.ExecuteAsync(edit, CancellationToken.None);
        Assert.Equal(1, approval.Asked);
    }

    [Fact]
    public void TheLoopForgives_ACopyRefusalThatOffersThePass_Only()
    {
        var inRun = CopiedContext.Refusal(new CopiedContext.Copy(CopiedContext.Side.Head, 3, "@page"), "a.razor") + CopiedContext.PassOffered;
        var outside = CopiedContext.Refusal(new CopiedContext.Copy(CopiedContext.Side.Head, 3, "@page"), "a.razor");
        var batch = new List<Inferpal.Models.ToolCallDto>
        {
            new(new Inferpal.Models.ToolCallFunction("apply_diff", Args(new { path = "a.razor", old_content = "x", new_content = "y" }))),
        };

        var counts = new Dictionary<string, int>();
        Assert.False(AgentLoopPolicy.IsLoop(counts, batch));
        AgentLoopPolicy.Forgive(counts, batch, [inRun]);
        Assert.False(AgentLoopPolicy.IsLoop(counts, batch));    // forgiven: the repeat is what the refusal asked for

        counts.Clear();
        Assert.False(AgentLoopPolicy.IsLoop(counts, batch));
        AgentLoopPolicy.Forgive(counts, batch, [outside]);
        Assert.True(AgentLoopPolicy.IsLoop(counts, batch));     // no pass offered: the repeat is a loop
    }
}
