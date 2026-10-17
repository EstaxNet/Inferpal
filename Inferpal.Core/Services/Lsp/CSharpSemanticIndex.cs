using System.IO;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Inferpal.Services.Lsp;

/// <summary>Where a symbol is declared or used.</summary>
/// <param name="RelPath">Repository-relative path, forward slashes.</param>
/// <param name="Line">1-based line number.</param>
/// <param name="Snippet">The source line, trimmed — enough for the agent to judge without re-reading.</param>
internal sealed record SymbolLocation(string RelPath, int Line, string Snippet)
{
    public override string ToString() => $"{RelPath}:{Line}  {Snippet}";
}

/// <summary>A rename's rewrites, or — when the name designates several symbols — the candidates, and nothing to rewrite.</summary>
/// <param name="Spans">Tokens to rewrite per file; empty when ambiguous or unresolved.</param>
/// <param name="Candidates">Each symbol the name designates, when there is more than one.</param>
internal sealed record RenamePlan(
    IReadOnlyDictionary<string, IReadOnlyList<Microsoft.CodeAnalysis.Text.TextSpan>> Spans,
    IReadOnlyList<(string Symbol, SymbolLocation Location)> Candidates);

/// <summary>Answer to a reference query — including which declaration it is about.</summary>
/// <param name="Declaration">The declaration the references belong to; null when nothing resolved.</param>
/// <param name="References">Uses of that exact symbol.</param>
/// <param name="OtherDeclarations">
/// Other symbols sharing the same name. <b>Non-empty means the answer is about one of
/// several candidates</b> and the caller should disambiguate.
/// </param>
/// <param name="Through">Calls through a member the symbol implements or overrides — asked for only
/// (<c>viaImplemented</c>), and never in <paramref name="References"/>: they can run the symbol, but changing its
/// signature alone does not break them.</param>
/// <remarks>
/// A bare list would hide the one thing that matters: <c>Diagnostics</c> names a class in the Core
/// and two test members here, so "references to Diagnostics" is not a question with one answer.
/// Answering silently about whichever declaration was scanned first is plausible and wrong, which
/// is the worst combination.
/// </remarks>
internal sealed record ReferenceResult(
    SymbolLocation? Declaration,
    IReadOnlyList<SymbolLocation> References,
    IReadOnlyList<SymbolLocation> OtherDeclarations,
    IReadOnlyList<SymbolLocation>? Through = null)
{
    /// <summary>True when the name matches several declarations, so the answer is partial.</summary>
    public bool IsAmbiguous => OtherDeclarations.Count > 0;

    /// <summary>The calls through an implemented or overridden member; empty unless asked for.</summary>
    public IReadOnlyList<SymbolLocation> ThroughCalls => Through ?? [];

    public static readonly ReferenceResult None = new(null, [], []);
}

/// <summary>
/// Cross-file <b>semantic</b> resolution for C#: what a name actually refers to, as opposed to
/// what merely spells the same.
/// </summary>
/// <remarks>
/// <para>
/// The gap it closes is wide: searching this repository for <c>Handle(</c> returns 61 occurrences,
/// three of which refer to <c>TaskCommandHandler.Handle</c>. The text-based tools
/// (<c>analyze_impact</c>, <c>rename_symbol</c>, <c>trace_dependency</c>) work on the first number.
/// </para>
/// <para>
/// <b>No MSBuild.</b> A <see cref="CSharpCompilation"/> is built directly from the parsed files
/// plus the runtime's reference assemblies — no <c>.csproj</c> parsing, no restore, no
/// <c>MSBuildLocator</c>, and no extra package (<c>Microsoft.CodeAnalysis.CSharp</c> is already
/// referenced for RAG chunking). On this repository: ~620 ms to parse 408 files, ~430 ms to build
/// the compilation, ~75 MB.
/// </para>
/// <para>
/// <b>Grep filters, semantics arbitrates.</b> Building a <see cref="SemanticModel"/> for all 408
/// files costs ~10 s, so a query first discards every file whose text does not contain the symbol
/// name — a cheap, exact pre-filter, since a reference cannot exist without the name appearing —
/// and only then resolves the survivors.
/// </para>
/// <para>
/// <b>Limits, deliberately.</b> Without <c>.csproj</c> there are no conditional-compilation
/// symbols and no NuGet references beyond the runtime's, so symbols coming from packages may not
/// resolve — harmless for impact analysis, which is about the project's own symbols. C# only;
/// other languages stay with <see cref="LspSemanticProvider"/>.
/// </para>
/// </remarks>
internal sealed class CSharpSemanticIndex
{
    private readonly string _root;
    /// <summary>
    /// Immutable view a query works on. Taken under <see cref="_gate"/>, then used without it:
    /// <see cref="CSharpCompilation"/> is immutable and the array is a copy, so a file saved
    /// mid-query cannot make the answer inconsistent — it simply lands in the next query.
    /// </summary>
    private sealed record Snapshot(CSharpCompilation Compilation, KeyValuePair<string, SyntaxTree>[] Trees);

    private readonly object _gate = new();
    private readonly Dictionary<string, SyntaxTree> _treesByPath = new(Services.PathComparer.Default);
    private CSharpCompilation? _compilation;

    /// <summary>
    /// The state a query reads, built if needed. Queries must never touch the mutable fields
    /// directly: the file watcher calls <see cref="Update"/> from a thread-pool thread while a
    /// tool call is querying, and a dictionary read during a write throws or lies.
    /// </summary>
    private Snapshot Current()
    {
        lock (_gate)
        {
            if (_compilation is null) BuildLocked(null);
            return new Snapshot(_compilation!, _treesByPath.ToArray());
        }
    }

