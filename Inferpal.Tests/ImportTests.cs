using System.IO;
using System.Text.Json.Nodes;
using Inferpal.Config;
using Inferpal.Localization;
using Inferpal.Services.Commands;
using Inferpal.Services.Docs;
using Inferpal.Services.Import;
using Inferpal.Services.Mcp;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// <c>/import continue|roo</c>: every part of a source is either an item to write or a skip with its cause; the preview
/// writes nothing; an application writes only what is ticked, after a byte-identical copy of the settings.
/// </summary>
/// <remarks>The configurations here are written for the tests: the real ones the gate measured stay private, they belong
/// to their authors (<c>docs/probes/import-continue-roo/</c>).</remarks>
[Collection(CultureSerialCollection.Name)]
public sealed class ImportTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "inferpal-tests", "import-" + Guid.NewGuid().ToString("N"));
    private readonly string _home;
    private readonly string _commands;
    private readonly string _configFile;

    public ImportTests()
    {
        _home = Path.Combine(_dir, "home");
        _commands = Path.Combine(_dir, "appdata", "prompts");
        _configFile = Path.Combine(_dir, "appdata", "config.json");
        Directory.CreateDirectory(_home);
        Directory.CreateDirectory(Path.GetDirectoryName(_configFile)!);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private const string ContinueYaml = """
        %YAML 1.1
        ---
        name: My local setup
        version: 1.0.0
        schema: v1
        shared: &shared
          provider: openai
          apiBase: http://127.0.0.1:8080/v1
          apiKey: ${{ secrets.LOCAL_KEY }}
        models:
          - name: Main
            <<: *shared
            model: qwen3-coder-30b
            roles: [chat, edit, apply]
            defaultCompletionOptions:
              contextLength: 32768
              temperature: 0.2
            chatOptions:
              baseSystemMessage: Answer briefly.
          - name: Second
            <<: *shared
            model: devstral
            roles: [chat]
          - name: Tab
            <<: *shared
            model: qwen2.5-coder:1.5b
            roles: [autocomplete]
          - name: Cloud
            provider: anthropic
            model: claude-sonnet
          - name: Elsewhere
            provider: ollama
            model: nomic-embed-text
            roles: [embed]
        rules:
          - Always run the tests.
          - name: Python style
            rule: |
              Use type hints.
              # A heading, not a comment.
            globs: "**/*.py"
        prompts:
          - name: review
            description: Review the selection
            prompt: |
              Review this: {{{ input }}}
          - name: summarize
            prompt: Summarize the file.
        mcpServers:
          - name: fs
            command: npx
            args: [-y, "@modelcontextprotocol/server-filesystem", "."]
        docs:
          - name: Rust book
            startUrl: https://doc.rust-lang.org/book/
        context:
          - provider: code
        """;

    private const string ContinueJson = """
        {
          // A comment, as Continue's config.json allowed.
          "models": [
            { "title": "Llama", "provider": "ollama", "model": "llama3.1:8b", "contextLength": 16384, "systemMessage": "Be terse." },
            { "title": "GPT", "provider": "openai", "model": "gpt-4o", "apiKey": "not-a-key" }
          ],
          "tabAutocompleteModel": { "title": "Starcoder", "provider": "ollama", "model": "starcoder2:3b" },
          "embeddingsProvider": { "provider": "transformers.js" },
          "customCommands": [
            { "name": "test", "description": "Write unit tests for highlighted code",
              "prompt": "{{{ input }}}\n\nWrite a comprehensive set of unit tests for the selected code. It should setup, run tests that check for correctness including important edge cases, and teardown. Ensure that the tests are complete." }
          ],
          "slashCommands": [ { "name": "commit", "description": "Generate a git commit message" } ],
          "experimental": { "modelContextProtocolServers": [ { "transport": { "type": "stdio", "command": "uvx", "args": ["mcp-server-git"] } } ] },
          "allowAnonymousTelemetry": true,
          "disableIndexing": true,
        }
        """;

    private string Write(string name, string text)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, text);
        return path;
    }

    private InferpalConfig NewConfig()
    {
        var config = new InferpalConfig { SavePathForTests = _configFile };
        config.Save();
        return config;
    }

    private ImportContext Context(InferpalConfig config) => new(config, _home, _commands, _ => null);

    private ImportPlan ReadYaml(InferpalConfig? config = null)
    {
        Directory.CreateDirectory(Path.Combine(_home, ".continue"));
        File.WriteAllText(Path.Combine(_home, ".continue", ".env"), "# Continue's secrets\nLOCAL_KEY=\"abc123\"\n");
        return ContinueImport.Read(Write("config.yaml", ContinueYaml), Context(config ?? NewConfig()));
    }

    private static T Effect<T>(ImportPlan plan) where T : ImportEffect => plan.Items.Select(i => i.Effect).OfType<T>().Single();

    // ── Continue, config.yaml ───────────────────────────────────────────────

    [Fact]
    public void AContinueYaml_KeepsTheServerOfItsFirstChatModel_AndItsRoles_ThroughTheAnchor()
    {
        var plan = ReadYaml();

        var server = Effect<SetBackend>(plan);
        Assert.Equal("openai-compatible", server.Provider);
        Assert.Equal("http://127.0.0.1:8080/v1", server.BaseUrl);
        Assert.Equal("abc123", server.ApiKey);    // the secret, found where Continue looks: ~/.continue/.env
        var models = plan.Items.Select(i => i.Effect).OfType<SetModel>().ToDictionary(m => m.Role, m => m.Model);
        Assert.Equal("qwen3-coder-30b", models[ImportItemKind.ChatModel]);
        Assert.Equal("qwen2.5-coder:1.5b", models[ImportItemKind.CompletionModel]);
        Assert.False(models.ContainsKey(ImportItemKind.EditModel));       // the chat model already edits
        Assert.Equal(32768, Effect<SetContextWindow>(plan).Tokens);
    }

    [Fact]
    public void AContinueYaml_NamesWhatItLeavesOut_WithTheReason()
    {
        var plan = ReadYaml();
        ImportSkipCause CauseOf(string path) => plan.Skipped.First(s => s.Path == path).Cause;

        Assert.Equal(ImportSkipCause.OtherChatModel, CauseOf("models[1].model"));          // a second chat model
        Assert.Equal(ImportSkipCause.HostedProvider, CauseOf("models[3].provider"));       // never a hosted provider
        Assert.Equal(ImportSkipCause.OtherServer, CauseOf("models[4].model"));             // one server
        Assert.Contains(plan.Skipped, s => s.Path == "models[0].roles" && s.Cause == ImportSkipCause.RoleNotInInferpal && s.Detail == "apply");
        Assert.Contains(plan.Skipped, s => s.Path == "models[0].defaultCompletionOptions" && s.Cause == ImportSkipCause.Sampling);
        Assert.Equal(ImportSkipCause.UnknownKey, CauseOf("shared"));
        Assert.Equal(ImportSkipCause.FileIdentity, CauseOf("schema"));
        Assert.Equal(ImportSkipCause.ContextProviders, CauseOf("context[0]"));
    }

    [Fact]
    public void AContinueYaml_CarriesRulesPromptsServersAndDocs()
    {
        var plan = ReadYaml();

        var instructions = plan.Items.Select(i => i.Effect).OfType<AddInstructions>().ToList();
        Assert.Contains(instructions, i => i.Text == "Always run the tests.");
        Assert.Contains(instructions, i => i.Text.Contains("`**/*.py`") && i.Text.Contains("# A heading, not a comment."));
        Assert.Contains(instructions, i => i.Text == "Answer briefly.");
        var scoped = plan.Items.Single(i => i.Effect is AddInstructions a && a.Text.Contains("type hints"));
        Assert.Contains(scoped.Notes, n => n.Kind == ImportNote.ScopeInText);

        var commands = plan.Items.Where(i => i.Effect is WriteCommand).ToList();
        var review = commands.Single(i => ((WriteCommand)i.Effect).Name == "review-continue");   // /review is Inferpal's
        Assert.Contains(review.Notes, n => n.Kind == ImportNote.Renamed && n.Detail == "/review");
        Assert.Equal("Review this: {args}", ((WriteCommand)review.Effect).Text);
        Assert.Contains(commands, i => ((WriteCommand)i.Effect).Name == "summarize");

        Assert.Equal("fs", Effect<AddMcpServer>(plan).Server.Name);
        Assert.Equal("https://doc.rust-lang.org/book/", Effect<AddDocSite>(plan).StartUrl);
    }

    /// <summary>
    /// The gate's G1, on these files: every top-level key, every element of a top-level list and every key of a model
    /// has an outcome — an item that carries it, or a skip that names why.
    /// </summary>
    [Theory]
    [InlineData("yaml")]
    [InlineData("json")]
    public void EveryPathOfAContinueFile_HasAnOutcome(string format)
    {
        var plan = format == "yaml" ? ReadYaml() : ContinueImport.Read(Write("config.json", ContinueJson), Context(NewConfig()));
        var root = format == "yaml"
            ? MiniYaml.Parse(ContinueYaml)!.AsObject()
            : JsonNode.Parse(ContinueJson, documentOptions: new System.Text.Json.JsonDocumentOptions
              { CommentHandling = System.Text.Json.JsonCommentHandling.Skip, AllowTrailingCommas = true })!.AsObject();
        var outcomes = plan.Items.SelectMany(i => i.SourcePaths).Concat(plan.Skipped.Select(s => s.Path)).ToList();
        bool Covered(string path) => outcomes.Any(o => o == path || o.StartsWith(path + ".") || o.StartsWith(path + "["));

        var paths = new List<string>();
        foreach (var (key, value) in root)
        {
            paths.Add(key);
            if (value is JsonArray list)
                for (var i = 0; i < list.Count; i++)
                {
                    paths.Add($"{key}[{i}]");
                    if (key == "models" && list[i] is JsonObject model) paths.AddRange(model.Select(p => $"{key}[{i}].{p.Key}"));
                }
            if (value is JsonObject one && key is "tabAutocompleteModel" or "embeddingsProvider")
                paths.AddRange(one.Select(p => $"{key}.{p.Key}"));
        }
        Assert.True(paths.Count > 20, "the enumeration read the file");              // a witness: the walk is alive
        Assert.Empty(paths.Where(p => !Covered(p)));
        Assert.DoesNotContain(plan.Skipped, s => !Enum.IsDefined(s.Cause));
    }

    /// <summary>
    /// ⚠ A Continue MCP server's <c>cwd</c> reaches the settings. The card showed it carried, and the settings dropped it:
    /// the server then started in the editor host's folder.
    /// </summary>
    [Fact]
    public void AnImportedServersWorkingDirectory_ReachesTheSettings()
    {
        const string yaml = """
            name: tools
            version: 1.0.0
            schema: v1
            mcpServers:
              - name: local
                command: python
                args: [server.py]
                cwd: /srv/mcp
            """;
        var config = NewConfig();
        var plan = ContinueImport.Read(Write("config.yaml", yaml), Context(config));
        var server = plan.Items.Single(i => i.Effect is AddMcpServer);
        Assert.Empty(ImportApplier.Apply(plan, new HashSet<string> { server.Id }, config, _configFile, _commands, DateTime.Now).Failed);

        Assert.Equal("/srv/mcp", Assert.Single(McpServerConfig.Parse(config.McpServersJson)).WorkingDirectory);
    }

    /// <summary>
    /// ⚠ A command an earlier import renamed (/review is Inferpal's: /review-continue) is already in place the next time.
    /// Looked for under its own name only, it was offered again on every import, ticked, as /review-continue-2, -3…
    /// </summary>
    [Fact]
    public void ARenamedCommand_IsAlreadyInPlace_TheSecondTime()
    {
        var config = NewConfig();
        var plan = ReadYaml(config);
        var review = plan.Items.Single(i => i.Effect is WriteCommand { Name: "review-continue" });   // WITNESS
        Assert.Empty(ImportApplier.Apply(plan, new HashSet<string> { review.Id }, config, _configFile, _commands, DateTime.Now).Failed);

        var again = ReadYaml(config);
        Assert.DoesNotContain(again.Items, i => i.Effect is WriteCommand { Name: var n } && n.StartsWith("review", StringComparison.Ordinal));
        Assert.Contains(again.Skipped, s => s.Cause == ImportSkipCause.AlreadyConfigured && s.Detail == "/review");
        // Reference arm: the commands not written yet are still offered.
        Assert.Contains(again.Items, i => i.Effect is WriteCommand { Name: "summarize" });
    }

    /// <summary>
    /// ⚠ An imported documentation source never replaces one of the user's. Its title is the imported file's: under a
    /// title the user's own source held — another address —, the apply replaced that source while the card said
    /// "added". It takes a free id; the same address already there stays "already in place".
    /// </summary>
    [Fact]
    public void AnImportedDocSource_UnderTheTitleOfTheUsersOwn_IsAddedBesideIt()
    {
        var config = NewConfig();
        var mine = new DocSite("rust-book", "Rust book", "https://mirror.corp.example/rust/");
        config.DocSitesJson = DocSite.Serialize([mine]);
        var plan = ReadYaml(config);
        var docs = plan.Items.Single(i => i.Effect is AddDocSite);   // WITNESS: offered, another address

        Assert.Empty(ImportApplier.Apply(plan, new HashSet<string> { docs.Id }, config, _configFile, _commands, DateTime.Now).Failed);
        var sites = DocSite.Parse(config.DocSitesJson);
        Assert.Contains(mine, sites);
        var added = Assert.Single(sites, s => s.StartUrl == "https://doc.rust-lang.org/book/");
        Assert.NotEqual(mine.Id, added.Id);

        // Reference arm: imported again, the address is there — already in place, nothing offered.
        Assert.DoesNotContain(ReadYaml(config).Items, i => i.Effect is AddDocSite);
    }

    // ── Continue, config.json ───────────────────────────────────────────────

    /// <summary>
    /// ⚠ A server the source left unnamed is named after the package its launcher runs. Named after the program, two
    /// servers <c>npx</c> starts were both "npx": the second was refused at apply, "added in the meantime" — false. One
    /// listed twice is named once; a configured server of another kind keeps its name, the new one takes a free one.
    /// </summary>
    [Fact]
    public void UnnamedServers_AreNamedAfterThePackageTheyRun_AndEachOneApplies()
    {
        const string json = """
            { "experimental": { "modelContextProtocolServers": [
              { "transport": { "type": "stdio", "command": "npx", "args": ["-y", "@modelcontextprotocol/server-filesystem", "/data"] } },
              { "transport": { "type": "stdio", "command": "npx", "args": ["-y", "@modelcontextprotocol/server-github@1.2.0"] } },
              { "transport": { "type": "stdio", "command": "npx", "args": ["-y", "@modelcontextprotocol/server-github@1.2.0"] } },
              { "transport": { "type": "stdio", "command": "uvx", "args": ["mcp-server-git==0.6"] } } ] } }
            """;
        var config = NewConfig();
        config.McpServersJson = """{ "mcpServers": { "mcp-server-git": { "command": "docker", "args": ["run", "git-mcp"] } } }""";
        var plan = ContinueImport.Read(Write("config.json", json), Context(config));

        Assert.Equal(["server-filesystem", "server-github", "mcp-server-git-2"],
                     plan.Items.Select(i => i.Effect).OfType<AddMcpServer>().Select(s => s.Server.Name));
        Assert.Contains(plan.Skipped, s => s.Path == "experimental.modelContextProtocolServers[2]"
                                           && s.Cause == ImportSkipCause.SameNameEarlier && s.Detail == "server-github");

        var outcome = ImportApplier.Apply(plan, plan.Items.Where(i => i.Kind == ImportItemKind.McpServer).Select(i => i.Id).ToHashSet(),
                                          config, _configFile, _commands, DateTime.Now);
        Assert.Empty(outcome.Failed);
        Assert.Equal(4, McpServerConfig.Parse(config.McpServersJson).Count);   // the user's own, and the three

        // Reference arm: imported again, each of the four listings is a server already in place.
        var again = ContinueImport.Read(Write("config.json", json), Context(config));
        Assert.DoesNotContain(again.Items, i => i.Kind == ImportItemKind.McpServer);
        Assert.Equal(4, again.Skipped.Count(s => s.Cause == ImportSkipCause.AlreadyConfigured));
    }

    /// <summary>
    /// ⚠ The transport a Continue server declares travels with it, in both formats: SSE is imported and says so when it
    /// fails, WebSocket — which nothing in Inferpal speaks — is named and left out. The old format's <c>transport.type</c>
    /// was read by nobody, and the current one's <c>type: websocket</c> imported a server that could only fail.
    /// </summary>
    [Fact]
    public void AServersDeclaredTransport_IsKept_AndWebSocketIsLeftOutByName()
    {
        const string json = """
            { "experimental": { "modelContextProtocolServers": [
              { "transport": { "type": "sse", "url": "https://mcp.example.com/sse" } },
              { "transport": { "type": "websocket", "url": "ws://localhost:9000" } },
              { "transport": { "type": "stdio", "command": "uvx", "args": ["mcp-server-git"] } } ] } }
            """;
        var plan = ContinueImport.Read(Write("config.json", json), Context(NewConfig()));

        var servers = plan.Items.Select(i => i.Effect).OfType<AddMcpServer>().Select(s => s.Server).ToList();
        Assert.True(Assert.Single(servers, s => s.Url == "https://mcp.example.com/sse").DeclaresSse);
        Assert.False(Assert.Single(servers, s => s.Command == "uvx").DeclaresSse);   // reference arm: stdio declares nothing
        Assert.DoesNotContain(servers, s => s.Url == "ws://localhost:9000");
        Assert.Contains(plan.Skipped, s => s.Path == "experimental.modelContextProtocolServers[1]"
                                           && s.Cause == ImportSkipCause.NoEquivalent && s.Detail == "type: websocket");

        // The current format, and a configuration read as it is written.
        var yaml = ContinueImport.Read(Write("config.yaml", """
            name: x
            version: 1.0.0
            schema: v1
            mcpServers:
              - name: live
                type: websocket
                url: ws://localhost:9000
            """), Context(NewConfig()));
        Assert.DoesNotContain(yaml.Items, i => i.Kind == ImportItemKind.McpServer);
        Assert.Empty(McpServerConfig.Parse("""{ "live": { "type": "websocket", "url": "ws://localhost:9000" } }""", out var rejected));
        Assert.Equal(McpServerConfig.WebSocketRefusal, Assert.Single(rejected).Reason);
    }

    /// <summary>
    /// ⚠ A relative path typed after <c>/import</c> is read from the workspace. Resolved against the process's folder, it
    /// was "not found" in Visual Studio — whose extension host keeps the folder it started in — right beside the file.
    /// </summary>
    [Fact]
    public void ARelativePath_IsReadFromTheWorkspace_NotFromTheProcesssFolder()
    {
        var workspace = Path.Combine(_dir, "repo");
        var file = Path.Combine(workspace, "settings", "continue.json");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, ContinueJson);
        var context = Context(NewConfig()) with { WorkspaceRoot = workspace };

        var relative = ImportCommandHandler.Handle(["/import", "continue", "settings/continue.json"], context, _configFile, DateTime.Now);
        Assert.NotNull(relative.Card);
        Assert.Equal(file, relative.Card!.File);

        // Reference arm: an absolute path is itself, workspace or not.
        Assert.NotNull(ImportCommandHandler.Handle(["/import", "continue", file], context with { WorkspaceRoot = null },
                                                   _configFile, DateTime.Now).Card);
    }

    /// <summary>Both editors give the import their workspace (Visual Studio's index root, the host's session root).</summary>
    [Fact]
    public void BothEditors_GiveTheImportTheirWorkspace()
    {
        string Code(params string[] path) => ConventionCoverageTests.CodeOnly(Path.Combine([ConventionCoverageTests.RepoRoot(), .. path]));
        Assert.Contains("ImportContext.For(_config, _indexService.RootDir)",
                        Code("Inferpal", "ToolWindow", "InferpalToolWindowData.SlashCommands.cs"), StringComparison.Ordinal);
        Assert.Contains("ImportContext.For(s.Config, s.RootDir)", Code("Inferpal.Host", "HostSlashCommands.cs"), StringComparison.Ordinal);
    }

    /// <summary>
    /// ⚠ Imported instructions are APPENDED to the custom system prompt. Imported again, the same file offered them again
    /// — every rule then went into every prompt twice, three times —, where a server or a command is "already in place".
    /// </summary>
    [Fact]
    public void ImportedInstructions_AreAlreadyInPlace_TheSecondTime()
    {
        var config = NewConfig();
        var plan = ContinueImport.Read(Write("config.json", ContinueJson), Context(config));
        var rules = plan.Items.Where(i => i.Kind == ImportItemKind.Instructions).ToList();
        Assert.NotEmpty(rules);   // WITNESS
        var ids = rules.Select(i => i.Id).ToHashSet();
        ImportApplier.Apply(plan, ids, config, _configFile, _commands, DateTime.Now);

        // Saved by the settings form since, with Windows line endings: the same text.
        config.CustomSystemPrompt = config.CustomSystemPrompt.Replace("\n", "\r\n", StringComparison.Ordinal);
        var once = config.CustomSystemPrompt;
        var again = ContinueImport.Read(Write("config.json", ContinueJson), Context(config));
        Assert.DoesNotContain(again.Items, i => i.Kind == ImportItemKind.Instructions);
        foreach (var rule in rules)
            Assert.Contains(again.Skipped, s => s.Cause == ImportSkipCause.AlreadyConfigured && s.Detail == ((AddInstructions)rule.Effect).Title);

        // The first card applied once more: nothing written twice.
        ImportApplier.Apply(plan, ids, config, _configFile, _commands, DateTime.Now);
        Assert.Equal(once, config.CustomSystemPrompt);
    }

    [Fact]
    public void AContinueJson_WithComments_IsRead_AndContinuesOwnDefaultCommandIsOfferedUnticked()
    {
        var plan = ContinueImport.Read(Write("config.json", ContinueJson), Context(NewConfig()));

        var test = plan.Items.Single(i => i.Effect is WriteCommand { Name: "test-continue" });
        Assert.False(test.Checked);
        Assert.Contains(test.Notes, n => n.Kind == ImportNote.ContinueDefault);
        Assert.Equal("llama3.1:8b", plan.Items.Select(i => i.Effect).OfType<SetModel>().Single(m => m.Role == ImportItemKind.ChatModel).Model);
        Assert.Equal(16384, Effect<SetContextWindow>(plan).Tokens);
        Assert.Contains(plan.Items, i => i.Effect is AddInstructions { Text: "Be terse." });
        Assert.Equal("mcp-server-git", Effect<AddMcpServer>(plan).Server.Name);   // the package uvx runs, not "uvx"
        Assert.False(Effect<SetIndexing>(plan).Enabled);
        Assert.Contains(plan.Skipped, s => s.Path == "models[1].model" && s.Cause == ImportSkipCause.HostedProvider && s.Detail == "openai");
        Assert.Contains(plan.Skipped, s => s.Path == "embeddingsProvider.provider" && s.Cause == ImportSkipCause.NoEquivalent);
        Assert.Contains(plan.Skipped, s => s.Path == "slashCommands[0]" && s.Detail == "/commit");
        // The server is Ollama at its own default address, the factory one: nothing to write, and said so.
        Assert.DoesNotContain(plan.Items, i => i.Kind == ImportItemKind.Backend);
        Assert.Contains(plan.Skipped, s => s.Path == "models[0].provider" && s.Cause == ImportSkipCause.AlreadyConfigured);
    }

    [Theory]
    [InlineData("openai", "https://api.openai.com/v1", true)]
    [InlineData("openai", "https://my-gateway.example.com/v1", false)]     // a company's own gateway: a server they run
    [InlineData("openai", null, true)]                                        // no apiBase: OpenAI's own API
    [InlineData("anthropic", null, true)]
    [InlineData("lmstudio", null, false)]
    [InlineData("llama.cpp", "http://gpu-box:8080", false)]
    public void TheServerIsJudged_NeverTheModelsName(string provider, string? apiBase, bool hosted) =>
        Assert.Equal(hosted, ImportServers.FromContinue(provider, apiBase) is null);

    // ── Roo Code ────────────────────────────────────────────────────────────

    private static string RooExport(bool executeAllowed, string modes = "[]") => $$"""
        {
          "providerProfiles": {
            "currentApiConfigName": "local",
            "apiConfigs": {
              "cloud": { "apiProvider": "openrouter", "openRouterModelId": "anthropic/claude-sonnet-4", "id": "a1" },
              "local": { "apiProvider": "ollama", "ollamaModelId": "devstral:24b", "ollamaBaseUrl": "http://localhost:11434", "id": "b2" }
            },
            "modeApiConfigs": { "code": "b2" }
          },
          "globalSettings": {
            "customInstructions": "Prefer small diffs.",
            "customModes": {{modes}},
            "autoApprovalEnabled": true,
            "alwaysAllowExecute": {{(executeAllowed ? "true" : "false")}},
            "alwaysAllowWrite": true,
            "allowedCommands": ["git log", "npm test"],
            "deniedCommands": ["rm -rf"],
            "language": "fr",
            "soundEnabled": false
          }
        }
        """;

    [Fact]
    public void ARooExport_OffersAllowedCommandsUnticked_AndDeniedOnesTicked()
    {
        var plan = RooImport.Read(Write("roo.json", RooExport(executeAllowed: true)), Context(NewConfig()));

        var rules = plan.Items.Where(i => i.Kind == ImportItemKind.PermissionRule).ToList();
        Assert.Equal(3, rules.Count);
        Assert.All(rules.Where(r => ((AddPermissionRule)r.Effect).Rule.StartsWith("allow")), r =>
        {
            Assert.False(r.Checked);
            Assert.Contains(r.Notes, n => n.Kind == ImportNote.RunsWithoutAsking);
        });
        var deny = rules.Single(r => ((AddPermissionRule)r.Effect).Rule.StartsWith("deny"));
        Assert.True(deny.Checked);
        Assert.Equal(@"deny run_command ^rm\ -rf(\s|$)", ((AddPermissionRule)deny.Effect).Rule);
        // The profile a user runs is kept; the hosted one is named, never imported; Roo's approval switches stay.
        Assert.Equal("devstral:24b", plan.Items.Select(i => i.Effect).OfType<SetModel>().Single().Model);
        Assert.Contains(plan.Skipped, s => s.Path.StartsWith("providerProfiles.apiConfigs[cloud]") && s.Cause == ImportSkipCause.HostedProvider);
        Assert.Contains(plan.Skipped, s => s.Path == "globalSettings.alwaysAllowWrite" && s.Cause == ImportSkipCause.AutoApproval);
        Assert.Equal("fr", Effect<SetLanguage>(plan).Code);
    }

    /// <summary>With Roo's automatic approval of commands off, its allowed list did nothing: importing it as rules would
    /// grant more than Roo did.</summary>
    [Fact]
    public void ARooExportWhoseCommandApprovalWasOff_OffersNoAllowRule()
    {
        var plan = RooImport.Read(Write("roo.json", RooExport(executeAllowed: false)), Context(NewConfig()));

        Assert.DoesNotContain(plan.Items, i => i.Effect is AddPermissionRule r && r.Rule.StartsWith("allow"));
        Assert.Contains(plan.Skipped, s => s.Path == "globalSettings.allowedCommands[1]" && s.Cause == ImportSkipCause.AutoApprovalWasOff);
    }

    [Fact]
    public void ARooCustomMode_BecomesACommand_RenamedAroundInferpalsOwn_WithoutItsToolGroups()
    {
        const string modes = """
            [ { "slug": "review", "name": "Strict reviewer", "roleDefinition": "You review code.",
                "customInstructions": "Be strict.", "groups": ["read"] } ]
            """;
        var plan = RooImport.Read(Write("roo.json", RooExport(true, modes)), Context(NewConfig()));

        var mode = plan.Items.Single(i => i.Effect is WriteCommand);
        var command = (WriteCommand)mode.Effect;
        Assert.Equal("review-roo", command.Name);
        Assert.Equal("You review code.\n\nBe strict.\n\n{args}", command.Text);
        Assert.Contains(mode.Notes, n => n.Kind == ImportNote.Renamed);
        Assert.Contains(mode.Notes, n => n.Kind == ImportNote.ModeToolsNotKept);
    }

    // ── Applying ────────────────────────────────────────────────────────────

    [Fact]
    public void Applying_CopiesTheSettingsAside_ThenWritesOnlyWhatIsTicked()
    {
        var config = NewConfig();
        var before = File.ReadAllBytes(_configFile);
        var plan = ReadYaml(config);
        var chat = plan.Items.Single(i => i.Kind == ImportItemKind.ChatModel);
        var summarize = plan.Items.Single(i => i.Effect is WriteCommand { Name: "summarize" });

        var outcome = ImportApplier.Apply(plan, new HashSet<string> { chat.Id, summarize.Id }, config, _configFile, _commands,
                                          new DateTime(2026, 10, 10, 18, 0, 0));

        Assert.Empty(outcome.Failed);
        Assert.Equal(before, File.ReadAllBytes(outcome.Backup!));     // the way back, byte for byte
        Assert.Equal("qwen3-coder-30b", config.DefaultModel);
        Assert.Equal("ollama", config.Provider);                       // the server was not ticked
        Assert.Equal(new InferpalConfig().CustomSystemPrompt, config.CustomSystemPrompt);
        Assert.Equal(["summarize.md"], Directory.GetFiles(_commands).Select(Path.GetFileName));
        Assert.False(outcome.ServerChanged);
    }

    /// <summary>The MCP list is merged entry by entry: an entry Inferpal's reader skips is the user's, and stays.</summary>
    [Fact]
    public void ImportingAServer_KeepsEveryEntryAlreadyThere_EvenOneTheReaderSkips()
    {
        var config = NewConfig();
        config.McpServersJson = """{ "mcpServers": { "mine": { "command": "x" }, "odd": { "notAField": true } } }""";
        var plan = ReadYaml(config);
        var server = plan.Items.Single(i => i.Kind == ImportItemKind.McpServer);

        ImportApplier.Apply(plan, new HashSet<string> { server.Id }, config, _configFile, _commands, DateTime.Now);

        var map = JsonNode.Parse(config.McpServersJson)!["mcpServers"]!.AsObject();
        Assert.Equal(["mine", "odd", "fs"], map.Select(p => p.Key));
        Assert.True(map["odd"]!["notAField"]!.GetValue<bool>());
        Assert.True(config.McpEnabled);
    }

    [Fact]
    public void ACommandFileThatAppearedSinceThePreview_IsNeverOverwritten()
    {
        var config = NewConfig();
        var plan = ReadYaml(config);
        var summarize = plan.Items.Single(i => i.Effect is WriteCommand { Name: "summarize" });
        Directory.CreateDirectory(_commands);
        File.WriteAllText(Path.Combine(_commands, "summarize.md"), "mine");

        var outcome = ImportApplier.Apply(plan, new HashSet<string> { summarize.Id }, config, _configFile, _commands, DateTime.Now);

        Assert.Single(outcome.Failed);
        Assert.Equal("mine", File.ReadAllText(Path.Combine(_commands, "summarize.md")));
    }

    // ── The command ─────────────────────────────────────────────────────────

    private static Dictionary<string, string> Fingerprint(string folder) =>
        Directory.GetFiles(folder, "*", SearchOption.AllDirectories)
                 .ToDictionary(f => f, f => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(f))));

    [Fact]
    public void ThePreview_WritesNothing_AndShowsTheCard()
    {
        var config = NewConfig();
        var file = Write("config.yaml", ContinueYaml);
        var before = Fingerprint(_dir);

        var result = ImportCommandHandler.Handle(["/import", "continue", file], Context(config), _configFile, DateTime.Now);

        Assert.Equal(before, Fingerprint(_dir));
        Assert.NotNull(result.Card);
        Assert.False(result.SaveConfig);
        Assert.Equal(Enumerable.Range(1, result.Card!.Items.Count), result.Card.Items.Select(i => i.Number));
        Assert.Contains(Strings.ImportApplyHint("continue"), result.Message);
        Assert.Contains(result.Card.Skipped, g => g.Reason.StartsWith(Strings.ImportSkipHostedProvider));
    }

    [Fact]
    public void APartLeftOutForTwoReasons_IsCountedOnce_InTheSummaryAndInTheFold()
    {
        var plan = ReadYaml();
        var card = ImportPreview.Card(plan);
        var parts = plan.Skipped.Select(s => s.Path).Distinct(StringComparer.Ordinal).Count();

        // WITNESS: a part sits in two groups (models[0].roles — the edit role already held, apply not in Inferpal).
        Assert.True(card.Skipped.Sum(g => g.Paths.Count) > parts, "no part sits in two groups: the case is not exercised");
        Assert.Equal(Strings.ImportPreviewSummary(card.Items.Count, parts), card.Summary);
        Assert.Equal(Strings.ImportSkippedTitle + " (" + parts + ")", card.SkippedTitle);
    }

    [Fact]
    public void BothEditors_ShowTheFoldsTitle_AsTheCoreCountedIt()
    {
        var root = ConversationPersistenceSilenceTests.RepoRoot();
        var vs = ConventionCoverageTests.CodeOnly(Path.Combine(root, "Inferpal", "ToolWindow", "ChatMessageItem.cs"));
        var webview = SettingsSchemaDriftTests.NeutralizeTypeScriptComments(
            File.ReadAllText(Path.Combine(root, "vscode", "src", "webview", "main.ts")));

        Assert.Contains("ImportSkippedTitle = card.SkippedTitle,", vs);
        Assert.Contains("fold.textContent = card.skippedTitle;", webview);
    }

    [Fact]
    public void Apply_WithNumbers_WritesThoseItems_AndAnUnknownNumberWritesNothing()
    {
        var config = NewConfig();
        var file = Write("config.yaml", ContinueYaml);
        var card = ImportCommandHandler.Handle(["/import", "continue", file], Context(config), _configFile, DateTime.Now).Card!;
        var chat = card.Items.Single(i => i.Target == Strings.ImportTargetChatModel);

        var refused = ImportCommandHandler.Handle(["/import", "continue", file, "apply", "99"], Context(config), _configFile, DateTime.Now);
        Assert.Equal(Strings.ImportSelectionUnknown("99"), refused.Message);
        Assert.False(refused.SaveConfig);

        var applied = ImportCommandHandler.Handle(["/import", "continue", file, "apply", chat.Number.ToString()],
                                                  Context(config), _configFile, DateTime.Now);
        Assert.True(applied.SaveConfig);
        Assert.StartsWith(Strings.ImportApplied(1), applied.Message);
        Assert.Equal("qwen3-coder-30b", config.DefaultModel);
    }

    /// <summary>
    /// A key belongs to the host it was typed for. Imported without one (here a Continue secret that is not found), the
    /// server kept the key in place, and the next request sent the company gateway's key to the imported machine.
    /// </summary>
    [Fact]
    public void AServerOnAnotherHost_DoesNotInheritTheKeyInPlace()
    {
        var config = NewConfig();
        config.Provider = "openai-compatible";
        config.BaseUrl  = "https://gateway.corp.example/v1";
        config.ApiKey   = "K-gw";   // short on purpose: a fixture, not a secret (the publication guard reads quoted values of six characters or more)
        var plan = ContinueImport.Read(Write("config.yaml", ContinueYaml), Context(config));   // no .env: the secret is missing

        var backend = plan.Items.Single(i => i.Kind == ImportItemKind.Backend);
        Assert.Equal("", ((SetBackend)backend.Effect).ApiKey);
        Assert.Contains(backend.Notes, n => n.Kind == ImportNote.KeyNotCarried && n.Detail == "gateway.corp.example");
        Assert.Contains(Strings.ImportNoteKeyNotCarried("gateway.corp.example"),
                        ImportPreview.Card(plan).Items.Single(i => i.Target == Strings.ImportTargetServer).Notes);

        ImportApplier.Apply(plan, new HashSet<string> { backend.Id }, config, _configFile, _commands, DateTime.Now);

        Assert.Equal("http://127.0.0.1:8080/v1", config.BaseUrl);
        Assert.Equal("", config.ApiKey);
    }

    [Fact]
    public void TheSameHost_KeepsItsKey()
    {
        // Reference arm: another provider on the same host and port is the same machine, and its key stays.
        var config = NewConfig();
        config.Provider = "lmstudio";
        config.BaseUrl  = "http://127.0.0.1:8080";
        config.ApiKey   = "K-lan";
        var plan = ContinueImport.Read(Write("config.yaml", ContinueYaml), Context(config));

        var backend = plan.Items.Single(i => i.Kind == ImportItemKind.Backend);
        Assert.Null(((SetBackend)backend.Effect).ApiKey);
        Assert.DoesNotContain(backend.Notes, n => n.Kind == ImportNote.KeyNotCarried);
        ImportApplier.Apply(plan, new HashSet<string> { backend.Id }, config, _configFile, _commands, DateTime.Now);
        Assert.Equal("K-lan", config.ApiKey);
    }

    /// <summary>
    /// The first rule that matches decides. Roo's allowed list comes before its denied one, so a deny appended after
    /// the allow it narrows never matched: <c>git push</c> ran unasked under a card that showed the deny ticked.
    /// </summary>
    [Fact]
    public void AnImportedDeny_IsReadBeforeAnyAllow()
    {
        const string export = """
            { "globalSettings": { "autoApprovalEnabled": true, "alwaysAllowExecute": true,
                                  "allowedCommands": ["git"], "deniedCommands": ["git push"] } }
            """;
        var config = NewConfig();
        config.PermissionRules = @"allow run_command ^dotnet(\s|$)";
        var plan = RooImport.Read(Write("roo.json", export), Context(config));

        ImportApplier.Apply(plan, plan.Items.Select(i => i.Id).ToHashSet(), config, _configFile, _commands, DateTime.Now);

        var policy = new PermissionPolicy(PermissionPolicy.ParseRules(config.PermissionRules));
        Assert.Equal(PermissionDecision.Deny, policy.Evaluate("run_command", "git push --force"));
        Assert.Equal(PermissionDecision.Allow, policy.Evaluate("run_command", "git status"));      // the allow still works
        Assert.Equal(PermissionDecision.Allow, policy.Evaluate("run_command", "dotnet build"));    // and the user's own
    }

    /// <summary>
    /// A card's boxes are numbers into a plan <c>apply</c> reads again. An item applied since the preview leaves that
    /// plan, the ones after it move up, and the card's numbers name other items: its Apply wrote an <c>allow</c> rule it
    /// showed unticked. It now carries the fingerprint of what it showed, and a plan that moved writes nothing.
    /// </summary>
    [Fact]
    public void ACardAppliedAfterThePlanMoved_WritesNothing()
    {
        var config = NewConfig();
        var file = Write("roo.json", RooExport(executeAllowed: true));
        var card = ImportCommandHandler.Handle(["/import", "roo", file], Context(config), _configFile, DateTime.Now).Card!;
        var ticked = card.Items.Where(i => i.Checked).Select(i => i.Number).ToList();
        var chat = card.Items.Single(i => i.Target == Strings.ImportTargetChatModel);
        var rulesBefore = config.PermissionRules;

        // The chat model applied by hand in the meantime: it is now in place, and the list moves.
        Assert.True(ImportCommandHandler.Handle(["/import", "roo", file, "apply", chat.Number.ToString()],
                                                Context(config), _configFile, DateTime.Now).SaveConfig);
        // WITNESS: read by number against the plan as it is now, a box the card ticked names an allow rule it did not.
        var now = ImportPreview.Card(RooImport.Read(file, Context(config)));
        Assert.Contains(ticked, n => n <= now.Items.Count && now.Items[n - 1].Label.StartsWith("allow", StringComparison.Ordinal)
                                     && !card.Items[n - 1].Label.StartsWith("allow", StringComparison.Ordinal));

        var stale = ImportCommandHandler.Handle(
            ["/import", "roo", file, "apply", string.Join(",", ticked), "#" + card.Fingerprint], Context(config), _configFile, DateTime.Now);

        Assert.Equal(Strings.ImportCardStale("roo"), stale.Message);
        Assert.False(stale.SaveConfig);
        Assert.Equal(rulesBefore, config.PermissionRules);
    }

    [Fact]
    public void ACardAppliedAsShown_WritesWhatItShowedTicked()
    {
        // Reference arm: the same card, applied before anything moved.
        var config = NewConfig();
        var file = Write("roo.json", RooExport(executeAllowed: true));
        var card = ImportCommandHandler.Handle(["/import", "roo", file], Context(config), _configFile, DateTime.Now).Card!;
        var ticked = card.Items.Where(i => i.Checked).Select(i => i.Number).ToList();

        var applied = ImportCommandHandler.Handle(
            ["/import", "roo", file, "apply", string.Join(",", ticked), "#" + card.Fingerprint], Context(config), _configFile, DateTime.Now);

        Assert.StartsWith(Strings.ImportApplied(ticked.Count), applied.Message);
        Assert.Contains("deny run_command", config.PermissionRules);
        Assert.DoesNotContain("allow run_command", config.PermissionRules);
    }

    [Fact]
    public void BothEditors_ApplyACard_WithItsFingerprint()
    {
        var root = ConversationPersistenceSilenceTests.RepoRoot();
        var vs = ConventionCoverageTests.CodeOnly(Path.Combine(root, "Inferpal", "ToolWindow", "InferpalToolWindowData.SlashCommands.cs"));
        var provider = SettingsSchemaDriftTests.NeutralizeTypeScriptComments(
            File.ReadAllText(Path.Combine(root, "vscode", "src", "chatViewProvider.ts")));

        Assert.Contains("\"#\" + card.Fingerprint", vs);
        Assert.Contains("#${card.fingerprint}`", provider);
    }

    [Fact]
    public void TheOutcome_NamesWhatWasWritten_AndNothingTheUserCleared()
    {
        var config = NewConfig();
        var file = Write("config.yaml", ContinueYaml);
        var card = ImportCommandHandler.Handle(["/import", "continue", file], Context(config), _configFile, DateTime.Now).Card!;
        var chat = card.Items.Single(i => i.Target == Strings.ImportTargetChatModel);
        var server = card.Items.Single(i => i.Target == Strings.ImportTargetServer);
        Assert.True(server.Checked);   // WITNESS: ticked on the card, so a reloaded card reads it ticked

        var applied = ImportCommandHandler.Handle(["/import", "continue", file, "apply", chat.Number.ToString()],
                                                  Context(config), _configFile, DateTime.Now);

        Assert.Contains("- " + chat.Target + " — " + chat.Label, applied.Message);
        Assert.DoesNotContain(server.Label, applied.Message);
    }

    [Fact]
    public void Apply_None_WritesNothing_AndSaysSo()
    {
        var config = NewConfig();
        var file = Write("config.yaml", ContinueYaml);

        var result = ImportCommandHandler.Handle(["/import", "continue", file, "apply", "none"], Context(config), _configFile, DateTime.Now);

        Assert.Equal(Strings.ImportNothingChosen, result.Message);
        Assert.False(result.SaveConfig);
    }

    [Fact]
    public void NoFileFound_NamesWhereItLooked_AndForRoo_HowToExport()
    {
        var result = ImportCommandHandler.Handle(["/import", "roo"], Context(NewConfig()), _configFile, DateTime.Now);

        Assert.Contains(Path.Combine(_home, "Downloads", "roo-code-settings.json"), result.Message);
        Assert.Contains(Strings.ImportRooExportHint, result.Message);
        Assert.Null(result.Card);
    }

    [Fact]
    public void AFileThatCannotBeRead_IsNamedWithItsReason()
    {
        var file = Write("config.yaml", "models:\n  - name: x\n    model: *nowhere\n");

        var result = ImportCommandHandler.Handle(["/import", "continue", file], Context(NewConfig()), _configFile, DateTime.Now);

        Assert.StartsWith(Strings.ImportReadFailed(file, "").TrimEnd(), result.Message);
        Assert.Contains("names no anchor", result.Message);
    }

    [Fact]
    public void AnUnknownSource_GetsTheUsage() =>
        Assert.Equal(Strings.ImportUsage,
                     ImportCommandHandler.Handle(["/import", "cursor"], Context(NewConfig()), _configFile, DateTime.Now).Message);

    [Fact]
    public void APathWithSpaces_IsReadWhole()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "my configs"));
        var file = Path.Combine(_dir, "my configs", "config.yaml");
        File.WriteAllText(file, ContinueYaml);
        var parts = ("/import continue \"" + file + "\"").Split(' ', StringSplitOptions.RemoveEmptyEntries);

        var result = ImportCommandHandler.Handle(parts, Context(NewConfig()), _configFile, DateTime.Now);

        Assert.Equal(file, result.Card!.File);
    }
}
