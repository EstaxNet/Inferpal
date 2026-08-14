using Inferpal.Services.Bench;
using Xunit;

namespace Inferpal.Tests;

// Per-file parsing and import resolution behind the measurement bench: which file declares which
// symbol, which is the ground truth a navigation question is graded against.
public class WorkspaceSymbolScannerTests
{
    // ── C# ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Parse_CSharp_ReadsNamespaceUsingsAndTypes()
    {
        const string src = """
            using System.Text;
            using Inferpal.Services.Rag;

            namespace Inferpal.Services.Prompting;

            internal sealed class WorkspaceSymbolScanner { }
            public interface IThing { }
            """;

        var f = WorkspaceSymbolScanner.Parse("Core/Services/Prompting/WorkspaceSymbolScanner.cs", ".cs", src);

        Assert.Equal("Inferpal.Services.Prompting", f.Module);
        Assert.Contains("System.Text", f.Imports);
        Assert.Contains("Inferpal.Services.Rag", f.Imports);
        Assert.Equal(["WorkspaceSymbolScanner", "IThing"], f.Symbols);
    }

    [Theory]
    [InlineData("internal readonly record struct ScanCoverage(int Total);", "ScanCoverage")]
    [InlineData("public record class Point(int X);", "Point")]
    [InlineData("internal sealed record PromptSection(string Kind);", "PromptSection")]
    [InlineData("public readonly struct Handle { }", "Handle")]
    public void Parse_CSharp_NamesRecordStructsCorrectly(string src, string expected)
    {
        // The alternation used to match `record struct X` as the type literally named "struct",
        // and the generated map showed it.
        var f = WorkspaceSymbolScanner.Parse("A/B.cs", ".cs", src);

        Assert.Equal([expected], f.Symbols);
    }

    [Fact]
    public void Parse_CSharp_SkipsUsingAliases()
    {
        // `using X = Y;` names no module — counting it would invent a dependency edge.
        const string src = "using Json = System.Text.Json;\nnamespace A;\nclass B { }";

        var f = WorkspaceSymbolScanner.Parse("A/B.cs", ".cs", src);

        Assert.Empty(f.Imports);
    }

    [Fact]
    public void Parse_CSharp_ReadsGlobalUsings()
    {
        var f = WorkspaceSymbolScanner.Parse("A/GlobalUsings.cs", ".cs", "global using Inferpal.Services.Rag;");

        Assert.Contains("Inferpal.Services.Rag", f.Imports);
    }

    // ── TypeScript / JavaScript ────────────────────────────────────────────────

    [Fact]
    public void Parse_TypeScript_UsesPathAsModuleAndResolvesImports()
    {
        const string src = """
            import { HostClient } from './hostClient';
            import * as vscode from 'vscode';
            export class ChatViewProvider { }
            export function activate() { }
            """;

        var f = WorkspaceSymbolScanner.Parse("vscode/src/chatViewProvider.ts", ".ts", src);

        Assert.Equal("vscode/src/chatViewProvider", f.Module);
        Assert.Contains("vscode/src/hostClient", f.Imports);
        Assert.DoesNotContain("vscode", f.Imports);          // package import, not a folder here
        Assert.Equal(["ChatViewProvider", "activate"], f.Symbols);
    }

    [Theory]
    [InlineData("vscode/src/a.ts", "./b", "vscode/src/b")]
    [InlineData("vscode/src/a.ts", "../protocol", "vscode/protocol")]
    [InlineData("vscode/src/deep/a.ts", "../../protocol", "vscode/protocol")]
    [InlineData("vscode/src/a.ts", "./sub/b.js", "vscode/src/sub/b")]
    [InlineData("a.ts", "./b", "b")]
    public void ResolveRelativeImport_NormalisesToRepoRelativePath(string from, string import, string expected) =>
        Assert.Equal(expected, WorkspaceSymbolScanner.ResolveRelativeImport(from, import));

    [Theory]
    [InlineData("vscode/src/a.ts", "vscode")]      // package
    [InlineData("vscode/src/a.ts", "@types/node")] // scoped package
    [InlineData("a.ts", "../escapes")]             // climbs out of the repository
    public void ResolveRelativeImport_RejectsWhatIsNotAFolderOfThisRepo(string from, string import) =>
        Assert.Null(WorkspaceSymbolScanner.ResolveRelativeImport(from, import));

    // ── Other languages: symbols only, no invented dependency edges ────────────

    [Theory]
    [InlineData(".py", "class Widget:\n    def helper(self):\n        pass\n", "Widget")]
    [InlineData(".go", "type Server struct {}\n", "Server")]
    [InlineData(".rs", "pub struct Engine;\n", "Engine")]
    [InlineData(".java", "public class Main { }\n", "Main")]
    public void Parse_OtherLanguages_ExtractTopLevelSymbolsOnly(string ext, string src, string expected)
    {
        var f = WorkspaceSymbolScanner.Parse("x/y" + ext, ext, src);

        Assert.Contains(expected, f.Symbols);
        Assert.Null(f.Module);
        Assert.Empty(f.Imports);
    }

    [Fact]
    public void Parse_Python_IgnoresIndentedDeclarations()
    {
        // Only top-level declarations name a file; methods are noise in a repository map.
        var f = WorkspaceSymbolScanner.Parse("x/y.py", ".py", "class Widget:\n    def helper(self):\n        pass\n");

        Assert.Equal(["Widget"], f.Symbols);
    }

    [Fact]
    public void Parse_NeverThrows_OnGarbage()
    {
        var f = WorkspaceSymbolScanner.Parse("x/y.cs", ".cs", new string('{', 5_000));

        Assert.NotNull(f);
        Assert.Empty(f.Symbols);
    }
}
