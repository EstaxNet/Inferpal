using System.IO;
using System.Text.Json;
using Inferpal.Services.Execution;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

// rename_symbol writes files, so its precision matters more than anywhere else: the syntactic path
// rewrote EVERY identifier spelled like the target, which on real code means renaming a dozen
// unrelated methods that happen to share a name (roadmap §14).
[Collection(CultureSerialCollection.Name)]
public class RenameSymbolSemanticTests : IDisposable
{
    private readonly string _root;

    public RenameSymbolSemanticTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ob-rename-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private void Write(string rel, string src)
    {
        var full = Path.Combine(_root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, src);
    }

    private string Read(string rel) => File.ReadAllText(Path.Combine(_root, rel));

    private async Task<string> RunAsync(string oldName, string newName, bool dryRun, string? declaringFile = null)
    {
        var approval = new AlwaysApprove();
        var tool = new RenameSymbolTool(approval, new FileHistoryService(), () => _root);
        var json = $$"""
            {"old_name": {{JsonSerializer.Serialize(oldName)}},
             "new_name": {{JsonSerializer.Serialize(newName)}},
             "dry_run": {{(dryRun ? "true" : "false")}}
             {{(declaringFile is null ? "" : $", \"declaring_file\": {JsonSerializer.Serialize(declaringFile)}")}}}
            """;
        return await tool.ExecuteAsync(JsonDocument.Parse(json).RootElement, CancellationToken.None);
    }

    private sealed class AlwaysApprove : IApprovalService
    {
        public Task<bool> RequestApprovalAsync(
            string toolName, string details, CancellationToken ct,
            string? subject = null, Services.CodeActions.DiffInfo? diff = null, bool forcePrompt = false)
            => Task.FromResult(true);
    }

    [Fact]
    public async Task AHomonymInAnotherType_IsNotRenamed()
    {
        // THE case. Syntactically, "Handle" appears five times across three files; only two of
        // them are the method being renamed.
        Write("Alpha.cs", """
            namespace App;
            public class Alpha { public void Handle() { } }
            """);
        Write("Beta.cs", """
            namespace App;
            public class Beta { public void Handle() { } }
            """);
        Write("Caller.cs", """
            namespace App;
            public class Caller
            {
                public void Run() { new Alpha().Handle(); new Beta().Handle(); }
            }
            """);

        // Which Handle is SAID: with two declared, the name alone designates neither (see the next test).
        await RunAsync("Handle", "Process", dryRun: false, declaringFile: "Alpha.cs");

        Assert.Contains("public void Process()", Read("Alpha.cs"));
        Assert.Contains("public void Handle()", Read("Beta.cs"));   // untouched
        Assert.Contains("new Alpha().Process()", Read("Caller.cs"));
        Assert.Contains("new Beta().Handle()", Read("Caller.cs"));  // untouched
    }

    /// <summary>
    /// Two classes declaring <c>Handle</c>: the name alone designates neither. The first declaration scanned was
    /// renamed — Alpha's, by file order — under "Applied", Beta's left untouched, whichever the model meant.
    /// </summary>
    [Fact]
    public async Task AnAmbiguousName_IsRefused_WithItsCandidates_AndNothingWritten()
    {
        Write("Alpha.cs", """
            namespace App;
            public class Alpha { public void Handle() { } }
            """);
        Write("Beta.cs", """
            namespace App;
            public class Beta { public void Handle() { } }
            """);

        var report = await RunAsync("Handle", "Process", dryRun: false);

        Assert.Contains("nothing was renamed", report);
        Assert.Contains("App.Alpha.Handle()", report);
        Assert.Contains("App.Beta.Handle()", report);
        Assert.Contains("declaring_file", report);
        Assert.Contains("public void Handle()", Read("Alpha.cs"));
        Assert.Contains("public void Handle()", Read("Beta.cs"));
    }