    public CSharpSemanticIndex(string root) => _root = root;

    // ── Per-workspace instance ────────────────────────────────────────────────

    private static readonly Dictionary<string, CSharpSemanticIndex> _byRoot =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The index for <paramref name="root"/>, built once and kept fresh by <see cref="Update"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Rebuilding per call costs ~750 ms; keeping one instance costs ~4 ms per saved file. The cache
    /// is only safe <b>because</b> something invalidates it — <c>ProjectIndexService</c>'s file
    /// watcher calls <see cref="Update"/> — so the two belong together. A cache without invalidation
    /// answers about code that no longer exists, which is the silent wrongness this class exists to
    /// remove.
    /// </para>
    /// <para>
    /// ⚠ The watcher is armed by <c>ProjectIndexService.SetRoot</c>, which both front-ends call with RAG on
    /// <b>or</b> off — armed by the indexing pass alone, it would not exist with RAG off and this cache would
    /// answer from a compilation frozen at its first use.
    /// </para>
    /// </remarks>
    public static CSharpSemanticIndex ForWorkspace(string root)
    {
        lock (_byRoot)
        {
            if (!_byRoot.TryGetValue(root, out var index))
                _byRoot[root] = index = new CSharpSemanticIndex(root);
            return index;
        }
    }

    /// <summary>Routes a changed file to the workspace index holding it, if any. Never throws.</summary>
    /// <remarks>Called from the file watcher, so a failure here must not disturb re-indexing.</remarks>
    public static void NotifyFileChanged(string path)
    {
        if (!path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) return;

        CSharpSemanticIndex[] indexes;
        lock (_byRoot) indexes = _byRoot.Values.ToArray();

        foreach (var index in indexes)
        {
            // BELOW the root, not merely spelled like it: "C:\dev\App2\x.cs" starts with "C:\dev\App".
            var root = index._root.TrimEnd('\\', '/');
            if (path.Length <= root.Length
                // Membership of a compilation: the same rule as every other path question.
                || !path.StartsWith(root, Services.PathComparer.Comparison)
                || path[root.Length] is not ('\\' or '/')) continue;
            // The full build's exclusions: a history snapshot (.inferpal/history/…_Foo.cs) or an obj/ file
            // added here became a second `class Foo`, and queries answered on the stale copy.
            if (WorkspaceScan.IsExcludedPath(path, root)) continue;
            try { index.Update(path); }
            catch (Exception ex) { Diagnostics.Swallow("CSharpSemanticIndex.NotifyFileChanged", ex); }
        }
    }

    /// <summary>
    /// A folder under a workspace was renamed, moved or removed: the trees under its path are dropped when their file is
    /// gone, and the C# files under it now are read.
    /// </summary>
    /// <remarks>
    /// ⚠ A folder event names no file: without this, the index kept the moved files under their old paths and never
    /// learned the new ones — and rename_symbol, whose spans come from here, renamed nothing inside the moved folder
    /// while it reported the occurrences it found elsewhere.
    /// </remarks>
    public static void NotifyDirectoryChanged(string dirPath)
    {
        CSharpSemanticIndex[] indexes;
        lock (_byRoot) indexes = _byRoot.Values.ToArray();

        foreach (var index in indexes)
        {
            var root = index._root.TrimEnd('\\', '/');
            if (dirPath.Length <= root.Length
                || !dirPath.StartsWith(root, Services.PathComparer.Comparison)
                || dirPath[root.Length] is not ('\\' or '/')) continue;
            if (WorkspaceScan.IsExcludedPath(dirPath, root)) continue;
            try { index.UpdateDirectory(dirPath); }
            catch (Exception ex) { Diagnostics.Swallow("CSharpSemanticIndex.NotifyDirectoryChanged", ex); }
        }
    }

    private void UpdateDirectory(string dirPath)
    {
        lock (_gate)
        {
            // Not built yet: the first query reads the disk as it is.
            if (_compilation is null) return;

            var prefix = dirPath.TrimEnd('\\', '/');
            foreach (var known in _treesByPath.Keys
                         .Where(p => p.Length > prefix.Length && p.StartsWith(prefix, Services.PathComparer.Comparison)
                                     && p[prefix.Length] is '\\' or '/')
                         .ToList())
                UpdateLocked(known);   // gone from disk → removed

            if (!Directory.Exists(dirPath)) return;
            foreach (var file in EnumerateCSharpFiles(dirPath))
                if (!WorkspaceScan.IsExcludedPath(file, _root)) UpdateLocked(file);
        }
    }

    /// <summary>Test seam: forget every cached workspace index.</summary>
    internal static void ResetCacheForTests()
    {
        lock (_byRoot) _byRoot.Clear();
    }

    /// <summary>Files parsed into the compilation.</summary>
    public int FileCount { get { lock (_gate) return _treesByPath.Count; } }

    /// <summary>Builds the compilation over every C# file under the root. Idempotent-ish: a second
    /// call rebuilds from disk, which is how a stale index is refreshed.</summary>
    public void Build(IEnumerable<string>? files = null)
    {
        lock (_gate) BuildLocked(files);
    }

