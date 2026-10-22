using System.IO;
using System.Linq;
using System.Text.Json;
using Inferpal.Config;
using Inferpal.Services;
using Inferpal.Services.CodeActions;
using Inferpal.Services.Commands;
using Inferpal.Services.Docs;
using Inferpal.Services.Editor;
using Inferpal.Services.Execution;
using Inferpal.Services.Lsp;
using Inferpal.Services.Mcp;
using Inferpal.Services.Rag;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A tool turned off, and the package check switched off or on, decide in every window from <c>config.json</c> as it is
/// NOW — never from the copy a window read when it opened.
/// </summary>
/// <remarks>
/// ⚠ Visual Studio and the VS Code host each read the file once and keep their copy. A tool turned off in VS Code went on
/// being offered, and run, in an open Visual Studio — with no card under an <c>allow</c> rule — and <c>/tools on</c> typed
/// there answered "already on" and wrote nothing, so VS Code went on refusing it.
/// </remarks>
[Collection(GlobalConfigPathCollection.Name)]
public sealed class ToolSwitchesFollowTheFileTests : IDisposable
{
    private readonly string? _previous = InferpalConfig.OverridePathForTests;
    private readonly string  _path = Path.Combine(Path.GetTempPath(), "inferpal-tests", $"switches-{Guid.NewGuid():N}.json");
    private readonly string  _root = Directory.CreateTempSubdirectory("inferpal-switches").FullName;

    public ToolSwitchesFollowTheFileTests()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        InferpalConfig.OverridePathForTests = _path;
    }

    public void Dispose()
    {
        InferpalConfig.OverridePathForTests = _previous;
        try { File.Delete(_path); } catch { }
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private sealed class Approves : IApprovalService
    {
        public Task<bool> RequestApprovalAsync(string toolName, string details, CancellationToken ct,
                                               string? subject = null, DiffInfo? diff = null, bool forcePrompt = false) =>
            Task.FromResult(true);
    }

    private sealed class CountingLookup : IPackageLookup
    {
        public int Asked;

        public Task<PackageLookupResult> LookUpAsync(AddedPackage package, CancellationToken ct)
        {
            Interlocked.Increment(ref Asked);
            return Task.FromResult(PackageLookupResult.Absent("PyPI"));
        }

        public Task<IReadOnlyList<string>> CloseNamesAsync(AddedPackage package, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>([]);
    }

    private ToolRegistry Registry(InferpalConfig config, IPackageLookup? lookup = null)
    {
        var client = new FakeInferenceProvider();
        var editor = new NullEditorSurface();
        var index  = new ProjectIndexService(client, config, new LspSemanticProvider());
        index.SetRoot(_root);
        var approval = new Approves();
        return new ToolRegistry(editor, approval, config, index, client, new ProjectMapService(editor),
                                new McpToolService(config, approval), new DocsIndexService(client, config),
                                new OpenDocumentOverlay(), new NullDebugSession(),
                                packageCheck: new PackageCheck(lookup ?? new CountingLookup()));
    }

    private void WriteAsTheOtherWindow(Action<InferpalConfig> change)
    {
        var other = JsonSerializer.Deserialize<InferpalConfig>(File.ReadAllText(_path))!;
        change(other);
        File.WriteAllText(_path, JsonSerializer.Serialize(other));
        // A different stamp than the save above, however fast the machine.
        File.SetLastWriteTimeUtc(_path, DateTime.UtcNow.AddSeconds(5));
    }

    private static bool Offers(ToolRegistry tools, string name) => tools.Definitions.Any(d => d.Function.Name == name);

    [Fact]
    public void AToolTurnedOffInTheOtherEditor_IsNeitherOfferedNorRunHere()
    {
        new InferpalConfig().Save();
        var tools = Registry(InferpalConfig.Load());
        Assert.True(Offers(tools, "run_command"));                                   // witness: offered before
        Assert.Null(tools.TurnedOffRefusal("run_command"));

        WriteAsTheOtherWindow(c => c.DisabledTools = "run_command");

        Assert.False(Offers(tools, "run_command"));
        Assert.NotNull(tools.TurnedOffRefusal("run_command"));
        Assert.Contains("run_command", tools.TurnedOffKeys());
    }

    [Fact]
    public void AToolTurnedOffInTheOtherEditor_IsTurnedBackOnHere_InTheFile()
    {
        new InferpalConfig().Save();
        var config = InferpalConfig.Load();
        var tools  = Registry(config);
        WriteAsTheOtherWindow(c => c.DisabledTools = "run_command\nweb_search");

        var result = ToolsCommandHandler.Switch(["run_command"], off: false, tools, config);
        config.Save();

        Assert.Contains(Inferpal.Localization.Strings.SlashToolsTurnedOn(1, "`run_command`"), result.Message);
        var onDisk = JsonSerializer.Deserialize<InferpalConfig>(File.ReadAllText(_path))!;
        Assert.DoesNotContain("run_command", onDisk.DisabledTools);
        Assert.Contains("web_search", onDisk.DisabledTools);                          // the other window's line stays
        Assert.True(Offers(tools, "run_command"));
    }

    /// <summary>The end of a long run, announced after the same time in both editors, by the setting as the file holds it
    /// now: turned off in one editor, the other stops announcing at its next turn's end.</summary>
    [Fact]
    public void TheEndOfALongRun_TurnedOffInTheOtherEditor_IsNotAnnouncedHere()
    {
        new InferpalConfig().Save();
        var config  = InferpalConfig.Load();
        var longRun = TimeSpan.FromSeconds(Services.Agent.ChatTurnPolicy.LongRunSeconds + 1);
        Assert.True(Services.Agent.ChatTurnPolicy.AnnouncesEnd(config, longRun));                 // witness: announced
        Assert.False(Services.Agent.ChatTurnPolicy.AnnouncesEnd(config, TimeSpan.FromSeconds(Services.Agent.ChatTurnPolicy.LongRunSeconds)));

        WriteAsTheOtherWindow(c => c.RunEndSoundEnabled = false);

        Assert.False(Services.Agent.ChatTurnPolicy.AnnouncesEnd(config, longRun));
    }

    [Fact]
    public async Task ThePackageCheck_SwitchedOffInTheOtherEditor_AsksNothingHere()
    {
        new InferpalConfig { PackageCheckEnabled = true, SmartFixEnabled = false }.Save();
        var lookup = new CountingLookup();
        var tools  = Registry(InferpalConfig.Load(), lookup);
        var path   = Path.Combine(_root, "requirements.txt");
        File.WriteAllText(path, "pytest\n");
        var adds   = JsonSerializer.SerializeToElement(new { path, old_content = "pytest\n", new_content = "pytest\nibantools\n" });

        Assert.StartsWith("Error: the package 'ibantools'", await tools.ExecuteAsync("apply_diff", adds, CancellationToken.None));
        Assert.Equal(1, lookup.Asked);                                                // witness: checked while on

        WriteAsTheOtherWindow(c => c.PackageCheckEnabled = false);
        await tools.ExecuteAsync("apply_diff", adds, CancellationToken.None);

        Assert.Equal(1, lookup.Asked);
        Assert.Contains("ibantools", File.ReadAllText(path));
    }
}
