using System.IO;
using Inferpal.Services.Presentation;
using Xunit;

namespace Inferpal.Tests;

// ──────────────────────────────────────────────────────────────────────────────────────────────
//  @folder lists the source files of every common language, and says what it leaves out.
//
//  Its allowlist had .cpp and .h but not .c, .go but not .rs, and no Ruby, PHP, Kotlin, Swift, SQL or
//  shell: a C folder listed only its headers, a Rust crate came back as an empty "Files:" with no
//  note — read as "this folder holds no source file".
// ──────────────────────────────────────────────────────────────────────────────────────────────
public sealed class FolderMentionTypesTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("inferpal-foldertypes-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private string Write(string name, string text)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, text);
        return path;
    }

    [Fact]
    public void ACProjectsSources_AreListedAndRead()
    {
        Write("main.c", "int main(void) { return helper(); }\n");
        Write("util.h", "int helper(void);\n");

        var context = MentionController.BuildFolderContext(_dir, CancellationToken.None);

        Assert.Contains("main.c", context);
        Assert.Contains("return helper();", context);
    }

    [Fact]
    public void ARustCrate_IsNotAnEmptyFolder()
    {
        Write("lib.rs", "pub fn add(a: i32, b: i32) -> i32 { a + b }\n");
        Write("Dockerfile", "FROM rust:1\n");

        var context = MentionController.BuildFolderContext(_dir, CancellationToken.None);

        Assert.Contains("lib.rs", context);
        Assert.Contains("Dockerfile", context);
    }

    [Fact]
    public void FilesOfOtherTypes_AreCounted_NotSilentlyLeftOut()
    {
        File.WriteAllBytes(Path.Combine(_dir, "logo.png"), [0x89, 0x50, 0x4E, 0x47]);
        File.WriteAllBytes(Path.Combine(_dir, "icon.png"), [0x89, 0x50, 0x4E, 0x47]);

        var context = MentionController.BuildFolderContext(_dir, CancellationToken.None);

        Assert.Contains("2 file(s) of other types are not listed: .png ×2", context);
    }

    [Fact]
    public void AFolderOfReadFiles_CarriesNoTypeNote()
    {
        // Reference arm: nothing left out, nothing said.
        Write("Program.cs", "class Program { }\n");

        var context = MentionController.BuildFolderContext(_dir, CancellationToken.None);

        Assert.DoesNotContain("other types", context);
    }
}
