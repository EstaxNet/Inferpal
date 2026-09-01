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

/// <summary>Answer to a reference query — including which declaration it is about.</summary>
/// <param name="Declaration">The declaration the references belong to; null when nothing resolved.</param>
/// <param name="References">Uses of that exact symbol.</param>
/// <param name="OtherDeclarations">
/// Other declarations sharing the same name. <b>Non-empty means the answer is about one of
/// several candidates</b> and the caller should disambiguate.
/// </param>
/// <remarks>
/// Returning a bare list would hide the one thing that matters: <c>Diagnostics</c> names a class in
/// the Core *and* two test members here, so "references to Diagnostics" is not a question with one
/// answer. Answering silently about whichever declaration was scanned first is precisely the
/// plausible-but-wrong failure that sank the ex-§8 prototype.
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
/// what merely spells the same (roadmap §14).
/// </summary>
/// <remarks>
/// <para>
/// This is the answer to a measured defect, not a hypothesis. Searching this repository for
/// <c>Handle(</c> returns <b>61</b> occurrences; exactly <b>3</b> of them refer to
/// <c>TaskCommandHandler.Handle</c>. The analysis tools shipped today (<c>analyze_impact</c>,
/// <c>rename_symbol</c>, <c>trace_dependency</c>) work on the former number — around 5 % precision
/// on a name shared by a dozen handlers. It is also what sank the ex-§8 "Next Edit" prototype at
/// 61 % precision: two homonyms in different scopes are indistinguishable to a regex.
/// </para>
/// <para>
/// <b>No MSBuild.</b> A <see cref="CSharpCompilation"/> is built directly from the parsed files
/// plus the runtime's reference assemblies — no <c>.csproj</c> parsing, no restore, no
/// <c>MSBuildLocator</c>, and no extra package (<c>Microsoft.CodeAnalysis.CSharp</c> is already
/// referenced for RAG chunking). Measured on this repository: 622 ms to parse 408 files, 433 ms to
/// build the compilation, ~75 MB.
/// </para>
/// <para>
/// <b>Grep filters, semantics arbitrates.</b> Building a <see cref="SemanticModel"/> for all 408
/// files costs ~10 s, so a query first discards every file whose text does not contain the symbol
/// name — a cheap, exact pre-filter, since a reference cannot exist without the name appearing —
/// and only then resolves the survivors. This inverts today's architecture, where the textual hit
/// <i>is</i> the answer.
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
    private readonly Dictionary<string, SyntaxTree> _treesByPath = new(StringComparer.OrdinalIgnoreCase);
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
    /// Rebuilding per call costs ~750 ms; keeping one instance costs 4 ms per saved file. The cache
    /// is only safe <b>because</b> something invalidates it — <c>ProjectIndexService</c>'s file
    /// watcher calls <see cref="Update"/> — which is why the two were added together and must stay
    /// together. A cache without invalidation would answer about code that no longer exists, the
    /// exact silent wrongness this class exists to remove.
    /// </para>
    /// <para>
    /// ⚠ With RAG disabled there is no watcher, so the index would go stale. <see cref="ForWorkspace"/>
    /// is therefore only used by callers that can accept a rebuild otherwise; see the tools.
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
            if (!path.StartsWith(index._root, StringComparison.OrdinalIgnoreCase)) continue;
            try { index.Update(path); }
            catch (Exception ex) { Diagnostics.Swallow("CSharpSemanticIndex.NotifyFileChanged", ex); }
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
                var text = File.ReadAllText(path);
                _treesByPath[path] = CSharpSyntaxTree.ParseText(text, path: path);
            }
            catch (Exception ex) { Diagnostics.Swallow($"CSharpSemanticIndex.Parse({Path.GetFileName(path)})", ex); }
        }

        _compilation = CSharpCompilation.Create(
            "inferpal.semantic",
            _treesByPath.Values,
            _runtimeReferences.Value,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }

    /// <summary>
    /// Re-parses a single file and swaps it into the compilation, without touching the others.
    /// Adds the file when it is new, removes it when it no longer exists on disk.
    /// </summary>
    /// <remarks>
    /// A full <see cref="Build"/> costs ~755 ms on this repository. Paying that on every save — the
    /// editor's file watcher fires constantly — would make the index the most expensive thing in
    /// the process for the sake of one changed file. <see cref="CSharpCompilation"/> is immutable
    /// and gives <c>ReplaceSyntaxTree</c> exactly for this: the new compilation shares everything
    /// with the old but one tree.
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
        try { newTree = CSharpSyntaxTree.ParseText(File.ReadAllText(path), path: path); }
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
        string symbolName, string? declaringFile = null, CancellationToken ct = default)
    {
        var snap = Current();
        var declarations = ResolveDeclarations(snap, symbolName, declaringFile, ct);
        if (declarations.Count == 0) return new Dictionary<string, IReadOnlyList<TextSpan>>();
        var target = declarations[0].Symbol;

        var byFile = new Dictionary<string, IReadOnlyList<TextSpan>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, tree) in _treesByPath)
        {
            ct.ThrowIfCancellationRequested();
            if (!tree.ToString().Contains(symbolName, StringComparison.Ordinal)) continue;

            var model = _compilation!.GetSemanticModel(tree);
            var spans = new List<TextSpan>();

            foreach (var token in tree.GetRoot(ct).DescendantTokens())
            {
                if (!token.IsKind(SyntaxKind.IdentifierToken)) continue;
                if (token.ValueText != symbolName) continue;

                var parent = token.Parent;
                if (parent is null) continue;

                // A declaration binds through GetDeclaredSymbol, a use through GetSymbolInfo.
                var declared = model.GetDeclaredSymbol(parent, ct);
                var bound = declared is not null && SymbolEqualityComparer.Default.Equals(declared, target)
                    || ResolvesTo(model, parent, target, ct);

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
            if (declaringFile is not null &&
                !path.EndsWith(declaringFile.Replace('/', Path.DirectorySeparatorChar),
                               StringComparison.OrdinalIgnoreCase))
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
    /// This used to carry its own four-entry list, matched on <c>\obj\</c> — a <b>backslash</b>.
    /// The host ships for linux-x64 and darwin-arm64 (VS Code, since 1.5.0), and on those the list
    /// excluded exactly nothing: the semantic index read <c>bin/</c>, <c>obj/</c>, <c>.git/</c> and
    /// <c>.inferpal/history/</c> — the last of which holds snapshot COPIES of the user's own
    /// sources, so "find references" answered with duplicates of an older version of the file the
    /// user was looking at. <see cref="WorkspaceScan"/> exists because seven copies of this list
    /// had drifted apart once already; this was the eighth, and it was the same defect.
    /// </remarks>
    private static IEnumerable<string> EnumerateCSharpFiles(string root) =>
        WorkspaceScan.EnumerateFiles(root, "*.cs");
}
