using System.IO;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

/// <summary>An edit that ADDS a parse error is refused before it is written — never a semantic error, never an error the
/// file already had, and never twice: the same edit sent again is the model insisting, and is written.</summary>
// One collection: both classes reset the guard's refusals, which a run in parallel would clear between two calls.
[Collection("SyntaxGuard")]
public class SyntaxGuardTests
{
    private const string Ok = "class A\n{\n    void M() { }\n}\n";

    public SyntaxGuardTests()
    {
        SyntaxGuard.ResetForTests();
        SyntaxGuard.Deadline = TimeSpan.FromSeconds(30);   // a cold interpreter under the suite's load
    }

    private static Task<string?> Refusal(string path, string? before, string after, string? run = "run-a") =>
        SyntaxGuard.RefusalAsync(path, before, after, root: null, run, CancellationToken.None);

    [Fact]
    public async Task ACSharpEditThatBreaksTheSyntax_IsRefused_WithItsLine()
    {
        var refusal = await Refusal("A.cs", Ok, "class A\n{\n    void M() { \n}\n");
        Assert.NotNull(refusal);
        Assert.Contains("A.cs at line", refusal, StringComparison.Ordinal);
        Assert.Contains("CS1513", refusal, StringComparison.Ordinal);
    }

    /// <summary>The checker can be older than the project's language: refused once, the same edit is then written.</summary>
    [Fact]
    public async Task TheSameEditSentAgain_IsWritten_NeverALoop()
    {
        const string broken = "class Again\n{\n    void M() { \n}\n";
        Assert.NotNull(await Refusal("Again.cs", Ok, broken));
        Assert.Null(await Refusal("Again.cs", Ok, broken));
        Assert.NotNull(await Refusal("Again.cs", Ok, broken + "// another try\n"));   // a different edit is judged again
    }

    /// <summary>The pass belongs to the run that was refused: a Regenerate, another thread or a task is judged again.</summary>
    [Fact]
    public async Task TheSameEditInAnotherRun_IsRefusedAgain()
    {
        const string broken = "class Other { void M() { }";
        Assert.NotNull(await Refusal("Other.cs", Ok, broken, run: "run-a"));
        Assert.NotNull(await Refusal("Other.cs", Ok, broken, run: "run-b"));
        Assert.Null(await Refusal("Other.cs", Ok, broken, run: "run-a"));
    }

    /// <summary>Outside a run nothing is remembered, and the refusal does not promise a pass it will not give.</summary>
    [Fact]
    public async Task OutsideARun_TheSameEditIsJudgedEveryTime_AndNoPassIsPromised()
    {
        const string broken = "class Outside { void M() { }";
        var first = await Refusal("Outside.cs", Ok, broken, run: null);
        Assert.NotNull(first);
        Assert.DoesNotContain("send the same edit again", first, StringComparison.Ordinal);
        Assert.NotNull(await Refusal("Outside.cs", Ok, broken, run: null));
    }

