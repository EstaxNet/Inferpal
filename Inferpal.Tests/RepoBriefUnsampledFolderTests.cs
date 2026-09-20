using System.IO;
using Inferpal.Services.Commands;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A folder the repository brief did not look inside is named, not left as a bare name.
/// </summary>
/// <remarks>
/// <para>
/// The brief is what <c>/onboard context</c> writes into <c>.inferpal/context.md</c> — the system
/// prompt of every session after it. Its "Inside each top-level folder" section exists for a reason
/// the code states itself: <i>without it the model only sees folder names, and a name is exactly the
/// kind of thing it will happily invent a purpose for</i>. A folder listed in the layout with no
/// sample line is back in that state, and three different causes render identically: cut by the
/// sample cap, empty, or holding nothing but build/vendor folders.
/// </para>
/// <para>
/// ⚠ The heading says "(sample)", which declares that not everything is there — but not WHICH, and
/// that is the half the model needs. Saying it costs one line per cause and only when it applies.
/// </para>
/// </remarks>
public sealed class RepoBriefUnsampledFolderTests : IDisposable
{
    private readonly string _root =
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"inferpal-brief-{Guid.NewGuid():N}")).FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static Task<(string, int)> Git(string args, CancellationToken _) =>
        Task.FromResult(("feat: something", 0));

    private void Folder(string name, string? child)
    {
        var d = Directory.CreateDirectory(Path.Combine(_root, name)).FullName;
        if (child is not null) File.WriteAllText(Path.Combine(d, child), "// x");
    }

    [Fact]
    public async Task ARepositoryThatFitsTheSample_SaysNothingAboutMissingFolders()
    {
        // The reference arm: the notices must not fire on an ordinary repository, or they become
        // the noise that gets a warning ignored.
        Folder("Core", "A.cs");
        Folder("Host", "B.cs");

        var brief = await OnboardCommandHandler.BuildRepoBriefAsync(_root, Git, CancellationToken.None);

        Assert.Contains("`Core/` → A.cs", brief, StringComparison.Ordinal);
        Assert.DoesNotContain("not looked inside", brief, StringComparison.Ordinal);
        Assert.DoesNotContain("nothing to sample", brief, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FoldersPastTheSampleCap_AreNamed_RatherThanLeftAsBareNames()
    {
        for (int i = 1; i <= 15; i++) Folder($"Folder{i:00}", $"File{i:00}.cs");

        var brief = await OnboardCommandHandler.BuildRepoBriefAsync(_root, Git, CancellationToken.None);

        // WITNESS: the section was produced and the sampled folders are there — the assertion below
        // is about what it says of the rest, not about a brief that never happened.
        Assert.Contains("`Folder01/` → File01.cs", brief, StringComparison.Ordinal);

        Assert.Contains("not looked inside", brief, StringComparison.Ordinal);
        Assert.Contains("Folder13", brief, StringComparison.Ordinal);
        Assert.Contains("Folder15", brief, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFolderWithNothingToShow_IsNamedToo()
    {
        // Empty, and "holds only build/vendor folders" — both produced no line at all, which reads
        // exactly like a folder that was never looked at.
        Folder("Real", "A.cs");
        Folder("TrulyEmpty", null);
        Directory.CreateDirectory(Path.Combine(_root, "OnlyExcluded", "node_modules"));

        var brief = await OnboardCommandHandler.BuildRepoBriefAsync(_root, Git, CancellationToken.None);

        Assert.Contains("`Real/` → A.cs", brief, StringComparison.Ordinal);
        Assert.Contains("nothing to sample", brief, StringComparison.Ordinal);
        Assert.Contains("TrulyEmpty", brief, StringComparison.Ordinal);
        Assert.Contains("OnlyExcluded", brief, StringComparison.Ordinal);
    }
}
