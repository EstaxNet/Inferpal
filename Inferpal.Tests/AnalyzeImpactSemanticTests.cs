using System.IO;
using System.Text.Json;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

// analyze_impact gained an exact-references section backed by the C# compiler (roadmap §14).
// Everything else in that report matches names; this section resolves them, and it must say so.
public class AnalyzeImpactSemanticTests : IDisposable
{
    private readonly string _root;

    public AnalyzeImpactSemanticTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ob-impact-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private string Write(string rel, string src)
    {
        var full = Path.Combine(_root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, src);
        return full;
    }

    private async Task<string> RunAsync(string path, string? symbol)
    {
        var tool = new AnalyzeImpactTool(() => _root);
        var json = symbol is null
            ? $$"""{"path": {{JsonSerializer.Serialize(path)}}}"""
            : $$"""{"path": {{JsonSerializer.Serialize(path)}}, "symbol": {{JsonSerializer.Serialize(symbol)}}}""";
        return await tool.ExecuteAsync(JsonDocument.Parse(json).RootElement, CancellationToken.None);
    }

    [Fact]
    public async Task NamedSymbol_GetsExactReferences_NotNameMatches()
    {
        var target = Write("Alpha.cs", """
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

        var report = await RunAsync(target, "Alpha");

        // The section must exist and must announce what it is, so the agent knows which lines
        // above are guesses.
        Assert.Contains("Exact references", report);
        Assert.Contains("resolved by the C# compiler", report);
    }

    [Fact]
    public async Task ASameNamedMemberElsewhere_DoesNotPolluteTheCount()
    {
        // This tool always knows which file it is analysing, so it disambiguates by construction:
        // the property named `Diag` in another file is out of scope, not an ambiguity to report.
        // (The ambiguity warning exists for callers that cannot name a file.)
        var target = Write("Zzz/Diag.cs", """
            namespace App;
            public static class Diag { public static void Swallow(string m) { } }
            """);
        Write("Aaa/Fake.cs", """
            namespace App.Tests;
            public class Fake { public string? Diag { get; set; } public void Use() { Diag = "x"; } }
            """);
        Write("Zzz/User.cs", """
            namespace App;
            public class User { public void A() => Diag.Swallow("a"); }
            """);

        var report = await RunAsync(target, "Diag");

        Assert.Contains("Exact references to `Diag`  (1)", report);   // the class use only
        Assert.Contains("Zzz/User.cs", report);
        Assert.Contains("declared at Zzz/Diag.cs", report);
        Assert.DoesNotContain("Aaa/Fake.cs", report);                 // the property is not a use
    }

    [Fact]
    public async Task DependantsOutsideTheAnalysedFolder_AreFound()
    {
        // The scan used to start at the analysed file's own directory, so a blast-radius tool
        // could not see past the folder it was pointed at. On the real repository that turned six
        // dependants into "0 dependants — safe to refactor freely".
        var target = Write("Core/Api.cs", """
            namespace App;
            public class Api { public void Go() { } }
            """);
        Write("Host/Consumer.cs", """
            namespace App.Host;
            using App;
            public class Consumer { public void Run() { new Api().Go(); } }
            """);

        var report = await RunAsync(target, symbol: null);

        Assert.Contains("Consumer.cs", report);
    }

    [Fact]
    public async Task TheVerdict_NeverContradictsTheReferencesItPrints()
    {
        // "No dependants detected — safe to refactor freely" printed above real call sites is the
        // most harmful thing this tool could say, so the exact count feeds the risk calculation.
        var target = Write("Core/Api.cs", """
            namespace App;
            public class Api { public void Go() { } }
            """);
        Write("Host/Consumer.cs", """
            namespace App.Host;
            using App;
            public class Consumer { public void Run() { new Api().Go(); } }
            """);

        var report = await RunAsync(target, "Api");

        Assert.Contains("Exact references to `Api`", report);
        Assert.DoesNotContain("No dependants detected", report);
    }

    [Fact]
    public async Task NoSymbolAsked_LeavesTheReportUnchanged()
    {
        // The section is additive: a question without a symbol has no exact answer to give, and
        // the existing heuristic report must not grow a confusing empty section.
        var target = Write("Alpha.cs", """
            namespace App;
            public class Alpha { public void Handle() { } }
            """);

        var report = await RunAsync(target, symbol: null);

        Assert.DoesNotContain("Exact references", report);
    }

    [Fact]
    public async Task NonCSharpFile_FallsBackToTheHeuristicWithoutClaimingPrecision()
    {
        var target = Write("app.ts", """
            export class Widget { render() { } }
            """);

        var report = await RunAsync(target, "Widget");

        // TypeScript has no Roslyn compilation here; the report must simply not offer the section
        // rather than present name-matching as resolution.
        Assert.DoesNotContain("resolved by the C# compiler", report);
    }
}