    /// <summary>
    /// ⚠ The model narrows an ambiguous rename to one declaration — and when its narrowing matched none (a file it named
    /// with "./", a line taken from the identifier instead of the declaration's start), the tool fell through to the
    /// text-based path and renamed EVERY symbol of that name, both methods the model had just chosen between.
    /// </summary>
    [Fact]
    public async Task ANarrowingThatMatchesNothing_RenamesNothing()
    {
        Write("Alpha.cs", """
            namespace App;
            public class Alpha { public void Handle() { } }
            """);
        Write("Beta.cs", """
            namespace App;
            public class Beta { public void Handle() { } }
            """);

        var report = await RunAsync("Handle", "Process", dryRun: false, declaringFile: "Gamma.cs");

        Assert.Contains("nothing was renamed", report);
        Assert.Contains("declaring_file 'Gamma.cs'", report);
        Assert.Contains("public void Handle()", Read("Alpha.cs"));
        Assert.Contains("public void Handle()", Read("Beta.cs"));
    }

    [Fact]
    public async Task ADeclaringFileWrittenWithALeadingDotSlash_IsThatFile()
    {
        Write("Alpha.cs", """
            namespace App;
            public class Alpha { public void Handle() { } }
            """);
        Write("Beta.cs", """
            namespace App;
            public class Beta { public void Handle() { } }
            """);

        await RunAsync("Handle", "Process", dryRun: false, declaringFile: "./Alpha.cs");

        Assert.Contains("public void Process()", Read("Alpha.cs"));
        Assert.Contains("public void Handle()", Read("Beta.cs"));   // untouched
    }

    [Theory]
    [InlineData("src/Cart.cs", "src/Cart.cs", true)]
    [InlineData("src/Cart.cs", "./src/Cart.cs", true)]
    [InlineData("src/Cart.cs", "Cart.cs", true)]
    [InlineData("src/MyCart.cs", "Cart.cs", false)]                   // a suffix starts at a folder boundary
    [InlineData("src/Cart.cs", "other/Cart.cs", false)]
    public void TheDeclaringFile_IsMatchedOnAFolderBoundary(string rel, string named, bool same)
    {
        var path = Path.Combine(_root, rel.Replace('/', Path.DirectorySeparatorChar));
        Assert.Equal(same, Inferpal.Services.Lsp.CSharpSemanticIndex.IsDeclaringFile(path, named));
    }

    [Fact]
    public async Task APartialTypeDeclaredInTwoFiles_IsOneSymbol()
    {
        // Reference arm: one symbol, two declarations — not an ambiguity.
        Write("Part1.cs", """
            namespace App;
            public partial class Widget { public void A() { } }
            """);
        Write("Part2.cs", """
            namespace App;
            public partial class Widget { public void B() { } }
            """);

        var report = await RunAsync("Widget", "Gadget", dryRun: false);

        Assert.DoesNotContain("nothing was renamed", report);
        Assert.Contains("partial class Gadget", Read("Part1.cs"));
        Assert.Contains("partial class Gadget", Read("Part2.cs"));
    }

    [Fact]
    public async Task TheDeclarationItself_IsRenamedToo()
    {
        // Unlike a reference query, a rename must rewrite the declaration — otherwise the code
        // stops compiling in the most obvious way possible.
        Write("Solo.cs", """
            namespace App;
            public class Solo { public void Only() { } }
            """);

        await RunAsync("Only", "Single", dryRun: false);

        Assert.Contains("public void Single()", Read("Solo.cs"));
        Assert.DoesNotContain("Only", Read("Solo.cs"));
    }

    [Fact]
    public async Task DryRun_ChangesNothingOnDisk()
    {
        Write("Alpha.cs", """
            namespace App;
            public class Alpha { public void Handle() { } }
            """);
        var before = Read("Alpha.cs");

        var report = await RunAsync("Handle", "Process", dryRun: true);

        Assert.Equal(before, Read("Alpha.cs"));
        Assert.Contains("Dry run", report);
    }

