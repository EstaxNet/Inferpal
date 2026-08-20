using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// Test classes that launch real shells share <c>ShellLauncher._overrideForTests</c>, a
/// process-wide seam: run in parallel, one class's override — even written cleanly — leaks into
/// another class's session mid-test (seen once on the ubuntu CI leg, 2026-08-20, as a PowerShell
/// wrapper handed to /bin/bash). Same pattern as the signal-test collection.
/// </summary>
public static class ShellSerialCollection
{
    public const string Name = "shell-serial";
}

[CollectionDefinition(ShellSerialCollection.Name, DisableParallelization = true)]
public class ShellSerialCollectionDefinition { }