    private void BuildLocked(IEnumerable<string>? files)
    {
        _treesByPath.Clear();

        foreach (var path in files ?? EnumerateCSharpFiles(_root))
        {
            try
            {
                // ⚠ Through the shared reader: rename_symbol applies these spans to the text TextFileEncoding
                // decodes, and a legacy-encoded file read as UTF-8 does not keep one character per byte (é + NBSP +
                // » is valid UTF-8, one character for three) — every later offset was off, and the rename refused.
                var text = Tools.TextFileEncoding.ReadText(path);
                _treesByPath[path] = CSharpSyntaxTree.ParseText(text, path: path);
            }
            catch (Exception ex) { Diagnostics.Swallow($"CSharpSemanticIndex.Parse({Path.GetFileName(path)})", ex); }
        }

        // ⚠ The SDK's implicit global usings (ImplicitUsings, on by default since .NET 6) live in a file the build
        // GENERATES under obj/, which the walk skips. Without them `System.Linq` is missing, `rules.ToList()` does not
        // bind, and a call reached through it — `foreach (var rule in _rules) rule.Apply(x)` — is not a reference:
        // rename_symbol renamed the declarations and left that call, and the build broke under "applied". The tree
        // joins the compilation only, never _treesByPath: nothing searches or rewrites it.
        var trees = _treesByPath.Values.ToList();
        if (ImplicitUsingsTree(_root) is { } implicitUsings) trees.Add(implicitUsings);

        _compilation = CSharpCompilation.Create(
            "inferpal.semantic",
            trees,
            _runtimeReferences.Value,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }

    /// <summary>The namespaces every C# file of an SDK project sees without writing them (Microsoft.NET.Sdk).</summary>
    internal static readonly string[] SdkImplicitUsings =
    [
        "System", "System.Collections.Generic", "System.IO", "System.Linq", "System.Net.Http", "System.Threading",
        "System.Threading.Tasks",
    ];

    private static readonly System.Text.RegularExpressions.Regex ImplicitUsingsOn = new(
        @"<ImplicitUsings>\s*(?:enable|true)\s*</ImplicitUsings>",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase, RegexBudget.Default);

    // <Using Include="Ns" /> — the project's own global usings; a Static or Alias one is not a namespace import.
    private static readonly System.Text.RegularExpressions.Regex UsingInclude = new(
        @"<Using\s+Include\s*=\s*""([A-Za-z_][\w.]*)""\s*/>",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase, RegexBudget.Default);

    /// <summary>
    /// The global usings the build would generate for the projects under <paramref name="root"/>, as a syntax tree;
    /// null when no project asks for any.
    /// </summary>
    internal static SyntaxTree? ImplicitUsingsTree(string root)
    {
        var namespaces = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var file in WorkspaceScan.EnumerateFiles(root, "*.csproj").Concat(WorkspaceScan.EnumerateFiles(root, "Directory.Build.props")))
        {
            string text;
            try { text = Tools.TextFileEncoding.ReadText(file); }
            catch (Exception ex) { Diagnostics.Swallow($"CSharpSemanticIndex.ImplicitUsings({Path.GetFileName(file)})", ex); continue; }
            try
            {
                if (ImplicitUsingsOn.IsMatch(text)) namespaces.UnionWith(SdkImplicitUsings);
                foreach (System.Text.RegularExpressions.Match m in UsingInclude.Matches(text))
                    namespaces.Add(m.Groups[1].Value);
            }
            catch (System.Text.RegularExpressions.RegexMatchTimeoutException) { }
        }
        return namespaces.Count == 0
            ? null
            : CSharpSyntaxTree.ParseText(string.Concat(namespaces.Select(ns => $"global using global::{ns};\n")),
                                         path: "<implicit global usings>");
    }

    /// <summary>
    /// Re-parses a single file and swaps it into the compilation, without touching the others.
    /// Adds the file when it is new, removes it when it no longer exists on disk.
    /// </summary>
    /// <remarks>
    /// A full <see cref="Build"/> costs ~750 ms on this repository. Paying that on every save — the
    /// editor's file watcher fires constantly — would make the index the most expensive thing in the
    /// process for one changed file. <see cref="CSharpCompilation"/> is immutable and gives
    /// <c>ReplaceSyntaxTree</c> exactly for this: the new compilation shares everything with the old
    /// but one tree.
    /// </remarks>
    /// <returns><c>true</c> when the index changed.</returns>
    public bool Update(string path)
    {
        lock (_gate) return UpdateLocked(path);
    }

    private bool UpdateLocked(string path)
    {
        if (_compilation is null) { BuildLocked(null); return true; }

        var existed = _treesByPath.TryGetValue(path, out var oldTree);

        if (!File.Exists(path))
        {
            if (!existed) return false;
            _treesByPath.Remove(path);
            _compilation = _compilation.RemoveSyntaxTrees(oldTree!);
            return true;
        }

        SyntaxTree newTree;
        try { newTree = CSharpSyntaxTree.ParseText(Tools.TextFileEncoding.ReadText(path), path: path); }
        catch (Exception ex)
        {
            // A file being written to is transiently unreadable; keeping the previous tree is a
            // better answer than dropping the file out of the index.
            Diagnostics.Swallow($"CSharpSemanticIndex.Update({Path.GetFileName(path)})", ex);
            return false;
        }

        _treesByPath[path] = newTree;
        _compilation = existed
            ? _compilation.ReplaceSyntaxTree(oldTree!, newTree)
            : _compilation.AddSyntaxTrees(newTree);
        return true;
    }

    /// <summary>
    /// Every reference to the symbol named <paramref name="symbolName"/> declared in
    /// <paramref name="declaringFile"/> (null = the first declaration found under that name).
    /// Returns an empty list when the symbol cannot be resolved — never a guess.
    /// </summary>
    public ReferenceResult FindReferences(
        string symbolName, string? declaringFile = null, CancellationToken ct = default) =>
        FindReferences(symbolName, declaringFile, declaringLine: null, viaImplemented: false, ct);

