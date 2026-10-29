using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Inferpal.Config;
using Inferpal.Localization;
using Inferpal.Services;
using Inferpal.Services.CodeActions;
using Inferpal.Services.Execution;
using Inferpal.Services.Mcp;
using Inferpal.Services.Mcp.OAuth;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// The MCP servers a repository declares for VS Code, Claude Code, Visual Studio, Cursor, Roo and Continue: read as
/// their tool reads them, their variables replaced, run in the repository — and never started before the user agrees,
/// an agreement kept per definition and asked again when the definition changes.
/// </summary>
public sealed class RepoMcpServersTests : IDisposable
{
    private readonly string _repo = Path.Combine(Path.GetTempPath(), "inferpal-tests", $"repo-mcp-{Guid.NewGuid():N}");
    private readonly string _state = Path.Combine(Path.GetTempPath(), "inferpal-tests", $"repo-mcp-state-{Guid.NewGuid():N}");

    public RepoMcpServersTests()
    {
        Directory.CreateDirectory(Path.Combine(_repo, ".git"));
        Directory.CreateDirectory(_state);
    }

    public void Dispose()
    {
        try { Directory.Delete(_repo, recursive: true); } catch { }
        try { Directory.Delete(_state, recursive: true); } catch { }
    }

    private string Write(string relative, string text)
    {
        var path = Path.Combine(_repo, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    private RepoMcpServer Server(string name) => Assert.Single(RepoMcpServers.Read(_repo).Servers, s => s.Name == name);

    private McpServerConfig Prepared(string name, IReadOnlyDictionary<string, string>? inputs = null,
                                     Func<string, string?>? environment = null)
    {
        var (config, missing, rejected) = RepoMcpServers.Prepare(Server(name), _repo, inputs ?? new Dictionary<string, string>(),
                                                                 home: "/home/me", environment: environment ?? (_ => null));
        Assert.True(config is not null, $"missing={string.Join(",", missing)} rejected={rejected}");
        return config!;
    }

    // ── The six formats ─────────────────────────────────────────────────────

    [Fact]
    public void VsCodesFile_ReadsItsServersKey_StdioAndHttp()
    {
        Write(".vscode/mcp.json", """
            {
              // comments are allowed in VS Code's file
              "servers": {
                "github": { "type": "http", "url": "https://api.githubcopilot.com/mcp/", "headers": { "X-Team": "core" } },
                "fs": { "type": "stdio", "command": "npx", "args": ["-y", "@modelcontextprotocol/server-filesystem", "${workspaceFolder}"] },
              }
            }
            """);

        var github = Prepared("github");
        Assert.Equal("https://api.githubcopilot.com/mcp/", github.Url);
        Assert.Equal("core", github.Headers!["X-Team"]);
        var fs = Prepared("fs");
        Assert.Equal("npx", fs.Command);
        Assert.Equal(["-y", "@modelcontextprotocol/server-filesystem", _repo], fs.Args);
        Assert.Equal("VS Code", Server("fs").Origin);
    }

    [Theory]
    [InlineData(".mcp.json", "Claude Code / VS Code")]
    [InlineData(".cursor/mcp.json", "Cursor")]
    [InlineData(".roo/mcp.json", "Roo Code")]
    [InlineData(".vs/mcp.json", "Visual Studio")]
    public void TheMcpServersFiles_AreRead(string file, string origin)
    {
        Write(file, """{ "mcpServers": { "db": { "command": "uvx", "args": ["mcp-server-sqlite"], "env": { "MODE": "ro" } } } }""");

        var db = Prepared("db");
        Assert.Equal("uvx", db.Command);
        Assert.Equal("ro", db.Env["MODE"]);
        Assert.Equal(origin, Server("db").Origin);
    }

    /// <summary>
    /// Continue's remote server carries its headers under <c>requestOptions.headers</c> and its key as <c>apiKey</c>: read as
    /// written, both were dropped and the server connected without them ("401").
    /// </summary>
    [Fact]
    public void ContinuesRemoteServer_KeepsItsHeadersAndItsKey()
    {
        Write(".continue/mcpServers/remote.yaml", """
            name: Remote
            version: 0.0.1
            schema: v1
            mcpServers:
              - name: remote
                url: https://mcp.example.com/mcp
                apiKey: ${env:REMOTE_KEY}
                requestOptions:
                  headers:
                    X-Team: alpha
            """);

        var remote = Prepared("remote", environment: n => n == "REMOTE_KEY" ? "s3cret" : null);

        Assert.Equal("alpha", remote.Headers!["X-Team"]);
        Assert.Equal("Bearer s3cret", remote.Headers["Authorization"]);
        // The agreement card shows what will be sent.
        Assert.Contains("X-Team", Server("remote").Definition["headers"]!.ToJsonString());
    }

    [Fact]
    public void AServerThatDeclaresSse_IsRead_AndRewrittenSo()
    {
        var parsed = McpServerConfig.Parse("""{ "old": { "type": "sse", "url": "https://mcp.example.com/sse" } }""");
        Assert.True(Assert.Single(parsed).DeclaresSse);
        Assert.Contains("\"type\":\"sse\"", McpServerConfig.Serialize(parsed).Replace(" ", ""));

        // Reference arm: a Streamable HTTP entry declares nothing of the kind.
        Assert.False(Assert.Single(McpServerConfig.Parse("""{ "new": { "url": "https://mcp.example.com/mcp" } }""")).DeclaresSse);
    }

    [Fact]
    public void ContinuesYamlBlock_IsRead()
    {
        Write(".continue/mcpServers/sqlite.yaml", """
            name: SQLite server
            version: 0.0.1
            schema: v1
            mcpServers:
              - name: sqlite   # the server
                command: npx
                args:
                  - "-y"
                  - mcp-sqlite
                  - ./data.db
                env: {}
            """);

        var sqlite = Prepared("sqlite");
        Assert.Equal("npx", sqlite.Command);
        Assert.Equal(["-y", "mcp-sqlite", "./data.db"], sqlite.Args);
        Assert.Equal("Continue", Server("sqlite").Origin);
    }

    [Fact]
    public void TheRealVsCodeRepositoryFile_GivesItsTwoServers()
    {
        // microsoft/vscode's own .mcp.json, as committed.
        Write(".mcp.json", """
            {
            	"mcpServers": {
            		"vscode-automation-mcp": { "type": "stdio", "command": "npm", "args": ["--prefix", "test/mcp", "run", "start-stdio"] },
            		"component-explorer": { "type": "stdio", "command": "npm",
            			"args": ["exec", "--no", "--", "component-explorer", "mcp", "-p", "./test/componentFixtures/component-explorer.json", "--use-daemon", "-vv"] }
            	}
            }
            """);

        Assert.Equal(["vscode-automation-mcp", "component-explorer"], RepoMcpServers.Read(_repo).Servers.Select(s => s.Name));
        Assert.Equal("npm --prefix test/mcp run start-stdio", Server("vscode-automation-mcp").Runs);
    }

    [Fact]
    public void AnUnreadableFile_IsNamedWithItsCause()
    {
        Write(".cursor/mcp.json", "{ not json");
        Write(".continue/mcpServers/bad.yaml", "mcpServers:\n  - name: x\n    command: *missing\n");

        var problems = RepoMcpServers.Read(_repo).Problems;
        Assert.Contains(problems, p => p.Source == ".cursor/mcp.json");
        Assert.Contains(problems, p => p.Source == ".continue/mcpServers/bad.yaml" && p.Reason.Contains("names no anchor"));
    }

    [Fact]
    public void AnEntryDeclaringBothACommandAndAUrl_IsNotOffered_AndSaysWhy()
    {
        Write(".mcp.json", """
            { "mcpServers": {
                "both": { "command": "echo", "args": ["hi"], "url": "https://elsewhere.example/mcp", "headers": { "X": "${env:TOKEN}" } },
                "stdio": { "command": "srv" } } }
            """);

        var scan = RepoMcpServers.Read(_repo);
        Assert.Equal(["stdio"], scan.Servers.Select(s => s.Name));   // witness: the file was read
        Assert.Contains(scan.Problems, p => p.Name == "both" && p.Reason.Contains("both a \"command\""));
    }

    // ── Variables and folder ────────────────────────────────────────────────

    [Fact]
    public void EveryDialectsVariables_AreReplaced()
    {
        Write(".mcp.json", """
            { "mcpServers": { "s": { "command": "${userHome}/bin/srv", "args": [
                "${workspaceFolderBasename}", "${env:TOKEN}", "${TOKEN}", "${MISSING_X:-fallback}", "${input:key}" ] } } }
            """);

        var s = Prepared("s", new Dictionary<string, string> { ["key"] = "K-VALUE" }, name => name == "TOKEN" ? "T-VALUE" : null);
        Assert.Equal("/home/me/bin/srv", s.Command);
        Assert.Equal([Path.GetFileName(_repo), "T-VALUE", "T-VALUE", "fallback", "K-VALUE"], s.Args);
    }

    [Fact]
    public void AVariableNothingFills_IsNamed_AndNothingStartsOnAnEmptyValue()
    {
        Write(".mcp.json", """{ "mcpServers": { "s": { "command": "srv", "env": { "KEY": "${env:NO_SUCH_VAR}", "S": "${{ secrets.API }}" } } } }""");

        var (config, missing, _) = RepoMcpServers.Prepare(Server("s"), _repo, new Dictionary<string, string>(), environment: _ => null);
        Assert.Null(config);
        Assert.Equal(["${env:NO_SUCH_VAR}", "${{ secrets.API }}"], missing);
    }

    [Fact]
    public void AServerRunsInTheRepository_OrInTheFolderItDeclaresUnderIt()
    {
        Write(".mcp.json", """{ "mcpServers": { "a": { "command": "srv" }, "b": { "command": "srv", "cwd": "tools/mcp" } } }""");

        Assert.Equal(_repo, Prepared("a").WorkingDirectory);
        Assert.Equal(Path.Combine(_repo, "tools", "mcp"), Prepared("b").WorkingDirectory);
    }

    // ── The agreement ───────────────────────────────────────────────────────

    private sealed class RecordingApproval(InferpalConfig config, ApprovalDecision answer) : ApprovalServiceBase(config, () => null)
    {
        public int Prompts;
        public string? LastMessage;
        public readonly List<string> Messages = [];
        public ApprovalDecision Answer = answer;
        /// <summary>What happens while the question waits for its answer.</summary>
        public Action? WhileAsked;

        protected override Task<ApprovalDecision> PromptUserAsync(string message, DiffInfo? diff, CancellationToken ct)
        {
            Prompts++;
            LastMessage = message;
            lock (Messages) Messages.Add(message);
            WhileAsked?.Invoke();
            return Task.FromResult(Answer);
        }
    }

    private sealed class FakeClient(McpServerConfig config, List<McpServerConfig> started, List<FakeClient> clients) : IMcpClient
    {
        public string ServerName => config.Name;
        public string? LastError => null;
        public bool NeedsAuthorization => false;
        public Task<bool> StartAsync(CancellationToken ct)
        {
            lock (started) started.Add(config);
            lock (clients) clients.Add(this);
            return Task.FromResult(true);
        }
        public Task<IReadOnlyList<McpToolInfo>?> ListToolsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<McpToolInfo>?>([]);
        public Task<string> CallToolAsync(string toolName, JsonElement arguments, CancellationToken ct) => Task.FromResult("ok");
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public event Action? ToolsChanged { add { } remove { } }
        public event Action? Closed;
        /// <summary>The server's process exits on its own.</summary>
        public void Die() => Closed?.Invoke();
    }

    /// <summary>Every client the service started, in order: the last one is the running one.</summary>
    private readonly List<FakeClient> _clients = [];

    private (McpToolService Service, RecordingApproval Approval, List<McpServerConfig> Started) Service(
        InferpalConfig? config = null, ApprovalDecision answer = ApprovalDecision.Once,
        Func<RepoMcpServer, RepoMcpInput, CancellationToken, Task<string?>>? askInput = null)
    {
        config ??= new InferpalConfig();
        config.McpEnabled = false;                      // no refresh at construction: each test refreshes itself
        var approval = new RecordingApproval(config, answer);
        var started = new List<McpServerConfig>();
        var service = new McpToolService(config, approval, cfg => new FakeClient(cfg, started, _clients),
                                         reconnectBackoff: [TimeSpan.FromMilliseconds(10)],
                                         tokenStore: new McpTokenStore(Path.Combine(_state, "tokens.bin")),
                                         sharedServersJson: () => null, workspaceRoot: () => _repo, askInput: askInput)
        {
            Consents = new RepoMcpConsents { PathOverride = Path.Combine(_state, "consents.json") },
        };
        config.McpEnabled = true;
        return (service, approval, started);
    }

    [Fact]
    public async Task AServerIsNotStarted_UntilTheUserAgrees()
    {
        Write(".vscode/mcp.json", """{ "servers": { "fs": { "command": "npx", "args": ["server"] } } }""");
        var (service, approval, started) = Service(answer: ApprovalDecision.Deny);

        await service.RefreshAsync();

        Assert.Equal(1, approval.Prompts);
        Assert.Empty(started);
        Assert.Contains("npx server", approval.LastMessage);
        var status = Assert.Single(service.Status, s => s.Name == "fs");
        Assert.False(status.Connected);
        Assert.Contains("not agreed to", status.Error);
        await service.DisposeAsync();
    }

    [Fact]
    public async Task AnAgreement_StartsIt_IsKept_AndAChangedDefinitionAsksAgain()
    {
        Write(".vscode/mcp.json", """{ "servers": { "fs": { "command": "npx", "args": ["server"] } } }""");
        var (service, approval, started) = Service();

        await service.RefreshAsync();
        Assert.Equal(1, approval.Prompts);
        Assert.Single(started);

        await service.RefreshAsync();                                    // kept: no new question
        Assert.Equal(1, approval.Prompts);
        Assert.Equal(2, started.Count);

        Write(".vscode/mcp.json", """{ "servers": { "fs": { "command": "npx", "args": ["server", "--write"] } } }""");
        await service.RefreshAsync();                                    // one argument more: asked again
        Assert.Equal(2, approval.Prompts);
        Assert.Equal(["server", "--write"], started[^1].Args);
        await service.DisposeAsync();
    }

    [Fact]
    public async Task TheAgreementIsAskedEvenInNoPromptMode_AndUnderAnAllowRule()
    {
        Write(".mcp.json", """{ "mcpServers": { "fs": { "command": "npx" } } }""");
        var config = new InferpalConfig { SecurityAlertsDisabled = true, PermissionRules = "allow mcp_server .*\nallow * .*" };
        var (service, approval, started) = Service(config, ApprovalDecision.Deny);

        await service.RefreshAsync();

        Assert.Equal(1, approval.Prompts);
        Assert.Empty(started);
        await service.DisposeAsync();
    }

    [Fact]
    public async Task AValueAServerAsksFor_IsAskedOnce_AndWithoutItTheServerDoesNotStart()
    {
        Write(".vscode/mcp.json", """
            { "inputs": [ { "type": "promptString", "id": "token", "description": "API token", "password": true } ],
              "servers": { "api": { "command": "srv", "args": ["--token", "${input:token}"] } } }
            """);
        var asked = 0;
        var (service, _, started) = Service(askInput: (_, input, _) =>
        {
            asked++;
            Assert.True(input.Password);
            Assert.Equal("API token", input.Description);
            return Task.FromResult<string?>("SECRET");
        });

        await service.RefreshAsync();
        await service.RefreshAsync();

        Assert.Equal(1, asked);
        Assert.Equal(["--token", "SECRET"], started[0].Args);
        await service.DisposeAsync();

        var (unanswered, _, none) = Service(askInput: (_, _, _) => Task.FromResult<string?>(null));
        await unanswered.RefreshAsync();
        Assert.Empty(none);
        Assert.Contains("input 'token'", Assert.Single(unanswered.Status, s => s.Name == "api").Error);
        await unanswered.DisposeAsync();
    }

    [Fact]
    public async Task AServerTheUserConfigured_KeepsItsName_TheRepositorysIsNotStarted()
    {
        Write(".mcp.json", """{ "mcpServers": { "fs": { "command": "repo-server" } } }""");
        var config = new InferpalConfig { McpServersJson = """{ "fs": { "command": "my-server" } }""" };
        var (service, approval, started) = Service(config);

        await service.RefreshAsync();

        Assert.Equal(0, approval.Prompts);
        Assert.Equal(["my-server"], started.Select(s => s.Command));
        Assert.Contains(service.Status, s => s.Name == "fs" && s.Error?.Contains("declared earlier") == true);
        await service.DisposeAsync();
    }

    /// <summary>A repository server's card shows its definition as the file writes it: started, its variables hold the
    /// environment's values, and the key itself was in the settings.</summary>
    [Fact]
    public async Task ARepositoryServersCard_ShowsItsDefinitionAsWritten_NeverTheValueFilledIn()
    {
        var variable = "INFERPAL_TEST_KEY_" + Guid.NewGuid().ToString("N")[..8];
        Environment.SetEnvironmentVariable(variable, "s3cret-value");
        try
        {
            Write(".mcp.json", $$"""{ "mcpServers": { "remote": { "url": "http://127.0.0.1:9/mcp?key=${env:{{variable}}}" } } }""");
            var (service, _, started) = Service();

            await service.RefreshAsync();

            Assert.Contains(started, s => s.Url?.Contains("s3cret-value") == true);   // WITNESS: started with the value
            var shown = Assert.Single(service.OtherServers, s => s.Name == "remote");
            Assert.Contains("${env:" + variable + "}", shown.Url);
            Assert.DoesNotContain("s3cret-value", shown.Url);
            await service.DisposeAsync();
        }
        finally { Environment.SetEnvironmentVariable(variable, null); }
    }

    [Theory]
    [InlineData("GitHub", "github")]          // the permission rules match tool names without case
    [InlineData("GitHub", "GitHub (work)")]   // GitHub's tool `work___create_issue` IS the user's `create_issue`
    [InlineData("GitHub (work)", "GitHub")]   // and the other way round
    public async Task ARepositoryServerWhoseToolsCouldCarryTheUsersNames_IsNotStarted(string repository, string users)
    {
        Write(".mcp.json", $$"""{ "mcpServers": { "{{repository}}": { "command": "repo-server" } } }""");
        var config = new InferpalConfig { McpServersJson = $$"""{ "{{users}}": { "command": "my-server" } }""" };
        var (service, approval, started) = Service(config);

        await service.RefreshAsync();

        Assert.Equal(0, approval.Prompts);
        Assert.Equal(["my-server"], started.Select(s => s.Command));   // witness: the user's server started
        Assert.Contains(service.Status, s => s.Name == repository && s.Error?.Contains(users) == true);
        await service.DisposeAsync();
    }

    [Fact]
    public async Task ARepositoryServerWhoseToolsWouldCarryTheUsersNames_IsNotStarted()
    {
        // "my.server" and "my-server" give the same tool names (mcp__my_server__*): started, the repository's server took
        // the user's names whenever the user's had none — reconnecting, failing — and a "Always" given to the user's tool
        // ran the repository's program unasked.
        Write(".mcp.json", """{ "mcpServers": { "my.server": { "command": "repo-server" } } }""");
        var config = new InferpalConfig { McpServersJson = """{ "my-server": { "command": "my-server" } }""" };
        var (service, approval, started) = Service(config);

        await service.RefreshAsync();

        Assert.Equal(0, approval.Prompts);
        Assert.Equal(["my-server"], started.Select(s => s.Command));
        Assert.Contains(service.Status, s => s.Name == "my.server" && s.Error?.Contains("my-server") == true);
        await service.DisposeAsync();
    }

    [Theory]
    [InlineData("github", "GitHub")]
    [InlineData("GitHub", "GitHub (work)")]
    public async Task AnEditorServerWhoseToolsCouldCarryTheUsersNames_IsNotStarted(string editors, string users)
    {
        var config = new InferpalConfig { McpServersJson = $$"""{ "{{users}}": { "command": "my-server" } }""" };
        var (service, _, started) = Service(config);
        service.SessionServers = [new McpServerConfig(editors, "editor-server", [], new Dictionary<string, string>())];

        await service.RefreshAsync();

        Assert.Equal(["my-server"], started.Select(s => s.Command));
        Assert.Contains(service.Status, s => s.Name == editors && s.Error?.Contains(users) == true);
        await service.DisposeAsync();
    }

    [Fact]
    public async Task AnEditorServerWhoseToolsWouldCarryTheUsersNames_IsNotStarted()
    {
        var config = new InferpalConfig { McpServersJson = """{ "my-server": { "command": "my-server" } }""" };
        var (service, _, started) = Service(config);
        service.SessionServers = [new McpServerConfig("my.server", "editor-server", [], new Dictionary<string, string>())];

        await service.RefreshAsync();

        Assert.Equal(["my-server"], started.Select(s => s.Command));
        Assert.Contains(service.Status, s => s.Name == "my.server" && s.Error?.Contains("my-server") == true);
        await service.DisposeAsync();
    }

    [Fact]
    public async Task ARepositoryWithoutMcpFile_ChangesNothing()
    {
        var config = new InferpalConfig { McpServersJson = """{ "mine": { "command": "my-server" } }""" };
        var (service, approval, started) = Service(config);

        await service.RefreshAsync();

        Assert.Equal(0, approval.Prompts);
        Assert.Equal(["mine"], started.Select(s => s.Name));
        Assert.Equal(["mine"], service.Status.Select(s => s.Name));
        await service.DisposeAsync();
    }

    // ── A refresh never runs under a turn ───────────────────────────────────

    [Fact]
    public async Task ARefreshAskedAtTheStartOfATurn_WaitsForTheTurnToEnd()
    {
        // ⚠ A refresh tears every server down: under a running turn, an agent mid-call gets its client disposed, and a
        // repository server's agreement question lands in the middle of the turn. Visual Studio pins the root — and asks
        // for the repository's servers — at the START of a turn.
        var config = new InferpalConfig { McpServersJson = """{ "mine": { "command": "my-server" } }""" };
        var (service, _, started) = Service(config);
        var busy = true;

        var refresh = service.RefreshWhenIdleAsync(() => Volatile.Read(ref busy), TimeSpan.FromMilliseconds(20));
        await Task.Delay(300);
        Assert.Empty(started);                       // the turn is running: nothing torn down, nothing respawned

        Volatile.Write(ref busy, false);
        await refresh.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(["mine"], started.Select(s => s.Name));
        await service.DisposeAsync();
    }

    [Fact]
    public async Task ARefreshAskedWithNoTurn_RunsAtOnce()
    {
        // Reference arm: idle, the refresh does not wait for anything.
        var config = new InferpalConfig { McpServersJson = """{ "mine": { "command": "my-server" } }""" };
        var (service, _, started) = Service(config);

        await service.RefreshWhenIdleAsync(() => false, TimeSpan.FromMilliseconds(20)).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(["mine"], started.Select(s => s.Name));
        await service.DisposeAsync();
    }

    // ── What the repository got wrong costs its own file, never the user's servers ──

    [Theory]
    // An escape YAML does not know (\q): a Windows path written between double quotes.
    [InlineData(".continue/mcpServers/bad.yaml", "mcpServers:\n  - name: bad\n    command: \"C:\\qux\\node.exe\"\n")]
    // The same server declared twice: the parser keeps both keys, reading them throws.
    [InlineData(".vscode/mcp.json", """{ "servers": { "fs": { "command": "a" }, "fs": { "command": "b" } } }""")]
    public async Task ARepositoryFileThatCannotBeRead_IsNamed_AndTheUsersOwnServersStillStart(string file, string text)
    {
        Write(file, text);
        var config = new InferpalConfig { McpServersJson = """{ "mine": { "command": "my-server" } }""" };
        var (service, _, started) = Service(config);

        await service.RefreshAsync();

        Assert.Equal(["mine"], started.Select(s => s.Name));
        Assert.Contains(service.Status, s => !s.Connected && s.Error?.Contains(file) == true && s.Error.Contains("cannot be read"));
        await service.DisposeAsync();
    }

    [Fact]
    public async Task TheAgreementShows_EverythingTheDefinitionChangesAboutWhatRuns()
    {
        // ⚠ The agreement is given to the whole definition (its fingerprint): an environment variable, a working folder
        // or a header changes what runs, and a question showing the command alone hides them from the one boundary.
        Write(".mcp.json", """
            { "mcpServers": {
                "gh":  { "command": "npx", "args": ["-y", "server-github"],
                         "env": { "NODE_OPTIONS": "--require ./.vscode/x.js" }, "cwd": "tools" },
                "api": { "url": "https://mcp.example.com/mcp", "headers": { "X-Route": "elsewhere" } } } }
            """);
        var (service, approval, _) = Service(answer: ApprovalDecision.Deny);

        await service.RefreshAsync();

        var gh = Assert.Single(approval.Messages, m => m.Contains("npx -y server-github"));
        Assert.Contains("NODE_OPTIONS=--require ./.vscode/x.js", gh);
        Assert.Contains("cwd: tools", gh);
        var api = Assert.Single(approval.Messages, m => m.Contains("https://mcp.example.com/mcp"));
        Assert.Contains("X-Route: elsewhere", api);
        await service.DisposeAsync();
    }

    [Fact]
    public async Task ACommandAlone_IsShownAsItAlwaysWas()
    {
        // Reference arm: nothing else declared, nothing else said.
        Write(".mcp.json", """{ "mcpServers": { "fs": { "command": "npx", "args": ["server"] } } }""");
        var (service, approval, _) = Service(answer: ApprovalDecision.Deny);

        await service.RefreshAsync();

        Assert.Contains("`npx server`", approval.LastMessage);
        await service.DisposeAsync();
    }

    // ── What the agreement covers: the definition, and the repository's files it runs ──

    private const string LocalScript = """{ "mcpServers": { "local": { "command": "node", "args": ["tools/mcp/server.js"] } } }""";

    private static async Task<McpServerStatus> StatusOnceAsync(McpToolService service, string name, Func<McpServerStatus, bool> until)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (clock.Elapsed < TimeSpan.FromSeconds(30))
        {
            if (service.Status.FirstOrDefault(s => s.Name == name) is { } status && until(status)) return status;
            await Task.Delay(20);
        }
        throw new Xunit.Sdk.XunitException(
            $"'{name}' never reached the awaited state: {service.Status.FirstOrDefault(s => s.Name == name)?.Error ?? "no status"}");
    }

    [Theory]
    [InlineData("""{ "command": "node", "args": ["tools/mcp/server.js"] }""")]
    [InlineData("""{ "command": "node", "args": ["${workspaceFolder}/tools/mcp/server.js"] }""")]
    [InlineData("""{ "command": "sh", "args": ["-c", "node tools/mcp/server.js --stdio"] }""")]
    [InlineData("""{ "command": "node", "args": ["--entry=tools/mcp/server.js"] }""")]
    [InlineData("""{ "command": "node", "args": ["server.js"], "cwd": "tools/mcp" }""")]
    [InlineData("""{ "command": "tools/mcp/server.js" }""")]
    [InlineData("""{ "command": "node", "args": ["-e", "0"], "env": { "NODE_OPTIONS": "--require ./tools/mcp/server.js" } }""")]
    public async Task AScriptAPullRewrote_IsAskedAboutAgain_HoweverTheDefinitionNamesIt(string definition)
    {
        Write("tools/mcp/server.js", "console.log('v1');");
        Write(".mcp.json", $$"""{ "mcpServers": { "local": {{definition}} } }""");
        var (service, approval, started) = Service();

        await service.RefreshAsync();
        await service.RefreshAsync();
        Assert.Equal(1, approval.Prompts);                                // agreed once, kept
        Assert.Equal(2, started.Count);

        Write("tools/mcp/server.js", "console.log('v2, other code');");   // what a `git pull` does
        await service.RefreshAsync();
        Assert.Equal(2, approval.Prompts);
        await service.DisposeAsync();
    }

    [Fact]
    public async Task AFileBesideTheScript_IsCoveredToo_ForTheScriptLoadsIt()
    {
        Write("tools/mcp/server.js", "require('./lib/tools.js');");
        Write("tools/mcp/lib/tools.js", "module.exports = 1;");
        Write(".mcp.json", LocalScript);
        var (service, approval, _) = Service();
        await service.RefreshAsync();

        Write("tools/mcp/lib/tools.js", "module.exports = 2;");
        await service.RefreshAsync();

        Assert.Equal(2, approval.Prompts);
        await service.DisposeAsync();
    }

    [Fact]
    public async Task AScriptRewrittenWhileTheQuestionWaited_DoesNotStart_AndSaysWhy()
    {
        Write("tools/mcp/server.js", "v1");
        Write(".mcp.json", LocalScript);
        var (service, approval, started) = Service();
        approval.WhileAsked = () => Write("tools/mcp/server.js", "v2, written while the card waited");

        await service.RefreshAsync();

        Assert.Empty(started);
        Assert.Contains("tools/mcp/server.js changed", Assert.Single(service.Status, s => s.Name == "local").Error);
        await service.DisposeAsync();
    }

    [Fact]
    public async Task AServerThatExits_AfterAPullRewroteItsScript_IsNotRestarted_AndSaysWhy()
    {
        Write("tools/mcp/server.js", "v1");
        Write(".mcp.json", LocalScript);
        var (service, approval, started) = Service();
        await service.RefreshAsync();
        Assert.Single(started);

        Write("tools/mcp/server.js", "v2");
        _clients[^1].Die();                                                // the process exits, and Inferpal restarts it

        var status = await StatusOnceAsync(service, "local", s => s.Error?.Contains("server.js") == true);
        Assert.Contains("tools/mcp/server.js changed", status.Error);
        Assert.Single(started);                                            // the new code never ran
        Assert.Equal(1, approval.Prompts);
        await service.DisposeAsync();
    }

    [Fact]
    public async Task AServerThatExits_WithItsFilesUnchanged_IsRestarted()
    {
        Write("tools/mcp/server.js", "v1");
        Write(".mcp.json", LocalScript);
        var (service, _, started) = Service();
        await service.RefreshAsync();

        _clients[^1].Die();

        for (var waited = 0; started.Count < 2 && waited < 30_000; waited += 20) await Task.Delay(20);
        Assert.Equal(2, started.Count);
        await service.DisposeAsync();
    }

    [Fact]
    public async Task WhatTheServerDoesNotRun_AsksNothingAgain()
    {
        Write("server.py", "print(1)");                                      // at the root: that file alone
        Write("README.md", "v1");
        Write("tools/mcp/server.js", "v1");
        Write("tools/mcp/bin/Debug/out.dll", "v1");                           // what a build writes beside the code
        Write("tools/mcp/node_modules/dep/index.js", "v1");                   // what an install writes
        Write(".mcp.json", """
            { "mcpServers": {
                "py": { "command": "python", "args": ["server.py"] },
                "js": { "command": "node", "args": ["tools/mcp/server.js"] },
                "pkg": { "command": "npx", "args": ["-y", "@scope/server"] } } }
            """);
        var (service, approval, _) = Service();
        await service.RefreshAsync();
        Assert.Equal(3, approval.Prompts);

        Write("README.md", "v2");
        Write("tools/mcp/bin/Debug/out.dll", "v2");
        Write("tools/mcp/node_modules/dep/index.js", "v2");
        await service.RefreshAsync();
        Assert.Equal(3, approval.Prompts);

        Write("server.py", "print(2)");                                       // the positive arm
        await service.RefreshAsync();
        Assert.Equal(4, approval.Prompts);
        await service.DisposeAsync();
    }

    [Fact]
    public async Task AServerThatNamesNoRepositoryFile_KeepsTheFingerprintItsAgreementWasGivenTo()
    {
        Write(".mcp.json", """{ "mcpServers": { "pkg": { "command": "npx", "args": ["-y", "@scope/server"] } } }""");

        var server = await RepoMcpServers.CoverAsync(Server("pkg"), _repo);

        Assert.Empty(server.Files!);
        // The formula a version that hashes the definition alone stores, written out: its agreement is still this one.
        var stored = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(".mcp.json\npkg\n" + server.Definition.ToJsonString())));
        Assert.Equal(stored, server.Fingerprint);
        Assert.Equal("pkg", RepoMcpConsents.KeyOf(server));
    }

    [Fact]
    public async Task AnAgreementAnotherVersionKeeps_IsLeftForIt_WhileThisVersionKeepsItsOwn()
    {
        Write("tools/mcp/server.js", "v1");
        Write(".mcp.json", LocalScript);
        var consents = Path.Combine(_state, "consents.json");
        // What the other editor wrote, running a version that hashes the definition alone.
        File.WriteAllText(consents, JsonSerializer.Serialize(new Dictionary<string, Dictionary<string, string>>
        {
            [_repo] = new() { ["local"] = Server("local").DefinitionFingerprint },
        }));
        var (service, approval, started) = Service();

        await service.RefreshAsync();                                       // this version covers server.js: asked
        await service.RefreshAsync();                                       // and kept, under a key of its own

        Assert.Equal(1, approval.Prompts);
        Assert.Equal(2, started.Count);
        var kept = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(File.ReadAllText(consents))!.Single().Value;
        Assert.Equal(Server("local").DefinitionFingerprint, kept["local"]);  // the other version's agreement, untouched
        Assert.Contains("local\u0001files", kept.Keys);
        await service.DisposeAsync();
    }

    [Fact]
    public async Task AFileOutsideTheRepository_IsNotCovered_HoweverItIsNamed()
    {
        var outside = Path.Combine(_state, "elsewhere.js");
        File.WriteAllText(outside, "x");
        Write("tools/mcp/server.js", "x");
        Write(".mcp.json", JsonSerializer.Serialize(new
        {
            mcpServers = new
            {
                local = new { command = "node", args = new[] { "tools/mcp/server.js", outside, $"../{Path.GetFileName(_state)}/elsewhere.js" } },
            },
        }));

        var server = await RepoMcpServers.CoverAsync(Server("local"), _repo);

        Assert.Equal(["tools/mcp/server.js"], server.Files!.Select(f => f.Path));
    }

    [Fact]
    public void APathTheRepositoryWrote_IsJudgedOnItsText()
    {
        var root = Path.GetFullPath(_repo);

        Assert.True(RepoMcpFiles.Under(Path.Combine(root, "tools", "x.js"), root));
        Assert.False(RepoMcpFiles.Under(root + "-other" + Path.DirectorySeparatorChar + "x.js", root));
        Assert.False(RepoMcpFiles.Under(@"\\attacker\share\x.js", root));
        Assert.False(RepoMcpFiles.Under(Path.GetFullPath(Path.Combine(root, "..", "x.js")), root));
    }

    [Theory]
    [InlineData("""{ "command": "node", "args": ["tools/mcp/server"] }""", "tools/mcp/server.js")]
    [InlineData("""{ "command": "python", "args": ["-m", "tools.mcp.server"] }""", "tools/mcp/server.py")]
    [InlineData("""{ "command": "npm", "args": ["run", "mcp"] }""", "tools/mcp/server.js")]
    [InlineData("""{ "command": "uv", "args": ["run", "--directory", "tools/mcp", "server.py"] }""", "tools/mcp/server.py")]
    public async Task WhatTheRuntimeFindsByItself_IsCoveredToo(string definition, string script)
    {
        Write("tools/mcp/server.js", "v1");
        Write("tools/mcp/server.py", "v1");
        Write("package.json", """{ "scripts": { "mcp": "node tools/mcp/server.js" } }""");
        Write(".mcp.json", $$"""{ "mcpServers": { "local": {{definition}} } }""");
        var (service, approval, _) = Service();
        await service.RefreshAsync();

        Write(script, "v2, other code");
        await service.RefreshAsync();

        Assert.Equal(2, approval.Prompts);
        await service.DisposeAsync();
    }

    [Fact]
    public async Task APathBehindAnEnvironmentVariable_IsCovered()
    {
        Write("tools/mcp/server.js", "v1");
        var definition = JsonNode.Parse("""{ "command": "node", "args": ["${env:MCP_HOME}/server.js"] }""")!.AsObject();

        var coverage = await RepoMcpFiles.CoverAsync(definition, _repo,
            environment: name => name == "MCP_HOME" ? Path.Combine(_repo, "tools", "mcp") : null);

        Assert.Contains("tools/mcp/server.js", coverage.Files!.Select(f => f.Path));
    }

    [Fact]
    public async Task InAGitRepository_WhatThisMachineWritesBesideTheCode_AsksNothingAgain()
    {
        var repo = Path.Combine(_state, "git-repo");
        Directory.CreateDirectory(Path.Combine(repo, "tools", "mcp"));
        File.WriteAllText(Path.Combine(repo, "tools", "mcp", "server.py"), "import lib");
        File.WriteAllText(Path.Combine(repo, "tools", "mcp", "lib.py"), "VALUE = 1");
        Assert.True(Git("init -q", repo) && Git("add -A", repo)
                    && Git("-c user.email=t@example.com -c user.name=t -c commit.gpgsign=false commit -q -m init", repo),
            "git could not make the repository this test needs: UNDECIDED, not green.");
        var definition = JsonNode.Parse("""{ "command": "python", "args": ["tools/mcp/server.py"] }""")!.AsObject();
        var before = (await RepoMcpFiles.CoverAsync(definition, repo)).Files!;
        Assert.Equal(["tools/mcp/lib.py", "tools/mcp/server.py"], before.Select(f => f.Path));

        // What running it writes: Python's cache, a log, a database — none of them a pull brings.
        Directory.CreateDirectory(Path.Combine(repo, "tools", "mcp", "__pycache__"));
        File.WriteAllText(Path.Combine(repo, "tools", "mcp", "__pycache__", "lib.cpython-312.pyc"), "bytecode");
        File.WriteAllText(Path.Combine(repo, "tools", "mcp", "server.log"), "started");
        Assert.Equal(before, (await RepoMcpFiles.CoverAsync(definition, repo)).Files!);

        File.WriteAllText(Path.Combine(repo, "tools", "mcp", "lib.py"), "VALUE = 2");   // and what a pull does
        Assert.NotEqual(before.Single(f => f.Path == "tools/mcp/lib.py").Digest,
                        (await RepoMcpFiles.CoverAsync(definition, repo)).Files!.Single(f => f.Path == "tools/mcp/lib.py").Digest);
    }

    private const string Commit = "-c user.email=t@example.com -c user.name=t -c commit.gpgsign=false commit -q -m c";

    [Fact]
    public async Task AWorkspaceInsideAnotherRepositoryThatDoesNotTrackIt_HasItsFoldersReadWhole()
    {
        var outer = Path.Combine(_state, "git-home");
        Directory.CreateDirectory(outer);
        File.WriteAllText(Path.Combine(outer, "notes.md"), "home");
        Assert.True(Git("init -q", outer) && Git("add -A", outer) && Git(Commit, outer),
            "git could not make the repository this test needs: UNDECIDED, not green.");
        var workspace = Path.Combine(outer, "unpacked", "project");   // tracked by nobody: git lists nothing here
        Directory.CreateDirectory(Path.Combine(workspace, "tools", "mcp"));
        File.WriteAllText(Path.Combine(workspace, "tools", "mcp", "server.js"), "require('./lib')");
        File.WriteAllText(Path.Combine(workspace, "tools", "mcp", "lib.js"), "v1");
        var definition = JsonNode.Parse("""{ "command": "node", "args": ["tools/mcp/server.js"] }""")!.AsObject();

        var paths = (await RepoMcpFiles.CoverAsync(definition, workspace)).Files!.Select(f => f.Path).ToList();

        Assert.Equal(["tools/mcp/lib.js", "tools/mcp/server.js"], paths);
    }

    [Fact]
    public async Task InAGitRepository_AFolderWrittenInAnotherCase_IsStillCovered()
    {
        if (OperatingSystem.IsLinux()) return;   // the file system does not fold case: another case is another file
        var repo = Path.Combine(_state, "git-case");
        Directory.CreateDirectory(Path.Combine(repo, "tools", "mcp"));
        File.WriteAllText(Path.Combine(repo, "tools", "mcp", "server.js"), "require('./lib')");
        File.WriteAllText(Path.Combine(repo, "tools", "mcp", "lib.js"), "v1");
        Assert.True(Git("init -q", repo) && Git("add -A", repo) && Git(Commit, repo),
            "git could not make the repository this test needs: UNDECIDED, not green.");
        var definition = JsonNode.Parse("""{ "command": "node", "args": ["Tools/MCP/server.js"] }""")!.AsObject();

        var paths = (await RepoMcpFiles.CoverAsync(definition, repo)).Files!.Select(f => f.Path).ToList();

        Assert.Contains(paths, p => p.EndsWith("/lib.js", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task InAGitRepository_ANonAsciiFolderWrittenInAnotherCase_IsCovered()
    {
        if (OperatingSystem.IsLinux()) return;   // the file system does not fold case: another case is another file
        var repo = Path.Combine(_state, "git-case-o");
        Directory.CreateDirectory(Path.Combine(repo, "ö", "data"));
        File.WriteAllText(Path.Combine(repo, "ö", "server.js"), "require('./lib')");
        File.WriteAllText(Path.Combine(repo, "ö", "lib.js"), "v1");
        File.WriteAllText(Path.Combine(repo, "ö", "data", "seed.json"), "{}");
        Assert.True(Git("init -q", repo) && Git("add -A", repo) && Git(Commit, repo),
            "git could not make the repository this test needs: UNDECIDED, not green.");
        // git's icase folds A to Z only: Ö lists nothing of ö, while ö/data lists a file the file system puts under Ö.
        var definition = JsonNode.Parse("""{ "command": "node", "args": ["Ö/server.js", "ö/data"] }""")!.AsObject();

        var paths = (await RepoMcpFiles.CoverAsync(definition, repo)).Files!.Select(f => f.Path).ToList();

        Assert.Contains(paths, p => p.EndsWith("/lib.js", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task InAGitRepository_AFolderWrittenInAnotherCase_BesideOneWrittenInGitsCase_IsCovered()
    {
        if (OperatingSystem.IsLinux()) return;   // the file system does not fold case: another case is another file
        var repo = Path.Combine(_state, "git-case-mixed");
        Directory.CreateDirectory(Path.Combine(repo, "tools", "mcp", "data"));
        File.WriteAllText(Path.Combine(repo, "tools", "mcp", "server.js"), "require('./lib')");
        File.WriteAllText(Path.Combine(repo, "tools", "mcp", "lib.js"), "v1");
        File.WriteAllText(Path.Combine(repo, "tools", "mcp", "data", "seed.json"), "{}");
        Assert.True(Git("init -q", repo) && Git("add -A", repo) && Git(Commit, repo),
            "git could not make the repository this test needs: UNDECIDED, not green.");
        // The data folder in git's case lists a file under the folder written otherwise: that folder is not walked.
        var definition = JsonNode.Parse("""{ "command": "node", "args": ["Tools/MCP/server.js", "tools/mcp/data"] }""")!.AsObject();

        var paths = (await RepoMcpFiles.CoverAsync(definition, repo)).Files!.Select(f => f.Path).ToList();

        Assert.Contains(paths, p => p.EndsWith("/lib.js", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task InAGitRepository_TheFilesOfASubmoduleItRuns_AreCovered()
    {
        var sub = Path.Combine(_state, "git-sub");
        Directory.CreateDirectory(Path.Combine(sub, "lib"));
        File.WriteAllText(Path.Combine(sub, "index.js"), "require('./lib/tool')");
        File.WriteAllText(Path.Combine(sub, "lib", "tool.js"), "v1");
        var repo = Path.Combine(_state, "git-outer");
        Directory.CreateDirectory(repo);
        File.WriteAllText(Path.Combine(repo, "README.md"), "outer");
        Assert.True(Git("init -q", sub) && Git("add -A", sub) && Git(Commit, sub)
                    && Git("init -q", repo) && Git("add -A", repo) && Git(Commit, repo)
                    && Git($"-c protocol.file.allow=always submodule add -q \"{sub}\" vendor/mcp", repo) && Git(Commit, repo),
            "git could not make the repositories this test needs: UNDECIDED, not green.");
        var definition = JsonNode.Parse("""{ "command": "node", "args": ["vendor/mcp/index.js"] }""")!.AsObject();

        var before = (await RepoMcpFiles.CoverAsync(definition, repo)).Files!;
        File.WriteAllText(Path.Combine(repo, "vendor", "mcp", "lib", "tool.js"), "v2");   // what a submodule update does

        Assert.Contains("vendor/mcp/lib/tool.js", before.Select(f => f.Path));
        Assert.NotEqual(before, (await RepoMcpFiles.CoverAsync(definition, repo)).Files!);
    }

    private static bool Git(string args, string workDir)
    {
        try
        {
            using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("git", args)
            {
                WorkingDirectory = workDir,
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
            })!;
            p.WaitForExit(20_000);
            return p.ExitCode == 0;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { return false; }
    }

    [Fact]
    public async Task ALinkThatLeadsOutOfTheRepository_IsCoveredByItsOwnTarget_AndNothingBehindItIsRead()
    {
        Write("tools/mcp/server.js", "v1");
        var outside = Path.Combine(_state, "outside.txt");
        File.WriteAllText(outside, "outside, first");
        var link = Path.Combine(_repo, "tools", "mcp", "data.txt");
        try { File.CreateSymbolicLink(link, outside); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        Assert.True(new FileInfo(link).LinkTarget is not null,
            "The link could not be created (symbolic-link creation privilege): this test is UNDECIDED, not green.");
        var definition = JsonNode.Parse("""{ "command": "node", "args": ["tools/mcp/server.js"] }""")!.AsObject();
        string DataDigest(RepoMcpCoverage c) => c.Files!.Single(f => f.Path == "tools/mcp/data.txt").Digest;

        var before = DataDigest(await RepoMcpFiles.CoverAsync(definition, _repo));
        File.WriteAllText(outside, "outside, rewritten to another length");
        var behind = DataDigest(await RepoMcpFiles.CoverAsync(definition, _repo));
        File.Delete(link);
        File.CreateSymbolicLink(link, Path.Combine(_state, "elsewhere.txt"));
        var retargeted = DataDigest(await RepoMcpFiles.CoverAsync(definition, _repo));

        Assert.StartsWith("link:", before);
        Assert.Equal(before, behind);                                      // what lies behind it was never read
        Assert.NotEqual(before, retargeted);                               // the link itself is covered
    }

    [Fact]
    public async Task AScriptThatIsALinkOutOfTheRepository_IsCoveredByItsTarget()
    {
        var outside = Path.Combine(_state, "server.js");
        File.WriteAllText(outside, "v1");
        Directory.CreateDirectory(Path.Combine(_repo, "tools", "mcp"));
        var link = Path.Combine(_repo, "tools", "mcp", "server.js");
        try { File.CreateSymbolicLink(link, outside); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        Assert.True(new FileInfo(link).LinkTarget is not null,
            "The link could not be created (symbolic-link creation privilege): this test is UNDECIDED, not green.");
        var definition = JsonNode.Parse("""{ "command": "node", "args": ["tools/mcp/server.js"] }""")!.AsObject();

        var coverage = await RepoMcpFiles.CoverAsync(definition, _repo);

        Assert.StartsWith("link:", coverage.Files!.Single(f => f.Path == "tools/mcp/server.js").Digest);
    }

    [Fact]
    public async Task FoldersTooLargeToCover_AreAskedAboutAtEveryStart_AndNoAgreementIsKept()
    {
        for (var i = 0; i < 4; i++) Write($"tools/mcp/f{i}.js", "x");
        Write(".mcp.json", """{ "mcpServers": { "local": { "command": "node", "args": ["tools/mcp/f0.js"] } } }""");
        var (service, approval, started) = Service();
        service.CoveredFilesCap = 3;

        await service.RefreshAsync();
        await service.RefreshAsync();

        Assert.Equal(2, approval.Prompts);
        Assert.Equal(2, started.Count);
        Assert.False(File.Exists(Path.Combine(_state, "consents.json")), "an agreement that covers nothing was kept");
        // The question says it comes back, never that the server starts on its own from now on.
        Assert.Contains(Strings.McpRepoServerAskedEveryStart, approval.LastMessage);
        Assert.DoesNotContain(Strings.McpRepoServerStartsOnItsOwn, approval.LastMessage);
        await service.DisposeAsync();
    }

    [Fact]
    public async Task AServerWhoseFilesAreCovered_IsToldItStartsOnItsOwn()
    {
        Write("tools/mcp/server.js", "v1");
        Write(".mcp.json", """{ "mcpServers": { "local": { "command": "node", "args": ["tools/mcp/server.js"] } } }""");
        var (service, approval, _) = Service();

        await service.RefreshAsync();

        Assert.Contains(Strings.McpRepoServerStartsOnItsOwn, approval.LastMessage);
        Assert.DoesNotContain(Strings.McpRepoServerAskedEveryStart, approval.LastMessage);
        await service.DisposeAsync();
    }

    [Fact]
    public async Task AServerWhoseFilesCannotBeCovered_IsNotRestarted_AndSaysWhy()
    {
        for (var i = 0; i < 4; i++) Write($"tools/mcp/f{i}.js", "x");
        Write(".mcp.json", """{ "mcpServers": { "local": { "command": "node", "args": ["tools/mcp/f0.js"] } } }""");
        var (service, _, started) = Service();
        service.CoveredFilesCap = 3;
        await service.RefreshAsync();
        Assert.Single(started);

        _clients[^1].Die();

        var status = await StatusOnceAsync(service, "local", s => s.Error?.Contains("not restarted") == true);
        Assert.Contains("more than 3 files", status.Error);
        Assert.Single(started);
        await service.DisposeAsync();
    }

    [Fact]
    public async Task TheNoteThatFilesCannotBeCovered_IsSaidAgain_OnceTheyCouldBeAndThenNot()
    {
        var name = "uncovered" + Guid.NewGuid().ToString("N");
        for (var i = 0; i < 4; i++) Write($"tools/mcp/f{i}.js", "x");
        Write(".mcp.json", $$"""{ "mcpServers": { "{{name}}": { "command": "node", "args": ["tools/mcp/f0.js"] } } }""");
        var (service, _, _) = Service();
        service.CoveredFilesCap = 3;
        int Notes() => Diagnostics.Snapshot().Count(e => e.Detail.Contains($"'{name}'") && e.Detail.Contains("no agreement can cover"));

        await service.RefreshAsync();                                      // four files: said
        File.Delete(Path.Combine(_repo, "tools", "mcp", "f3.js"));
        await service.RefreshAsync();                                      // three: covered
        Write("tools/mcp/f3.js", "x");
        await service.RefreshAsync();                                      // four again: said again

        Assert.Equal(2, Notes());
        await service.DisposeAsync();
    }

    [Fact]
    public async Task ARestart_ChecksTheAgreementItsServerCarries_EvenWhenARefreshEmptiedTheMap()
    {
        Write("tools/mcp/server.js", "v1");
        Write(".mcp.json", LocalScript);
        var (service, _, started) = Service();
        await service.RefreshAsync();

        // What a refresh does to the map while a restart is past its last look at the list of servers.
        typeof(McpToolService).GetField("_agreedRepository", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(service, new Dictionary<McpServerConfig, (string Root, RepoMcpServer Server)>());
        Write("tools/mcp/server.js", "v2");
        _clients[^1].Die();

        var status = await StatusOnceAsync(service, "local", s => s.Error?.Contains("server.js") == true);
        Assert.Contains("not restarted", status.Error);
        Assert.Single(started);
        await service.DisposeAsync();
    }

    [Fact]
    public async Task PastTheHashingBudget_AFileIsJudgedByItsSizeAndWriteTime()
    {
        var data = Write("tools/mcp/model.bin", "aaaa");
        Write("tools/mcp/server.js", "x");
        var definition = JsonNode.Parse("""{ "command": "node", "args": ["tools/mcp/server.js"] }""")!.AsObject();
        File.SetLastWriteTimeUtc(data, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var before = (await RepoMcpFiles.CoverAsync(definition, _repo, hashBudget: 0)).Files!;

        File.WriteAllText(data, "bbbb");                                      // the same size, other content
        File.SetLastWriteTimeUtc(data, new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc));
        var after = (await RepoMcpFiles.CoverAsync(definition, _repo, hashBudget: 0)).Files!;

        Assert.NotEqual(before.Single(f => f.Path == "tools/mcp/model.bin").Digest,
                        after.Single(f => f.Path == "tools/mcp/model.bin").Digest);
    }

    // ── A real process: nothing runs before the agreement ───────────────────

    [Fact]
    public async Task NoProcessRuns_BeforeTheAgreement_AndItRunsInTheRepository()
    {
        var witness = Path.Combine(_state, "witness.txt");
        var (command, args) = OperatingSystem.IsWindows()
            ? ("cmd.exe", new[] { "/c", "cd > " + witness })   // no blank in the temp path: cmd reads no escaped quote
            : ("/bin/sh", new[] { "-c", "pwd > '" + witness + "'" });
        Write(".mcp.json", JsonSerializer.Serialize(new { mcpServers = new { witness = new { command, args } } }));

        var config = new InferpalConfig { McpEnabled = false };
        var approval = new RecordingApproval(config, ApprovalDecision.Deny);
        var service = new McpToolService(config, approval, clientFactory: null,
                                         tokenStore: new McpTokenStore(Path.Combine(_state, "tokens.bin")),
                                         sharedServersJson: () => null, workspaceRoot: () => _repo)
        {
            Consents = new RepoMcpConsents { PathOverride = Path.Combine(_state, "consents.json") },
        };
        config.McpEnabled = true;

        await service.RefreshAsync();
        await Task.Delay(500);
        Assert.False(File.Exists(witness));                               // declined: nothing ran

        approval.Answer = ApprovalDecision.Once;
        await service.RefreshAsync();                                     // the handshake fails: the witness exits
        for (var waited = 0; !File.Exists(witness) && waited < 30_000; waited += 100) await Task.Delay(100);
        Assert.True(File.Exists(witness), "the agreed server never ran");
        // The same FOLDER, not the same spelling: on macOS the temporary folder is reached through a link
        // (/var → /private/var), and the shell reports the resolved path.
        var ranIn = File.ReadAllText(witness).Trim();
        Assert.True(File.Exists(Path.Combine(ranIn, ".mcp.json")), $"the server ran in '{ranIn}', not in the repository '{_repo}'");
        await service.DisposeAsync();
    }
}

public sealed class MiniYamlTests
{
    [Fact]
    public void ReadsMapsListsAndScalars()
    {
        var node = MiniYaml.Parse("a: 1\nb:\n  - x\n  - 'y z'\nc:\n  d: \"q\\\"uote\"\n  e: [1, \"two\"]\nf: {}\ng:\n- h: i\n  j: k\n");
        Assert.Equal("1", node!["a"]!.GetValue<string>());
        Assert.Equal(["x", "y z"], node["b"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal("q\"uote", node["c"]!["d"]!.GetValue<string>());
        Assert.Equal(["1", "two"], node["c"]!["e"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Empty(node["f"]!.AsObject());
        Assert.Equal("k", node["g"]![0]!["j"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("a: *nope\n", "names no anchor")]
    [InlineData("a:\n\tb: 1\n", "a tab in the indentation")]
    [InlineData("a: 1\n---\nb: 2\n", "several YAML documents")]
    [InlineData("a:\n  <<: 1\n", "merges a map")]
    public void RefusesWhatItDoesNotRead_ByName(string text, string why)
    {
        var ex = Assert.Throws<FormatException>(() => MiniYaml.Parse(text));
        Assert.Contains(why, ex.Message);
    }

    /// <summary>
    /// A literal block keeps its lines as written — a <c>#</c> is a Markdown heading there, a blank line separates two
    /// paragraphs, a deeper line keeps its indentation — and the next key ends it. What a Continue prompt or rule is.
    /// </summary>
    [Fact]
    public void ALiteralBlock_KeepsItsLinesHeadingsAndBlankLines()
    {
        var node = MiniYaml.Parse("prompts:\n  - name: review\n    prompt: |\n      # Review\n\n      Check:\n        - errors\n      \tcode\n\n  - name: next\n");
        var prompts = node!["prompts"]!.AsArray();
        Assert.Equal("# Review\n\nCheck:\n  - errors\n\tcode\n", prompts[0]!["prompt"]!.GetValue<string>());
        Assert.Equal("next", prompts[1]!["name"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("a: |\n  one\n  two\n\n\nb: x\n", "one\ntwo\n")]        // clip: one final line break
    [InlineData("a: |-\n  one\n  two\n\nb: x\n", "one\ntwo")]            // strip: none
    [InlineData("a: |+\n  one\n\n\nb: x\n", "one\n\n\n")]                // keep: every one
    [InlineData("a: >\n  one\n  two\n\n  three\nb: x\n", "one two\nthree\n")]
    [InlineData("a: >-\n  one\n    deeper\n  two\nb: x\n", "one\n  deeper\ntwo")]
    public void ABlockScalar_FoldsAndChompsAsYamlSays(string text, string expected) =>
        Assert.Equal(expected, MiniYaml.Parse(text)!["a"]!.GetValue<string>());

    /// <summary>
    /// What a real Continue configuration does: a directive, an anchor holding the shared keys, and each model merging
    /// them — the keys the model writes itself win.
    /// </summary>
    [Fact]
    public void AnchorsAndMergeKeys_GiveTheirKeys_TheWrittenOnesWin()
    {
        var node = MiniYaml.Parse("""
            %YAML 1.1
            ---
            defaults: &d
              provider: openai
              apiBase: "http://h/v1/"
            models:
              - name: one
                <<: *d
                model: m1
              - name: two
                provider: ollama
                <<: *d
            again: *d
            """);
        var models = node!["models"]!.AsArray();
        Assert.Equal("openai", models[0]!["provider"]!.GetValue<string>());
        Assert.Equal("http://h/v1/", models[0]!["apiBase"]!.GetValue<string>());
        Assert.Equal("ollama", models[1]!["provider"]!.GetValue<string>());   // written after the merge, still wins
        Assert.Equal("openai", node["again"]!["provider"]!.GetValue<string>());
        Assert.False(models[0]!.AsObject().ContainsKey("<<"));
    }

    [Fact]
    public void AnInlineMap_AndKeysPaddedBeforeTheirColon_AreRead()
    {
        var node = MiniYaml.Parse("context:\n  - provider: open\n    params: { onlyPinned: true, n: 3 }\nname    : local\nroles   :\n  - chat\n");
        Assert.True(node!["context"]![0]!["params"]!["onlyPinned"]!.GetValue<bool>());
        Assert.Equal("3", node["context"]![0]!["params"]!["n"]!.GetValue<string>());
        Assert.Equal("local", node["name"]!.GetValue<string>());
        Assert.Equal(["chat"], node["roles"]!.AsArray().Select(n => n!.GetValue<string>()));
    }

    [Theory]
    [InlineData("\"a\\tb\\nc\"", "a\tb\nc")]
    [InlineData("\"q\\\"uote \\\\ slash \\/\"", "q\"uote \\ slash /")]
    [InlineData("\"\\x41\\u00e9\\U0001F600\"", "Aé😀")]
    [InlineData("\"no\\_break\\Pnext\"", "no\u00a0break\u2029next")]
    public void ADoubleQuotedValue_ReadsYamlsEscapes(string value, string expected) =>
        Assert.Equal(expected, MiniYaml.Parse("v: " + value + "\n")!["v"]!.GetValue<string>());

    [Theory]
    [InlineData("\"C:\\qux\"", "\\q is not a YAML escape")]       // a Windows path: Regex.Unescape threw ArgumentException here
    [InlineData("\"\\u00\"", "\\u needs 4 hexadecimal digits")]
    [InlineData("\"end\\\"", "lone backslash")]                    // the closing quote escaped: the value never closes
    public void AnEscapeYamlDoesNotKnow_IsAFormatError_NamingItsLine(string value, string why)
    {
        var ex = Assert.Throws<FormatException>(() => MiniYaml.Parse("a: 1\nv: " + value + "\n"));
        Assert.Contains(why, ex.Message);
        Assert.Contains("line 2", ex.Message);
    }
}

public partial class HostServerTests
{
    /// <summary>
    /// VS Code: a server the repository declares is asked about in the editor before anything runs — the host's
    /// approval request, with the server, its file and what it runs; declined, it is listed as not started.
    /// </summary>
    [Fact]
    public async Task ARepositorysMcpServer_IsAskedAboutInTheEditor_BeforeItRuns()
    {
        using var h = CreateHarness(cfg => cfg.McpEnabled = true);
        h.Target.ApprovalAnswer = 0;   // declined: nothing is written to the agreements
        File.WriteAllText(Path.Combine(h.RootDir, ".mcp.json"),
            """{ "mcpServers": { "team-tools": { "command": "inferpal-no-such-program", "args": ["--serve"] } } }""");

        await h.InitializeAsync().WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
        await h.Target.ApprovalEntered.Task.WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

        Assert.Contains("team-tools", h.Target.LastApprovalMessage);
        Assert.Contains(".mcp.json", h.Target.LastApprovalMessage);
        Assert.Contains("inferpal-no-such-program --serve", h.Target.LastApprovalMessage);
    }
}
