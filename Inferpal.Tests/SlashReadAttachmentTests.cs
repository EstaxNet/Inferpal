using System.IO;
using Inferpal.Services;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// <c>/read</c> attaches a file it read, never the sentence saying it could not: a missing path, a folder, a binary file
/// or a path outside the workspace is shown as a message, not as a 📄 chip named after the file.
/// </summary>
/// <remarks>
/// Both front-ends attached whatever <c>read_file</c> returned: <c>/read Pirce.cs</c> put "File not found" under a chip
/// called <c>Pirce.cs</c> — the user believes the file joined the question, and the model receives the refusal as its
/// content. The twin of an @-mention that attaches nothing, which says so.
/// </remarks>
public class SlashReadAttachmentTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "inferpal-tests", $"slashread-{Guid.NewGuid():N}")).FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static SlashToolAction Read(string path) =>
        (SlashToolAction)SlashCommandRouter.Route($"/read {path}", [])!;

    [Fact]
    public void AFileThatIsRead_IsAttached()
    {
        File.WriteAllText(Path.Combine(_root, "Pricing.cs"), "class Pricing { }\n");

        Assert.True(Read(Path.Combine(_root, "Pricing.cs")).AttachesResult(_root, overlay: null));
        Assert.True(Read("Pricing.cs").AttachesResult(_root, overlay: null));   // relative to the root, as the tool reads it
    }

    [Fact]
    public void WhatIsNotARead_IsNotAttached()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        File.WriteAllBytes(Path.Combine(_root, "lib.dll"), [0x4D, 0x5A, 0x00, 0x00, 0x03]);
        var outside = Path.Combine(Path.GetTempPath(), $"inferpal-outside-{Guid.NewGuid():N}.cs");
        File.WriteAllText(outside, "class Elsewhere { }\n");
        try
        {
            Assert.False(Read("Pirce.cs").AttachesResult(_root, overlay: null));    // missing
            Assert.False(Read("src").AttachesResult(_root, overlay: null));         // a folder
            Assert.False(Read("lib.dll").AttachesResult(_root, overlay: null));     // binary
            Assert.False(Read(outside).AttachesResult(_root, overlay: null));       // outside the workspace
        }
        finally { File.Delete(outside); }
    }

    [Fact]
    public void AnUnsavedBuffer_IsAttached_LikeTheToolReadsIt()
    {
        var overlay = new Services.Editor.OpenDocumentOverlay();
        var path    = Path.Combine(_root, "New.cs");
        overlay.Set(path, "class New { }\n");

        Assert.True(Read(path).AttachesResult(_root, overlay));
        Assert.False(Read(path).AttachesResult(_root, overlay: null));   // reference arm: no buffer, no file
    }

    [Fact]
    public void OtherAttachingCommands_AreUnchanged()
    {
        // /diff attaches git's answer as before; a command without a chip attaches nothing.
        Assert.True(new SlashToolAction("get_git_status", new { mode = "diff" }, AttachAs: "📊 git diff").AttachesResult(_root, null));
        Assert.False(new SlashToolAction("list_files", new { path = "." }).AttachesResult(_root, null));
    }

    // ── The Visual Studio window (not runnable from the suite) ──────────────────

    [Fact]
    public void TheVisualStudioWindow_AsksTheSameQuestion()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Inferpal.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var vm = ConventionCoverageTests.CodeOnly(
            Path.Combine(dir!.FullName, "Inferpal", "ToolWindow", "InferpalToolWindowData.SlashCommands.cs"));

        Assert.Contains("case SlashToolAction tool:", vm, StringComparison.Ordinal);   // witness
        Assert.Contains("tool.AttachesResult(", vm, StringComparison.Ordinal);
        Assert.DoesNotContain("attachAs: tool.AttachAs)", vm, StringComparison.Ordinal);
    }
}
