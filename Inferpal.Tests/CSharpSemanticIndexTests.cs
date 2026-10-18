using System.IO;
using Inferpal.Services.Lsp;
using Xunit;

namespace Inferpal.Tests;

// Semantic resolution for C# (CSharpSemanticIndex). The point of every test here is the same: tell apart
// what a name REFERS TO from what merely spells the same. A regex cannot, which is why
// analyze_impact answers with ~5 % precision today and why the Next Edit prototype died at 61 %.
public class CSharpSemanticIndexTests : IDisposable
{
    private readonly string _root;

    public CSharpSemanticIndexTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ob-semantic-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private void Write(string relPath, string source)
    {
        var full = Path.Combine(_root, relPath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, source);
    }

    private CSharpSemanticIndex Built()
    {
        var idx = new CSharpSemanticIndex(_root);
        idx.Build();
        return idx;
    }

    // ── The case that justifies the whole fiche ────────────────────────────────

    [Fact]
    public void Homonyms_InDifferentTypes_AreNotConfused()
    {
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
                public void Run()
                {
                    new Alpha().Handle();
                    new Beta().Handle();
                    new Beta().Handle();
                }
            }
            """);

        var result = Built().FindReferences("Handle", declaringFile: "Alpha.cs");

        // A textual search for "Handle" would return 5 hits across the three files.
        Assert.Single(result.References);
        Assert.Equal("Caller.cs", result.References[0].RelPath);
        Assert.Contains("new Alpha().Handle()", result.References[0].Snippet);
        Assert.False(result.IsAmbiguous);              // the declaring file disambiguated it
        Assert.Equal("Alpha.cs", result.Declaration!.RelPath);
    }

    [Fact]
    public void LocalVariable_DoesNotMatchAFieldOfTheSameName()
    {
        Write("Shadow.cs", """
            namespace App;
            public class Shadow
            {
                private int counter;
                public void Bump()
                {
                    int counter = 0;   // shadows the field — a different symbol entirely
                    counter++;
                }
                public void UseField() { counter++; }
            }
            """);

        var result = Built().FindReferences("counter");

        // Two declarations share the name, so the answer MUST say so rather than pick one in
        // silence — and it must never merge them: the file spells "counter" five times.
        Assert.True(result.IsAmbiguous, "the field and the local must be reported as ambiguous");
        Assert.True(result.References.Count is 1 or 2,
            $"expected the field XOR the local, got {result.References.Count}");
    }

    // ── Regressions: both were live on real code while every test above was green ──
    // The synthetic sources here are three lines and resolve perfectly. Real code does not, and
    // that difference is exactly what produced two silent wrong answers on this very repository.

    [Fact]
    public void References_AreFound_WhenOverloadResolutionFails()
    {
        // Real code does not always type-check inside our reference-less compilation. When it does
        // not, SymbolInfo.Symbol is null and the right method sits in CandidateSymbols — ignoring
        // candidates made ExecuteToolSafeAsync report ZERO references when it has seven.
        // Reproduced deterministically here with an argument-count mismatch.
        Write("Api.cs", """
            namespace App;
            public static class Api { public static void Send(int a) { } }
            """);
        Write("Caller.cs", """
            namespace App;
            public class Caller { public void Run() { Api.Send(1, 2); } }
            """);

        var refs = Built().FindReferences("Send").References;

        Assert.Single(refs);
        Assert.Equal("Caller.cs", refs[0].RelPath);
    }

    [Fact]
    public void AClassAndAMemberSharingAName_ResolveToTheClass_AndReportTheRest()
    {
        // The `Diagnostics` case: a Core class plus test members of the same name. Answering about
        // whichever declaration came first gave 4 references instead of 173 — silently.
        // ⚠ The member's file sorts BEFORE the class's on purpose: with a plain path ordering the
        // property wins and this test goes red, which is the whole point of it.
        Write("Aaa/Fake.cs", """
            namespace App.Tests;
            public class Fake { public string? Diag { get; set; } }
            """);
        Write("Zzz/Diag.cs", """
            namespace App;
            public static class Diag { public static void Swallow(string m) { } }
            """);
        Write("Zzz/User.cs", """
            namespace App;
            public class User
            {
                public void A() => Diag.Swallow("a");
                public void B() => Diag.Swallow("b");
            }
            """);

        var result = Built().FindReferences("Diag");

        Assert.True(result.IsAmbiguous, "the property sharing the name must be reported");
        Assert.Equal("Zzz/Diag.cs", result.Declaration!.RelPath);   // the type wins over the member
        Assert.Equal(2, result.References.Count);
    }

    [Fact]
    public void Resolution_IsDeterministic_AcrossQueries()
    {
        // The first version picked the first declaration a Dictionary happened to yield. Two runs
        // of the same query must name the same declaration, or no result can be trusted.
        Write("Zeta.cs", "namespace App; public class Shared { }");
        Write("Alpha.cs", "namespace App; public class Holder { public string? Shared; }");

        var idx = Built();

        Assert.Equal(idx.FindReferences("Shared").Declaration,
                     idx.FindReferences("Shared").Declaration);
    }

    // ── Incremental refresh (only the touched file is re-parsed) ───────────────

    [Fact]
    public void Update_SeesANewReference_WithoutRebuilding()
    {
        Write("Api.cs", "namespace App; public class Api { public void Go() { } }");
        Write("Caller.cs", "namespace App; public class Caller { public void Run() { } }");
        var idx = Built();
        Assert.Empty(idx.FindReferences("Go").References);

        Write("Caller.cs", """
            namespace App;
            public class Caller { public void Run() { new Api().Go(); } }
            """);
        Assert.True(idx.Update(Path.Combine(_root, "Caller.cs")));

        Assert.Single(idx.FindReferences("Go").References);
    }

    [Fact]
    public void Update_AddsAFileThatDidNotExistAtBuildTime()
    {
        Write("Api.cs", "namespace App; public class Api { public void Go() { } }");
        var idx = Built();
        Assert.Equal(1, idx.FileCount);

        Write("Late.cs", """
            namespace App;
            public class Late { public void Run() { new Api().Go(); } }
            """);
        Assert.True(idx.Update(Path.Combine(_root, "Late.cs")));

        Assert.Equal(2, idx.FileCount);
        Assert.Single(idx.FindReferences("Go").References);
    }

    [Fact]
    public void Update_DropsADeletedFile_AndItsReferencesWithIt()
    {
        Write("Api.cs", "namespace App; public class Api { public void Go() { } }");
        Write("Caller.cs", """
            namespace App;
            public class Caller { public void Run() { new Api().Go(); } }
            """);
        var idx = Built();
        Assert.Single(idx.FindReferences("Go").References);

        File.Delete(Path.Combine(_root, "Caller.cs"));
        Assert.True(idx.Update(Path.Combine(_root, "Caller.cs")));

        Assert.Equal(1, idx.FileCount);
        Assert.Empty(idx.FindReferences("Go").References);
    }

    [Fact]
    public void Update_OfAnUnknownAbsentFile_ChangesNothing()
    {
        Write("Api.cs", "namespace App; public class Api { public void Go() { } }");
        var idx = Built();

        Assert.False(idx.Update(Path.Combine(_root, "NeverExisted.cs")));
        Assert.Equal(1, idx.FileCount);
    }

    [Fact]
    public void Update_BeforeAnyBuild_BuildsInstead()
    {
        Write("Api.cs", "namespace App; public class Api { public void Go() { } }");
        var idx = new CSharpSemanticIndex(_root);   // never built

        Assert.True(idx.Update(Path.Combine(_root, "Api.cs")));

        Assert.Equal(1, idx.FileCount);
    }

    [Fact]
    public async Task QueriesAndUpdates_CanRunConcurrently()
    {
        // Once the file watcher drives Update(), it fires on a thread-pool thread while a tool call
        // is querying. A dictionary read during a write throws or lies, so queries work on an
        // immutable snapshot taken under the lock.
        for (var i = 0; i < 12; i++)
            Write($"File{i:D2}.cs", $"namespace App; public class Type{i:D2} {{ public void Go() {{ }} }}");
        Write("Caller.cs", """
            namespace App;
            public class Caller { public void Run() { new Type00().Go(); } }
            """);
        var idx = Built();

        var stop = false;
        var writer = Task.Run(() =>
        {
            var n = 0;
            while (!Volatile.Read(ref stop))
            {
                var f = $"File{n++ % 12:D2}.cs";
                Write(f, $"namespace App; public class Type{(n - 1) % 12:D2} {{ public void Go() {{ }} }}");
                idx.Update(Path.Combine(_root, f));
            }
        });

        try
        {
            for (var i = 0; i < 30; i++)
                Assert.NotNull(idx.FindReferences("Go").References);   // must never throw
        }
        finally
        {
            Volatile.Write(ref stop, true);
            await writer;
        }
    }

    // ── Declarations ───────────────────────────────────────────────────────────

    [Fact]
    public void Declarations_AreFoundAcrossFiles()
    {
        Write("A.cs", "namespace App; public class Widget { }");
        Write("B.cs", "namespace App; public class Gadget { }");

        var decls = Built().FindDeclarations("Widget");

        Assert.Single(decls);
        Assert.Equal("A.cs", decls[0].RelPath);
    }

    [Fact]
    public void DeclarationItself_IsNotCountedAsAReference()
    {
        Write("Solo.cs", """
            namespace App;
            public class Solo { public void Only() { } }
            """);

        Assert.Empty(Built().FindReferences("Only").References);
    }

    // ── Locations are usable, not just true ────────────────────────────────────

    [Fact]
    public void Hits_CarryPathLineAndSourceLine()
    {
        Write("Sub/Dir/Target.cs", "namespace App; public class Target { public void Go() { } }");
        Write("Sub/Dir/User.cs", """
            namespace App;
            public class User { public void Run() { new Target().Go(); } }
            """);

        var refs = Built().FindReferences("Go").References;

        Assert.Single(refs);
        Assert.Equal("Sub/Dir/User.cs", refs[0].RelPath);   // forward slashes, repo-relative
        Assert.Equal(2, refs[0].Line);                      // 1-based
        Assert.Contains("new Target().Go()", refs[0].Snippet);
    }

    // ── Honest failure ─────────────────────────────────────────────────────────

    [Fact]
    public void UnknownSymbol_ReturnsNothing_RatherThanGuessing()
    {
        Write("A.cs", "namespace App; public class Widget { }");

        Assert.Empty(Built().FindReferences("NoSuchSymbol").References);
        Assert.Empty(Built().FindDeclarations("NoSuchSymbol"));
    }

    [Fact]
    public void UnparseableFile_DoesNotSinkTheIndex()
    {
        Write("Broken.cs", "namespace App; public class {{{{ ");
        Write("Good.cs", """
            namespace App;
            public class Good { public void Ping() { } }
            """);
        Write("Caller.cs", """
            namespace App;
            public class C { public void R() { new Good().Ping(); } }
            """);

        // Roslyn parses broken code into a tree with errors rather than throwing; the rest of the
        // repository must still resolve.
        var refs = Built().FindReferences("Ping").References;

        Assert.Single(refs);
    }

    /// <summary>
    /// ⚠ An extension method called as an instance method binds to its REDUCED form, a symbol of its own: compared
    /// as is, no <c>x.Twice()</c> is a reference — the references say nobody calls it, and a rename leaves the calls on
    /// the old name under "applied".
    /// </summary>
    [Fact]
    public void AnExtensionMethodCalledOnItsReceiver_IsAReference()
    {
        Write("Ext.cs", """
            namespace App;
            public static class Ext { public static int Twice(this int x) => x * 2; }
            """);
        Write("Caller.cs", """
            namespace App;
            public class C
            {
                public int R() => 5.Twice();
                public int S() => Ext.Twice(3);
            }
            """);

        var refs = Built().FindReferences("Twice").References;

        Assert.Equal(2, refs.Count);
        Assert.All(refs, r => Assert.Equal("Caller.cs", r.RelPath));
    }

    /// <summary>Overloads in one file: the declaring line picks the one to rename, never the first one scanned.</summary>
    [Fact]
    public void TheDeclaringLine_PicksTheOverload()
    {
        Write("Job.cs", """
            namespace App;
            public class Job
            {
                public void Run() { }
                public void Run(int times) { }
            }
            """);
        Write("Caller.cs", """
            namespace App;
            public class C { public void R(Job j) { j.Run(); j.Run(2); j.Run(3); } }
            """);
        var idx = Built();

        var second = idx.PlanRename("Run", "Job.cs", 5);
        var first = idx.PlanRename("Run", "Job.cs", 4);

        // The declaration and its calls: Run(int) has two, Run() one.
        Assert.Equal(3, second.Spans.Values.Sum(v => v.Count));
        Assert.Equal(2, first.Spans.Values.Sum(v => v.Count));
        // Designated exactly: the other overload is no ambiguity.
        Assert.Empty(second.Candidates);
        Assert.Empty(first.Candidates);
    }

    /// <summary>
    /// A declaring line designates the declaration that spans it — an attribute above it, a line of its body; a line in
    /// no declaration designates nothing, never another one.
    /// </summary>
    [Fact]
    public void TheDeclaringLine_DesignatesTheSpanningDeclaration()
    {
        Write("Job.cs", """
            namespace App;
            public class Job
            {
                [System.Obsolete]
                public void Run() { }

