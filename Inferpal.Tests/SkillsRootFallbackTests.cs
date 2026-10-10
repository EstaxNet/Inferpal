using System.IO;
using System.Text.Json;
using Inferpal.Config;
using Inferpal.Services;
using Inferpal.Services.Docs;
using Inferpal.Services.Execution;
using Inferpal.Services.Lsp;
using Inferpal.Services.Mcp;
using Inferpal.Services.Prompting;
using Inferpal.Services.Rag;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// ⚠ The skills the system prompt advertises and the skills <c>read_skill_file</c> loads come from ONE root. With nothing
/// pinned (Visual Studio on a folder without a solution, or before the first question pins one), the prompt's catalog
/// read the editor's project root while the tool read the empty pinned root: the model was told to load a skill the
/// tool could not find.
/// </summary>
[Collection(SignalCollection.Name)]
public sealed class SkillsRootFallbackTests : IDisposable
{
    private readonly SignalScratchDir _scratch = new();
    private readonly string _repo = Path.Combine(Path.GetTempPath(), "inferpal-tests", $"skills-root-{Guid.NewGuid():N}");
    private readonly List<IDisposable> _services = [];

    public SkillsRootFallbackTests()
    {
        TestRagStore.Redirect();
        Directory.CreateDirectory(Path.Combine(_repo, ".git"));
        var skill = Directory.CreateDirectory(Path.Combine(_repo, ".claude", "skills", "deploy-checklist"));
        File.WriteAllText(Path.Combine(skill.FullName, "SKILL.md"),
                          "---\nname: deploy-checklist\ndescription: How to ship a release.\n---\nCheck the tag first.\n");
        RepoSkills.InvalidateCache();
    }

    public void Dispose()
    {
        foreach (var s in _services) s.Dispose();
        RepoSkills.InvalidateCache();
        try { Directory.Delete(_repo, recursive: true); } catch { }
        _scratch.Dispose();
    }

    private (ToolRegistry Registry, ProjectIndexService Index) Unpinned(InferpalConfig config)
    {
        var client = new FakeInferenceProvider();
        var index = new ProjectIndexService(client, config, new LspSemanticProvider());
        _services.Add(index);
        var editor = new NullEditorSurface();
        var approval = new NoopApproval();
        var registry = new ToolRegistry(editor, approval, config, index, client, new ProjectMapService(editor, index),
                                        new McpToolService(config, approval), new DocsIndexService(client, config),
                                        new OpenDocumentOverlay(), new NullDebugSession());
        return (registry, index);
    }

    private static Task<string> LoadSkill(ToolRegistry registry) =>
        registry.ExecuteAsync("read_skill_file",
                              JsonSerializer.SerializeToElement(new { skill = "deploy-checklist", path = "SKILL.md" }),
                              CancellationToken.None);

    [Fact]
    public async Task WithNothingPinned_TheToolReadsTheRootThePromptsCatalogReads()
    {
        var config = new InferpalConfig { RagEnabled = false, SkillsAutoMode = true };
        var (registry, index) = Unpinned(config);
        registry.ProjectRootFallback = () => _repo;      // the editor's project root, as Visual Studio supplies it
        Assert.Equal(string.Empty, index.RootDir);

        // The prompt lists it (workspace root empty: the catalog falls back on the project root)…
        var sections = new SystemPromptBuilder(config, contextWindow: 0, workspaceRoot: index.RootDir)
            .BuildSections("base", projectRoot: _repo);
        Assert.Contains(sections, s => s.Kind == PromptSectionKind.Skills && s.Content.Contains("- deploy-checklist:"));

        // …and the tool the prompt names loads it.
        Assert.Contains("Check the tag first.", await LoadSkill(registry));
        Assert.Equal(_repo, registry.SkillsRoot());
        registry.Dispose();
    }

    [Fact]
    public async Task APinnedRoot_WinsOverTheFallback()
    {
        // Reference arm: once a root is pinned, it is the one both read.
        var config = new InferpalConfig { RagEnabled = false };
        var (registry, index) = Unpinned(config);
        index.SetRoot(_repo);
        registry.ProjectRootFallback = () => Path.GetTempPath();

        Assert.Equal(_repo, registry.SkillsRoot());
        Assert.Contains("Check the tag first.", await LoadSkill(registry));
        registry.Dispose();
    }
}
