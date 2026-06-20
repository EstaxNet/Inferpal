using System.IO;
using Inferpal.Commands;
using Xunit;

namespace Inferpal.Tests;

// Covers the pure test-file path resolution used by the /test code action
// (and the "Add unit tests" context-menu command).
public class TestFilePathResolverTests
{
    private static string Name(string path) => Path.GetFileName(TestFilePathResolver.Resolve(path));

    [Theory]
    [InlineData(@"C:\proj\Foo.cs",      "FooTests.cs")]
    [InlineData(@"C:\proj\Foo.swift",   "FooTests.swift")]
    [InlineData(@"C:\proj\Foo.java",    "FooTest.java")]
    [InlineData(@"C:\proj\Foo.kt",      "FooTest.kt")]
    [InlineData(@"C:\proj\Foo.php",     "FooTest.php")]
    [InlineData(@"C:\proj\foo.go",      "foo_test.go")]
    [InlineData(@"C:\proj\foo.rb",      "foo_test.rb")]
    [InlineData(@"C:\proj\foo.rs",      "foo_test.rs")]
    [InlineData(@"C:\proj\foo.py",      "test_foo.py")]
    [InlineData(@"C:\proj\foo.ts",      "foo.test.ts")]
    [InlineData(@"C:\proj\foo.tsx",     "foo.test.tsx")]
    [InlineData(@"C:\proj\foo.js",      "foo.test.js")]
    [InlineData(@"C:\proj\foo.unknown", "fooTests.unknown")]
    public void Resolve_UsesIdiomaticNaming(string source, string expectedFile)
    {
        Assert.Equal(expectedFile, Name(source));
    }

    [Fact]
    public void Resolve_KeepsTheSameDirectory()
    {
        var resolved = TestFilePathResolver.Resolve(@"C:\proj\src\Foo.cs");
        Assert.Equal(@"C:\proj\src", Path.GetDirectoryName(resolved));
    }

    [Theory]
    [InlineData(@"C:\proj\FooTests.cs")]
    [InlineData(@"C:\proj\FooTest.java")]
    [InlineData(@"C:\proj\foo_test.go")]
    [InlineData(@"C:\proj\test_foo.py")]
    [InlineData(@"C:\proj\foo.test.ts")]
    [InlineData(@"C:\proj\foo.spec.ts")]
    public void Resolve_OnAnExistingTestFile_ReturnsItself(string testPath)
    {
        // /test on a test file extends it in place instead of creating FooTestsTests.
        Assert.Equal(testPath, TestFilePathResolver.Resolve(testPath));
    }
}
