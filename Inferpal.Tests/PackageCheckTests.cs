using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using Inferpal.Config;
using Inferpal.Services;
using Inferpal.Services.CodeActions;
using Inferpal.Services.Docs;
using Inferpal.Services.Editor;
using Inferpal.Services.Execution;
using Inferpal.Services.Lsp;
using Inferpal.Services.Mcp;
using Inferpal.Services.Rag;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A package the model adds that no source of the project knows is refused before the approval card; one that exists,
/// or that cannot be told, goes to the card as it always did.
/// </summary>
/// <remarks>
/// ⚠ The refusal is a guard against an invented name, never a frontier: every answer that is not a certain "absent" —
/// a source that does not answer, a private index, a tool that is not installed, a name the project serves itself, the
/// value of an option — lets the call through. A wrong refusal blocks a real package with no way for the model to get
/// past it. No test here reaches a registry or starts a process: the lookup is a fake, and the parsers read outputs
/// captured from the real tools.
/// </remarks>
public class PackageCheckTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("inferpal-pkgcheck").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private sealed class FakeLookup(Func<AddedPackage, PackageLookupResult> answer, Func<CancellationToken, Task<IReadOnlyList<string>>>? close = null)
        : IPackageLookup
    {
        public List<AddedPackage> Asked { get; } = [];

        public Task<PackageLookupResult> LookUpAsync(AddedPackage package, CancellationToken ct)
        {
            lock (Asked) Asked.Add(package);
            return Task.FromResult(answer(package));
        }

        public Task<IReadOnlyList<string>> CloseNamesAsync(AddedPackage package, CancellationToken ct) =>
            close?.Invoke(ct) ?? Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class SlowLookup : IPackageLookup
    {
        public async Task<PackageLookupResult> LookUpAsync(AddedPackage package, CancellationToken ct)
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return PackageLookupResult.Absent("PyPI");
        }

        public Task<IReadOnlyList<string>> CloseNamesAsync(AddedPackage package, CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class CountingApproval : IApprovalService
    {
        public int Calls { get; private set; }

        public Task<bool> RequestApprovalAsync(string toolName, string details, CancellationToken ct,
                                               string? subject = null, DiffInfo? diff = null, bool forcePrompt = false)
        {
            Calls++;
            return Task.FromResult(true);
        }
    }

    /// <summary>The real rules: the repository's deny overlay under the root, read as at every approval.</summary>
    private sealed class RuleApproval(string root) : ApprovalServiceBase(new InferpalConfig(), () => root)
    {
        public int Prompts { get; private set; }
        protected override Task<ApprovalDecision> PromptUserAsync(string message, DiffInfo? diff, CancellationToken ct)
        {
            Prompts++;
            return Task.FromResult(ApprovalDecision.Once);
        }
    }

    private ToolRegistry Registry(IApprovalService approval, IPackageLookup lookup, bool enabled = true)
    {
        var config = new InferpalConfig { PackageCheckEnabled = enabled, SmartFixEnabled = false };
        var client = new FakeInferenceProvider();
        var editor = new NullEditorSurface();
        var index  = new ProjectIndexService(client, config, new LspSemanticProvider());
        index.SetRoot(_root);
        return new ToolRegistry(editor, approval, config, index, client, new ProjectMapService(editor),
                                new McpToolService(config, approval), new DocsIndexService(client, config),
                                new OpenDocumentOverlay(), new NullDebugSession(), packageCheck: new PackageCheck(lookup));
    }

    private static JsonElement Args(object o) => JsonSerializer.SerializeToElement(o);

    private string Requirements(string content)
    {
        var path = Path.Combine(_root, "requirements.txt");
        File.WriteAllText(path, content);
        return path;
    }

    private string Write(string relative, string content)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private IEnumerable<string> Names(string tool, object args, ShellState? shell = null) =>
        PackageCheck.Added(tool, Args(args), _root, shell).Select(a => a.Name);

    // ── the funnel ─────────────────────────────────────────────────────────────

    /// <summary>
    /// ⚠ A call the person's rules refuse sends no name anywhere: checked before the card, the name would reach the public
    /// registry under a `deny run_command`, and the card would then refuse the call.
    /// </summary>
    [Fact]
    public async Task ACallTheRulesRefuse_SendsNoNameToTheRegistry_AndIsRefusedByTheRule()
    {
        var lookup   = new FakeLookup(_ => PackageLookupResult.Absent("PyPI"));
        var approval = new RuleApproval(_root);
        var tools    = Registry(approval, lookup);
        var call     = Args(new { command = "pip install ibantools" });

        // Reference arm: without the rule, the name is looked up and the invented package refused.
        var unruled = await tools.ExecuteAsync("run_command", call, CancellationToken.None);
        Assert.Contains("does not exist on PyPI", unruled, StringComparison.Ordinal);
        Assert.Single(lookup.Asked);

        DenyRules("deny run_command .*");
        lookup.Asked.Clear();
        var refused = await tools.ExecuteAsync("run_command", call, CancellationToken.None);

        Assert.Empty(lookup.Asked);
        Assert.Contains("permission rules refuse", refused, StringComparison.Ordinal);   // the rule's refusal, not another
        Assert.Equal(0, approval.Prompts);
    }

    private void DenyRules(params string[] rules) =>
        Write(Path.Combine(".inferpal", "permissions.json"), JsonSerializer.Serialize(new { rules }));

    [Fact]
    public async Task AWriteTheRulesRefuse_IsJudgedOnThePathTheCardSees_NotTheOneTheModelWrote()
    {
        // A rule written for the absolute path, a model that writes a relative one: the card resolves it, so must the check.
        DenyRules($"deny write_file ^{System.Text.RegularExpressions.Regex.Escape(_root)}");
        var lookup = new FakeLookup(_ => PackageLookupResult.Absent("npm"));
        var tools  = Registry(new RuleApproval(_root), lookup);

        var refused = await tools.ExecuteAsync("write_file",
            Args(new { path = "package.json", content = "{ \"dependencies\": { \"left-pad-invented\": \"1.0.0\" } }" }), CancellationToken.None);

        Assert.Empty(lookup.Asked);
        Assert.Contains("permission rules refuse", refused, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ABatchTheRulesRefuse_SendsNoName_WhicheverOfItsFilesTheRuleNames()
    {
        var path = Requirements("pytest\n");
        DenyRules("deny apply_edits .*requirements\\.txt$");
        var lookup = new FakeLookup(_ => PackageLookupResult.Absent("PyPI"));
        var tools  = Registry(new RuleApproval(_root), lookup);

        await tools.ExecuteAsync("apply_edits",
            Args(new { edits = new[] { new { path, old_content = "pytest\n", new_content = "pytest\nibantools\n" } } }), CancellationToken.None);

        Assert.Empty(lookup.Asked);
        Assert.Equal("pytest\n", File.ReadAllText(path));
    }

    [Fact]
    public async Task UnderTdd_TheGuardThatWrapsTheCard_KeepsThePersonsRules()
    {
        // /tdd runs its fix rounds on a sibling registry whose approval is a guard around the card: the guard holds no
        // rules, the registry still asks the ones it was built with.
        DenyRules("deny run_command .*");
        var lookup  = new FakeLookup(_ => PackageLookupResult.Absent("PyPI"));
        var real    = new RuleApproval(_root);
        var sibling = Registry(real, lookup).WithApprovalService(new Inferpal.Services.Commands.TestFileWriteGuard(real));

        await sibling.ExecuteAsync("run_command", Args(new { command = "pip install ibantools" }), CancellationToken.None);

        Assert.Empty(lookup.Asked);
    }

    [Fact]
    public async Task AnInventedPackage_IsRefusedBeforeTheApprovalCard_AndNothingIsWritten()
    {
        var path     = Requirements("pytest\n");
        var approval = new CountingApproval();
        var lookup   = new FakeLookup(p => p.Name == "ibantools" ? PackageLookupResult.Absent("PyPI") : PackageLookupResult.Exists("PyPI"));
        var tools    = Registry(approval, lookup);

        var result = await tools.ExecuteAsync("apply_diff", Args(new { path, old_content = "pytest\n", new_content = "pytest\nibantools==1.0\n" }),
                                              CancellationToken.None);

        Assert.StartsWith("Error: the package 'ibantools' does not exist on PyPI", result, StringComparison.Ordinal);
        Assert.Contains("the call was NOT executed", result, StringComparison.Ordinal);
        Assert.Equal(0, approval.Calls);
        Assert.Equal("pytest\n", File.ReadAllText(path));
        var asked = Assert.Single(lookup.Asked);
        Assert.Equal((PackageEcosystem.PyPI, "ibantools", _root, _root), (asked.Ecosystem, asked.Name, asked.Directory, asked.Root));
    }

    [Fact]
    public async Task ACallThePersonMade_IsNeitherAskedNorRefused_AndTheNextModelCallIs()
    {
        // `/run pip install ibantools`: the person typed the name and knows whether it exists. Their call goes to the card
        // as before; once the scope ends, a call is the model's again and is checked.
        var path     = Requirements("pytest\n");
        var approval = new CountingApproval();
        var lookup   = new FakeLookup(_ => PackageLookupResult.Absent("PyPI"));
        var tools    = Registry(approval, lookup);
        var adds     = Args(new { path, old_content = "pytest\n", new_content = "pytest\nibantools==1.0\n" });

        string theirs;
        using (PackageCheck.ThePersonsOwnCalls())
            theirs = await tools.ExecuteAsync("apply_diff", adds, CancellationToken.None);

        Assert.DoesNotContain("does not exist", theirs, StringComparison.Ordinal);
        Assert.Empty(lookup.Asked);
        Assert.Equal(1, approval.Calls);
        Assert.Contains("ibantools", File.ReadAllText(path), StringComparison.Ordinal);

        File.WriteAllText(path, "pytest\n");
        var models = await tools.ExecuteAsync("apply_diff", adds, CancellationToken.None);
        Assert.StartsWith("Error: the package 'ibantools' does not exist on PyPI", models, StringComparison.Ordinal);
    }

    [Fact]
    public void AManifestSavedInTheLegacyCodePage_IsReadAsTheEditReadsIt()
    {
        // A BOM-less requirements.txt in the machine's legacy code page, with an accented comment: read as UTF-8, its text
        // no longer held the edit's old_content, and the name the edit adds was never asked.
        var legacy = Inferpal.Services.Tools.TextFileEncoding.LegacyEncoding;
        const string before = "# café\npytest\n";
        var bytes = legacy.GetBytes(before);
        Assert.True(legacy.GetString(bytes) == before && !Inferpal.Services.Tools.TextFileEncoding.Utf8NoBom.GetString(bytes).Equals(before),
                    "this code page does not write the accent apart from UTF-8: the test is UNDECIDED");
        var path = Path.Combine(_root, "requirements.txt");
        File.WriteAllBytes(path, bytes);

        Assert.Equal(["ibantools"], Names("apply_diff", new { path, old_content = before, new_content = before + "ibantools==1.0\n" }));
    }

    [Fact]
    public void BothFrontEndsSlashCommandTools_RunAsThePersonsOwnCalls()
    {
        // The two places a typed command (/run, /read, /git…) reaches the registry, in Visual Studio and in the host that
        // serves VS Code and the ACP agent. Read without comments: a call left in a comment is not a call.
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "README.md"))) root = root.Parent;
        Assert.NotNull(root);
        foreach (var file in new[] { "Inferpal/ToolWindow/InferpalToolWindowData.ToolInvocation.cs", "Inferpal.Host/HostSlashCommands.cs" })
        {
            var code = ConventionCoverageTests.CodeOnly(Path.Combine(root!.FullName, file));
            Assert.Contains("SlashToolAction.Shown(", code, StringComparison.Ordinal);
            Assert.Contains("PackageCheck.ThePersonsOwnCalls()", code, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task TheRefusal_OffersTheCloseNamesTheSourceKnows()
    {
        var approval = new CountingApproval();
        var lookup   = new FakeLookup(_ => PackageLookupResult.Absent("the npm registry (registry.npmjs.org)"),
                                      _ => Task.FromResult<IReadOnlyList<string>>(["pdf-lib", "@pdf-lib/upng"]));

        var result = await Registry(approval, lookup).ExecuteAsync("run_command", Args(new { command = "npm install @pdf-lib/pdf-lib" }), CancellationToken.None);

        Assert.Contains("'@pdf-lib/pdf-lib' does not exist on the npm registry (registry.npmjs.org)", result, StringComparison.Ordinal);
        Assert.Contains("Packages that exist with close names: pdf-lib, @pdf-lib/upng.", result, StringComparison.Ordinal);
        Assert.Equal(0, approval.Calls);
    }

    [Fact]
    public async Task APackageThatExists_GoesToTheApprovalCard()
    {
        var path     = Requirements("pytest\n");
        var approval = new CountingApproval();

        var result = await Registry(approval, new FakeLookup(_ => PackageLookupResult.Exists("PyPI"))).ExecuteAsync(
            "apply_diff", Args(new { path, old_content = "pytest\n", new_content = "pytest\nschwifty==2024.1.1\n" }), CancellationToken.None);

        Assert.DoesNotContain("does not exist", result, StringComparison.Ordinal);
        Assert.Equal(1, approval.Calls);
        Assert.Contains("schwifty", File.ReadAllText(path), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASourceThatCannotTell_NeverRefuses()
    {
        var path     = Requirements("pytest\n");
        var approval = new CountingApproval();

        var result = await Registry(approval, new FakeLookup(_ => PackageLookupResult.Unknown("PyPI did not answer"))).ExecuteAsync(
            "apply_diff", Args(new { path, old_content = "pytest\n", new_content = "pytest\nibantools==1.0\n" }), CancellationToken.None);

        Assert.DoesNotContain("does not exist", result, StringComparison.Ordinal);
        Assert.Equal(1, approval.Calls);
        Assert.Contains("ibantools", File.ReadAllText(path), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ALookupThatThrows_LetsTheCallThrough()
    {
        var path     = Requirements("pytest\n");
        var approval = new CountingApproval();

        var result = await Registry(approval, new FakeLookup(_ => throw new InvalidOperationException("the registry exploded"))).ExecuteAsync(
            "apply_diff", Args(new { path, old_content = "pytest\n", new_content = "pytest\nibantools==1.0\n" }), CancellationToken.None);

        Assert.DoesNotContain("does not exist", result, StringComparison.Ordinal);
        Assert.Equal(1, approval.Calls);
    }

    [Fact]
    public async Task TurnedOff_NoSourceIsAsked()
    {
        var path     = Requirements("pytest\n");
        var approval = new CountingApproval();
        var lookup   = new FakeLookup(_ => PackageLookupResult.Absent("PyPI"));

        await Registry(approval, lookup, enabled: false).ExecuteAsync(
            "apply_diff", Args(new { path, old_content = "pytest\n", new_content = "pytest\nibantools==1.0\n" }), CancellationToken.None);

        Assert.Empty(lookup.Asked);
        Assert.Equal(1, approval.Calls);
    }

    [Fact]
    public void TheSetting_IsOnUnlessTurnedOff() => Assert.True(new InferpalConfig().PackageCheckEnabled);

    [Fact]
    public void TheSuite_NeverReachesARegistry()
    {
        // The shared check every registry of the suite falls back to answers "cannot tell" without a process or a request.
        Assert.IsType<NoAnswerPackageLookup>(PackageCheck.Shared.Lookup);
    }

    [Fact]
    public async Task ALookupPastItsBudget_LetsTheCallThrough()
    {
        var check = new PackageCheck(new SlowLookup(), budget: TimeSpan.FromMilliseconds(200));
        var clock = Stopwatch.StartNew();

        var refusal = await check.RefusalAsync("run_command", Args(new { command = "pip install ibantools" }), _root, null, CancellationToken.None);

        Assert.Null(refusal);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(20), $"the budget did not stop the lookup ({clock.Elapsed})");
    }

    [Fact]
    public async Task TheCallersStop_IsNotSwallowed()
    {
        using var stop = new CancellationTokenSource();
        await stop.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new PackageCheck(new SlowLookup()).RefusalAsync("run_command", Args(new { command = "pip install ibantools" }), _root, null, stop.Token));
    }

    [Fact]
    public async Task CloseNamesThatTakeTooLong_LeaveTheRefusalStanding()
    {
        var lookup = new FakeLookup(_ => PackageLookupResult.Absent("PyPI"), async ct =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return ["never"];
        });
        var check = new PackageCheck(lookup, closeNamesBudget: TimeSpan.FromMilliseconds(200));

        var refusal = await check.RefusalAsync("run_command", Args(new { command = "pip install ibantools" }), _root, null, CancellationToken.None);

        Assert.StartsWith("Error: the package 'ibantools' does not exist on PyPI", refusal, StringComparison.Ordinal);
        Assert.DoesNotContain("close names", refusal, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("App.csproj:stream")]
    [InlineData("bad\0name/package.json")]
    public async Task AMalformedPath_NeverThrowsOutOfTheCheck(string path)
    {
        var check = new PackageCheck(new FakeLookup(_ => PackageLookupResult.Absent("nuget.org")));

        var refusal = await check.RefusalAsync("apply_diff", Args(new { path, old_content = "a", new_content = "<PackageReference Include=\"X\" />" }), _root, null, CancellationToken.None);

        Assert.Null(refusal);
    }

    // ── which names a call adds ──────────────────────────────────────────────────

    [Fact]
    public void ACall_AddsOnlyTheNamesItBrings_NeverTheOnesAlreadyThere_NorFlagsOrFiles()
    {
        Write("App.csproj", "<Project><ItemGroup><PackageReference Include=\"Newtonsoft.Json\" Version=\"13.0.3\" /></ItemGroup></Project>");
        Requirements("pytest\n");

        Assert.Equal(["Apache.Parquet"], Names("write_file", new
        {
            path = "App.csproj",
            content = "<Project><ItemGroup><PackageReference Include=\"Newtonsoft.Json\" Version=\"13.0.3\" />"
                    + "<PackageReference Include=\"Apache.Parquet\" Version=\"1.0.0\" /></ItemGroup></Project>",
        }));
        Assert.Equal(["Serilog", "Polly"], Names("write_file", new
        {
            path = "App.csproj",
            content = "<Project><ItemGroup><PackageReference Include=\"Newtonsoft.Json\" Version=\"13.0.3\" />"
                    + "<PackageReference Include=\"Serilog;Polly\" /><PackageReference Include=\"@(Extra)\" />"
                    + "<PackageReference Include=\"$(Analyzer)\" /></ItemGroup></Project>",
        }));
        Assert.Equal(["ibantools"], Names("apply_diff", new { path = "requirements.txt", old_content = "pytest\n", new_content = "pytest\nibantools==1.0\n" }));
        Assert.Equal(["dayjs"], Names("write_file", new
        {
            path = "package.json",
            content = "{ \"name\": \"app\", \"version\": \"1.0.0\", \"scripts\": { \"test\": \"node --test test/\" }, "
                    + "\"engines\": { \"node\": \">=20\" }, \"dependencies\": { \"dayjs\": \"^1.11.13\" } }",
        }));
        Assert.Equal(["ibantools"], Names("run_command", new { command = "pip install ibantools" }));
        Assert.Equal(["dayjs", "lodash"], Names("run_command", new { command = "npm install dayjs@1.11 lodash" }));
        Assert.Equal(["@pdf-lib/pdf-lib"], Names("run_command", new { command = "npm i @pdf-lib/pdf-lib@1.0.0" }));
        Assert.Equal(["Parquet.Net"], Names("run_command", new { command = "dotnet add package Parquet.Net --version 5.0.2" }));
        Assert.Equal(["Parquet.Net"], Names("run_command", new { command = "dotnet add App.csproj package Parquet.Net" }));
        Assert.Equal(["Parquet.Net"], Names("run_command", new { command = "dotnet package add Parquet.Net" }));
        Assert.Equal(["requests"], Names("run_command", new { command = "uv add 'requests>=2'" }));
        Assert.Empty(Names("run_command", new { command = ".venv/bin/python -m pip install -q -r requirements.txt && .venv/bin/python -m pytest -q" }));
        Assert.Empty(Names("run_command", new { command = "npm install --no-audit --no-fund && npm test" }));
        Assert.Empty(Names("run_command", new { command = "pip install -e ." }));
        Assert.Empty(Names("run_command", new { command = "npm install ./local-lib ../other.tgz git+https://example.com/x.git" }));
        Assert.Empty(Names("write_file", new { path = "README.md", content = "pip install ibantools" }));
    }

    [Fact]
    public void AShellRedirection_IsNeverAName()
    {
        // Qwen3.8 27B's own commands: a pipe or a redirection after the install is the shell's, not a package.
        Assert.Equal(["piexif"], Names("run_command", new { command = "python -m pip install piexif 2>&1 | tail -n 3 && python -m pytest -q" }));
        Assert.Equal(["fpdf2"], Names("run_command", new { command = "pip install fpdf2 2>&1 | tail -3", timeout = 120 }));
        Assert.Equal(["cld3"], Names("run_command", new { command = "npm install cld3 --save 2>&1 | tail -5" }));
        Assert.Equal(["pytest", "aiolimiter"], Names("run_command", new
        {
            command = ".venv/bin/pip install pytest aiolimiter; echo \"exit=$?\"; .venv/bin/python -c \"print('pytest', 'ok')\"",
        }));
        Assert.Equal(["requests"], Names("run_command", new { command = "pip install requests 1>out.txt" }));
        Assert.Equal(["requests"], Names("run_command", new { command = "pip install requests >log 2>&1" }));
        Assert.Equal(["requests"], Names("run_command", new { command = "pip install \"requests>=2\"" }));
    }

    [Fact]
    public void AnOptionsValue_IsNeverAsked()
    {
        // An option this reader does not know as a switch takes the word after it: a date, a folder, a strategy.
        Directory.CreateDirectory(Path.Combine(_root, "web"));
        Assert.Equal(["httpx"], Names("run_command", new { command = "uv add --exclude-newer 2024-06-01 httpx" }));
        Assert.Equal(["httpx"], Names("run_command", new { command = "uv add --resolution lowest-direct httpx" }));
        Assert.Equal(["lodash"], Names("run_command", new { command = "npm install -C web lodash" }));
        Assert.Equal(["lodash"], Names("run_command", new { command = "npm install --os linux --cpu x64 lodash" }));
        Assert.Equal(["requests"], Names("run_command", new { command = "pip install --upgrade --user requests" }));
        Assert.Equal(["requests"], Names("run_command", new { command = "pip install --platform manylinux2014_x86_64 --only-binary=:all: requests" }));
        Assert.Equal(["Serilog"], Names("run_command", new { command = "dotnet add package --version 3.1.1 Serilog" }));
    }

    [Fact]
    public void AManifest_AddsOnlyItsDependencies_NeverItsOwnNameOrSettings()
    {
        // A fragment holds a project's own name and its settings beside its dependencies: neither is a package to ask for.
        Assert.Equal(["httpx", "rich", "pytest"], Names("write_file", new { path = "pyproject.toml", content = """
            [project]
            name = "my-app"
            version = "0.1.0"
            description = "An app"
            requires-python = ">=3.11"
            dependencies = [
                "httpx[http2]>=0.27",
                "rich",
            ]

            [project.optional-dependencies]
            test = ["pytest>=8"]
            all = ["my-app[test]"]

            [dependency-groups]
            ci = [{ include-group = "test" }]

            [tool.ruff]
            line-length = 120
            """ }));
        Assert.Equal(["requests"], Names("write_file", new { path = "pyproject.toml", content = """
            [tool.poetry]
            name = "my-app"
            version = "0.1.0"

            [tool.poetry.dependencies]
            python = "^3.11"
            requests = "^2.32"
            local-lib = { path = "../local-lib" }
            """ }));
        Assert.Equal(["httpx"], Names("write_file", new { path = "pyproject.toml", content = """
            [project]
            name = "workspace-root"
            dependencies = ["member-lib", "httpx"]

            [tool.uv.sources]
            member-lib = { workspace = true }
            """ }));
        Assert.Equal(["dayjs"], Names("write_file", new { path = "package.json", content = """
            {
              "name": "my-app",
              "version": "1.0.0",
              "config": { "apiVersion": "2", "port": "8080" },
              "volta": { "node": "20.11.0", "yarn": "1.22.19" },
              "dependencies": { "dayjs": "~1.11.13", "shared": "file:../shared", "utils": "workspace:*",
                                "lib": "../lib", "fork": "org/fork#v1", "home": "~/code/home" }
            }
            """ }));
        Assert.Equal(["lodash"], Names("write_file", new { path = "package.json", content = """{ "dependencies": { "lodash-mathz": "npm:lodash@^4.17.21" } }""" }));
    }

    [Fact]
    public void AnEditThatDoesNotMatchTheFile_IsNotGuessedAt()
    {
        var path = Requirements("pytest\r\nrequests\r\n");

        // Line endings aside, the replacement is applied to the whole file; one that matches nothing adds nothing to ask.
        Assert.Equal(["ibantools"], Names("apply_diff", new { path, old_content = "pytest\nrequests\n", new_content = "pytest\nrequests\nibantools\n" }));
        Assert.Empty(Names("apply_diff", new { path, old_content = "flask\n", new_content = "flask\nibantools\n" }));
    }

    [Fact]
    public void ACommandThatChoosesItsOwnSource_AddsNothingToAsk()
    {
        // The source is the command's: what the project's configuration answers says nothing about it.
        Assert.Empty(Names("run_command", new { command = "pip install --index-url https://pypi.company.example/simple company-telemetry" }));
        Assert.Empty(Names("run_command", new { command = "pip install -i https://pypi.company.example/simple company-telemetry" }));
        Assert.Empty(Names("run_command", new { command = "pip install --extra-index-url https://pypi.company.example/simple company-telemetry" }));
        Assert.Empty(Names("run_command", new { command = "pip install --no-index --find-links ./wheels company-telemetry" }));
        Assert.Empty(Names("run_command", new { command = "uv add --index company=https://pypi.company.example/simple company-telemetry" }));
        Assert.Empty(Names("run_command", new { command = "npm install @company/telemetry --registry https://npm.company.example/" }));
        Assert.Empty(Names("run_command", new { command = "npm install --@company:registry=https://npm.company.example/ @company/telemetry" }));
        Assert.Empty(Names("run_command", new { command = "dotnet add package Company.Telemetry --source https://nuget.company.example/v3/index.json" }));
        Assert.Empty(Names("run_command", new { command = "dotnet add package Company.Telemetry -s ./packages" }));
    }

    [Fact]
    public void AVariableThatChoosesTheSource_EndsWhatIsAsked()
    {
        Assert.Empty(Names("run_command", new { command = "$env:PIP_INDEX_URL='https://pypi.company.example/simple'; pip install corp-lib" }));
        Assert.Empty(Names("run_command", new { command = "PIP_INDEX_URL=https://pypi.company.example/simple pip install corp-lib" }));
        Assert.Equal(["lodash"], Names("run_command", new { command = "npm i lodash && export npm_config_registry=https://npm.company.example/ && npm i @corp/x" }));
    }

    [Fact]
    public void ACallThatDeclaresItsSource_AddsNothingToAsk()
    {
        // The source arrives with the name: the configuration on disk does not have it yet.
        Assert.Empty(Names("write_file", new { path = "requirements.txt", content = "--extra-index-url https://pypi.company.example/simple\ncorp-sdk\n" }));
        Assert.Empty(Names("write_file", new { path = "pyproject.toml", content = """
            [project]
            name = "app"
            dependencies = ["corp-sdk"]

            [[tool.uv.index]]
            name = "corp"
            url = "https://pypi.company.example/simple"
            """ }));
        Assert.Empty(Names("write_file", new { path = "App.csproj", content = """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><RestoreAdditionalProjectSources>$(MSBuildThisFileDirectory)packages</RestoreAdditionalProjectSources></PropertyGroup>
              <ItemGroup><PackageReference Include="Corp.Sdk" Version="1.0.0" /></ItemGroup>
            </Project>
            """ }));
        Assert.Empty(Names("apply_edits", new
        {
            edits = new object[]
            {
                new { path = "nuget.config", old_content = "<packageSources>", new_content = "<packageSources><add key=\"corp\" value=\"https://nuget.company.example/v3/index.json\" />" },
                new { path = "App.csproj", old_content = "</ItemGroup>", new_content = "<PackageReference Include=\"Corp.Sdk\" /></ItemGroup>" },
            },
        }));
    }

    [Fact]
    public void ACommand_IsAskedWhereItRuns_WithTheSessionsEnvironment()
    {
        Directory.CreateDirectory(Path.Combine(_root, "web", "api"));
        Directory.CreateDirectory(Path.Combine(_root, "api"));
        var env = new Dictionary<string, string> { ["NPM_CONFIG_USERCONFIG"] = "/home/me/.npmrc-corp" };
        var session = new ShellState(Path.Combine(_root, "web"), env);

        AddedPackage One(object args, ShellState? shell = null) => PackageCheck.Added("run_command", Args(args), _root, shell).Single();

        Assert.Equal(Path.Combine(_root, "web"), One(new { command = "cd web && npm install dayjs" }).Directory);
        Assert.Equal(Path.Combine(_root, "api"), One(new { command = "npm install dayjs", working_directory = "api" }).Directory);
        Assert.Equal(_root, One(new { command = "npm install dayjs" }).Directory);

        // An earlier `cd web` holds: the next command runs there, and its working_directory is read from there.
        var later = One(new { command = "npm install dayjs" }, session);
        Assert.Equal(Path.Combine(_root, "web"), later.Directory);
        Assert.Equal("/home/me/.npmrc-corp", later.Environment["NPM_CONFIG_USERCONFIG"]);
        Assert.Equal(Path.Combine(_root, "web", "api"), One(new { command = "npm install dayjs", working_directory = "api" }, session).Directory);
    }

    [Fact]
    public void AStepThatChangesWherePackagesComeFrom_EndsWhatIsAsked()
    {
        Assert.Empty(Names("run_command", new { command = "dotnet nuget add source https://pkgs.company.example/v3/index.json -n corp && dotnet add package Corp.Internal" }));
        Assert.Empty(Names("run_command", new { command = "npm config set @corp:registry https://npm.company.example/ && npm i @corp/x" }));
        Assert.Empty(Names("run_command", new { command = "echo registry=https://npm.company.example/ > .npmrc && npm i y" }));
        Assert.Empty(Names("run_command", new { command = "pip config set global.index-url https://pypi.company.example/simple; pip install y" }));
        Assert.Equal(["lodash"], Names("run_command", new { command = "npm i lodash && npm config set registry https://npm.company.example/ && npm i z" }));
    }

    [Fact]
    public void TheProjectACommandNames_ChoosesWhereItIsAsked()
    {
        Write("src/App/App.csproj", "<Project />");
        Directory.CreateDirectory(Path.Combine(_root, "web"));

        AddedPackage One(string command) => PackageCheck.Added("run_command", Args(new { command }), _root).Single();

        Assert.Equal(Path.Combine(_root, "src", "App"), One("dotnet add src/App/App.csproj package Corp.Lib").Directory);
        Assert.Equal(Path.Combine(_root, "src", "App"), One("dotnet package add Corp.Lib --project src/App").Directory);
        Assert.Equal(Path.Combine(_root, "web"), One("npm --prefix web install lodash").Directory);
        Assert.Equal(Path.Combine(_root, "web"), One("npm install -C web lodash").Directory);
        Assert.Equal(Path.Combine(_root, "web"), One("uv add --directory web httpx").Directory);
        // A project this reader cannot find is not asked from anywhere else.
        Assert.Empty(PackageCheck.Added("run_command", Args(new { command = "dotnet add missing/App.csproj package Corp.Lib" }), _root));
    }

    [Fact]
    public void ASeparatorInsideQuotes_IsPartOfTheArgument()
    {
        // A direct reference names no registry package: split at its `&egg=`, it became a bare `mypkg` asked of PyPI.
        Assert.Empty(PackageCheck.Added("run_command",
            Args(new { command = "pip install \"mypkg @ git+https://host/repo.git#subdirectory=a&egg=mypkg\"" }), _root));
        Assert.Equal(["npm i lodash ", " npm i other"], PackageCheck.Steps("npm i lodash & npm i other"));
        Assert.Equal(["echo 'a;b'", " npm i x"], PackageCheck.Steps("echo 'a;b'; npm i x"));
    }

    [Fact]
    public void AnInstallAfterALoneAmpersand_IsCheckedToo()
    {
        // pwsh 7 and bash run both commands: the second is an install like the first, never one the check skips.
        var names = PackageCheck.Added("run_command", Args(new { command = "npm i lodash & npm i fake-pkg-xyz" }), _root)
                                .Select(p => p.Name);

        Assert.Equal(["lodash", "fake-pkg-xyz"], names);
    }

    [Fact]
    public void AFolderThatCannotBeResolved_LeavesTheRestUnknown()
    {
        Directory.CreateDirectory(Path.Combine(_root, "web"));

        Assert.Equal(Path.Combine(_root, "web"),
            PackageCheck.Added("run_command", Args(new { command = "Set-Location -Path web; npm install @corp/ui" }), _root).Single().Directory);
        Assert.Empty(Names("run_command", new { command = "cd $dir && npm i lodash" }));
        Assert.Empty(Names("run_command", new { command = "cd ~/src && npm i lodash" }));
        Assert.Empty(Names("run_command", new { command = "mkdir app && cd app && npm i lodash" }));
    }

    [Fact]
    public void OnlyThePublicRegistries_AreEverSentAName()
    {
        // `dotnet nuget list source --format short` and `npm config get`, captured: read offline, before anything is sent.
        const string nugetDefault = "E https://api.nuget.org/v3/index.json\nEM C:\\Program Files (x86)\\Microsoft SDKs\\NuGetPackages\\\n";
        const string nugetCompany = "E https://pkgs.company.invalid/v3/index.json\nE C:\\src\\pkgs\nE https://api.nuget.org/v3/index.json\nDM C:\\Program Files (x86)\\Microsoft SDKs\\NuGetPackages\\\n";
        const string nugetCompanyOff = "D https://pkgs.company.invalid/v3/index.json\nE https://api.nuget.org/v3/index.json\n";
        Assert.Null(PackageManagerLookup.NuGetOtherSource(nugetDefault));
        Assert.Equal("https://pkgs.company.invalid/v3/index.json", PackageManagerLookup.NuGetOtherSource(nugetCompany));
        Assert.Null(PackageManagerLookup.NuGetOtherSource(nugetCompanyOff));
        // A folder on a network share would receive the name over the network; a local one never does.
        Assert.Equal("\\\\corp-nas\\nuget", PackageManagerLookup.NuGetOtherSource("E https://api.nuget.org/v3/index.json\nE \\\\corp-nas\\nuget\n"));
        Assert.Equal("file://corp-nas/nuget", PackageManagerLookup.NuGetOtherSource("E file://corp-nas/nuget\n"));
        Assert.Null(PackageManagerLookup.NuGetOtherSource("E file:///C:/feeds/local\nE /home/me/feed\n"));

        Assert.Null(PackageManagerLookup.NpmOtherRegistry("https://registry.npmjs.org/\n", scope: null));
        Assert.Equal("https://registry.company.invalid/", PackageManagerLookup.NpmOtherRegistry("https://registry.company.invalid/\n", scope: null));
        const string scoped = "registry=https://registry.npmjs.org/\n@corp:registry=https://npm.corp.invalid/\n";
        Assert.Equal("https://npm.corp.invalid/", PackageManagerLookup.NpmOtherRegistry(scoped, "@corp"));
        Assert.Null(PackageManagerLookup.NpmOtherRegistry("registry=https://registry.npmjs.org/\n@other:registry=undefined\n", "@other"));
        Assert.NotNull(PackageManagerLookup.NpmOtherRegistry("", scope: null));

        // The close names of a name found absent on the public registry are searched THERE: npm search would otherwise
        // ask the default registry, which can be a company's even when the name's scope maps to the public one.
        var search = PackageManagerLookup.NpmSearchArguments("@types/nodee");
        Assert.Equal("search", search[0]);
        Assert.Contains("--registry=https://registry.npmjs.org/", search);
        Assert.Equal("@types/nodee", search[^1]);
    }

    [Fact]
    public void AManifestWrite_IsAskedInTheManifestsFolder()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        var added = PackageCheck.Added("write_file", Args(new
        {
            path = "src/App.csproj",
            content = "<Project><ItemGroup><PackageReference Include=\"Apache.Parquet\" Version=\"1.0.0\" /></ItemGroup></Project>",
        }), _root).Single();

        Assert.Equal((PackageEcosystem.NuGet, "Apache.Parquet", Path.Combine(_root, "src")), (added.Ecosystem, added.Name, added.Directory));
    }

    // ── what the project serves itself ───────────────────────────────────────────

    [Fact]
    public void WhatTheProjectServesItself_IsNoRegistrysToAnswerFor()
    {
        Write("package.json", """{ "name": "root", "private": true, "workspaces": ["packages/*", "apps/*"] }""");
        Write("packages/ui/package.json", """{ "name": "@acme/ui", "version": "0.1.0" }""");
        Directory.CreateDirectory(Path.Combine(_root, "apps", "web"));
        Assert.NotNull(PackageManagerLookup.NpmWorkspacePackage("@acme/ui", Path.Combine(_root, "apps", "web"), _root));
        Assert.Null(PackageManagerLookup.NpmWorkspacePackage("@acme/uii", Path.Combine(_root, "apps", "web"), _root));

        var py = Path.Combine(_root, "py");
        Write("py/pyproject.toml", "[project]\nname = \"My_App\"\n\n[tool.uv.sources]\nmember-lib = { workspace = true }\n");
        Assert.NotNull(PackageManagerLookup.PythonProjectProvides("member_lib", py, _root));
        Assert.NotNull(PackageManagerLookup.PythonProjectProvides("my-app", py, _root));
        Assert.Null(PackageManagerLookup.PythonProjectProvides("ibantools", py, _root));
        Write("ws/pyproject.toml", "[tool.uv.workspace]\nmembers = [\"libs/*\"]\n");
        Assert.NotNull(PackageManagerLookup.PythonProjectProvides("anything", Path.Combine(_root, "ws"), _root));

        Write("feed/App.csproj", "<Project><PropertyGroup><RestoreSources>https://api.nuget.org/v3/index.json;./local</RestoreSources></PropertyGroup></Project>");
        Assert.NotNull(PackageManagerLookup.MSBuildRestoreSource(Path.Combine(_root, "feed"), _root));
        Write("plain/App.csproj", "<Project><ItemGroup><PackageReference Include=\"X\" /></ItemGroup></Project>");
        Assert.Null(PackageManagerLookup.MSBuildRestoreSource(Path.Combine(_root, "plain"), _root));
    }

    // ── what the package managers answer (outputs captured from the real tools) ────────────────

    private const string NuGetAbsent = """
        {
          "version": 2,
          "problems": [],
          "searchResult": [
            { "sourceName": "Microsoft Visual Studio Offline Packages", "packages": [] },
            { "sourceName": "nuget.org", "packages": [] }
          ]
        }
        """;

    private const string NuGetFound = """
        {
          "version": 2,
          "problems": [],
          "searchResult": [
            { "sourceName": "Microsoft Visual Studio Offline Packages", "packages": [ { "id": "Newtonsoft.Json", "version": "13.0.1" } ] },
            { "sourceName": "nuget.org", "packages": [ { "id": "Newtonsoft.Json", "version": "3.5.8" } ] }
          ]
        }
        """;

    // A source that does not answer is reported INSIDE its own result, and the command still exits 0.
    private const string NuGetSourceDown = """
        {
          "version": 2,
          "problems": [],
          "searchResult": [
            { "sourceName": "nuget.org", "packages": [] },
            {
              "sourceName": "https://pkgs.company.invalid/v3/index.json",
              "problems": [ { "text": "Unable to load the service index for source https://pkgs.company.invalid/v3/index.json.", "problemType": "Error" } ],
              "packages": []
            }
          ]
        }
        """;

    private const string NuGetSearch = """
        {
          "version": 2,
          "problems": [],
          "searchResult": [
            {
              "sourceName": "nuget.org",
              "packages": [
                { "id": "Parquet.Net", "latestVersion": "6.1.0", "totalDownloads": 32218596, "owners": "aloneguid" },
                { "id": "ParquetSharp", "latestVersion": "25.0.1", "totalDownloads": 2839291, "owners": "g-research" }
              ]
            }
          ]
        }
        """;

    [Fact]
    public void NuGet_IsAbsentOnlyWhenEverySourceAnswered_AndNoneHasIt()
    {
        var absent = PackageManagerLookup.NuGetAnswer(0, NuGetAbsent);
        Assert.Equal(PackageAnswer.Absent, absent.Answer);
        Assert.Equal("Microsoft Visual Studio Offline Packages and nuget.org", absent.Where);

        Assert.Equal(PackageAnswer.Exists, PackageManagerLookup.NuGetAnswer(0, NuGetFound).Answer);

        var down = PackageManagerLookup.NuGetAnswer(0, NuGetSourceDown);
        Assert.Equal(PackageAnswer.Unknown, down.Answer);
        Assert.Contains("Unable to load the service index", down.Cause, StringComparison.Ordinal);

        Assert.Equal(PackageAnswer.Unknown, PackageManagerLookup.NuGetAnswer(1, "error: 'package' is not a valid command").Answer);
        Assert.Equal(PackageAnswer.Unknown, PackageManagerLookup.NuGetAnswer(0, """{ "version": 2, "problems": [], "searchResult": [] }""").Answer);
        Assert.Equal(PackageAnswer.Unknown, PackageManagerLookup.NuGetAnswer(0, """{ "version": 2, "problems": [ { "text": "x", "problemType": "Error" } ], "searchResult": [ { "sourceName": "nuget.org", "packages": [] } ] }""").Answer);

        Assert.Equal(["Parquet.Net", "ParquetSharp"], PackageManagerLookup.NuGetNames(NuGetSearch));
    }

    [Fact]
    public void Npm_IsAbsentOnlyOnA404()
    {
        const string e404 = """
            {
              "error": {
                "code": "E404",
                "summary": "Not Found - GET https://registry.npmjs.org/lodash-mathz - Not found",
                "detail": "The requested resource 'lodash-mathz@*' could not be found or you do not have permission to access it."
              }
            }
            """;
        const string unreachable = """
            {
              "error": {
                "code": "ENOTFOUND",
                "summary": "request to https://registry.company.invalid/lodash failed, reason: getaddrinfo ENOTFOUND registry.company.invalid",
                "detail": "This is a problem related to network connectivity."
              }
            }
            """;

        var absent = PackageManagerLookup.NpmAnswer(1, e404, "npm error code E404");
        Assert.Equal(PackageAnswer.Absent, absent.Answer);
        Assert.Equal("the npm registry (registry.npmjs.org)", absent.Where);

        Assert.Equal(PackageAnswer.Exists, PackageManagerLookup.NpmAnswer(0, "\"lodash\"\n", "").Answer);
        Assert.Equal(PackageAnswer.Unknown, PackageManagerLookup.NpmAnswer(1, unreachable, "npm error code ENOTFOUND").Answer);
        Assert.Equal(PackageAnswer.Unknown, PackageManagerLookup.NpmAnswer(1, "", "'npm' is not recognized").Answer);

        Assert.Equal(["pdf-lib", "@pdf-lib/upng"],
            PackageManagerLookup.NpmNames("""[{"name":"pdf-lib","version":"1.17.1"},{"name":"@pdf-lib/upng","version":"1.0.1"}]"""));
    }

    [Fact]
    public async Task AScopeYarnInstallsFromAnotherRegistry_IsNeverSentToNpm()
    {
        // npm reads no yarn or bun file: asked, it named the public registry, and the internal name went to npmjs.org.
        Write(".yarnrc.yml", "npmScopes:\n  acme:\n    npmRegistryServer: https://npm.acme.internal\n");
        var package = new AddedPackage(PackageEcosystem.Npm, "@acme/ui", _root, _root, new Dictionary<string, string>());

        var answer = await new PackageManagerLookup().LookUpAsync(package, CancellationToken.None);

        Assert.Equal(PackageAnswer.Unknown, answer.Answer);
        Assert.Contains(Path.Combine(_root, ".yarnrc.yml"), answer.Cause, StringComparison.Ordinal);
        // The person's own configuration counts too; a yarn file that chooses no registry does not.
        var home = Write(Path.Combine("home", ".bunfig.toml"), "[install.scopes]\nacme = \"https://npm.acme.internal\"\n");
        File.WriteAllText(Path.Combine(_root, ".yarnrc.yml"), "nodeLinker: node-modules\n");
        Assert.Equal(home, PackageManagerLookup.OtherClientRegistry(_root, _root, Path.GetDirectoryName(home)));
        Assert.Null(PackageManagerLookup.OtherClientRegistry(_root, _root, home: null));
    }

    [Fact]
    public void AWorkspaceOpenedOnASubFolder_ReadsItsRepositorysConfiguration()
    {
        // VS Code opened on repo/src/Api: the restore source is declared at repo/, above the workspace.
        Write("Directory.Build.props", "<Project><PropertyGroup><RestoreAdditionalProjectSources>https://feed.acme.internal/v3/index.json</RestoreAdditionalProjectSources></PropertyGroup></Project>");
        Write("src/Api/Api.csproj", "<Project><ItemGroup><PackageReference Include=\"Acme.Logging\" /></ItemGroup></Project>");
        var api = Path.Combine(_root, "src", "Api");
        // Reference arm: outside a git repository, the workspace is where the reading stops.
        Assert.Null(PackageManagerLookup.MSBuildRestoreSource(api, api));

        Directory.CreateDirectory(Path.Combine(_root, ".git"));

        Assert.NotNull(PackageManagerLookup.MSBuildRestoreSource(api, api));
    }

    [Fact]
    public void PyPI_IsAskedOnlyWhenNoOtherIndexIsDeclared()
    {
        string? Env(string _) => null;
        Assert.Null(PackageManagerLookup.OtherPythonIndex(_root, _root, Env, []));

        Requirements("--extra-index-url https://pypi.company.example/simple\npytest\n");
        Assert.Contains("requirements.txt", PackageManagerLookup.OtherPythonIndex(_root, _root, Env, []), StringComparison.Ordinal);
        File.Delete(Path.Combine(_root, "requirements.txt"));

        var conf = Path.Combine(_root, "pip.conf");
        File.WriteAllText(conf, "[global]\nindex-url = https://pypi.company.example/simple\n");
        Assert.Contains("pip.conf", PackageManagerLookup.OtherPythonIndex(_root, _root, Env, [conf]), StringComparison.Ordinal);
        File.WriteAllText(conf, "[global]\nindex_url = https://pypi.company.example/simple\n");
        Assert.Contains("pip.conf", PackageManagerLookup.OtherPythonIndex(_root, _root, Env, [conf]), StringComparison.Ordinal);
        Assert.Null(PackageManagerLookup.OtherPythonIndex(_root, _root, Env, [Path.Combine(_root, "missing.conf")]));

        Assert.Contains("PIP_INDEX_URL", PackageManagerLookup.OtherPythonIndex(_root, _root, n => n == "PIP_INDEX_URL" ? "https://x/simple" : null, []), StringComparison.Ordinal);

        Write("pyproject.toml", "[project]\nname = \"app\"\n\n[[tool.uv.index]]\nname = \"company\"\nurl = \"https://pypi.company.example/simple\"\n");
        Assert.Contains("pyproject.toml", PackageManagerLookup.OtherPythonIndex(_root, _root, Env, []), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(200, "Exists")]
    [InlineData(404, "Absent")]
    [InlineData(503, "Unknown")]
    [InlineData(301, "Unknown")]
    public void PyPI_IsAbsentOnlyOnA404(int status, string expected) =>
        Assert.Equal(expected, PackageManagerLookup.PyPIAnswer(status).Answer.ToString());
}
