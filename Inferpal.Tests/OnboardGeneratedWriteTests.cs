using System.IO;
using Inferpal.Services.Commands;
using Inferpal.Services.Execution;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// <c>/onboard context force</c> does not destroy the <c>context.md</c> it replaces.
/// </summary>
/// <remarks>
/// Both front-ends wrote the model's draft over the existing file — often written or corrected by
/// hand — with no backup. The writer is shared, so it is tested once.
/// </remarks>
public class OnboardGeneratedWriteTests
{
    private static async Task InTempDir(Func<string, Task> body)
    {
        var dir = Directory.CreateTempSubdirectory("inferpal-onboard-").FullName;
        try { await body(dir); }
        finally { try { Directory.Delete(dir, recursive: true); } catch { /* cleanup */ } }
    }

    [Fact]
    public Task ANewFile_IsWrittenWithoutBom_AndHasNothingToBackUp() => InTempDir(async dir =>
    {
        var path = Path.Combine(dir, ".inferpal", "context.md");

        var outcome = await OnboardCommandHandler.WriteGeneratedAsync(
            new(path, "# Context"), new FileHistoryService(), CancellationToken.None);

        Assert.True(outcome.Written);
        Assert.Equal(string.Empty, outcome.Snapshot);
        var bytes = await File.ReadAllBytesAsync(path);
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF,
            "A new file is written as UTF-8 without a BOM.");
        Assert.Equal("# Context", await File.ReadAllTextAsync(path));
    });

    [Fact]
    public Task AnExistingFile_IsBackedUpBeforeBeingReplaced() => InTempDir(async dir =>
    {
        var path = Path.Combine(dir, ".inferpal", "context.md");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "written by hand");

        var outcome = await OnboardCommandHandler.WriteGeneratedAsync(
            new(path, "# Model draft"), new FileHistoryService(), CancellationToken.None);

        Assert.True(outcome.Written);
        Assert.NotEqual(string.Empty, outcome.Snapshot);
        Assert.Equal("written by hand", await File.ReadAllTextAsync(outcome.Snapshot));
        Assert.Equal("# Model draft", await File.ReadAllTextAsync(path));
    });

    [Fact]
    public Task AnExistingFile_ThatCannotBeBackedUp_IsLeftUntouched() => InTempDir(async dir =>
    {
        var path = Path.Combine(dir, ".inferpal", "context.md");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "written by hand");

        // Outside a git repository backups go to <file's directory>/.inferpal/history: a FILE in
        // place of that directory makes the backup fail on every OS.
        var historyRoot = Path.Combine(Path.GetDirectoryName(path)!, ".inferpal");
        await File.WriteAllTextAsync(historyRoot, "not a directory");

        var outcome = await OnboardCommandHandler.WriteGeneratedAsync(
            new(path, "# Model draft"), new FileHistoryService(), CancellationToken.None);

        Assert.False(outcome.Written);
        Assert.Equal("written by hand", await File.ReadAllTextAsync(path));
    });
}