    /// <summary>
    /// <see cref="FindReferences(string, string?, CancellationToken)"/>, the declaration chosen by its line when several
    /// share the name in the file (overloads), and — <paramref name="viaImplemented"/> — the calls through the members
    /// the symbol implements or overrides, in <see cref="ReferenceResult.Through"/>: a call through the interface is a
    /// call that can land here. One pass for both lists.
    /// </summary>
    /// <param name="declaringLine">A line of the declaration — its attributes, signature or body (<see cref="AtLine"/>). A
    /// line that falls in none designates nothing: <see cref="ReferenceResult.None"/>, never another declaration.</param>
    public ReferenceResult FindReferences(
        string symbolName, string? declaringFile, int? declaringLine, bool viaImplemented, CancellationToken ct = default)
    {
        var snap = Current();
        var declarations = ResolveDeclarations(snap, symbolName, declaringFile, ct);
        if (declaringLine is { } line) declarations = AtLine(declarations, line);
        if (declarations.Count == 0) return ReferenceResult.None;

        var (target, targetLocation, _, _) = declarations[0];
        // ⚠ The other parts of a partial type or member are the SAME symbol: listed as others, one class would read as
        // ambiguous.
        var others = declarations.Skip(1).Where(d => !SymbolEqualityComparer.Default.Equals(PartialDefinition(d.Symbol), PartialDefinition(target)))
                                 .Select(d => d.Location).ToList();
        var through = viaImplemented ? CallableThrough(snap, target, ct) : [];

        var hits = new List<SymbolLocation>();
        var throughHits = new List<SymbolLocation>();
        // Pre-filter: a reference cannot exist in a file that never spells the name.
        foreach (var (_, tree) in snap.Trees)
        {
            ct.ThrowIfCancellationRequested();
            if (!tree.ToString().Contains(symbolName, StringComparison.Ordinal)) continue;

            var model = snap.Compilation.GetSemanticModel(tree);
            foreach (var node in tree.GetRoot(ct).DescendantNodes())
            {
                if (node is not (IdentifierNameSyntax or GenericNameSyntax)) continue;
                if (((SimpleNameSyntax)node).Identifier.Text != symbolName) continue;

                // One binding per name, compared to every member a call can go through. ⚠ Not through `base.`: a call
                // to the base's member from another derived class runs that member, never this one.
                var viaBase = node.Parent is MemberAccessExpressionSyntax { Expression: BaseExpressionSyntax } access
                              && access.Name == node;
                var bound = Bound(model.GetSymbolInfo(node, ct)).ToList();
                var exact = bound.Any(b => Same(b, target));
                if (!exact && (viaBase || !bound.Any(b => through.Any(t => Through(b, t))))) continue;
                // Inside a type's own declaration, its name is no dependent; inside a method's, it is a recursive call.
                if (target is INamedTypeSymbol && IsWithinDeclaration(node, target)) continue;

                (exact ? hits : throughHits).Add(Locate(tree, node.Span));
            }
        }

        return new ReferenceResult(targetLocation, hits, others, viaImplemented ? throughHits : null);
    }

    /// <summary>
    /// The members a call can go through and land in <paramref name="target"/>: the members it overrides, up the chain,
    /// and the interface members it implements — for its own type and for every source type deriving from it, since a
    /// derived class can declare an interface its base satisfies.
    /// </summary>
    /// <remarks>⚠ Members declared in the workspace's source only: through <c>object.ToString()</c> or
    /// <c>IDisposable.Dispose()</c>, every call of the framework member anywhere would be a caller.</remarks>
    private static List<ISymbol> CallableThrough(Snapshot snap, ISymbol target, CancellationToken ct)
    {
        // Only a method, property or event can be reached through another member — a static one only by implementing a
        // static abstract or virtual interface member (T.Make() through IMake<T>) — and the walk below rescans the workspace.
        if (target is not (IMethodSymbol or IPropertySymbol or IEventSymbol)) return [];
        if (target.IsStatic && target.ContainingType?.AllInterfaces.Any(i => i.GetMembers().Any(m => m.IsStatic && (m.IsAbstract || m.IsVirtual))) != true)
            return [];
        var chain = new List<ISymbol> { target };
        for (var over = Overridden(target); over is not null; over = Overridden(over)) chain.Add(over);
        var through = new List<ISymbol>(chain.Skip(1));
        if (target.ContainingType is { TypeKind: TypeKind.Class or TypeKind.Struct } own)
            foreach (var type in WithDerivedTypes(snap, own, ct))
                foreach (var iface in type.AllInterfaces)
                    // ⚠ Every member, never a lookup by the target's name: an explicit implementation is named "App.IRule.Check".
                    foreach (var member in iface.GetMembers())
                        if (type.FindImplementationForInterfaceMember(member) is { } impl
                            && chain.Any(c => SymbolEqualityComparer.Default.Equals(impl.OriginalDefinition, c.OriginalDefinition)))
                            through.Add(member);
        return [.. through.Where(m => m.Locations.Any(l => l.IsInSource))];
    }

