using System.IO;
using Inferpal.Services.Execution;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A write no approval prompt covers (<c>/onboard context force</c>, <c>/test</c> on an existing
/// test file) does not destroy the file it replaces.
/// </summary>
/// <remarks>
/// Both front-ends wrote the model's content over the existing file — written or corrected by hand,
/// or tests the model may have dropped — with no backup. The writer is shared, so it is tested once.
/// </remarks>
public class BackedUpFileWriterTests
{
    private static async Task InTempDir(Func<string, Task> body)
    {
        var dir = Directory.CreateTempSubdirectory("inferpal-backedup-").FullName;
        try { await body(dir); }
        finally { try { Directory.Delete(dir, recursive: true); } catch { /* cleanup */ } }
    }

    [Fact]
    public Task ANewFile_IsWrittenWithoutBom_AndHasNothingToBackUp() => InTempDir(async dir =>
    {
        var path = Path.Combine(dir, ".inferpal", "context.md");

        var outcome = await BackedUpFileWriter.WriteAsync(path, "# Context", new FileHistoryService(), CancellationToken.None);

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

        var outcome = await BackedUpFileWriter.WriteAsync(path, "# Model draft", new FileHistoryService(), CancellationToken.None);

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

        var outcome = await BackedUpFileWriter.WriteAsync(path, "# Model draft", new FileHistoryService(), CancellationToken.None);

        Assert.False(outcome.Written);
        Assert.Equal("written by hand", await File.ReadAllTextAsync(path));
    });
}
