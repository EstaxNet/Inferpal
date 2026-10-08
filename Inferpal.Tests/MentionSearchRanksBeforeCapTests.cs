using System.IO;
using Inferpal.Services.Presentation;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// The @-mention picker (both front-ends: <c>MentionController.FindFiles</c>/<c>FindFolders</c>) ranks every match the
/// walk met, then keeps the best eight — it never stops at a number of matches first.
/// </summary>
/// <remarks>
/// The walk stopped at 100 matching files (60 folders) in the order it met them: with a hundred names that merely
/// contained the query in an earlier folder, the file named exactly as typed was never collected, and the picker offered
/// eight lesser matches. The walk is now bounded by the entries it visits, never by its matches.
/// </remarks>
public sealed class MentionSearchRanksBeforeCapTests : IDisposable
{
    private readonly string _root =
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"inferpal-mention-rank-{Guid.NewGuid():N}")).FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void TheFileNamedAsTyped_IsOffered_AfterAHundredLesserMatches()
    {
        // "a/" is walked before "z/" in any order: 120 names that contain "order", then the exact one.
        var a = Directory.CreateDirectory(Path.Combine(_root, "a")).FullName;
        for (var i = 0; i < 120; i++) File.WriteAllText(Path.Combine(a, $"purchaseorder{i:000}.cs"), "// x");
        var z = Directory.CreateDirectory(Path.Combine(_root, "z")).FullName;
        File.WriteAllText(Path.Combine(z, "order.cs"), "// x");

        var found = MentionController.FindFiles(_root, "order", CancellationToken.None);

        Assert.Equal(8, found.Count);   // witness: the lesser matches are there too
        Assert.Equal(Path.Combine(z, "order.cs"), found[0]);
    }

    [Fact]
    public void TheFolderNamedAsTyped_IsOffered_AfterSixtyLesserMatches()
    {
        var a = Directory.CreateDirectory(Path.Combine(_root, "a")).FullName;
        for (var i = 0; i < 70; i++) Directory.CreateDirectory(Path.Combine(a, $"mycore{i:00}"));
        var core = Directory.CreateDirectory(Path.Combine(_root, "z", "core")).FullName;

        var found = MentionController.FindFolders(_root, "core", CancellationToken.None);

        Assert.Equal(core, found[0]);
    }

    [Fact]
    public void AFewMatches_AreRankedAsBefore()
    {
        // Reference arm: exact, then prefix, then contains.
        File.WriteAllText(Path.Combine(_root, "myorder.cs"), "// x");
        File.WriteAllText(Path.Combine(_root, "orderline.cs"), "// x");
        File.WriteAllText(Path.Combine(_root, "order.cs"), "// x");

        var found = MentionController.FindFiles(_root, "order", CancellationToken.None);

        Assert.Equal(["order.cs", "orderline.cs", "myorder.cs"], found.Select(Path.GetFileName));
    }
}