    /// <summary><paramref name="type"/> and the source types deriving from it, at any depth.</summary>
    private static List<INamedTypeSymbol> WithDerivedTypes(Snapshot snap, INamedTypeSymbol type, CancellationToken ct)
    {
        var found = new List<INamedTypeSymbol> { type };
        if (type.IsSealed || type.TypeKind != TypeKind.Class) return found;

        // A type spells its base's name in its base list — or an alias of it, which this does not follow (a base named
        // through a `using` alias is missed): a file that spells none of the names found so far declares none of their
        // derived types. A new name found makes another pass — over the files not examined yet only: an
        // examined file had every type checked against the whole base chain, its grandchildren included.
        var origin = type.OriginalDefinition;
        var names = new HashSet<string>(StringComparer.Ordinal) { type.Name };
        var seen = new HashSet<ISymbol>(SymbolEqualityComparer.Default) { origin };
        var texts = new Dictionary<SyntaxTree, string>();
        var examined = new HashSet<SyntaxTree>();
        for (var grew = true; grew;)
        {
            grew = false;
            foreach (var (_, tree) in snap.Trees)
            {
                ct.ThrowIfCancellationRequested();
                if (examined.Contains(tree)) continue;
                if (!texts.TryGetValue(tree, out var text)) texts[tree] = text = tree.ToString();
                if (!names.Any(n => text.Contains(n, StringComparison.Ordinal))) continue;
                examined.Add(tree);

                var model = snap.Compilation.GetSemanticModel(tree);
                foreach (var declaration in tree.GetRoot(ct).DescendantNodes().OfType<TypeDeclarationSyntax>())
                    if (model.GetDeclaredSymbol(declaration, ct) is { } derived && DerivesFrom(derived, origin) && seen.Add(derived))
                    {
                        found.Add(derived);
                        grew |= names.Add(derived.Name);
                    }
            }
        }
        return found;

        static bool DerivesFrom(INamedTypeSymbol t, INamedTypeSymbol baseType)
        {
            for (var b = t.BaseType; b is not null; b = b.BaseType)
                if (SymbolEqualityComparer.Default.Equals(b.OriginalDefinition, baseType)) return true;
            return false;
        }
    }

    /// <summary>
    /// A call bound to <paramref name="bound"/> goes through <paramref name="member"/>: the same member, in a construction
    /// of its type that can be the one implemented.
    /// </summary>
    /// <remarks>⚠ Compared by definition alone, a call through <c>IRule&lt;string&gt;</c> would count for a type that
    /// implements <c>IRule&lt;int&gt;</c> only. A type argument still open (a type parameter) can be either.</remarks>
    private static bool Through(ISymbol bound, ISymbol member)
    {
        var b = (bound as IMethodSymbol)?.ConstructedFrom ?? bound;
        return SymbolEqualityComparer.Default.Equals(b.OriginalDefinition, member.OriginalDefinition)
            && Compatible(b.ContainingType, member.ContainingType);

        static bool Compatible(ITypeSymbol? x, ITypeSymbol? y)
        {
            if (x is null || y is null) return x is null && y is null;
            if (x is ITypeParameterSymbol || y is ITypeParameterSymbol) return true;
            if (SymbolEqualityComparer.Default.Equals(x, y)) return true;
            if (x is IArrayTypeSymbol xa && y is IArrayTypeSymbol ya)
                return xa.Rank == ya.Rank && Compatible(xa.ElementType, ya.ElementType);
            return x is INamedTypeSymbol xn && y is INamedTypeSymbol yn
                && SymbolEqualityComparer.Default.Equals(xn.OriginalDefinition, yn.OriginalDefinition)
                && xn.TypeArguments.Zip(yn.TypeArguments).All(p => Compatible(p.First, p.Second))
                && Compatible(xn.ContainingType, yn.ContainingType);
        }
    }

    /// <summary>
    /// Whether <paramref name="node"/> refers to <paramref name="target"/>.
    /// </summary>
    /// <remarks>
    /// <b>Candidates count too.</b> A compilation built without <c>.csproj</c> cannot resolve every
    /// parameter type — a NuGet type, a VS SDK type — and overload resolution then fails, leaving
    /// <see cref="SymbolInfo.Symbol"/> null while the right method sits in
    /// <see cref="SymbolInfo.CandidateSymbols"/>. Ignoring candidates made
    /// <c>ExecuteToolSafeAsync</c> report <b>zero</b> references when it has four: silent, total,
    /// and exactly the kind of wrong answer this class exists to prevent.
    /// </remarks>
    private static bool ResolvesTo(SemanticModel model, SyntaxNode node, ISymbol target, CancellationToken ct) =>
        Bound(model.GetSymbolInfo(node, ct)).Any(s => Same(s, target));

    /// <summary>What a name binds to: the symbol, and the candidates (<see cref="ResolvesTo"/>).</summary>
    private static IEnumerable<ISymbol> Bound(SymbolInfo info)
    {
        if (info.Symbol is { } s) yield return s;
        foreach (var candidate in info.CandidateSymbols) yield return candidate;
    }

    /// <summary>A bound symbol is <paramref name="target"/>, in any construction of its type.</summary>
    /// <remarks>⚠ An extension method called on its receiver (<c>x.Twice()</c>) binds to its REDUCED form, another symbol:
    /// it is compared by the method it was reduced from, or no such call is a reference.</remarks>
    private static bool Same(ISymbol bound, ISymbol target) =>
        SymbolEqualityComparer.Default.Equals(PartialDefinition(((bound as IMethodSymbol)?.ReducedFrom ?? bound).OriginalDefinition),
                                              PartialDefinition(target));

    /// <summary>A partial member's definition part — the one its calls bind to —, or the symbol itself.</summary>
    /// <remarks>⚠ The two parts of a partial method or property are two symbols to Roslyn and one member to C#: compared
    /// as they are, the implementation part had no reference, and a rename rewrote one part under "applied".</remarks>
    private static ISymbol PartialDefinition(ISymbol s) => s switch
    {
        IMethodSymbol { PartialDefinitionPart: { } definition }   => definition,
        IPropertySymbol { PartialDefinitionPart: { } definition } => definition,
        _                                                         => s,
    };

    /// <summary>
    /// Identifier tokens that must change when the symbol is renamed: every use <b>and</b> its
    /// declaration, grouped by file. Empty when the symbol does not resolve.
    /// </summary>
    /// <remarks>
    /// The difference with <see cref="FindReferences(string, string?, CancellationToken)"/> is the declaration, which is a reference to
    /// nothing but must obviously be rewritten. Returned as spans so the caller edits text and
    /// keeps the file byte-identical everywhere else.
    /// </remarks>
    public IReadOnlyDictionary<string, IReadOnlyList<TextSpan>> FindRenameSpans(
        string symbolName, string? declaringFile = null, CancellationToken ct = default) =>
        PlanRename(symbolName, declaringFile, null, ct).Spans;

