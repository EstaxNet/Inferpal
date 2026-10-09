using System.IO;
using System.Text.Json;
using Inferpal.Config;
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
        Write(".continue/mcpServers/bad.yaml", "mcpServers:\n  - name: x\n    command: |\n      npx\n");

        var problems = RepoMcpServers.Read(_repo).Problems;
        Assert.Contains(problems, p => p.Source == ".cursor/mcp.json");
        Assert.Contains(problems, p => p.Source == ".continue/mcpServers/bad.yaml" && p.Reason.Contains("block scalar"));
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
        public ApprovalDecision Answer = answer;

        protected override Task<ApprovalDecision> PromptUserAsync(string message, DiffInfo? diff, CancellationToken ct)
        {
            Prompts++;
            LastMessage = message;
            return Task.FromResult(Answer);
        }
    }

    private sealed class FakeClient(McpServerConfig config, List<McpServerConfig> started) : IMcpClient
    {
        public string ServerName => config.Name;
        public string? LastError => null;
        public bool NeedsAuthorization => false;
        public Task<bool> StartAsync(CancellationToken ct) { lock (started) started.Add(config); return Task.FromResult(true); }
        public Task<IReadOnlyList<McpToolInfo>?> ListToolsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<McpToolInfo>?>([]);
        public Task<string> CallToolAsync(string toolName, JsonElement arguments, CancellationToken ct) => Task.FromResult("ok");
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public event Action? ToolsChanged { add { } remove { } }
        public event Action? Closed { add { } remove { } }
    }

    private (McpToolService Service, RecordingApproval Approval, List<McpServerConfig> Started) Service(
        InferpalConfig? config = null, ApprovalDecision answer = ApprovalDecision.Once,
        Func<RepoMcpServer, RepoMcpInput, CancellationToken, Task<string?>>? askInput = null)
    {
        config ??= new InferpalConfig();
        config.McpEnabled = false;                      // no refresh at construction: each test refreshes itself
        var approval = new RecordingApproval(config, answer);
        var started = new List<McpServerConfig>();
        var service = new McpToolService(config, approval, cfg => new FakeClient(cfg, started),
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
        Assert.Equal(Path.GetFullPath(_repo), File.ReadAllText(witness).Trim());
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
    [InlineData("a: |\n  text\n", "block scalar")]
    [InlineData("a: &x 1\n", "anchor")]
    [InlineData("a: 1\n---\nb: 2\n", "several YAML documents")]
    public void RefusesWhatItDoesNotRead_ByName(string text, string why)
    {
        var ex = Assert.Throws<FormatException>(() => MiniYaml.Parse(text));
        Assert.Contains(why, ex.Message);
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
