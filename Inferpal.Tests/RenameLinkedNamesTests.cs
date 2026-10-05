using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Inferpal.Services.Execution;
using Inferpal.Services.Tools;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Inferpal.Tests;

// ──────────────────────────────────────────────────────────────────────────────────────────────
//  rename_symbol renames every name C# links to the symbol.
//
//  A class's constructors and destructor carry its name but declare methods: renaming the class left
//  them behind — "class ShoppingCart { public Cart(…) }", CS1520 — under "✅ Applied". And an interface
//  method and its implementation (a virtual method and its override) were two candidates: picking one
//  renamed it alone, CS0535 / CS0115. Each test compiles the result.
// ──────────────────────────────────────────────────────────────────────────────────────────────
[Collection(CultureSerialCollection.Name)]
public sealed class RenameLinkedNamesTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("inferpal-renamelinked-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private void Write(string rel, string src) => File.WriteAllText(Path.Combine(_root, rel), src);
    private string Read(string rel) => File.ReadAllText(Path.Combine(_root, rel));

    private sealed class AlwaysApprove : IApprovalService
    {
        public Task<bool> RequestApprovalAsync(string toolName, string details, CancellationToken ct, string? subject = null,
                                               Services.CodeActions.DiffInfo? diff = null, bool forcePrompt = false)
            => Task.FromResult(true);
    }

    private async Task<string> Rename(string oldName, string newName, string? declaringFile = null)
    {
        var args = declaringFile is null
            ? JsonSerializer.SerializeToElement(new { old_name = oldName, new_name = newName, dry_run = false })
            : JsonSerializer.SerializeToElement(new { old_name = oldName, new_name = newName, dry_run = false, declaring_file = declaringFile });
        return await new RenameSymbolTool(new AlwaysApprove(), new FileHistoryService(), () => _root)
            .ExecuteAsync(args, CancellationToken.None);
    }

    /// <summary>The errors of the workspace's C# files compiled together.</summary>
    private string[] Errors(params string[] files)
    {
        var trees = files.Select(f => CSharpSyntaxTree.ParseText(Read(f))).ToList();
        var refs = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Select(p => MetadataReference.CreateFromFile(p));
        var compilation = CSharpCompilation.Create("probe", trees, refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        return [.. compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.Id)];
    }

    [Fact]
    public async Task AClassRenamed_TakesItsConstructorsAndDestructor()
    {
        Write("Cart.cs", "namespace App;\npublic class Cart\n{\n    public Cart(int n) { }\n    static Cart() { }\n    ~Cart() { }\n}\n");
        Write("Use.cs",  "namespace App;\npublic static class Use\n{\n    public static object Make() => new Cart(1);\n}\n");

        var said = await Rename("Cart", "ShoppingCart");

        Assert.False(Regex.IsMatch(Read("Cart.cs"), @"\bCart\b"), Read("Cart.cs"));
        Assert.Contains("new ShoppingCart(1)", Read("Use.cs"));
        Assert.Empty(Errors("Cart.cs", "Use.cs"));
        Assert.Contains("ShoppingCart", said);
    }

    [Fact]
    public async Task AnInterfaceMethod_IsRenamedWithItsImplementations_EvenWhenOneIsNamed()
    {
        Write("IRepo.cs", "namespace App;\npublic interface IRepo { void Save(); }\n");
        Write("Sql.cs",   "namespace App;\npublic class Sql : IRepo { public void Save() { } }\n");
        Write("Mem.cs",   "namespace App;\npublic class Mem : IRepo { void IRepo.Save() { } }\n");
        Write("Use.cs",   "namespace App;\npublic static class Use { public static void Go(IRepo r, Sql s) { r.Save(); s.Save(); } }\n");

        await Rename("Save", "Persist", declaringFile: "IRepo.cs");

        foreach (var f in new[] { "IRepo.cs", "Sql.cs", "Mem.cs", "Use.cs" })
            Assert.False(Regex.IsMatch(Read(f), @"\bSave\b"), $"{f}: {Read(f)}");
        Assert.Empty(Errors("IRepo.cs", "Sql.cs", "Mem.cs", "Use.cs"));
    }

    [Fact]
    public async Task AVirtualMethod_IsRenamedWithItsOverride()
    {
        Write("Shape.cs", "namespace App;\npublic class Shape { public virtual double Area() => 0; }\npublic class Square : Shape { public override double Area() => 1; }\n");

        await Rename("Area", "Surface");

        Assert.False(Regex.IsMatch(Read("Shape.cs"), @"\bArea\b"), Read("Shape.cs"));
        Assert.Empty(Errors("Shape.cs"));
    }

    [Fact]
    public async Task TwoUnrelatedHomonyms_AreStillAnAmbiguity()
    {
        // Reference arm: linking is by C#'s rules, never by name — two unrelated Handle methods are still refused.
        Write("Alpha.cs", "namespace App;\npublic class Alpha { public void Handle() { } }\n");
        Write("Beta.cs",  "namespace App;\npublic class Beta { public void Handle() { } }\n");

        await Rename("Handle", "Run");

        Assert.Contains("Handle", Read("Alpha.cs"));
        Assert.Contains("Handle", Read("Beta.cs"));
    }
}
