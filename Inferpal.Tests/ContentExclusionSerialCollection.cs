using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// <see cref="ContentExclusionTests"/>, run alone.
/// </summary>
/// <remarks>
/// ⚠ Whether Inferpal's backups are read depends on EVERY declared root: while any one excludes something, none is read.
/// The reference arm reads a backup with no ignore file of its own, so beside any class that declares a root with an
/// ignore file it was refused — a red that depended on which classes the scheduler ran together.
/// </remarks>
public static class ContentExclusionSerialCollection
{
    public const string Name = "content-exclusion-serial";
}

[CollectionDefinition(ContentExclusionSerialCollection.Name, DisableParallelization = true)]
public class ContentExclusionSerialCollectionDefinition { }