    [Fact]
    public async Task AStringContainingTheName_IsNeverRewritten()
    {
        Write("Alpha.cs", """
            namespace App;
            public class Alpha
            {
                public void Handle() { var msg = "call Handle to continue"; }
            }
            """);

        await RunAsync("Handle", "Process", dryRun: false);

        var after = Read("Alpha.cs");
        Assert.Contains("public void Process()", after);
        Assert.Contains("\"call Handle to continue\"", after);
    }
    // ── What the scan never opened ──────────────────────────────────────────────
    //
    // The enumeration drops any source past CodeChunker.MaxFileSizeBytes (200 kB — a generated
    // Reference.cs, a bundled script, a big designer file). This tool WRITES what it finds, so a
    // file it never opened keeps the old name while the report says the rename is done; and with
    // no hit at all, "No occurrences found in N scanned file(s)" reads as "the symbol does not
    // exist". Same discipline as analyze_impact and trace_dependency: a capped scan says so.

    /// <summary>
    /// A source bigger than the size filter, carrying <paramref name="body"/> where nobody will look.
    /// </summary>
    /// <remarks>
    /// ⚠ The body must not DECLARE a second method spelled like the target: the semantic rename then
    /// has two unrelated symbols to choose from and may pick this one, so the small file gets no
    /// spans and the test measures the wrong thing. A CALL is also the realistic shape — the
    /// declaration lives in ordinary code, the generated file uses it.
    /// </remarks>
    private void WriteOversized(string rel, string body)
    {
        var padding = new string('x', (int)Inferpal.Services.Rag.CodeChunker.MaxFileSizeBytes);
        Write(rel, $"namespace App;\n// {padding}\n{body}\n");
        Assert.True(new FileInfo(Path.Combine(_root, rel)).Length >= Inferpal.Services.Rag.CodeChunker.MaxFileSizeBytes,
                    "the fixture is not oversized — the test measures nothing.");
    }

    /// <summary>The exact line the product appends when one file of two was left out.</summary>
    private static string OneOfTwoLeftOut => Inferpal.Localization.Strings.ScanPartial(1, 2);

    [Fact]
    public async Task AFileTooBigToScan_IsNotSilentlyCountedAsSearched()
    {
        Write("Small.cs", "namespace App;\npublic class Small { }\n");
        WriteOversized("Big.cs", "public class Big { public void Handle() { } }");

        var report = await RunAsync("Handle", "Process", dryRun: true);

        Assert.Contains("No occurrences", report);
        Assert.Contains("1 scanned file(s)", report);   // the oversized one is not in the total…
        Assert.Contains(OneOfTwoLeftOut, report);       // …and the report says one was left out
    }

    [Fact]
    public async Task AnAppliedRename_SaysWhenPartOfTheTreeWasNeverOpened()
    {
        // The severe half: the rename really is partial, and the model is told so instead of
        // reading "✅ Applied to 1 file(s)" and moving on.
        Write("Small.cs", "namespace App;\npublic class Small { public void Handle() { } }\n");
        WriteOversized("Big.cs", "public class Big { public void Use(Small s) { s.Handle(); } }");

        var report = await RunAsync("Handle", "Process", dryRun: false);

        Assert.Contains("Applied to 1 file(s)", report);
        Assert.Contains(OneOfTwoLeftOut, report);
        Assert.Contains("Process", Read("Small.cs"));
        Assert.Contains("Handle", Read("Big.cs"));      // untouched, as it always was
    }

    [Fact]
    public async Task AWholeTreeThatFits_CarriesNoWarningAtAll()
    {
        // Witness: the two rules above must read as "the warning appeared", not as "some sentence
        // about numbers appeared". A complete scan stays silent.
        Write("Small.cs", "namespace App;\npublic class Small { public void Handle() { } }\n");
        Write("Other.cs", "namespace App;\npublic class Other { }\n");

        var report = await RunAsync("Handle", "Process", dryRun: true);

        Assert.Contains("scanned 2", report);
        Assert.DoesNotContain(OneOfTwoLeftOut, report);
    }
}
