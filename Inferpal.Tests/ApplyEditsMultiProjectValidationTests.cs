using System.IO;
using System.Linq;
using System.Text.Json;
using Inferpal.Config;
using Inferpal.Services.CodeActions;
using Inferpal.Services.Execution;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// <c>apply_edits</c> is the multi-file tool, and it ran Smart Fix on <b>one</b> of the files it wrote.
/// </summary>
/// <remarks>
/// <para>
/// ⚠ The call site said so itself: <i>"Smart Fix once: building any edited file validates its project
/// (covers same-project edits)"</i> — the scope is named, and the case outside it is waved away. But
/// a coordinated refactor is exactly what this tool exists for, and it routinely crosses a project
/// boundary (Core + Tests) or an ecosystem one (<c>.cs</c> + <c>.ts</c>, which do not even share a
/// validator). Everything but <c>changed[0]</c>'s project was written, reported as applied, and never
/// compiled — under a Smart Fix note that reads as "the build is fine".
/// </para>
/// <para>
/// ⚠ The check is <b>deduplicated by what it would actually run</b> (command + directory), so the
/// ordinary batch — several files of one project — still builds once. And it is capped, because a
/// refactor touching ten projects would otherwise spend ten builds; the cap says so rather than
/// trimming in silence.
/// </para>
/// <para>
/// Measured without running anything: the workspace validator is force-prompted (a command the
/// repository wrote is never run unattended), so a refusing approval service counts exactly how many
/// distinct checks the tool resolved.
/// </para>
/// </remarks>
public sealed class ApplyEditsMultiProjectValidationTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "inferpal-tests", $"applyedits-{Guid.NewGuid():N}");

    public ApplyEditsMultiProjectValidationTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".inferpal"));
        // `{project}` is substituted, so each project yields a DIFFERENT command string — which is
        // what makes the per-session approval cache count targets instead of collapsing them.
        File.WriteAllText(
            Path.Combine(_root, ".inferpal", "validators.json"),
            """{ ".cs": { "marker": "*.csproj", "command": "node check.js {project}" } }""");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    /// <summary>Says yes to the edit, no to the validator's command — so nothing is executed.</summary>
    private sealed class Approval : IApprovalService
    {
        public List<string> ValidatorCommands { get; } = [];

        public Task<bool> RequestApprovalAsync(
            string toolName, string details, CancellationToken ct, string? subject = null,
            DiffInfo? diff = null, bool forcePrompt = false)
        {
            if (toolName == "smart_fix_validator") { ValidatorCommands.Add(details); return Task.FromResult(false); }
            return Task.FromResult(true);
        }
    }

    private string Project(string name, params string[] files)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, name + ".csproj"), "<Project />");
        foreach (var f in files) File.WriteAllText(Path.Combine(dir, f), "class C { int X = 1; }");
        return dir;
    }

    private async Task<(string Result, Approval Approval)> ApplyAsync(params string[] paths)
    {
        var approval = new Approval();
        var smartFix = new SmartFixValidator(
            new InferpalConfig { SmartFixEnabled = true }, () => _root, approval);
        var tool = new ApplyEditsTool(approval, new FileHistoryService(), () => _root, smartFix);

        var edits = paths.Select(p => new { path = p, old_content = "int X = 1;", new_content = "int X = 2;" });
        using var args = JsonDocument.Parse(JsonSerializer.Serialize(new { edits }));

        return (await tool.ExecuteAsync(args.RootElement, CancellationToken.None), approval);
    }

    [Fact]
    public async Task ABatchAcrossTwoProjects_ValidatesBoth()
    {
        var a = Project("Alpha", "Foo.cs");
        var b = Project("Beta",  "Bar.cs");

        var (result, approval) = await ApplyAsync(Path.Combine(a, "Foo.cs"), Path.Combine(b, "Bar.cs"));

        Assert.Contains("2", result, StringComparison.Ordinal);   // witness: both edits were applied
        Assert.Equal(2, approval.ValidatorCommands.Count);
        Assert.Contains(approval.ValidatorCommands, c => c.Contains("Alpha", StringComparison.Ordinal));
        Assert.Contains(approval.ValidatorCommands, c => c.Contains("Beta",  StringComparison.Ordinal));
    }

    [Fact]
    public async Task ABatchInsideOneProject_StillValidatesOnce()
    {
        // REFERENCE ARM: the common case must not start paying one build per file. Without it, a fix
        // that simply validated every written file would pass the test above.
        var a = Project("Alpha", "Foo.cs", "Bar.cs", "Baz.cs");

        var (_, approval) = await ApplyAsync(
            Path.Combine(a, "Foo.cs"), Path.Combine(a, "Bar.cs"), Path.Combine(a, "Baz.cs"));

        Assert.Single(approval.ValidatorCommands);
    }

    [Fact]
    public async Task ABatchAcrossMoreProjectsThanTheCap_SaysWhatItDidNotCheck()
    {
        var dirs = Enumerable.Range(1, 5).Select(i => Project($"P{i}", "F.cs")).ToArray();

        var (result, approval) = await ApplyAsync(dirs.Select(d => Path.Combine(d, "F.cs")).ToArray());

        Assert.Equal(SmartFixValidator.MaxBatchTargets, approval.ValidatorCommands.Count);
        // A cap that trims in silence is the defect this repository keeps paying for. Asserted on
        // the localized sentence, not on an English fragment: the suite does not run in English.
        Assert.Contains(
            Inferpal.Localization.Strings.SmartFixBatchCapped(SmartFixValidator.MaxBatchTargets, 2),
            result, StringComparison.Ordinal);
    }
}
