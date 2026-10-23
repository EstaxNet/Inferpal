using System.IO;
using Inferpal.Config;
using Inferpal.Localization;
using Inferpal.Models;
using Inferpal.Services;
using Inferpal.Services.Agent;
using Inferpal.Services.Commands;
using Inferpal.Services.Governance;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// <c>/pr [base]</c> on a real git repository: the branch described from where it left its base — the commits' subjects
/// and the diff sent to the utility model, the range named above the proposal, the proposal in a block to copy — and
/// every way there is nothing to describe said instead.
/// </summary>
public sealed class PrCommandHandlerTests : IDisposable
{
    private const string Marker = "MARKER_pr_5d1e";
    private readonly string _repo = Directory.CreateTempSubdirectory("inferpal-pr").FullName;

    public void Dispose()
    {
        ContentExclusions.ForgetForTests(_repo);
        try
        {
            foreach (var f in Directory.EnumerateFiles(_repo, "*", SearchOption.AllDirectories))
                File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(_repo, recursive: true);
        }
        catch { /* best-effort cleanup */ }
    }

    private async Task Git(string args)
    {
        var (output, exit) = await GitProcess.RunAsync(args, _repo, CancellationToken.None);
        Assert.True(exit == 0, $"git {args}: {output}");
    }

    private void Write(string rel, string text)
    {
        var full = Path.Combine(_repo, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
    }

    /// <summary><c>main</c> with one commit; the branch <c>feature</c>, checked out, adds two: Invoice.cs and Tax.cs.</summary>
    private async Task BranchAsync(Action? beforeTheBranchCommits = null)
    {
        await Git("init -q -b main");
        await Git("config user.email t@example.test");
        await Git("config user.name t");
        Write("src/Invoice.cs", "class Invoice { }\n");
        await Git("add -A");
        await Git("-c commit.gpgsign=false commit -q -m base");
        await Git("checkout -q -b feature");
        beforeTheBranchCommits?.Invoke();
        Write("src/Invoice.cs", "class Invoice { decimal Total() => 42m; }\n");
        await Git("add -A");
        await Git("-c commit.gpgsign=false commit -q -m \"feat: total an invoice\"");
        Write("src/Tax.cs", "class Tax { }\n");
        await Git("add -A");
        await Git("-c commit.gpgsign=false commit -q -m \"feat: add the tax\"");
    }

    private static FakeInferenceProvider Proposing(string reply, bool cut = false) => new()
    {
        RunAgentThroughChat = true,
        ChatResult = new ChatTurnResult(reply, [], 0, 0, CutAtLimit: cut),
    };

    private Task<string> Pr(FakeInferenceProvider client, params string[] parts) =>
        PrCommandHandler.HandleAsync(client, new InferpalConfig(), ["/pr-description", .. parts], GitProcess.For(_repo), null,
                                     CancellationToken.None);

    private static string Sent(FakeInferenceProvider client) =>
        string.Join("\n", client.AgentRuns.SelectMany(r => r.History).Select(m => m.Content ?? string.Empty));

    [Fact]
    public async Task TheBranch_IsDescribed_FromItsCommitsAndItsDiff_InABlockToCopy()
    {
        await BranchAsync();
        var client = Proposing("Total an invoice, and add the tax\n\nThe invoice now has a total.");

        var answer = await Pr(client, "main");

        var sent = Sent(client);
        Assert.Contains("- feat: total an invoice\n- feat: add the tax", sent);   // oldest first
        Assert.Contains("decimal Total() => 42m;", sent);
        Assert.Contains("+++ b/src/Tax.cs", sent);
        Assert.StartsWith(Strings.PrDescribedRange("main...HEAD", 2), answer);
        Assert.Contains("```markdown\nTotal an invoice, and add the tax\n\nThe invoice now has a total.\n```", answer);
    }

    [Fact]
    public async Task WithoutABase_TheDefaultBranchIsTheBase()
    {
        await BranchAsync();
        var client = Proposing("Total an invoice");

        var answer = await Pr(client);

        Assert.StartsWith(Strings.PrDescribedRange("main...HEAD", 2), answer);
    }

    [Fact]
    public async Task NothingToDescribe_AnUnknownBase_OrNoBaseAtAll_IsSaid_AndNoModelIsAsked()
    {
        await BranchAsync();
        var client = Proposing("never asked");

        var unknown = await Pr(client, "nope");
        var option  = await Pr(client, "--output=x");
        await Git("checkout -q main");
        var empty   = await Pr(client, "main");
        var noBase  = await Pr(client);   // on main: no other base to find

        Assert.Equal(Strings.PrUnknownBase("nope"), unknown);
        Assert.Equal(Strings.PrUnknownBase("--output=x"), option);   // never an option on git's command line
        Assert.Equal(Strings.PrRangeEmpty("main...HEAD"), empty);
        Assert.Equal(Strings.PrNoBase, noBase);
        Assert.Empty(client.AgentRuns);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AFileTheIgnoreFilesExclude_NeverReachesTheModel_AndThatIsSaid(bool excluded)
    {
        await BranchAsync(() =>
        {
            if (excluded) Write(".aiignore", "secrets.txt\n");
            Write("secrets.txt", $"key = {Marker}\n");
        });
        // Declared once the ignore file exists: the rules are cached between rechecks, and any reader of every root (a
        // parallel test's AnyRules) could have read them before it was written.
        ContentExclusions.Declare(_repo, () => ContentExclusions.DefaultFiles);
        var client = Proposing("Total an invoice");

        var answer = await Pr(client, "main");

        var sent = Sent(client);
        Assert.Contains("decimal Total() => 42m;", sent);   // WITNESS: the visible change reached the model
        if (!excluded)
        {
            Assert.Contains(Marker, sent);
            return;
        }
        Assert.DoesNotContain(Marker, sent);
        Assert.Contains(".aiignore", answer);
    }

    [Fact]
    public async Task ADiffPastTheCap_AndACutReply_AreSaidAboveTheProposal()
    {
        await BranchAsync(() => Write("src/Big.cs", string.Join("\n", Enumerable.Range(0, 3_000).Select(i => $"// line {i}"))));
        var client = Proposing("Total an invoice", cut: true);

        var answer = await Pr(client, "main");

        // The cut notice, past its two counts (whose total depends on git's diff; their order, on the language).
        var cutNotice = Strings.PrDiffTruncated(111, 999);
        Assert.Contains(cutNotice[(Math.Max(cutNotice.LastIndexOf("111", StringComparison.Ordinal),
                                            cutNotice.LastIndexOf("999", StringComparison.Ordinal)) + 3)..], answer);
        Assert.Contains(Strings.PrDescriptionCut, answer);
        Assert.True(answer.IndexOf(Strings.PrDescriptionCut, StringComparison.Ordinal)
                    < answer.IndexOf("```markdown", StringComparison.Ordinal), "the notices come above the proposal");
    }

    /// <summary>Past the cap, the OLDEST subjects are read, in order — git limits before it reverses, so a capped
    /// <c>log --reverse</c> would send the newest — and the cut is said to the person.</summary>
    [Fact]
    public async Task ALongBranch_SendsItsFirstSubjectsInOrder_AndSaysTheCut()
    {
        await BranchAsync();
        for (var i = 1; i <= PrCommandHandler.MaxSubjects + 3; i++)
            await Git($"-c commit.gpgsign=false commit -q --allow-empty -m c{i:D3}");
        var client = Proposing("A long branch");

        var answer = await Pr(client, "main");

        var sent = Sent(client);
        Assert.Contains("- feat: total an invoice\n- feat: add the tax\n- c001", sent);
        Assert.Contains("- c098", sent);
        Assert.DoesNotContain("- c099", sent);   // 2 + 98 = the first 100
        Assert.Contains(Strings.PrSubjectsCut(PrCommandHandler.MaxSubjects, PrCommandHandler.MaxSubjects + 5), answer);
    }

    [Fact]
    public async Task ABranchThatChangesNothingInTheEnd_OrOnlyExcludedFiles_IsSaid_NeverDescribed()
    {
        await Git("init -q -b main");
        await Git("config user.email t@example.test");
        await Git("config user.name t");
        Write(".aiignore", "secrets.txt\n");
        ContentExclusions.Declare(_repo, () => ContentExclusions.DefaultFiles);   // once the ignore file exists
        Write("src/Invoice.cs", "class Invoice { }\n");
        await Git("add -A");
        await Git("-c commit.gpgsign=false commit -q -m base");
        await Git("checkout -q -b undone");
        Write("src/Invoice.cs", "class Invoice { int X; }\n");
        await Git("-c commit.gpgsign=false commit -q -am change");
        Write("src/Invoice.cs", "class Invoice { }\n");
        await Git("-c commit.gpgsign=false commit -q -am \"change back\"");
        var client = Proposing("never asked");

        var undone = await Pr(client, "main");
        await Git("checkout -q -b secret main");
        Write("secrets.txt", $"key = {Marker}\n");
        await Git("add -A");
        await Git("-c commit.gpgsign=false commit -q -m secret");
        var secret = await Pr(client, "main");

        Assert.EndsWith(Strings.PrDiffEmpty("main...HEAD"), undone);
        Assert.EndsWith(Strings.PrDiffEmpty("main...HEAD"), secret);
        Assert.Contains(".aiignore", secret);   // why nothing was left: said
        Assert.Empty(client.AgentRuns);
    }

    /// <summary>A reply the model wrapped in its own block is unwrapped — pasted, it would be one code block — and only the
    /// reasoning in FRONT of it is removed: a description may name a reasoning tag in its prose.</summary>
    [Fact]
    public async Task TheModelsOwnBlock_IsUnwrapped_AndOnlyTheLeadingReasoningIsRemoved()
    {
        await BranchAsync();
        var wrapped   = Proposing("```markdown\nTotal an invoice\n\nThe invoice now has a total.\n```");
        var reasoning = Proposing("<think>plan the text</think>Strip reasoning\n\nThe parser now keeps <think> named in prose.");

        var unwrapped = await Pr(wrapped, "main");
        var kept      = await Pr(reasoning, "main");

        Assert.EndsWith("```markdown\nTotal an invoice\n\nThe invoice now has a total.\n```", unwrapped);
        Assert.Equal(1, unwrapped.Split("```markdown").Length - 1);
        Assert.Contains("The parser now keeps <think> named in prose.", kept);
        Assert.DoesNotContain("plan the text", kept);
    }

    /// <summary>No description, no "Described:" and no block: a loop, a failed run, an empty reply are said alone.</summary>
    [Fact]
    public async Task AReplyThatIsNoDescription_IsSaidAlone_NeverOfferedToCopy()
    {
        await BranchAsync();
        FakeInferenceProvider Run(AgentResult result) => new() { AgentRunResult = result };

        var looped = await Pr(Run(new AgentResult("again and again and", [], [], AnswerCut: true, AnswerRepeating: true)), "main");
        var failed = await Pr(Run(new AgentResult("the backend refused the request", [], [], Failed: true)), "main");

        Assert.Equal(Strings.AnswerStoppedRepeating, looped);
        Assert.Equal("the backend refused the request", failed);
    }

    /// <summary>The default base is never the branch's own copy on the remote, nor a default branch pruned since.</summary>
    [Fact]
    public async Task TheDefaultBase_IsTheRemotesDefaultBranch_NeverTheBranchItself_NorAPrunedOne()
    {
        await BranchAsync();
        await Git("update-ref refs/remotes/origin/main main");
        await Git("symbolic-ref refs/remotes/origin/HEAD refs/remotes/origin/main");
        var client = Proposing("Total an invoice");

        var onFeature = await Pr(client);
        await Git("checkout -q main");
        var onMain    = await Pr(client);
        await Git("checkout -q feature");
        await Git("update-ref -d refs/remotes/origin/main");   // pruned: origin/HEAD still points at it
        var pruned    = await Pr(client);

        Assert.StartsWith(Strings.PrDescribedRange("origin/main...HEAD", 2), onFeature);
        Assert.Equal(Strings.PrNoBase, onMain);
        Assert.StartsWith(Strings.PrDescribedRange("main...HEAD", 2), pruned);
    }

    [Fact]
    public async Task ACodeBlockInTheDescription_NeverClosesTheBlockToCopy()
    {
        await BranchAsync();
        var client = Proposing("Total an invoice\n\n```csharp\nvar t = invoice.Total();\n```");

        var answer = await Pr(client, "main");

        Assert.Contains("````markdown\nTotal an invoice", answer);
        Assert.EndsWith("```\n````", answer);
    }
}
