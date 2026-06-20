using System.IO;
using Inferpal.Services;
using Xunit;

namespace Inferpal.Tests;

public class NotesStoreTests : IDisposable
{
    private readonly string _root;

    public NotesStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"notes_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void NotesPath_IsUnderInferpalFolder()
    {
        Assert.Equal(
            Path.Combine(_root, ".inferpal", "notes.md"),
            NotesStore.NotesPath(_root));
    }

    [Fact]
    public void FormatLine_TimestampedMarkdownBullet()
    {
        var line = NotesStore.FormatLine("use Vulkan backend", new DateTime(2026, 6, 12, 9, 5, 0));

        Assert.Equal("- [2026-06-12 09:05] use Vulkan backend\n", line);
    }

    [Fact]
    public async Task AppendAsync_CreatesFolderAndFile()
    {
        await NotesStore.AppendAsync(_root, "first note", DateTime.Now, CancellationToken.None);

        Assert.True(File.Exists(NotesStore.NotesPath(_root)));
    }

    [Fact]
    public async Task AppendAsync_AccumulatesLines()
    {
        var now = new DateTime(2026, 6, 12, 10, 0, 0);
        await NotesStore.AppendAsync(_root, "one", now, CancellationToken.None);
        await NotesStore.AppendAsync(_root, "two", now, CancellationToken.None);

        var content = await NotesStore.ReadAsync(_root, CancellationToken.None);

        Assert.Equal("- [2026-06-12 10:00] one\n- [2026-06-12 10:00] two", content);
    }

    [Fact]
    public async Task ReadAsync_MissingFile_ReturnsNull()
    {
        Assert.Null(await NotesStore.ReadAsync(_root, CancellationToken.None));
    }

    [Fact]
    public async Task ReadAsync_BlankFile_ReturnsEmpty_NotNull()
    {
        // /notes shows a different message for "no file yet" vs "file exists but is blank".
        var path = NotesStore.NotesPath(_root);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "   \n  ");

        var content = await NotesStore.ReadAsync(_root, CancellationToken.None);

        Assert.NotNull(content);
        Assert.Equal(string.Empty, content);
    }

    [Fact]
    public async Task Clear_DeletesFile()
    {
        await NotesStore.AppendAsync(_root, "note", DateTime.Now, CancellationToken.None);

        NotesStore.Clear(_root);

        Assert.False(File.Exists(NotesStore.NotesPath(_root)));
    }

    [Fact]
    public void Clear_MissingFile_DoesNotThrow()
    {
        var ex = Record.Exception(() => NotesStore.Clear(_root));

        Assert.Null(ex);
    }
}
