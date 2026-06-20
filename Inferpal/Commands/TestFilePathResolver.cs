using System.IO;

namespace Inferpal.Commands;

/// <summary>
/// Pure, testable resolution of the conventional unit-test file path for a source file.
/// <para>
/// The test file is placed <b>next to the source</b> (same directory) using the idiomatic
/// naming of the detected language: <c>Foo.cs → FooTests.cs</c>, <c>foo.py → test_foo.py</c>,
/// <c>foo.ts → foo.test.ts</c>, <c>foo.go → foo_test.go</c>, <c>Foo.java → FooTest.java</c>…
/// </para>
/// <para>
/// If the source file already looks like a test file, its own path is returned so <c>/test</c>
/// extends it in place rather than creating a <c>FooTestsTests</c> sibling.
/// </para>
/// </summary>
internal static class TestFilePathResolver
{
    /// <summary>Returns the absolute path of the test file for <paramref name="sourcePath"/>.</summary>
    public static string Resolve(string sourcePath)
    {
        var dir  = Path.GetDirectoryName(sourcePath) ?? string.Empty;
        var ext  = Path.GetExtension(sourcePath);                  // ".cs"
        var name = Path.GetFileNameWithoutExtension(sourcePath);   // "Foo" (or "foo.test" for foo.test.ts)

        // Already a test file → edit it in place.
        if (IsTestFileName(name))
            return sourcePath;

        var testFile = ext.ToLowerInvariant() switch
        {
            ".cs" or ".swift"                              => $"{name}Tests{ext}",
            ".java" or ".kt" or ".php"                     => $"{name}Test{ext}",
            ".go" or ".rb" or ".rs"                        => $"{name}_test{ext}",
            ".py"                                          => $"test_{name}{ext}",
            ".ts" or ".tsx" or ".js" or ".jsx"
                or ".mjs" or ".cjs"                        => $"{name}.test{ext}",
            _                                              => $"{name}Tests{ext}",
        };

        return Path.Combine(dir, testFile);
    }

    /// <summary>
    /// True when <paramref name="nameWithoutExt"/> already follows a common test-file convention
    /// (<c>FooTests</c>, <c>FooTest</c>, <c>foo_test</c>, <c>test_foo</c>, <c>foo.test</c>, <c>foo.spec</c>).
    /// </summary>
    public static bool IsTestFileName(string nameWithoutExt)
    {
        var n = nameWithoutExt;
        return n.EndsWith("Tests", System.StringComparison.Ordinal)
            || n.EndsWith("Test",  System.StringComparison.Ordinal)
            || n.EndsWith("_test", System.StringComparison.OrdinalIgnoreCase)
            || n.StartsWith("test_", System.StringComparison.OrdinalIgnoreCase)
            || n.EndsWith(".test", System.StringComparison.OrdinalIgnoreCase)
            || n.EndsWith(".spec", System.StringComparison.OrdinalIgnoreCase);
    }
}
