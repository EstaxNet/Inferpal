using System;
using System.IO;
using Inferpal.Services.Governance;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// An ignore file held by another program at the moment it is read keeps protecting what it protected. Read as "no
/// rules", the files it excludes are readable by the model until its next edit: its date does not move, and the stamp
/// says nothing changed.
/// </summary>
public sealed class ExclusionFileHeldTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("inferpal-held").FullName;

    private string Ignore => Path.Combine(_root, ".aiignore");
    private string Secret => Path.Combine(_root, "secret.txt");

    public ExclusionFileHeldTests()
    {
        File.WriteAllText(Ignore, "secret.txt\n");
        File.WriteAllText(Secret, "the key\n");
        ContentExclusions.Declare(_root, () => ".aiignore");
    }

    public void Dispose()
    {
        ContentExclusions.ForgetForTests(_root);
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    /// <summary>What another program does: the file open and shut to anyone else, its date moved (FileShare.None is an
    /// advisory lock off Windows, hence the guard).</summary>
    private FileStream Held()
    {
        var held = new FileStream(Ignore, FileMode.Open, FileAccess.Read, FileShare.None);
        File.SetLastWriteTimeUtc(Ignore, DateTime.UtcNow.AddMinutes(1));
        return held;
    }

    [Fact]
    public void AFileHeldForAMoment_KeepsItsRules()
    {
        if (!OperatingSystem.IsWindows()) return;
        Assert.NotNull(ContentExclusions.Of(Secret));

        using (Held())
        {
            ContentExclusions.Changed(Ignore);
            Assert.NotNull(ContentExclusions.Of(Secret));
        }
    }

    [Fact]
    public void AFileHeldForAMoment_IsReadAgain_WithoutAnEdit()
    {
        if (!OperatingSystem.IsWindows()) return;
        Assert.NotNull(ContentExclusions.Of(Secret));

        using (Held())
        {
            ContentExclusions.Changed(Ignore);
            Assert.NotNull(ContentExclusions.Of(Secret));
        }
        // Its bytes changed while nobody could read them: the next look takes them, its size and date those it was held with.
        var date = File.GetLastWriteTimeUtc(Ignore);
        File.WriteAllText(Ignore, "others.txt\n");
        File.SetLastWriteTimeUtc(Ignore, date);
        ContentExclusions.Changed(Ignore);

        Assert.Null(ContentExclusions.Of(Secret));
    }

    [Fact]
    public void AFileNeverRead_IsReadAtTheNextLook_OnceItCanBe()
    {
        if (!OperatingSystem.IsWindows()) return;
        ContentExclusions.AnyRules();   // what a test beside this one does: every declared root read, this one cached
        using (Held())
        {
            // Declared again while held: a test running beside this one (AnyRules reads every declared root) could
            // otherwise read the file before it is held, and the rules it read stay for RecheckEvery.
            ContentExclusions.ForgetForTests(_root);
            ContentExclusions.Declare(_root, () => ".aiignore");
            Assert.Null(ContentExclusions.Of(Secret));   // nothing was ever read: no rule to keep, and it is said in /diagnostics
        }

        ContentExclusions.Changed(Ignore);
        Assert.NotNull(ContentExclusions.Of(Secret));
    }

    [Fact]
    public void AFileRemoved_TakesItsRulesWithIt()
    {
        Assert.NotNull(ContentExclusions.Of(Secret));

        File.Delete(Ignore);
        ContentExclusions.Changed(Ignore);

        Assert.Null(ContentExclusions.Of(Secret));
    }
}
