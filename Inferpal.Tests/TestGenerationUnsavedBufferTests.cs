using System.IO;
using Inferpal.Localization;
using Inferpal.Models;
using Inferpal.Services.CodeActions;
using Inferpal.Services.Editor;
using Xunit;

namespace Inferpal.Tests;

// ──────────────────────────────────────────────────────────────────────────────────────────────
//  /test never loses tests the user has not saved yet.
//
//  The planner read the test file from DISK. Visual Studio then replaced the open buffer with the
//  result, and the unsaved tests in it disappeared ("tests extended"); the VS Code host wrote the disk
//  behind the dirty buffer. Visual Studio now plans from its buffer; VS Code refuses until the file is
//  saved, like its writing tools.
// ──────────────────────────────────────────────────────────────────────────────────────────────
public sealed class TestGenerationUnsavedBufferTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("inferpal-testbuffer-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private (string Source, string Test) Fixture(string onDisk)
    {
        var source = Path.Combine(_dir, "Widget.cs");
        File.WriteAllText(source, "public class Widget { public int Add(int a, int b) => a + b; }");
        var test = TestFilePathResolver.Resolve(source);
        Directory.CreateDirectory(Path.GetDirectoryName(test)!);
        File.WriteAllText(test, onDisk);
        return (source, test);
    }

    private static (FakeInferenceProvider Model, List<string> Prompts) Recording()
    {
        var prompts = new List<string>();
        var model = new FakeInferenceProvider
        {
            OnChatRequest = (_, messages, _, _) =>
            {
                prompts.Add(string.Join("\n", messages.Select(m => m.Content)));
                return Task.FromResult(new ChatTurnResult("public class WidgetTests { }", null, 0, 0));
            },
        };
        return (model, prompts);
    }

    [Fact]
    public async Task AnEditorThatWritesIntoItsBuffer_PlansFromThatBuffer()
    {
        var (source, test) = Fixture("public class WidgetTests { /* saved */ }");
        var (model, prompts) = Recording();

        var plan = await TestGenerationPlanner.PlanAsync(model, "m", source, File.ReadAllText(source), CancellationToken.None,
            openText: (path, _) => Task.FromResult<string?>(
                path == test ? "public class WidgetTests { /* typed, not saved */ }" : null));

        Assert.True(plan.Extended);
        Assert.Contains("typed, not saved", Assert.Single(prompts));
        Assert.DoesNotContain("/* saved */", prompts[0]);
    }

    [Fact]
    public async Task WithoutAnOpenBuffer_TheDiskIsRead()
    {
        // Reference arm: a test file that is not open is read from the disk, as before.
        var (source, _) = Fixture("public class WidgetTests { /* saved */ }");
        var (model, prompts) = Recording();

        await TestGenerationPlanner.PlanAsync(model, "m", source, File.ReadAllText(source), CancellationToken.None,
            openText: (_, _) => Task.FromResult<string?>(null));

        Assert.Contains("/* saved */", Assert.Single(prompts));
    }

    [Fact]
    public void AnEditorThatWritesTheDisk_RefusesATestFileWithUnsavedChanges()
    {
        var (source, test) = Fixture("public class WidgetTests { }");
        var overlay = new OpenDocumentOverlay();

        Assert.Null(TestGenerationPlanner.UnsavedTestFile(overlay, source));          // nothing open
        overlay.Set(test, "public class WidgetTests { }", unsaved: false);
        Assert.Null(TestGenerationPlanner.UnsavedTestFile(overlay, source));          // open and saved

        overlay.Set(test, "public class WidgetTests { /* typed */ }", unsaved: true);
        Assert.Equal(Strings.TestsFileUnsaved(Path.GetFileName(test)), TestGenerationPlanner.UnsavedTestFile(overlay, source));
    }

    [Fact]
    public void BothFrontEnds_TakeTheBufferIntoAccount_BeforeTheModelIsAsked()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "README.md"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var host = ConventionCoverageTests.CodeOnly(Path.Combine(dir!.FullName, "Inferpal.Host", "HostSlashCommands.cs"));
        var vs   = ConventionCoverageTests.CodeOnly(Path.Combine(dir.FullName, "Inferpal", "Commands", "TestGenerationEdit.cs"));

        var refusal = host.IndexOf("TestGenerationPlanner.UnsavedTestFile(", StringComparison.Ordinal);
        var planned = host.IndexOf("TestGenerationPlanner.PlanAsync(", StringComparison.Ordinal);
        Assert.True(refusal >= 0 && planned > refusal, "The host refuses an unsaved test file BEFORE asking the model.");
        Assert.Contains("openText:", vs, StringComparison.Ordinal);
        Assert.Contains("GetOpenDocumentAsync(", vs, StringComparison.Ordinal);
    }
}
