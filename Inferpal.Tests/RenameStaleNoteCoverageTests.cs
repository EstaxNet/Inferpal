using System.IO;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// The note after a rename made by hand says when its search for the old name read only part of the workspace — found
/// or not — instead of saying nothing.
/// </summary>
/// <remarks>
/// Past 3,000 source files the scan returned "cannot say", and the edit tools then added no note at all: on exactly the
/// repositories where a hand rename is the likeliest to miss an occurrence, the silence read as "renamed everywhere".
/// </remarks>
public sealed class RenameStaleNoteCoverageTests : IDisposable
{
    private readonly string _root =
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"inferpal-renamecov-{Guid.NewGuid():N}")).FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void PastTheCap_TheNoteSaysThePartNotRead()
    {
        for (var i = 0; i < RenameIntent.MaxFilesScanned + 5; i++)
            File.WriteAllText(Path.Combine(_root, $"f{i:00000}.cs"), "class C { }");
        File.WriteAllText(Path.Combine(_root, "zzz.cs"), "class Z { void M() { OldName(); } }");   // past the cap, in ordinal order
        var edited = Path.Combine(_root, "f00000.cs");

        var note = RenameIntent.StaleNote(("OldName", "NewName"), _root, edited);

        Assert.Contains("⚠ `OldName` was looked for in part of the workspace only:", note, StringComparison.Ordinal);
        Assert.Contains($"{RenameIntent.MaxFilesScanned} ", note, StringComparison.Ordinal);   // the coverage counts
        Assert.Contains("rename_symbol(old_name: \"OldName\", new_name: \"NewName\")", note, StringComparison.Ordinal);
    }

    [Fact]
    public void AWholeScanWithNothingLeft_SaysNothing()
    {
        // Reference arm: an ordinary workspace where the rename is complete adds no line.
        File.WriteAllText(Path.Combine(_root, "A.cs"), "class A { void NewName() { } }");
        File.WriteAllText(Path.Combine(_root, "B.cs"), "class B { void M(A a) { a.NewName(); } }");

        Assert.Equal(string.Empty, RenameIntent.StaleNote(("OldName", "NewName"), _root, Path.Combine(_root, "A.cs")));
    }

    [Fact]
    public void AWholeScanThatFindsTheOldName_NamesTheFile()
    {
        File.WriteAllText(Path.Combine(_root, "A.cs"), "class A { void NewName() { } }");
        File.WriteAllText(Path.Combine(_root, "B.cs"), "class B { void M(A a) { a.OldName(); } }");

        var note = RenameIntent.StaleNote(("OldName", "NewName"), _root, Path.Combine(_root, "A.cs"));

        Assert.Contains("still appears in 1 file(s): B.cs", note, StringComparison.Ordinal);
        Assert.DoesNotContain("part of the workspace", note, StringComparison.Ordinal);
    }
}
