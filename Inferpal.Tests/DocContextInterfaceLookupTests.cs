using System.IO;
using Inferpal.Services.Prompting;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// <c>/doc</c> lists the members of the interfaces a class implements, read from sibling
/// <c>I*.cs</c> files.
/// </summary>
public sealed class DocContextInterfaceLookupTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "inferpal-doccontext-" + Guid.NewGuid().ToString("N"));

    public DocContextInterfaceLookupTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    /// <summary>
    /// The lookup searched the text "interface IFoo", which also matches "interface IFooBar": a file
    /// declaring IFooBar that was read first handed its members to IFoo, and the model documented
    /// members the class does not have.
    /// </summary>
    [Fact]
    public async Task AnInterfaceWhoseNameStartsAnother_IsNotConfusedWithIt()
    {
        // File names chosen so the IFooBar file is enumerated first on an ordered file system.
        File.WriteAllText(Path.Combine(_dir, "IFooA.cs"), "namespace N;\npublic interface IFooBar\n{\n    void Bar();\n}\n");
        File.WriteAllText(Path.Combine(_dir, "IFooB.cs"), "namespace N;\npublic interface IFoo\n{\n    void Foo();\n}\n");
        var source  = Path.Combine(_dir, "Impl.cs");
        var content = "namespace N;\npublic class Impl : IFoo\n{\n    public void Foo() { }\n}\n";

        var block = await DocContextExtractor.BuildContextBlockAsync(source, content, CancellationToken.None);

        Assert.Contains("Foo()", block, StringComparison.Ordinal);
        Assert.DoesNotContain("Bar()", block, StringComparison.Ordinal);
    }
}
