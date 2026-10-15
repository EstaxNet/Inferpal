using System.IO;
using Inferpal.Services.Editor;
using Inferpal.Services.Presentation;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// An <c>@file</c> attaches the file as the editor shows it — its unsaved changes included — as <c>read_file</c> and
/// <c>@folder</c> read it.
/// </summary>
/// <remarks>
/// Visual Studio read an <c>@file</c> (and an open file picked from the bare <c>@</c> list) from the disk: a file with
/// unsaved changes was attached as it was before them, and the question about the code on the screen was answered about
/// the code last saved.
/// </remarks>
public sealed class FileMentionUnsavedTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("inferpal-file-mention-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort cleanup */ }
    }

    [Fact]
    public async Task AFileWithUnsavedChanges_IsAttachedAsTheEditorShowsIt()
    {
        var file = Path.Combine(_dir, "Cart.cs");
        File.WriteAllText(file, "int Total() => 1; // saved\n");
        var overlay = new OpenDocumentOverlay();
        overlay.Set(file, "int Total() => 2; // typed, not saved\n");

        Assert.Equal("int Total() => 2; // typed, not saved\n",
                     await MentionController.FileMentionTextAsync(file, overlay, CancellationToken.None));
    }

    [Fact]
    public async Task ASavedFile_IsReadFromTheDisk()
    {
        // Reference arm: nothing unsaved, or no editor buffers at all.
        var file = Path.Combine(_dir, "Cart.cs");
        File.WriteAllText(file, "int Total() => 1;\n");
        var overlay = new OpenDocumentOverlay();
        overlay.Set(file, "int Total() => 1;\n", unsaved: false);

        Assert.Equal("int Total() => 1;\n", await MentionController.FileMentionTextAsync(file, overlay, CancellationToken.None));
        Assert.Equal("int Total() => 1;\n", await MentionController.FileMentionTextAsync(file, null, CancellationToken.None));
    }

    [Fact]
    public void VisualStudio_AttachesAFileThroughTheSharedReader()
    {
        var vm = ConventionCoverageTests.CodeOnly(Path.Combine(ConventionCoverageTests.RepoRoot(), "Inferpal", "ToolWindow",
                                                                "InferpalToolWindowData.Mentions.cs"));
        Assert.Contains("MentionController.FileMentionTextAsync(filePath, _tools.Overlay, ct)", vm, StringComparison.Ordinal);
        Assert.Contains("await overlay.RefreshAsync(ct, [filePath]);", vm, StringComparison.Ordinal);
    }
}
