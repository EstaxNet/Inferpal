using System.IO;
using System.Text.Json;
using Inferpal.Config;
using Inferpal.Models;
using Inferpal.Services;
using Inferpal.Services.Docs;
using Inferpal.Services.Lsp;
using Inferpal.Services.Mcp;
using Inferpal.Services.Rag;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A mode: read from Copilot's, Roo's and Inferpal's files without losing a field Inferpal carries, every other field
/// named; its tools only ever narrowed — a tool outside it neither offered nor run, an edit outside its pattern refused
/// before the approval card.
/// </summary>
public sealed class ChatModesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "inferpal-modes-" + Guid.NewGuid().ToString("N"));
    private readonly string _user;

    public ChatModesTests()
    {
        Directory.CreateDirectory(_root);
        _user = Path.Combine(_root, "_user-modes");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    /// <summary>The file /import roo writes reads back as the same mode — names, descriptions and patterns with quotes,
    /// hashes, colons, backslashes and line breaks; options it cannot read stay unreadable, edits narrowed to nothing.</summary>
    [Fact]
    public void ARooModeFile_ReadsBackAsTheSameMode_WhateverItsValuesHold()
    {
        var groups = (System.Text.Json.Nodes.JsonArray)System.Text.Json.Nodes.JsonNode.Parse("""
            ["read", ["edit", { "fileRegex": "^docs/.*\\.(md|txt)$|it's # odd: \"q\"", "description": "Docs: 'only' # here" }],
             "command", "mcp", "browser"]
            """)!;
        var text = ChatModes.RooModeFile("It's \"odd\" # name: x", "Line one\nline two", "qwen3:8b", groups, "Be careful.\n\n---\nStill text.");

        var mode = ChatModes.ReadAgentFile(text, "odd.md", ChatModeOrigin.User, "odd.md", [])!;

        Assert.Equal("It's \"odd\" # name: x", mode.Name);
        Assert.Equal("Line one line two", mode.Description);
        Assert.Equal(["qwen3:8b"], mode.Models);
        Assert.Equal("^docs/.*\\.(md|txt)$|it's # odd: \"q\"", mode.Tools!.EditPatternText);
        Assert.Equal("Docs: 'only' # here", mode.Tools.EditPatternDescription);
        Assert.True(mode.Tools.Allows("run_command"));
        Assert.Contains("browser", mode.NotCarried);
        Assert.Equal("Be careful.\n\n---\nStill text.", mode.Instructions);

        // A key the reader does not read, in any shape, leaves the pattern readable; a double quote beside a control
        // character survives the comment the reader looks for.
        var extra = (System.Text.Json.Nodes.JsonArray)System.Text.Json.Nodes.JsonNode.Parse(
            """[["edit", { "fileRegex": "\\.md$", "description": { "en": "Markdown" }, "other": [1, 2] }]]""")!;
        var kept = ChatModes.ReadAgentFile(ChatModes.RooModeFile("Tab\t\" # x", null, null, extra, "x"), "k.md", ChatModeOrigin.User, "k.md", [])!;
        Assert.Equal("\\.md$", kept.Tools!.EditPatternText);
        Assert.Equal("Tab\" # x", kept.Name);   // the tab is cleaned away by the reader, the rest kept

        // Options in another shape: as Roo, edits narrowed to nothing — never a pattern that lets them through.
        var odd = (System.Text.Json.Nodes.JsonArray)System.Text.Json.Nodes.JsonNode.Parse("""[["edit", ["\\.md$"]]]""")!;
        var narrowed = ChatModes.ReadAgentFile(ChatModes.RooModeFile("Odd", null, null, odd, "x"), "o.md", ChatModeOrigin.User, "o.md", [])!;
        Assert.True(narrowed.Tools!.EditPattern!.IsMatch("README.md") is false);
        Assert.Contains("edit options", narrowed.NotCarried);
    }

    /// <summary>A mode file with both lists keeps its own (<c>tools</c>) and names the other: one list decides.</summary>
    [Fact]
    public void AModeFileWithToolsAndGroups_KeepsItsTools_AndNamesTheGroups()
    {
        var mode = ChatModes.ReadAgentFile("---\nname: Both\ntools: [read]\ngroups: [edit]\n---\nx\n", "b.md", ChatModeOrigin.User, "b.md", [])!;

        Assert.False(mode.Tools!.MayWrite);
        Assert.Contains("groups", mode.NotCarried);
    }

    private static string Fixture(string name) =>
        Path.Combine(ConversationPersistenceSilenceTests.RepoRoot(), "Inferpal.Tests", "Fixtures", "modes", name);

    private void Write(string relative, string text)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    /// <summary>The mode's instructions are the system message's last layer — they win a conflict —, named in the X-Ray,
    /// and switched off there like any other; the conversation's saved file keeps the mode.</summary>
    [Fact]
    public async Task AModesInstructions_AreThePromptsLastLayer_NamedInTheXRay_AndSavedWithTheConversation()
    {
        var mode = new ChatMode("Review", "", "MODE-INSTRUCTIONS", [], null, null, ".inferpal/modes/review.md", ChatModeOrigin.Inferpal, []);
        var builder = new SystemPromptBuilder(new InferpalConfig { CustomSystemPrompt = "CUSTOM" });

        var sections = builder.BuildSections("BASE", mode: mode);
        var last = sections.Last(s => s.Placement == PromptPlacement.System);
        Assert.Equal(PromptSectionKind.Mode, last.Kind);
        Assert.Equal("\n\n## Mode: Review\n\nMODE-INSTRUCTIONS", last.Content);
        Assert.Equal("Mode|Review", Services.Presentation.XRayPanelPresenter.SectionId(last));
        Assert.Contains("Review", Services.Presentation.XRayPanelPresenter.Label(last), StringComparison.Ordinal);
        Assert.DoesNotContain("MODE-INSTRUCTIONS",
                              builder.Build("BASE", disabledSectionIds: new HashSet<string> { "Mode|Review" }, mode: mode), StringComparison.Ordinal);
        Assert.DoesNotContain("## Mode", builder.Build("BASE"), StringComparison.Ordinal);   // reference arm: no mode, no layer

        var store = new Services.Persistence.ConversationStore(Path.Combine(_root, "sessions"));
        await store.SaveAsync("held", [new Services.Persistence.SavedMessage("user", "hello")], CancellationToken.None, _root, mode: "Review");
        Assert.Equal("Review", (await store.LoadAsync("held", CancellationToken.None))!.Mode);
    }

    [Fact]
    public void ARealCopilotAgent_IsReadWithEveryFieldInferpalCarries()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".github", "agents"));
        File.Copy(Fixture("context-architect.agent.md"), Path.Combine(_root, ".github", "agents", "context-architect.agent.md"));

        var catalog = ChatModes.Discover(_root, null);

        var mode = Assert.Single(catalog.Modes);
        Assert.Equal("Context Architect", mode.Name);
        Assert.StartsWith("An agent that helps plan", mode.Description, StringComparison.Ordinal);
        Assert.StartsWith("You are a Context Architect", mode.Instructions, StringComparison.Ordinal);
        Assert.Equal(["GPT-5"], mode.Models);
        Assert.Equal(ChatModeOrigin.Copilot, mode.Origin);
        Assert.Equal(".github/agents/context-architect.agent.md", mode.Source);
        var tools = mode.Tools!;
        foreach (var kept in new[] { "search_codebase", "analyze_code", "get_diagnostics", "read_file", "write_file", "apply_edits",
                                     "run_command", "fetch_url" })
            Assert.True(tools.Allows(kept), kept);
        foreach (var dropped in new[] { "run_tests", "web_search", "mcp__github__list_issues", "deploy" })
            Assert.False(tools.Allows(dropped), dropped);
        Assert.Contains("execute/getTerminalOutput", mode.NotCarried);
        // A Copilot model never sits on a local server: none picked, the chat's model answers (said by the caller).
        Assert.Null(mode.PickModel(["devstral-small-2", "qwen3.8:27b"]));
    }

    [Fact]
    public void ARealRooModesFile_IsReadWithItsGroupsItsPatternsAndItsInstructions()
    {
        File.Copy(Fixture("batfish.roomodes"), Path.Combine(_root, ".roomodes"));

        var catalog = ChatModes.Discover(_root, null);

        Assert.Equal(10, catalog.Modes.Count);
        Assert.Empty(catalog.Notes);
        var docs = catalog.Find("📝 Documentation Writer")!;
        Assert.Equal(ChatModeOrigin.Roo, docs.Origin);
        Assert.Equal(".roomodes (documentation-writer)", docs.Source);
        Assert.StartsWith("You are Roo, a technical documentation specialist", docs.Instructions, StringComparison.Ordinal);
        Assert.Contains("Focus on creating documentation that is clear", docs.Instructions, StringComparison.Ordinal);
        Assert.Empty(docs.Models);   // Roo keeps no model in the file
        var tools = docs.Tools!;
        foreach (var kept in new[] { "read_file", "search_in_files", "write_file", "run_command", "mcp__ctx__get_docs" })
            Assert.True(tools.Allows(kept), kept);
        Assert.True(RegexBudget.IsMatch(tools.EditPattern!, "docs/guide.md"));
        Assert.False(RegexBudget.IsMatch(tools.EditPattern!, "src/main/App.java"));
        Assert.Equal("Markdown files only", tools.EditPatternDescription);
        Assert.Contains("whenToUse", docs.NotCarried);

        var code = catalog.Find("💻 Code")!;
        Assert.Contains("browser", code.NotCarried);
        var ask = catalog.Find("❓ Ask")!;
        Assert.False(ask.Tools!.Allows("write_file"));   // read only: no edit group
        Assert.False(ask.Tools.Allows("run_command"));
    }

    /// <summary>Roo writes a narrowed group as a list inside the list (<c>- - edit</c>, then <c>- fileRegex: …</c> under the
    /// second dash): read as such, never refused as "unexpected indentation" — which lost every mode of the file.</summary>
    [Fact]
    public void AListInsideAList_OnOneLine_IsRead()
    {
        var node = MiniYaml.Parse("groups:\n  - read\n  - - edit\n    - fileRegex: \\.md$\n      description: Markdown only\n  - command\n")!;

        var groups = node["groups"]!.AsArray();
        Assert.Equal(3, groups.Count);
        Assert.Equal("read", groups[0]!.GetValue<string>());
        var edit = groups[1]!.AsArray();
        Assert.Equal("edit", edit[0]!.GetValue<string>());
        Assert.Equal("\\.md$", edit[1]!["fileRegex"]!.GetValue<string>());
        Assert.Equal("Markdown only", edit[1]!["description"]!.GetValue<string>());
        Assert.Equal("command", groups[2]!.GetValue<string>());
    }

    [Fact]
    public void AnInferpalMode_CarriesItsModelListItsEffortAndItsTools()
    {
        Write(".inferpal/modes/team-review.md", """
            ---
            Name: Team Review
            description: Reads the change and comments; never writes
            model: [qwen3.8:27b, devstral-small-2]
            Tools: [read, search, get_git_status]
            reasoning-effort: max
            handoffs:
              - label: Fix it
                agent: agent
            ---
            Review the change against the repository's conventions. Never edit a file.
            """);

        var mode = Assert.Single(ChatModes.Discover(_root, null).Modes);

        Assert.Equal("Team Review", mode.Name);   // keys read case aside: `Tools:` is the tools, never "every tool"
        Assert.Equal(ChatModeOrigin.Inferpal, mode.Origin);
        Assert.False(mode.Tools!.Allows("write_file"));
        Assert.False(mode.Tools.MayWrite);
        Assert.Equal(ReasoningEffort.High, mode.Effort);
        Assert.Contains("reasoning-effort: max (high)", mode.NotCarried);
        Assert.Contains("handoffs", mode.NotCarried);
        Assert.Equal("devstral-small-2", mode.PickModel(["Devstral-Small-2", "llama3.1"]) is { } m ? m.ToLowerInvariant() : null);
        Assert.Equal("qwen3.8:27b", mode.PickModel(["devstral-small-2", "qwen3.8:27b"]));
        Assert.Equal("Review the change against the repository's conventions. Never edit a file.", mode.Instructions);
    }

    /// <summary>The person's own mode is read first: a repository's mode of the same name never takes its place, and no
    /// file takes the name of one of Inferpal's modes, hidden characters or spaces around it included.</summary>
    [Fact]
    public void ThePersonsOwnModeWins_AndInferpalsNamesStayInferpals()
    {
        Write("_user-modes/review.md", "---\nname: Review\ntools: [read]\n---\nMy own review.\n");
        Write(".github/agents/review.agent.md", "---\nname: review\n---\nThe repository's review, every tool.\n");
        Write(".inferpal/modes/chat.md", "---\nname: Chat\n---\nA mode that would hide Inferpal's own.\n");
        Write(".inferpal/modes/agent.md", "---\nname: \"Agent​ \"\n---\nThe same, with an invisible character.\n");

        var catalog = ChatModes.Discover(_root, _user);

        var mode = Assert.Single(catalog.Modes);
        Assert.Equal(ChatModeOrigin.User, mode.Origin);
        Assert.Equal("My own review.", mode.Instructions);
        Assert.Contains(catalog.Notes, n => n.Kind == ChatModeNoteKind.Duplicate && n.Source == ".github/agents/review.agent.md");
        Assert.Contains(catalog.Notes, n => n.Kind == ChatModeNoteKind.ReservedName && n.Detail == "Chat");
        Assert.Contains(catalog.Notes, n => n.Kind == ChatModeNoteKind.ReservedName && n.Detail == "Agent");
    }

    /// <summary>A mode file — or a Roo mode's rules folder — reached through a link that leaves the repository is not
    /// read: its content would go to the model as instructions (a cloned link to ~/.ssh).</summary>
    [Fact]
    public void AModeFileLinkedOutsideTheRepository_IsNotRead_AndSaidSo()
    {
        var outside = Path.Combine(Path.GetTempPath(), "inferpal-tests", $"outside-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "secret.md"), "---\nname: Leak\n---\nOUTSIDE-SECRET\n");
        Directory.CreateDirectory(Path.Combine(_root, ".github", "agents"));
        Directory.CreateDirectory(Path.Combine(_root, ".roo"));
        Write(".roomodes", "customModes:\n  - slug: docs\n    name: Docs\n    roleDefinition: You write docs.\n    groups: [read]\n");
        try
        {
            try
            {
                File.CreateSymbolicLink(Path.Combine(_root, ".github", "agents", "leak.agent.md"), Path.Combine(outside, "secret.md"));
                Directory.CreateSymbolicLink(Path.Combine(_root, ".roo", "rules-docs"), outside);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }   // no privilege: undecided

            var catalog = ChatModes.Discover(_root, null);

            Assert.DoesNotContain(catalog.Modes, m => m.Name == "Leak");
            Assert.Contains(catalog.Notes, n => n.Kind == ChatModeNoteKind.LeavesTheRepository && n.Source == ".github/agents/leak.agent.md");
            Assert.Equal("You write docs.", catalog.Find("Docs")!.Instructions);   // the linked rules folder is not walked
            Assert.DoesNotContain(catalog.Modes, m => m.Instructions.Contains("OUTSIDE-SECRET", StringComparison.Ordinal));
        }
        finally { try { Directory.Delete(outside, recursive: true); } catch { } }
    }

    [Fact]
    public void ARooFileWithoutModes_AndAPatternInAnotherShape_AreSaid_AndNeverWidenEdits()
    {
        Write(".roomodes", "custom_modes:\n  - slug: docs\n");
        var none = ChatModes.Discover(_root, null);
        Assert.Empty(none.Modes);
        Assert.Contains(none.Notes, n => n.Kind == ChatModeNoteKind.NoModes && n.Source == ".roomodes");

        Write(".roomodes", "customModes:\n  - slug: docs\n    name: Docs\n    groups:\n      - - edit\n        - \"\\\\.md$\"\n");
        var mode = Assert.Single(ChatModes.Discover(_root, null).Modes);
        Assert.False(RegexBudget.IsMatch(mode.Tools!.EditPattern!, "README.md"));   // options unread: edits to nothing, never to all
        Assert.Contains("edit options", mode.NotCarried);
    }

    [Fact]
    public void AFileNestedPastTheBound_IsRefusedAsUnreadable_NeverCrashesTheHost()
    {
        Write(".inferpal/modes/deep.md", "---\ntools:\n  " + string.Concat(Enumerable.Repeat("- ", 20_000)) + "read\n---\nBody.\n");
        Write(".inferpal/modes/brackets.md", "---\ntools: " + new string('[', 20_000) + new string(']', 20_000) + "\n---\nBody.\n");

        var catalog = ChatModes.Discover(_root, null);

        Assert.Empty(catalog.Modes);
        Assert.Equal(2, catalog.Notes.Count(n => n.Kind == ChatModeNoteKind.Unreadable && n.Detail!.Contains("levels deep", StringComparison.Ordinal)));
    }

    [Fact]
    public void WhatAModeMayWrite_CountsCommandsAndMcpTools_AndDebugInspectIsNoRead()
    {
        Assert.False(ModeTools.FromCopilot(["read", "search", "web"]).MayWrite);
        Assert.True(ModeTools.FromCopilot(["read", "runInTerminal"]).MayWrite);
        Assert.True(ModeTools.FromCopilot(["read", "github"]).MayWrite);
        Assert.True(ModeTools.FromCopilot(["github/create_issue"]).MayWrite);
        Assert.False(ModeTools.FromCopilot(["read"]).Allows("debug_inspect"));   // its evaluate runs code in the program
        Assert.True(ModeTools.FromCopilot(["execute"]).Allows("debug_inspect"));
    }

    [Fact]
    public void AFamilyTurnedOff_ReadsNoneOfItsModes_AndASubAgentIsNoMode()
    {
        Write(".github/agents/helper.agent.md", "---\nname: Helper\nuser-invocable: false\n---\nOnly for other agents.\n");
        Write(".github/agents/writer.agent.md", "---\nname: Writer\n---\nWrite.\n");
        File.Copy(Fixture("batfish.roomodes"), Path.Combine(_root, ".roomodes"));

        var onlyAgents = ChatModes.Discover(_root, null, new HashSet<RepoInstructionFamily> { RepoInstructionFamily.Agents });
        Assert.Empty(onlyAgents.Modes);

        var all = ChatModes.Discover(_root, null);
        Assert.Contains(all.Modes, m => m.Name == "Writer");
        Assert.DoesNotContain(all.Modes, m => m.Name == "Helper");
        Assert.Contains(all.Notes, n => n.Kind == ChatModeNoteKind.SubAgentOnly && n.Source == ".github/agents/helper.agent.md");
    }

    [Fact]
    public void AnUnreadableFile_IsNamedWithItsCause_AndTheOthersAreStillRead()
    {
        Write(".inferpal/modes/broken.md", "---\nname: Broken\ntools:\n\t- read\n---\nBody.\n");
        Write(".inferpal/modes/fine.md", "---\nname: Fine\n---\nBody.\n");

        var catalog = ChatModes.Discover(_root, null);

        Assert.Equal("Fine", Assert.Single(catalog.Modes).Name);
        var note = Assert.Single(catalog.Notes);
        Assert.Equal(ChatModeNoteKind.Unreadable, note.Kind);
        Assert.Equal(".inferpal/modes/broken.md", note.Source);
        Assert.False(string.IsNullOrWhiteSpace(note.Detail));
    }

    [Fact]
    public void ARooModesOwnRules_FollowItsInstructions_InTheOrderRooReadsThem()
    {
        Write(".roomodes", "customModes:\n  - slug: docs\n    name: Docs\n    roleDefinition: You write docs.\n    groups: [read]\n" +
                          "  - slug: other\n    name: Other\n    roleDefinition: Other.\n    groups: [read]\n");
        Write(".roo/rules-docs/b.md", "Rule B.");
        Write(".roo/rules-docs/A.md", "Rule A.");
        Write(".roorules-other", "Other's own rule.");

        var catalog = ChatModes.Discover(_root, null);

        Assert.Equal("You write docs.\n\nRule A.\n\nRule B.", catalog.Find("Docs")!.Instructions);
        Assert.Equal("Other.\n\nOther's own rule.", catalog.Find("Other")!.Instructions);
    }

    [Fact]
    public void ToolsANameCannotWiden_AndExecuteKeepsTheUsersOwnTools()
    {
        var readOnly = ModeTools.FromCopilot(["read", "todo", "someUnknownThing"]);
        Assert.True(readOnly.Allows("read_file"));
        foreach (var no in new[] { "write_file", "run_command", "deploy", "mcp__github__list_issues" })
            Assert.False(readOnly.Allows(no), no);
        Assert.Contains("todo", readOnly.NotCarried);

        var execute = ModeTools.FromCopilot(["execute"]);
        Assert.True(execute.Allows("deploy"));         // a tool of the user's own: a command
        Assert.True(execute.Allows("run_tests"));
        Assert.False(execute.Allows("write_file"));

        var github = ModeTools.FromCopilot(["github/*", "read/readFile"]);
        Assert.True(github.Allows("mcp__github__list_issues"));
        Assert.False(github.Allows("mcp__jira__list_issues"));
        Assert.True(github.Allows("tool_search"));
        Assert.True(github.Allows("read_file"));
        Assert.False(github.Allows("list_files"));      // read/readFile is one tool, never the family
    }

    [Fact]
    public void EveryRegisteredBuiltInTool_IsInAFamily()
    {
        var config   = new InferpalConfig();
        var client   = new FakeInferenceProvider();
        var editor   = new NullEditorSurface();
        var approval = new NoopApproval();
        var registry = new ToolRegistry(editor, approval, config, new ProjectIndexService(client, config, new LspSemanticProvider()),
                                        client, new ProjectMapService(editor), new McpToolService(config, approval),
                                        new DocsIndexService(client, config), new OpenDocumentOverlay(), new NullDebugSession());

        Assert.True(registry.BuiltInNames.Count > 20);   // witness: the registry's own list was read
        // Left out, a built-in tool would read as a tool of the user's and be kept only with execute.
        Assert.All(registry.BuiltInNames.Where(n => n != "tool_search"), n => Assert.True(ModeTools.IsBuiltIn(n), n));
    }

    private sealed class FakeRegistry : IToolRegistry
    {
        public List<string> Executed { get; } = [];

        public IReadOnlyList<ToolDefinition> Definitions =>
        [
            Def("read_file"), Def("write_file"), Def("apply_edits"), Def("run_command"), Def("insert_at_cursor"), Def("deploy"),
        ];

        public Task<string> ExecuteAsync(string name, JsonElement args, CancellationToken ct)
        {
            Executed.Add(name);
            return Task.FromResult($"ran:{name}");
        }

        public DiffInfo? ConsumeDiff() => null;

        private static ToolDefinition Def(string name) => new("function", new ToolFunction(name, "d", new { }));
    }

    private static JsonElement Args(object value) => JsonSerializer.SerializeToElement(value);

    private ChatMode RooDocs() =>
        new("Docs Writer", "", "", [], ModeTools.FromRoo([("read", null, null, false), ("edit", @"\.(md|mdx)$", "Markdown files only", false)]),
            null, ".roomodes (docs)", ChatModeOrigin.Roo, []);

    [Fact]
    public void AToolOutsideTheMode_IsNeitherOfferedNorRun()
    {
        var inner = new FakeRegistry();
        IToolRegistry mode = new ModeToolRegistry(inner, RooDocs(), _root);

        Assert.Equal(["read_file", "write_file", "apply_edits", "insert_at_cursor"], mode.Definitions.Select(d => d.Function.Name));
        var refused = mode.ExecuteAsync("run_command", Args(new { command = "rm -rf ." }), CancellationToken.None).Result;
        Assert.StartsWith("Error: tool 'run_command' is not part of the 'Docs Writer' mode", refused, StringComparison.Ordinal);
        Assert.Empty(inner.Executed);
        Assert.DoesNotContain("deploy", mode.CallableNames);
    }

    [Fact]
    public void AnEditOutsideThePattern_IsRefusedBeforeTheApprovalCard_AndOneInsideReachesIt()
    {
        var inner = new FakeRegistry();
        IToolRegistry mode = new ModeToolRegistry(inner, RooDocs(), _root);

        var outside = mode.ExecuteAsync("write_file", Args(new { path = "src/cli.ts", content = "x" }), CancellationToken.None).Result;
        Assert.Contains("not written: src/cli.ts", outside, StringComparison.Ordinal);
        Assert.Contains(@"\.(md|mdx)$ (Markdown files only)", outside, StringComparison.Ordinal);
        var batch = mode.ExecuteAsync("apply_edits", Args(new { edits = new[] { new { path = "README.md" }, new { path = "src/a.ts" } } }),
                                      CancellationToken.None).Result;
        Assert.Contains("not written: src/a.ts", batch, StringComparison.Ordinal);
        var unnamed = mode.ExecuteAsync("insert_at_cursor", Args(new { text = "x" }), CancellationToken.None).Result;
        Assert.Contains("does not name every file it writes", unnamed, StringComparison.Ordinal);
        // Read as the tool reads it: a number is a name too, and a `..` climbs before the pattern judges.
        var number = mode.ExecuteAsync("write_file", Args(new { path = 2024, content = "x" }), CancellationToken.None).Result;
        Assert.Contains("not written: 2024", number, StringComparison.Ordinal);
        var climbed = mode.ExecuteAsync("write_file", Args(new { path = "docs/../src/cli.ts", content = "x" }), CancellationToken.None).Result;
        Assert.Contains("not written: src/cli.ts", climbed, StringComparison.Ordinal);
        var missing = mode.ExecuteAsync("write_file", Args(new { content = "x" }), CancellationToken.None).Result;
        Assert.Contains("does not name every file it writes", missing, StringComparison.Ordinal);
        Assert.Empty(inner.Executed);   // nothing reached the registry that asks

        var inside = mode.ExecuteAsync("write_file", Args(new { path = Path.Combine(_root, "docs", "guide.md"), content = "x" }),
                                       CancellationToken.None).Result;
        Assert.Equal("ran:write_file", inside);
    }

    [Fact]
    public void WritingAModesFile_AsksFirst()
    {
        foreach (var path in new[]
                 {
                     ".inferpal/modes/review.md", ".github/agents/review.agent.md", ".GitHub/Agents/x.md", ".roomodes",
                     "sub/.roomodes", ".roo/rules-docs/a.md", ".roo/rules-docs/deep/b.txt", ".roorules-docs", ".clinerules-docs",
                 })
            Assert.True(Services.Execution.AgentInstructionFiles.Targets(path), path);
        // Reference arm: neighbours that are no mode.
        foreach (var path in new[] { ".github/workflows/ci.md", ".inferpal/modes.md", "docs/agents/x.md", ".roo/rules-docs" })
            Assert.False(Services.Execution.AgentInstructionFiles.Targets(path), path);
    }
}
