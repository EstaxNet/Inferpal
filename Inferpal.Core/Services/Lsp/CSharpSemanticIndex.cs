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
/// Other declarations sharing the same name. <b>Non-empty means the answer is about one of
/// several candidates</b> and the caller should disambiguate.
/// </param>
/// <remarks>
/// A bare list would hide the one thing that matters: <c>Diagnostics</c> names a class in the Core
/// and two test members here, so "references to Diagnostics" is not a question with one answer.
/// Answering silently about whichever declaration was scanned first is plausible and wrong, which
/// is the worst combination.
/// </remarks>
internal sealed record ReferenceResult(
    SymbolLocation? Declaration,
    IReadOnlyList<SymbolLocation> References,
    IReadOnlyList<SymbolLocation> OtherDeclarations)
{
    /// <summary>True when the name matches several declarations, so the answer is partial.</summary>
    public bool IsAmbiguous => OtherDeclarations.Count > 0;

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
        string symbolName, string? declaringFile = null, CancellationToken ct = default)
    {
        var snap = Current();
        var declarations = ResolveDeclarations(snap, symbolName, declaringFile, ct);
        if (declarations.Count == 0) return ReferenceResult.None;

        var (target, targetLocation) = declarations[0];
        var others = declarations.Skip(1).Select(d => d.Location).ToList();

        var hits = new List<SymbolLocation>();
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

                if (!ResolvesTo(model, node, target, ct)) continue;
                if (IsWithinDeclaration(node, target)) continue;   // a declaration is not a use

                hits.Add(Locate(tree, node.Span));
            }
        }

        return new ReferenceResult(targetLocation, hits, others);
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
    private static bool ResolvesTo(SemanticModel model, SyntaxNode node, ISymbol target, CancellationToken ct)
    {
        var info = model.GetSymbolInfo(node, ct);

        if (info.Symbol is { } s && Same(s, target)) return true;
        foreach (var candidate in info.CandidateSymbols)
            if (Same(candidate, target)) return true;
        return false;

        static bool Same(ISymbol a, ISymbol b) =>
            SymbolEqualityComparer.Default.Equals(a.OriginalDefinition, b);
    }

    /// <summary>
    /// Identifier tokens that must change when the symbol is renamed: every use <b>and</b> its
    /// declaration, grouped by file. Empty when the symbol does not resolve.
    /// </summary>
    /// <remarks>
    /// The difference with <see cref="FindReferences"/> is the declaration, which is a reference to
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
        var declarations = ResolveDeclarations(snap, symbolName, declaringFile, ct)
            .Where(d => declaringLine is null || d.Location.Line == declaringLine)
            .ToList();
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
        Overrides(a, b) || Overrides(b, a) || Implements(a, b) || Implements(b, a);

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

    // ── Internals ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Every declaration of <paramref name="symbolName"/>, ordered so the answer is stable across
    /// runs: dictionary enumeration order is not a defensible way to pick which
    /// <c>Diagnostics</c> the user meant.
    /// </summary>
    private List<(ISymbol Symbol, SymbolLocation Location)> ResolveDeclarations(
        Snapshot snap, string symbolName, string? declaringFile, CancellationToken ct)
    {
        var found = new List<(ISymbol, SymbolLocation)>();

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
                    found.Add((s, Locate(tree, node.Span)));
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
