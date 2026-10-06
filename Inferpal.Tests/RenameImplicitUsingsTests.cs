using System.IO;
using System.Text.Json;
using Inferpal.Services.CodeActions;
using Inferpal.Services.Execution;
using Inferpal.Services.Lsp;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

// ──────────────────────────────────────────────────────────────────────────────────────────────
//  A rename sees every call, in a project that relies on the SDK's implicit usings.
//
//  Found by the real-condition battery (an interface method renamed in a larger shop): rename_symbol
//  renamed the interface, its three implementations and five calls, and left `rule.Apply(amount)` in
//  RuleChain — `_rules = rules.ToList()` needs System.Linq, which every modern .NET project gets
//  from ImplicitUsings in a file the build generates under obj/, a folder the index never reads.
//  The call did not bind, so it was not a reference; the build broke under "applied".
// ──────────────────────────────────────────────────────────────────────────────────────────────
[Collection(CultureSerialCollection.Name)]
public sealed class RenameImplicitUsingsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rename-implicit-" + Guid.NewGuid().ToString("N"));

    public RenameImplicitUsingsTests()
    {
        Directory.CreateDirectory(_root);
        CSharpSemanticIndex.ResetCacheForTests();
    }

    public void Dispose()
    {
        CSharpSemanticIndex.ResetCacheForTests();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private void Write(string rel, string text)
    {
        var full = Path.Combine(_root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
    }

    private sealed class Approve : IApprovalService
    {
        public Task<bool> RequestApprovalAsync(string toolName, string details, CancellationToken ct,
            string? subject = null, DiffInfo? diff = null, bool forcePrompt = false) => Task.FromResult(true);
    }

    private void WriteRules(bool implicitUsings)
    {
        Write("src/Shop/Shop.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net8.0</TargetFramework>"
            + (implicitUsings ? "<ImplicitUsings>enable</ImplicitUsings>" : "")
            + "</PropertyGroup></Project>");
        Write("src/Shop/IPriceRule.cs", "namespace Shop;\npublic interface IPriceRule { decimal Apply(decimal amount); }\n");
        Write("src/Shop/Half.cs", "namespace Shop;\npublic sealed class Half : IPriceRule { public decimal Apply(decimal a) => a / 2; }\n");
        // No `using System.Linq;` in the file: the SDK provides it.
        Write("src/Shop/RuleChain.cs", """
            namespace Shop;
            public sealed class RuleChain(IEnumerable<IPriceRule> rules) : IPriceRule
            {
                private readonly List<IPriceRule> _rules = rules.ToList();
                public decimal Apply(decimal amount)
                {
                    foreach (var rule in _rules)
                        amount = rule.Apply(amount);
                    return amount;
                }
            }
            """);
    }

    private async Task RenameAsync() =>
        await new RenameSymbolTool(new Approve(), new FileHistoryService(), () => _root).ExecuteAsync(
            JsonSerializer.SerializeToElement(new
            {
                old_name = "Apply", new_name = "ApplyTo", declaring_file = "src/Shop/IPriceRule.cs",
            }), CancellationToken.None);

    [Fact]
    public async Task ACallThatBindsThroughAnImplicitUsing_IsRenamedToo()
    {
        WriteRules(implicitUsings: true);

        await RenameAsync();

        var chain = File.ReadAllText(Path.Combine(_root, "src/Shop/RuleChain.cs"));
        Assert.Contains("public decimal ApplyTo(decimal amount)", chain);   // WITNESS: the rename ran
        Assert.Contains("amount = rule.ApplyTo(amount);", chain);
        Assert.DoesNotContain(".Apply(", chain);
    }

    [Fact]
    public void TheImplicitUsings_AreTheSdksAndTheProjectsOwn_OnlyWhenAsked()
    {
        WriteRules(implicitUsings: true);
        Write("src/Other/Other.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><Using Include=\"Shop\" /><Using Include=\"Xunit\" /></ItemGroup></Project>");
        var source = CSharpSemanticIndex.ImplicitUsingsTree(_root)!.ToString();
        Assert.Contains("global using global::System.Linq;", source);
        Assert.Contains("global using global::Xunit;", source);

        // Reference arm: a project that does not opt in gets nothing it did not write.
        Directory.Delete(Path.Combine(_root, "src/Other"), recursive: true);
        WriteRules(implicitUsings: false);
        Assert.Null(CSharpSemanticIndex.ImplicitUsingsTree(_root));
    }
}
