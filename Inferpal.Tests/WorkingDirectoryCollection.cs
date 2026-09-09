using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// Test classes that move the process's current directory. <c>UpdateMemoryTool.FindProjectRoot</c>
/// walks up from <c>Directory.GetCurrentDirectory()</c>, so exercising it means pointing the whole
/// process at a temp workspace — a seam every other test in the run shares.
/// </summary>
public static class WorkingDirectoryCollection
{
    public const string Name = "cwd-serial";
}

[CollectionDefinition(WorkingDirectoryCollection.Name, DisableParallelization = true)]
public class WorkingDirectoryCollectionDefinition { }
