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
