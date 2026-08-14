using System.IO;
using System.Text.Json;
using Inferpal.Services.Execution;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

// rename_symbol writes files, so its precision matters more than anywhere else: the syntactic path
// rewrote EVERY identifier spelled like the target, which on real code means renaming a dozen
// unrelated methods that happen to share a name (roadmap §14).
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

    private async Task<string> RunAsync(string oldName, string newName, bool dryRun)
    {
        var approval = new AlwaysApprove();
        var tool = new RenameSymbolTool(approval, new FileHistoryService(), () => _root);
        var json = $$"""
            {"old_name": {{JsonSerializer.Serialize(oldName)}},
             "new_name": {{JsonSerializer.Serialize(newName)}},
             "dry_run": {{(dryRun ? "true" : "false")}}}
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

        await RunAsync("Handle", "Process", dryRun: false);

        Assert.Contains("public void Process()", Read("Alpha.cs"));
        Assert.Contains("public void Handle()", Read("Beta.cs"));   // untouched
        Assert.Contains("new Alpha().Process()", Read("Caller.cs"));
        Assert.Contains("new Beta().Handle()", Read("Caller.cs"));  // untouched
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
}