    [Fact]
    public async Task TheRefusal_NamesTheFileByItsPath_InTheWorkspace()
    {
        var root = Path.Combine(Path.GetTempPath(), "ws");
        var refusal = await SyntaxGuard.RefusalAsync(Path.Combine(root, "src", "App", "Program.cs"), Ok,
                                                     "class A {", root, "run-a", CancellationToken.None);
        Assert.Contains("src/App/Program.cs at line", refusal, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("class A\n{\n    void M() { Missing.Call(); }\n}\n")]   // CS0103: semantic, a legitimate state
    [InlineData("class A\n{\n    void M() { }\n}\nclass A { }\n")]        // CS0101: a move between two edits
    public async Task ASemanticError_IsNeverRefused(string after) => Assert.Null(await Refusal("A.cs", Ok, after));

    [Fact]
    public async Task AFileThatDidNotParseBefore_IsLetThrough() =>
        Assert.Null(await Refusal("A.cs", "class A {", "class A { void M() {"));

    [Fact]
    public async Task ANewFile_IsJudgedAgainstNothing() =>
        Assert.NotNull(await Refusal("B.cs", null, "class B { \\ }"));

    [Fact]
    public async Task ARazorBraceLeftOpen_IsRefused_ButMarkupApostrophesAreNotStrings()
    {
        const string page = "@page \"/counter\"\n<p>Don't click</p>\n@code {\n    private int n = 0;\n}\n";
        Assert.Null(await Refusal("Counter.razor", "@code {\n}\n", page));
        Assert.NotNull(await Refusal("Counter.razor", page, page.Replace("}\n", "\n")));
    }

    [Fact]
    public async Task ARazorFileUnbalancedBefore_IsLetThrough() =>
        Assert.Null(await Refusal("Counter.razor", "@code {\n", "@code {\n {\n"));

    [Fact]
    public async Task AnUncheckedLanguage_IsLetThrough() =>
        Assert.Null(await Refusal("notes.txt", "a", "{{{"));

    [Theory]
    [InlineData("    private char c = '{';\n")]                         // a C# character literal
    [InlineData("    // opens { in a comment\n")]                       // a line comment
    [InlineData("    private string u = \"https://x/{id}\";\n")]
    [InlineData("    private string s = \"Use \\\"{\\\" to open\";\n")]  // escaped quotes around a brace
    [InlineData("    private string f = \"src/*.cs\";\n    // see src/*.cs\n")]
    public async Task ARazorBraceInALiteralOrAComment_DoesNotCount(string added)
    {
        const string page = "@code {\n    private int n = 0;\n}\n";
        Assert.Null(await Refusal("Counter.razor", page, page.Replace("@code {\n", "@code {\n" + added)));
    }

    [Theory]
    [InlineData("#if !NET8_0_OR_GREATER\n#error Requires .NET 8\n#endif\n")]   // the symbols are the project's, unknown here
    [InlineData("class B { string p = @\"C:\\Temp\\\"; }\n")]
    public async Task ACSharpFileTheCompilerAccepts_IsNotRefused(string added) =>
        Assert.Null(await Refusal("A.cs", Ok, added + Ok));

    /// <summary>.NET 10's file-based programs: <c>#:package</c> is read as the SDK reads it.</summary>
    [Fact]
    public async Task AFileBasedProgram_IsNotRefused() =>
        Assert.Null(await Refusal("app.cs", null, "#:package Humanizer@2.14.1\nConsole.WriteLine(1);\n"));

    [Theory]
    [InlineData("@if (show) {\n    <p>A 27\" monitor</p>\n}\n")]                       // a quote in the text
    [InlineData("<script>if (/[{]/.test(x)) { y(); }</script>\n")]                     // another language
    [InlineData("<style>.a { color: red; } .b {</style>\n")]
    [InlineData("@code {\n    private string p = @\"C:\\Temp\\\";\n}\n")]               // a verbatim string
    public async Task ARazorPageTheCompilerAccepts_IsNotRefused(string page) =>
        Assert.Null(await Refusal("Page.razor", "@code {\n}\n", page));

    /// <summary>A <c>/*</c> in the markup, never closed, is text: as a comment it hid every brace after it.</summary>
    [Fact]
    public async Task AnOpenBlockCommentInMarkup_IsText() =>
        Assert.Null(await Refusal("Files.razor", "@code {\n}\n",
                                  "@if (show) {\n    <p>Files: src/*.cs</p>\n}\n@code {\n}\n"));

    /// <summary>A .js file is a module, a script, JSX or Flow by its project, which a copy on its own does not carry.</summary>
    [Theory]
    [InlineData(null, "import React from 'react';\nexport default () => <div>{</div>;\n")]
    [InlineData("const x = 1;\n", "const x = 1;\nexport function f() {}\n")]
    public async Task AJsFile_IsNeverJudged(string? before, string after) =>
        Assert.Null(await Refusal("src/Button.js", before, after));

    /// <summary>The line is the checked file's, never the traceback's first frame (the checker's own script).</summary>
    [Fact]
    public async Task APythonError_IsSaidAtItsLine()
    {
        if (!await PythonForTests.IsInstalledAsync()) return;   // UNDECIDED without Python on the machine — never read as a pass

        var refusal = await Refusal("shop/pricing.py", "def f():\n    return 1\n", "def f():\n    return 1\n\ndef g(:\n    pass\n");

        Assert.NotNull(refusal);
        Assert.Contains("pricing.py at line 4", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnEsModule_IsJudgedByNode()
    {
        if (await SyntaxGuard.FirstErrorAsync("probe.cjs", "(", CancellationToken.None) is null) return;   // no node: UNDECIDED

        const string module = "import fs from 'fs';\nexport const a = 1;\n";
        Assert.Null(await Refusal("src/a.mjs", module, module + "export const b = 2;\n"));
        var refusal = await Refusal("src/a.mjs", module, module + "export const b = ;\n");
        Assert.NotNull(refusal);
        Assert.Contains("a.mjs at line 3", refusal, StringComparison.Ordinal);
    }
}

/// <summary>The guard where the model meets it: each writing tool refuses before the approval card, and writes nothing.</summary>
[Collection("SyntaxGuard")]
public sealed class SyntaxGuardToolsTests : IDisposable
{
    private readonly string _ws = Directory.CreateTempSubdirectory("inferpal-syntax-tools").FullName;
    private const string Clean = "class A\n{\n    void M() { }\n}\n";

    public SyntaxGuardToolsTests() => SyntaxGuard.ResetForTests();

    public void Dispose()
    {
        try { Directory.Delete(_ws, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private sealed class CountingApproval : Inferpal.Services.Execution.IApprovalService
    {
        public int Asked;
        public Task<bool> RequestApprovalAsync(string toolName, string details, CancellationToken ct, string? subject = null,
                                               Inferpal.Services.CodeActions.DiffInfo? diff = null, bool forcePrompt = false)
        {
            Asked++;
            return Task.FromResult(true);
        }
    }

    private static System.Text.Json.JsonElement Args(object o) =>
        System.Text.Json.JsonSerializer.SerializeToElement(o);

    private string FileWith(string content)
    {
        var path = Path.Combine(_ws, "A.cs");
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public async Task WriteFile_RefusesBeforeTheCard_AndWritesNothing()
    {
        var path = FileWith(Clean);
        var approval = new CountingApproval();
        var tool = new WriteFileTool(approval, new Inferpal.Services.Execution.FileHistoryService(), () => _ws);

        var answer = await tool.ExecuteAsync(Args(new { path, content = "class A {\n" }), CancellationToken.None);

        Assert.Contains("leaves a syntax error", answer, StringComparison.Ordinal);
        Assert.Equal(0, approval.Asked);
        Assert.Equal(Clean, File.ReadAllText(path));
    }

    [Fact]
    public async Task ApplyDiff_RefusesBeforeTheCard_AndWritesNothing()
    {
        var path = FileWith(Clean);
        var approval = new CountingApproval();
        var tool = new ApplyDiffTool(approval, new Inferpal.Services.Execution.FileHistoryService(), () => _ws);

        var answer = await tool.ExecuteAsync(Args(new { path, old_content = "void M() { }", new_content = "void M() {" }),
                                             CancellationToken.None);

        Assert.Contains("leaves a syntax error", answer, StringComparison.Ordinal);
        Assert.Equal(0, approval.Asked);
        Assert.Equal(Clean, File.ReadAllText(path));
    }

    [Fact]
    public async Task ApplyEdits_RefusesTheWholeBatch_AndWritesNothing()
    {
        var path = FileWith(Clean);
        var other = Path.Combine(_ws, "B.cs");
        File.WriteAllText(other, "class B { }\n");
        var approval = new CountingApproval();
        var tool = new ApplyEditsTool(approval, new Inferpal.Services.Execution.FileHistoryService(), () => _ws);

        var answer = await tool.ExecuteAsync(Args(new
        {
            edits = new object[]
            {
                new { path = other, old_content = "class B { }", new_content = "class B { int x; }" },
                new { path, old_content = "void M() { }", new_content = "void M() {" },
            },
        }), CancellationToken.None);

        Assert.Contains("leaves a syntax error", answer, StringComparison.Ordinal);
        Assert.Equal(0, approval.Asked);
        Assert.Equal(Clean, File.ReadAllText(path));
        Assert.Equal("class B { }\n", File.ReadAllText(other));   // all or nothing
    }

    /// <summary>Two files of one batch left unparsable: both refusals said at once, so the batch sent again is written
    /// whole — never refused file after file, a refusal already given coming back.</summary>
    [Fact]
    public async Task ApplyEdits_WithTwoBrokenFiles_PassesWholeOnceSentAgain()
    {
        var path = FileWith(Clean);
        var other = Path.Combine(_ws, "B.cs");
        File.WriteAllText(other, "class B { }\n");
        var approval = new CountingApproval();
        var history = new Inferpal.Services.Execution.FileHistoryService();
        history.BeginRun();   // the pass belongs to a run, as in every turn
        var tool = new ApplyEditsTool(approval, history, () => _ws);
        var batch = Args(new
        {
            edits = new object[]
            {
                new { path = other, old_content = "class B { }", new_content = "class B {" },
                new { path, old_content = "void M() { }", new_content = "void M() {" },
            },
        });

        var first = await tool.ExecuteAsync(batch, CancellationToken.None);
        Assert.Contains("B.cs at line", first, StringComparison.Ordinal);
        Assert.Contains("A.cs at line", first, StringComparison.Ordinal);
        Assert.Equal(0, approval.Asked);

        await tool.ExecuteAsync(batch, CancellationToken.None);
        Assert.Equal(1, approval.Asked);
        Assert.Equal("class B {\n", File.ReadAllText(other));
    }
}