                public void Run(int times)
                {
                    System.Console.WriteLine(times);
                }
            }
            """);
        Write("Caller.cs", """
            namespace App;
            public class C { public void R(Job j) { j.Run(2); } }
            """);
        var idx = Built();

        // Run(): its declaration alone; Run(int): its declaration and its one call.
        Assert.Equal(1, idx.PlanRename("Run", "Job.cs", 4).Spans.Values.Sum(s => s.Count));
        Assert.Equal(2, idx.PlanRename("Run", "Job.cs", 9).Spans.Values.Sum(s => s.Count));
        Assert.Empty(idx.PlanRename("Run", "Job.cs", 6).Spans);
    }

    /// <summary>A method that calls itself is one of its callers; a type named inside its own declaration is no dependent.</summary>
    [Fact]
    public void ARecursiveCall_IsAReference_ASelfUseOfATypeIsNot()
    {
        Write("Math.cs", """
            namespace App;
            public class Calc
            {
                public static Calc Make() => new Calc();
                public static int Fact(int n) => n <= 1 ? 1 : n * Fact(n - 1);
            }
            """);
        Write("Caller.cs", """
            namespace App;
            public class C { public int R() => Calc.Fact(3); }
            """);
        var idx = Built();

        var fact = idx.FindReferences("Fact").References;
        var calc = idx.FindReferences("Calc").References;

        Assert.Equal(new[] { "Caller.cs", "Math.cs" }, fact.Select(r => r.RelPath).Order());
        Assert.Equal("Caller.cs", Assert.Single(calc).RelPath);
    }

    /// <summary>
    /// The references of an override are its exact calls: a call through the base or the interface is the base's or the
    /// interface's, even when it can land here.
    /// </summary>
    [Fact]
    public void TheReferencesOfAnOverride_AreItsExactCalls_NotTheCallsThroughTheBase()
    {
        Write("Rule.cs", """
            namespace App;
            public interface IRule { bool Check(int v); }
            public abstract class RuleBase : IRule { public abstract bool Check(int v); }
            """);
        Write("Positive.cs", """
            namespace App;
            public class Positive : RuleBase { public override bool Check(int v) => v > 0; }
            """);
        Write("Caller.cs", """
            namespace App;
            public class C
            {
                public bool ByInterface(IRule r) => r.Check(1);
                public bool ByBase(RuleBase r) => r.Check(2);
                public bool Direct(Positive p) => p.Check(3);
            }
            """);

        var refs = Built().FindReferences("Check", "Positive.cs").References;

        Assert.Equal(6, Assert.Single(refs).Line);
    }

    /// <summary>A use inside an inactive <c>#if</c> branch is text to the compiler: no reference.</summary>
    [Fact]
    public void ANameInAnInactiveBranch_IsNoReference()
    {
        Write("Job.cs", """
            namespace App;
            public class Job { public void Run() { } }
            """);
        Write("Caller.cs", """
            namespace App;
            public class C
            {
                public void R(Job j)
                {
                    j.Run();
            #if NETFRAMEWORK_ONLY
                    j.Run();
            #endif
                }
            }
            """);

        Assert.Equal(6, Assert.Single(Built().FindReferences("Run").References).Line);
    }

    /// <summary>A partial type declared in two files is ONE symbol: no ambiguity, the other part is no other symbol.</summary>
    [Fact]
    public void APartialTypeInTwoFiles_IsNotAmbiguous()
    {
        Write("Invoice.cs", """
            namespace App;
            public partial class Invoice { public int Total() => 1; }
            """);
        Write("Invoice.Designer.cs", """
            namespace App;
            public partial class Invoice { private int _lines; }
            """);
        Write("Caller.cs", """
            namespace App;
            public class C { public int R() => new Invoice().Total(); }
            """);

        var result = Built().FindReferences("Invoice");

        Assert.False(result.IsAmbiguous);
        Assert.Contains(result.References, r => r.RelPath == "Caller.cs");
    }

    /// <summary>
    /// Two overloads written on ONE line are both designated by it: a rename is refused — never one of them picked as if
    /// it were the only one.
    /// </summary>
    [Fact]
    public void TwoOverloadsOnOneLine_AreBothDesignated()
    {
        Write("Job.cs", """
            namespace App;
            public class Job
            {
                public void Run() { } public void Run(int t) { }
            }
            """);
        var idx = Built();

        var plan = idx.PlanRename("Run", "Job.cs", 4);
        Assert.Equal(2, plan.Candidates.Count);
        Assert.Empty(plan.Spans);
    }

    /// <summary>The attribute line above a field designates the field, as it does for any other member.</summary>
    [Fact]
    public void TheAttributeLineOfAField_DesignatesIt()
    {
        Write("Job.cs", """
            namespace App;
            public class Job
            {
                [System.Obsolete]
                public int Count;
                public void Bump() => Count++;
            }
            """);

        var plan = Built().PlanRename("Count", "Job.cs", 4);

        // The field and its one use.
        Assert.Empty(plan.Candidates);
        Assert.Equal(2, plan.Spans.Values.Sum(s => s.Count));
    }

    /// <summary>
    /// The two parts of a partial method are one member: designated by either file, its calls are its references, and
    /// no part is "another symbol".
    /// </summary>
    [Fact]
    public void APartialMethod_IsOneMember_WhicheverPartIsDesignated()
    {
        Write("Order.cs", """
            namespace App;
            public partial class Order { partial void OnSaved(); public void Save() => OnSaved(); }
            """);
        Write("Order.Hooks.cs", """
            namespace App;
            public partial class Order { partial void OnSaved() { } }
            """);
        var idx = Built();

        var byDefinition = idx.FindReferences("OnSaved", "Order.cs");
        var byImplementation = idx.FindReferences("OnSaved", "Order.Hooks.cs");

        Assert.Single(byDefinition.References);
        Assert.Single(byImplementation.References);
        Assert.False(byDefinition.IsAmbiguous);
    }

    [Fact]
    public void EmptyWorkspace_IsHarmless()
    {
        var idx = Built();

        Assert.Equal(0, idx.FileCount);
        Assert.Empty(idx.FindReferences("Anything").References);
    }
}
