using System.IO;
using Inferpal.Services.Rag;

namespace Inferpal.Tests;

/// <summary>
/// The one redirect of the code index store for the whole test run — never the user's own index.
/// </summary>
/// <remarks>
/// <see cref="RagDatabase.BaseDir"/> is static. Tests that each pointed it at their own folder, running
/// in parallel, moved each other's database between two passes of the same test. The store is one
/// file per workspace root and every test uses its own root, so one shared folder isolates them anyway.
/// </remarks>
internal static class TestRagStore
{
    internal static readonly string Dir =
        Path.Combine(Path.GetTempPath(), "inferpal-tests", $"ragdb-{Guid.NewGuid():N}");

    internal static void Redirect() => RagDatabase.BaseDir = () => Dir;

    /// <summary>The documentation index's folder for the run: a host a test builds opens that database.</summary>
    internal static readonly string DocsDir =
        Path.Combine(Path.GetTempPath(), "inferpal-tests", $"docsdb-{Guid.NewGuid():N}");
}