    /// <summary>
    /// What renaming <paramref name="symbolName"/> would rewrite — or, when the name designates SEVERAL symbols, those
    /// candidates and nothing to rewrite.
    /// </summary>
    /// <remarks>
    /// ⚠ The rule <see cref="ReferenceResult"/> states — answering silently about whichever declaration was scanned first
    /// is plausible and wrong — matters most HERE, where the answer is written: renaming <c>Handle</c> with two
    /// classes declaring one rewrote the first class's method and reported "✅ Applied", the other untouched. A
    /// partial type declared in several files is ONE symbol, and not a candidate twice.
    /// </remarks>
    public RenamePlan PlanRename(string symbolName, string? declaringFile, int? declaringLine, CancellationToken ct = default)
    {
        var snap = Current();
        var declarations = ResolveDeclarations(snap, symbolName, declaringFile, ct);
        if (declaringLine is { } line) declarations = AtLine(declarations, line);
        // ⚠ Members C# links by name — an interface member and its implementations, a virtual member and its overrides —
        // are ONE rename: renamed alone, the other keeps the old name and the build breaks (CS0535, CS0115) under
        // "✅ Applied". Families are formed over EVERY declaration of the name, so a narrowed one still brings its links.
        var all = declaringFile is null && declaringLine is null ? declarations : ResolveDeclarations(snap, symbolName, null, ct);
        var families = LinkedFamilies(all.Select(d => d.Symbol));
        var symbols = declarations
            .GroupBy(d => families[d.Symbol])
            .Select(g => (Family: g.Key, g.First().Symbol, g.First().Location))
            .ToList();
        if (symbols.Count == 0) return new RenamePlan(new Dictionary<string, IReadOnlyList<TextSpan>>(), []);
        if (symbols.Count > 1)
            return new RenamePlan(new Dictionary<string, IReadOnlyList<TextSpan>>(),
                [.. symbols.Select(s => (s.Symbol.ToDisplayString(), s.Location))]);
        var targets = families.Where(f => f.Value == symbols[0].Family).Select(f => f.Key).ToList();
        return new RenamePlan(RenameSpans(snap, symbolName, targets, ct), []);
    }

    /// <summary>Each symbol's family: symbols linked by an override or an interface implementation share one.</summary>
    private static Dictionary<ISymbol, int> LinkedFamilies(IEnumerable<ISymbol> symbols)
    {
        var list = symbols.Distinct(SymbolEqualityComparer.Default).Cast<ISymbol>().ToList();
        var parent = Enumerable.Range(0, list.Count).ToArray();
        int Find(int i) => parent[i] == i ? i : (parent[i] = Find(parent[i]));
        for (var i = 0; i < list.Count; i++)
            for (var j = i + 1; j < list.Count; j++)
                if (Linked(list[i], list[j])) parent[Find(i)] = Find(j);
        var families = new Dictionary<ISymbol, int>(SymbolEqualityComparer.Default);
        for (var i = 0; i < list.Count; i++) families[list[i]] = Find(i);
        return families;
    }

    private static bool Linked(ISymbol a, ISymbol b) =>
        Overrides(a, b) || Overrides(b, a) || Implements(a, b) || Implements(b, a)
        // The two parts of a partial member are one member: renamed alone, the other keeps the old name (CS0759).
        || SymbolEqualityComparer.Default.Equals(PartialDefinition(a), PartialDefinition(b));

    private static bool Overrides(ISymbol member, ISymbol baseMember)
    {
        for (var s = Overridden(member); s is not null; s = Overridden(s))
            if (SymbolEqualityComparer.Default.Equals(s.OriginalDefinition, baseMember.OriginalDefinition)) return true;
        return false;
    }

    private static ISymbol? Overridden(ISymbol s) => s switch
    {
        IMethodSymbol m   => m.OverriddenMethod,
        IPropertySymbol p => p.OverriddenProperty,
        IEventSymbol e    => e.OverriddenEvent,
        _                 => null,
    };

