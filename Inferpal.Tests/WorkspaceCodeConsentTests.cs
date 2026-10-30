using System.IO;
using System.Text.Json;
using Inferpal.Config;
using Inferpal.Services.CodeActions;
using Inferpal.Services.Commands;
using Inferpal.Services.Execution;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// ⚠ A build or a test run executes what the repository wrote — a project's build targets, its test scripts, its own
/// Python environment. A model's <c>run_tests</c> or <c>get_diagnostics</c> ran a cloned repository's code with no card.
/// The person agrees once per workspace; a call they typed is theirs.
/// </summary>
public sealed class WorkspaceCodeConsentTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("inferpal-consent").FullName;
    private string? _current;

    public WorkspaceCodeConsentTests() => _current = _root;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    /// <summary>Answers every card with <paramref name="answer"/>, counting the cards and whether they were forced.</summary>
    private sealed class Asking(Func<string?> root, bool answer) : IApprovalService
    {
        public int Cards;
        public bool AllForced = true;
        public Task<bool> RequestApprovalAsync(string toolName, string details, CancellationToken ct, string? subject = null,
                                               DiffInfo? diff = null, bool forcePrompt = false)
        {
            _ = root;
            Cards++;
            AllForced &= forcePrompt;
            return Task.FromResult(answer);
        }
    }

    private static JsonElement Args(object o) => JsonSerializer.SerializeToElement(o);

    [Fact]
    public async Task AModelsRun_IsAskedOncePerWorkspace()
    {
        var approval = new Asking(() => _current, answer: true);
        var consent  = new WorkspaceCodeConsent(approval, () => _current);

        Assert.Null(await consent.RefusalAsync("run_tests", CancellationToken.None));
        Assert.Null(await consent.RefusalAsync("get_diagnostics", CancellationToken.None));
        Assert.Equal(1, approval.Cards);

        // Another workspace is another repository's code: asked again.
        _current = Directory.CreateDirectory(Path.Combine(_root, "other")).FullName;
        Assert.Null(await consent.RefusalAsync("run_tests", CancellationToken.None));
        Assert.Equal(2, approval.Cards);
    }

    /// <summary>⚠ A refusal holds: a model that runs the tests again does not show the card again at each try.</summary>
    [Fact]
    public async Task ARefusal_IsRemembered()
    {
        var approval = new Asking(() => _current, answer: false);
        var consent  = new WorkspaceCodeConsent(approval, () => _current);

        Assert.Equal(WorkspaceCodeConsent.Refusal, await consent.RefusalAsync("run_tests", CancellationToken.None));
        Assert.Equal(WorkspaceCodeConsent.Refusal, await consent.RefusalAsync("run_tests", CancellationToken.None));
        Assert.Equal(1, approval.Cards);
    }

    /// <summary>The agreement belongs to the workspace, not to one registry: /tdd and each ACP thread build their own.</summary>
    [Fact]
    public async Task AnAgreement_HoldsForAnotherRegistry()
    {
        var approval = new Asking(() => _current, answer: true);
        await new WorkspaceCodeConsent(approval, () => _current).RefusalAsync("run_tests", CancellationToken.None);

        Assert.Null(await new WorkspaceCodeConsent(approval, () => _current).RefusalAsync("run_tests", CancellationToken.None));
        Assert.Equal(1, approval.Cards);
    }

    [Fact]
    public async Task ACallThePersonTyped_IsNotAsked()
    {
        var approval = new Asking(() => _current, answer: false);
        var consent  = new WorkspaceCodeConsent(approval, () => _current);

        using (PackageCheck.ThePersonsOwnCalls())
            Assert.Null(await consent.RefusalAsync("run_tests", CancellationToken.None));

        Assert.Equal(0, approval.Cards);
        // And the person's own run is their agreement: the assistant's next run is not asked either.
        Assert.Null(await consent.RefusalAsync("run_tests", CancellationToken.None));
        Assert.Equal(0, approval.Cards);
    }

    /// <summary>⚠ Smart Fix's built-in check after a write builds the project too: its command is ours, what it builds
    /// is not.</summary>
    [Fact]
    public async Task SmartFixsBuiltInCheck_IsAskedLikeABuild()
    {
        var project = Path.Combine(_root, "App.csproj");
        File.WriteAllText(project, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        var source = Path.Combine(_root, "Program.cs");
        File.WriteAllText(source, "class P {}");
        var approval  = new Asking(() => _current, answer: false);
        var validator = new SmartFixValidator(new InferpalConfig { SmartFixEnabled = true }, () => _root, approval,
                                              new WorkspaceCodeConsent(approval, () => _current));

        var note = await validator.ValidateAsync(source, CancellationToken.None);

        Assert.Equal(1, approval.Cards);
        Assert.Contains(WorkspaceCodeConsent.Refusal, note ?? "");
    }

    /// <summary>The card is forced: an "Always" grant, an allow rule or the approvals turned off do not skip it.</summary>
    [Fact]
    public async Task TheCard_IsForced()
    {
        var approval = new Asking(() => _current, answer: false);
        var consent  = new WorkspaceCodeConsent(approval, () => _current);

        Assert.Equal(WorkspaceCodeConsent.Refusal, await consent.RefusalAsync("run_tests", CancellationToken.None));
        Assert.Equal(1, approval.Cards);
        Assert.True(approval.AllForced);
    }

    [Fact]
    public async Task ARefusedRun_StartsNothing_AndReadsAsNothingRan()
    {
        File.WriteAllText(Path.Combine(_root, "Cargo.toml"), "[package]\nname = \"x\"\nversion = \"0.1.0\"\n");
        var approval = new Asking(() => _current, answer: false);
        var tool     = new RunTestsTool(() => _root, new WorkspaceCodeConsent(approval, () => _current));

        var report = await tool.ExecuteAsync(Args(new { runner = "cargo" }), CancellationToken.None);

        Assert.Equal(WorkspaceCodeConsent.Refusal, report);
        Assert.Equal(1, approval.Cards);
        // /tdd reads it as nothing having run — never as a failing test to fix.
        Assert.True(TddCommandHandler.NothingRan(report));
        Assert.False(TddCommandHandler.TestsFailed(report));
    }

    [Fact]
    public async Task ARefusedBuild_IsNotBuilt()
    {
        var project = Path.Combine(_root, "App.csproj");
        File.WriteAllText(project, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        var approval = new Asking(() => _current, answer: false);
        var tool     = new GetDiagnosticsTool(getRoot: () => _root, consent: new WorkspaceCodeConsent(approval, () => _current));

        var report = await tool.ExecuteAsync(Args(new { path = project }), CancellationToken.None);

        Assert.Equal(WorkspaceCodeConsent.Refusal, report);
        Assert.Equal(GetDiagnosticsTool.BuildVerdict.NotBuilt, GetDiagnosticsTool.ReadVerdict(report));
    }

    /// <summary>Answers each card from <paramref name="answer"/>, given the subject and the card's number.</summary>
    private sealed class Scripted(Func<string?, int, Task<bool>> answer) : IApprovalService
    {
        public int Cards;
        public Task<bool> RequestApprovalAsync(string toolName, string details, CancellationToken ct, string? subject = null,
                                               DiffInfo? diff = null, bool forcePrompt = false) =>
            answer(subject, ++Cards);
    }

    /// <summary>Refuses every card, through the portable approval base.</summary>
    private sealed class Refusing(Func<string?> root) : ApprovalServiceBase(new InferpalConfig(), root)
    {
        public int Cards;
        protected override Task<ApprovalDecision> PromptUserAsync(string message, DiffInfo? diff, CancellationToken ct)
        {
            Cards++;
            return Task.FromResult(ApprovalDecision.Deny);
        }
    }

    /// <summary>The card a steering note takes away, through the portable approval base.</summary>
    private sealed class Withdrawing(Func<string?> root) : ApprovalServiceBase(new InferpalConfig(), root)
    {
        public int Cards;
        protected override Task<ApprovalDecision> PromptUserAsync(string message, DiffInfo? diff, CancellationToken ct) =>
            Task.FromResult(++Cards == 1 ? ApprovalDecision.Withdrawn : ApprovalDecision.Once);
    }

    /// <summary>⚠ A card a note took away is no answer: remembered as a "No", every later build of the workspace was
    /// refused for the rest of the session.</summary>
    [Fact]
    public async Task AWithdrawnCard_IsNoRefusal_TheNextRunAsksAgain()
    {
        var approval = new Withdrawing(() => _current);
        var consent  = new WorkspaceCodeConsent(approval, () => _current);

        Assert.Equal(WorkspaceCodeConsent.Refusal, await consent.RefusalAsync("run_tests", CancellationToken.None));
        Assert.Null(await consent.RefusalAsync("run_tests", CancellationToken.None));
        Assert.Equal(2, approval.Cards);
    }

    /// <summary>⚠ A rule that denies the card is a refusal, never an exception thrown after a write that happened.</summary>
    [Fact]
    public async Task ARuleThatDeniesTheCard_IsARefusal_NotAnException()
    {
        var consent = new WorkspaceCodeConsent(
            new Scripted((_, _) => throw new PermissionDeniedException("denied by rule")), () => _current);

        Assert.Equal(WorkspaceCodeConsent.Refusal, await consent.RefusalAsync("smart_fix_validator", CancellationToken.None));
    }

    /// <summary>⚠ One workspace's card does not hold another's: an ACP thread on another folder reaches its own card.</summary>
    [Fact]
    public async Task OneWorkspacesCard_DoesNotHoldAnothers()
    {
        var held     = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var other    = Directory.CreateDirectory(Path.Combine(_root, "elsewhere")).FullName;
        var approval = new Scripted((subject, n) => n == 1 ? held.Task : Task.FromResult(true));
        var first    = new WorkspaceCodeConsent(approval, () => _root).RefusalAsync("run_tests", CancellationToken.None);

        try
        {
            var second = await new WorkspaceCodeConsent(approval, () => other).RefusalAsync("run_tests", CancellationToken.None)
                                                                              .WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Null(second);
            Assert.Equal(2, approval.Cards);
        }
        finally { held.TrySetResult(true); }   // released whatever happened: a held card would hold every later test
        Assert.Null(await first);
    }

    /// <summary>⚠ Asked in the middle of a write already approved, a "No" refuses the build, never the write: marked as a
    /// refused call, what a repository's fixer then rewrote never entered the run.</summary>
    [Fact]
    public async Task ANoInTheMiddleOfACall_DoesNotRefuseTheCall()
    {
        var approval = new Refusing(() => _current);
        var bracket  = await WorkTreeBracket.OpenAsync(_root, null, CancellationToken.None);
        WorkTreeBracket.Enter(bracket);
        try
        {
            // The consent's card, refused, through the approval base — where a refused card marks the call.
            var consent = new WorkspaceCodeConsent(approval, () => _current);
            Assert.Equal(WorkspaceCodeConsent.Refusal, await consent.RefusalAsync("smart_fix_validator", CancellationToken.None));
            Assert.Equal(1, approval.Cards);
            Assert.False(bracket.Refused);

            // Reference arm: the call's own card, refused, does mark it.
            await approval.RequestApprovalAsync("write_file", "write", CancellationToken.None, subject: _root, forcePrompt: true);
            Assert.True(bracket.Refused);
        }
        finally { WorkTreeBracket.Enter(null!); }
    }

    /// <summary>The person's own run of their tests (/test coverage) is their agreement.</summary>
    [Fact]
    public async Task ThePersonsOwnCoverageRun_IsTheirAgreement()
    {
        var approval = new Asking(() => _current, answer: false);
        WorkspaceCodeConsent.NoteThePersonsRun(_root);

        Assert.Null(await new WorkspaceCodeConsent(approval, () => _current).RefusalAsync("smart_fix_validator", CancellationToken.None));
        Assert.Equal(0, approval.Cards);
        Assert.Contains("WorkspaceCodeConsent.NoteThePersonsRun(root);",
            ConventionCoverageTests.CodeOnly(Path.Combine(ConventionCoverageTests.RepoRoot(), "Inferpal.Core", "Services",
                                                          "Commands", "TestCoverageCommandHandler.cs")), StringComparison.Ordinal);
    }

    /// <summary>
    /// ⚠ A batch asks every card before any check runs: asked between two checks, the consent card read the work tree
    /// again after a repository's fixer had rewritten files, which then never entered the run.
    /// </summary>
    [Fact]
    public async Task ABatchAsksEveryCard_BeforeAnyCheckRuns()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".inferpal"));
        File.WriteAllText(Path.Combine(_root, ".inferpal", "validators.json"),
                          """{ ".ts": { "command": "echo fixed > fixed.txt", "marker": "package.json" } }""");
        File.WriteAllText(Path.Combine(_root, "package.json"), "{}");
        File.WriteAllText(Path.Combine(_root, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        var ts = Path.Combine(_root, "a.ts");
        var cs = Path.Combine(_root, "A.cs");
        File.WriteAllText(ts, "let a = 1;");
        File.WriteAllText(cs, "class A {}");
        var fixedFile = Path.Combine(_root, "fixed.txt");
        bool? fixerHadRun = null;
        var approval = new Scripted((subject, _) =>
        {
            if (subject is not null && subject.Contains("echo", StringComparison.Ordinal)) return Task.FromResult(true);
            fixerHadRun = File.Exists(fixedFile);   // the consent card: has a check already run?
            return Task.FromResult(false);
        });
        var validator = new SmartFixValidator(new InferpalConfig { SmartFixEnabled = true }, () => _root, approval,
                                              new WorkspaceCodeConsent(approval, () => _root));

        await validator.ValidateManyAsync([ts, cs], CancellationToken.None);

        Assert.False(fixerHadRun);                  // asked before the fixer ran
        Assert.True(File.Exists(fixedFile));        // witness: the approved fixer did run, afterwards
    }

    /// <summary>The registry the model calls through asks — what wires the card to the model's tools.</summary>
    [Fact]
    public async Task TheModelsRegistry_AsksBeforeARun()
    {
        File.WriteAllText(Path.Combine(_root, "Cargo.toml"), "[package]\nname = \"x\"\nversion = \"0.1.0\"\n");
        var approval = new Asking(() => _root, answer: false);
        var config   = new InferpalConfig();
        var client   = new FakeInferenceProvider();
        var editor   = new NullEditorSurface();
        var index    = new Inferpal.Services.Rag.ProjectIndexService(client, config, new Inferpal.Services.Lsp.LspSemanticProvider());
        index.SetRoot(_root);
        var tools = new ToolRegistry(editor, approval, config, index, client, new Inferpal.Services.ProjectMapService(editor),
                                     new Inferpal.Services.Mcp.McpToolService(config, approval),
                                     new Inferpal.Services.Docs.DocsIndexService(client, config),
                                     new Inferpal.Services.Editor.OpenDocumentOverlay(), null);

        var report = await tools.ExecuteAsync("run_tests", Args(new { runner = "cargo" }), CancellationToken.None);

        Assert.Contains(WorkspaceCodeConsent.Refusal, report);
        Assert.Equal(1, approval.Cards);
    }
}