    /// <summary><paramref name="member"/> implements <paramref name="interfaceMember"/>, implicitly or explicitly, through
    /// any construction of its interface.</summary>
    private static bool Implements(ISymbol member, ISymbol interfaceMember)
    {
        if (interfaceMember.ContainingType is not { TypeKind: TypeKind.Interface } iface) return false;
        if (member.ContainingType is not { } type) return false;
        foreach (var implemented in type.AllInterfaces)
        {
            if (!SymbolEqualityComparer.Default.Equals(implemented.OriginalDefinition, iface.OriginalDefinition)) continue;
            foreach (var candidate in implemented.GetMembers())
            {
                if (!SymbolEqualityComparer.Default.Equals(candidate.OriginalDefinition, interfaceMember.OriginalDefinition)) continue;
                if (type.FindImplementationForInterfaceMember(candidate) is { } impl
                    && SymbolEqualityComparer.Default.Equals(impl.OriginalDefinition, member.OriginalDefinition))
                    return true;
            }
        }
        return false;
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<TextSpan>> RenameSpans(
        Snapshot snap, string symbolName, IReadOnlyList<ISymbol> targets, CancellationToken ct)
    {
        var byFile = new Dictionary<string, IReadOnlyList<TextSpan>>(Services.PathComparer.Default);
        // ⚠ The snapshot, never the live fields: a save during a rename (the watcher calls Update) made
        // this loop throw "collection modified" — falling back to the text rename, homonyms included —
        // or bind symbols of the new compilation against a target from the old one.
        foreach (var (path, tree) in snap.Trees)
        {
            ct.ThrowIfCancellationRequested();
            if (!tree.ToString().Contains(symbolName, StringComparison.Ordinal)) continue;

            var model = snap.Compilation.GetSemanticModel(tree);
            var spans = new List<TextSpan>();

            foreach (var token in tree.GetRoot(ct).DescendantTokens())
            {
                if (!token.IsKind(SyntaxKind.IdentifierToken)) continue;
                if (token.ValueText != symbolName) continue;

                var parent = token.Parent;
                if (parent is null) continue;

                // A declaration binds through GetDeclaredSymbol, a use through GetSymbolInfo.
                // ⚠ A constructor or a destructor carries its TYPE's name but declares a method: left out, renaming a class
                // with a constructor wrote "class ShoppingCart { public Cart(…) }" — CS1520 — under "✅ Applied".
                var declared = model.GetDeclaredSymbol(parent, ct);
                var bound = declared is not null && targets.Any(t => SymbolEqualityComparer.Default.Equals(declared, t))
                    || declared is IMethodSymbol { MethodKind: MethodKind.Constructor or MethodKind.StaticConstructor
                                                   or MethodKind.Destructor } structor
                       && targets.Any(t => SymbolEqualityComparer.Default.Equals(structor.ContainingType, t))
                    || targets.Any(t => ResolvesTo(model, parent, t, ct));

                if (bound) spans.Add(token.Span);
            }

            if (spans.Count > 0) byFile[path] = spans;
        }
        return byFile;
    }

    /// <summary>Where <paramref name="symbolName"/> is declared. Empty when it resolves to nothing.</summary>
    public IReadOnlyList<SymbolLocation> FindDeclarations(string symbolName, CancellationToken ct = default)
    {
        var snap = Current();

        var found = new List<SymbolLocation>();
        foreach (var (_, tree) in snap.Trees)
        {
            ct.ThrowIfCancellationRequested();
            if (!tree.ToString().Contains(symbolName, StringComparison.Ordinal)) continue;

            var model = snap.Compilation.GetSemanticModel(tree);
            foreach (var node in tree.GetRoot(ct).DescendantNodes())
            {
                if (!IsDeclarationNamed(node, symbolName)) continue;
                if (model.GetDeclaredSymbol(node, ct) is not null)
                    found.Add(Locate(tree, node.Span));
            }
        }
        return found;
    }

    /// <summary>
    /// Where <paramref name="name"/> is written, as a whole word, in code this index does not compile: the inactive
    /// branches of <c>#if</c> — no preprocessor symbol is defined here, so a target framework's or a configuration's
    /// branch is text. A reference written there is one no answer of this index counts.
    /// </summary>
    public IReadOnlyList<SymbolLocation> FindInInactiveCode(string name, CancellationToken ct = default)
    {
        var snap = Current();
        var found = new List<SymbolLocation>();
        foreach (var (_, tree) in snap.Trees.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            if (!tree.ToString().Contains(name, StringComparison.Ordinal)) continue;
            foreach (var trivia in tree.GetRoot(ct).DescendantTrivia(descendIntoTrivia: true))
            {
                if (!trivia.IsKind(SyntaxKind.DisabledTextTrivia)) continue;
                var text = trivia.ToString();
                for (var at = text.IndexOf(name, StringComparison.Ordinal); at >= 0;
                     at = text.IndexOf(name, at + name.Length, StringComparison.Ordinal))
                    if (IsWordAt(text, at, name.Length))
                        found.Add(Locate(tree, new TextSpan(trivia.SpanStart + at, name.Length)));
            }
        }
        return found;

        static bool IsWordAt(string text, int at, int length) =>
            (at == 0 || !IsIdentifierChar(text[at - 1]))
            && (at + length == text.Length || !IsIdentifierChar(text[at + length]));

        static bool IsIdentifierChar(char c) => char.IsLetterOrDigit(c) || c == '_';
    }

    // ── Internals ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Every declaration of <paramref name="symbolName"/>, ordered so the answer is stable across
    /// runs: dictionary enumeration order is not a defensible way to pick which
    /// <c>Diagnostics</c> the user meant.
    /// </summary>
    private List<(ISymbol Symbol, SymbolLocation Location, int StartLine, int EndLine)> ResolveDeclarations(
        Snapshot snap, string symbolName, string? declaringFile, CancellationToken ct)
    {
        var found = new List<(ISymbol, SymbolLocation, int, int)>();

        foreach (var (path, tree) in snap.Trees.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            if (declaringFile is not null && !IsDeclaringFile(path, declaringFile))
                continue;
            if (!tree.ToString().Contains(symbolName, StringComparison.Ordinal)) continue;

            var model = snap.Compilation.GetSemanticModel(tree);
            foreach (var node in tree.GetRoot(ct).DescendantNodes())
            {
                if (!IsDeclarationNamed(node, symbolName)) continue;
                if (model.GetDeclaredSymbol(node, ct) is { } s)
                {
                    // A field or an event declarator spans its name only: its extent is the member declaration, so the
                    // attribute line above it designates it like any other member's.
                    var extent = node is VariableDeclaratorSyntax { Parent.Parent: BaseFieldDeclarationSyntax member }
                        ? member.Span : node.Span;
                    var lines = tree.GetLineSpan(extent);
                    found.Add((s, Locate(tree, node.Span), lines.StartLinePosition.Line + 1, lines.EndLinePosition.Line + 1));
                }
            }
        }

        // A type is what a bare name usually means; a local or a field of the same name is the
        // surprising case. Surface the likeliest first, and report the rest as ambiguity.
        return found
            .OrderByDescending(f => f.Item1.Kind == SymbolKind.NamedType)
            .ThenByDescending(f => f.Item1.Kind == SymbolKind.Method)
            .ToList();
    }

    /// <summary>
    /// The declarations <paramref name="line"/> designates: those whose declaration spans it — attributes, signature or
    /// body —, the innermost when one nests in another. Empty when the line falls in none.
    /// </summary>
    /// <remarks>
    /// ⚠ One reader for every designation (rename, references): the same file and line name the same symbol to both,
    /// and a designation that designates nothing is refused by both. ⚠ Only a declaration that CONTAINS another one on
    /// the line gives way: two overloads written on one line both stay, and the answer is ambiguous — kept to the first,
    /// a rename would rewrite one of them under "applied".
    /// </remarks>
    private static List<(ISymbol Symbol, SymbolLocation Location, int StartLine, int EndLine)> AtLine(
        List<(ISymbol Symbol, SymbolLocation Location, int StartLine, int EndLine)> declarations, int line)
    {
        var spanning = declarations.Where(d => d.StartLine <= line && line <= d.EndLine).ToList();
        return [.. spanning.Where(d => !spanning.Any(e =>
            Services.PathComparer.Default.Equals(e.Location.RelPath, d.Location.RelPath)
            && d.StartLine <= e.StartLine && e.EndLine <= d.EndLine
            && (d.StartLine, d.EndLine) != (e.StartLine, e.EndLine)))];
    }

    /// <summary>
    /// Does <paramref name="path"/> designate the file the model named? Relative to the root (<c>src/Cart.cs</c>, with or
    /// without a leading <c>./</c>, either separator) or absolute.
    /// </summary>
    /// <remarks>⚠ A plain suffix test refused <c>./src/Cart.cs</c> — the form a model copies from a listing — and matched
    /// <c>MyCart.cs</c> for <c>Cart.cs</c>: the suffix must start at a folder boundary.</remarks>
    internal static bool IsDeclaringFile(string path, string declaringFile)
    {
        var sep  = Path.DirectorySeparatorChar;
        var name = declaringFile.Replace('/', sep).Replace('\\', sep);
        var full = path.Replace('/', sep);
        if (Path.IsPathRooted(name))
            return Services.PathComparer.Default.Equals(Path.GetFullPath(name), Path.GetFullPath(path))
                // ⚠ "/src/Shop/Rules/IPriceRule.cs" — the workspace path written with a leading separator — names the
                // trailing components it spells, as a relative name does; an absolute path elsewhere matches nothing.
                // Compared as a full path only, the narrowing matched no declaration and the rename was refused.
                || full.EndsWith(name, Services.PathComparer.Comparison);
        while (name.StartsWith("." + sep, StringComparison.Ordinal)) name = name[2..];
        return full.Equals(name, Services.PathComparer.Comparison)
            || full.EndsWith(sep + name, Services.PathComparer.Comparison);
    }

    private static bool IsDeclarationNamed(SyntaxNode node, string name) => node switch
    {
        MethodDeclarationSyntax m   => m.Identifier.Text == name,
        BaseTypeDeclarationSyntax t => t.Identifier.Text == name,
        PropertyDeclarationSyntax p => p.Identifier.Text == name,
        VariableDeclaratorSyntax v  => v.Identifier.Text == name,
        _ => false,
    };

    private static bool IsWithinDeclaration(SyntaxNode node, ISymbol target) =>
        target.DeclaringSyntaxReferences.Any(r =>
            r.SyntaxTree == node.SyntaxTree && r.Span.Contains(node.Span));

    private SymbolLocation Locate(SyntaxTree tree, TextSpan span)
    {
        var pos  = tree.GetLineSpan(span).StartLinePosition;
        var line = tree.GetText().Lines[pos.Line].ToString().Trim();
        return new SymbolLocation(
            Path.GetRelativePath(_root, tree.FilePath).Replace('\\', '/'),
            pos.Line + 1,
            line);
    }

    /// <summary>
    /// The runtime's own reference assemblies — enough to resolve BCL types without MSBuild.
    /// </summary>
    /// <remarks>
    /// Built once per process and shared. There are ~200 of them, they are read from disk, and they
    /// cannot change while the process lives: rebuilding the list on every <see cref="Build"/> made
    /// an index refresh needlessly expensive, and made a test suite that creates several indexes
    /// contend for the thread pool.
    /// </remarks>
    private static readonly Lazy<IReadOnlyList<MetadataReference>> _runtimeReferences = new(() =>
    {
        var tpa  = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string ?? string.Empty;
        var refs = new List<MetadataReference>();
        foreach (var path in tpa.Split(Path.PathSeparator))
        {
            if (!path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) continue;
            try { refs.Add(MetadataReference.CreateFromFile(path)); }
            catch (Exception ex) { Diagnostics.Swallow("CSharpSemanticIndex.MetadataReference", ex); }
        }
        return refs;
    }, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// C# files under <paramref name="root"/>, common exclusions applied.
    /// </summary>
    /// <remarks>
    /// <see cref="WorkspaceScan"/>, never a private list: one matched on <c>\obj\</c> — a
    /// <b>backslash</b> — excludes nothing on the linux-x64 and darwin-arm64 hosts, and the
    /// semantic index then reads <c>bin/</c>, <c>obj/</c>, <c>.git/</c> and
    /// <c>.inferpal/history/</c>. That last one holds snapshot copies of the user's own sources, so
    /// "find references" answers with duplicates of an older version of the file in front of them.
    /// </remarks>
    private static IEnumerable<string> EnumerateCSharpFiles(string root) =>
        WorkspaceScan.EnumerateFiles(root, "*.cs");
}
