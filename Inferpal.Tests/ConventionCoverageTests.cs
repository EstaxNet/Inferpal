using System.IO;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Inferpal.Tests;

// §27.2 - the systemic answer to the pre-1.6.0 architecture review, modelled on
// SlashCommandCoverageTests: every convention the review had to repair site by site is now
// enforced by a scan of the sources. A new site that bypasses the guard rail fails the suite with
// a message naming the file and the rule - instead of waiting for the next architecture review.
//
// The four locked conventions:
//   1. SafeFileWriter    - the only text writer under Services\Tools (encoding/BOM preservation).
//   2. RegexBudget       - every Regex built or called statically under Services\Tools carries a
//                          timeout (workspace/web content is uncontrolled, backtracking = freeze).
//   3. AtomicFile        - the only writer of the Services\Persistence stores (write-rename, no
//                          truncation before writing). Append* stays out of scope: an append is
//                          additive by nature, there is no atomic append.
//   4. WorkspaceScan     - every recursive enumeration in a tool goes through the shared
//                          exclusions (bin/obj/.git/node_modules/.inferpal...).
//   5. Chat scrolling    - the literal Tag that ties the XAML list to the in-process WPF class
//                          handlers (two projects, no compiler link between them), and the two
//                          attributes (IsVirtualizing/CanContentScroll set to False) without
//                          which BringIntoView has nothing to bring into view and ScrollToEnd
//                          aims at a wrong extent. The agreement was STATED - in a comment - and
//                          held by nobody; breaking any of the three kills auto-scroll with no
//                          error, no exception, and no trace.
//   6. Remote UI boundary - a [DataMember] property carries a type the boundary can actually
//                          cross, otherwise it is INVISIBLE on the VS side (no error, no
//                          binding). The whitelist was MEASURED before it was written: the
//                          stated doctrine left out AsyncCommand (83 sites), double and a bare
//                          [DataContract] type - it forbade what works.
//   7. Ambient culture   - the language is overridden through Strings.OverrideCulture, never
//                          written into the thread: these processes are not ours (devenv, the
//                          Extensibility host), and a forced culture changes the formatting of
//                          code that is not ours either, without throwing anything.
//   8. GPU discipline    - an embedding loop YIELDS before every call, a query NEVER waits (it
//                          runs inside an agent run already holding the lease: waiting for
//                          yourself is a deadlock). The criterion is the SHAPE - in a loop means
//                          background work, outside a loop means someone is waiting - not a list
//                          of files.
public class ConventionCoverageTests
{
    // ── 1. SafeFileWriter sous Services\Tools ─────────────────────────────────

    [Fact]
    public void ToolTextWrites_GoThroughSafeFileWriter()
    {
        // The funnel itself is the only exemption: it is the one allowed to call
        // File.WriteAllTextAsync (with the detected encoding).
        string[] exempt = ["SafeFileWriter.cs"];

        foreach (var file in ToolsSources().Where(f => !exempt.Contains(Path.GetFileName(f))))
        {
            var source = File.ReadAllText(file);
            Assert.False(
                System.Text.RegularExpressions.Regex.IsMatch(source, @"(?<![\w.])File\.WriteAllText(Async)?\s*\("),
                $"{Rel(file)} writes a text file directly - File.WriteAllText emits UTF-8 without " +
                "a BOM whatever happens (VS BOM stripped, UTF-16 silently transcoded). " +
                "Use SafeFileWriter.WritePreservingAsync, or add a justified exemption here.");
        }
    }

    // ── 2. RegexBudget sous Services\Tools ────────────────────────────────────

    [Fact]
    public void ToolRegexes_CarryAMatchTimeout()
    {
        // A match without a timeout on workspace content = one pathological minified file freezes
        // the agent turn with no error. Regex.Escape/Unescape match nothing -> out of scope.
        var callSite = new System.Text.RegularExpressions.Regex(
            @"(?<![\w.])(?:Regex\.(?:IsMatch|Match|Matches|Replace|Split)|new\s+Regex|(?<=\bRegex\s+\w{1,64}\s*=\s*)new)\s*\(");

        foreach (var file in UntrustedInputSources())
        {
            var source = File.ReadAllText(file);
            foreach (System.Text.RegularExpressions.Match m in callSite.Matches(source))
            {
                var args = BalancedArguments(source, source.IndexOf('(', m.Index + m.Length - 1));
                Assert.True(args is not null,
                    $"{Rel(file)}: argument list never closed after \"{Snippet(source, m.Index)}\" - unexpected balancing or source.");
                Assert.True(
                    args!.Contains("RegexBudget") || args.Contains("Timeout"),
                    $"{Rel(file)}: \"{Snippet(source, m.Index)}\" has no match timeout - " +
                    "pass RegexBudget.Default (last argument), like all of its neighbours.");
            }
        }
    }

    // ── 3. AtomicFile sous Services\Persistence ───────────────────────────────

    [Fact]
    public void PersistenceStores_WriteThroughAtomicFile()
    {
        string[] exempt = ["AtomicFile.cs"]; // the funnel writes the staging file itself

        foreach (var file in CoreSources(Path.Combine("Services", "Persistence"))
                     .Where(f => !exempt.Contains(Path.GetFileName(f))))
        {
            var source = File.ReadAllText(file);
            Assert.False(
                System.Text.RegularExpressions.Regex.IsMatch(
                    source, @"(?<![\w.])File\.(WriteAllText|WriteAllBytes)(Async)?\s*\("),
                $"{Rel(file)} writes a store directly - File.WriteAll* truncates the target before " +
                "writing (crash/full disk = store lost, and the config file is shared " +
                "VS <-> VS Code). Use AtomicFile.WriteAllText[Async]/WriteAllBytes.");
        }
    }

    // ── 4. WorkspaceScan sous Services\Tools ──────────────────────────────────

    [Fact]
    public void ToolRecursiveEnumerations_RouteThroughWorkspaceScan()
    {
        // File granularity, like the router test: a tool that enumerates recursively must at
        // least know about WorkspaceScan (EnumerateFiles, or an explicit IsExcludedPath filter).
        foreach (var file in ToolsSources())
        {
            var source = File.ReadAllText(file);
            if (!source.Contains("SearchOption.AllDirectories")) continue;

            Assert.True(source.Contains("WorkspaceScan."),
                $"{Rel(file)} enumerates recursively without referencing WorkspaceScan - it walks " +
                "bin/obj/.git/node_modules and .inferpal/history (COPIES of sources). " +
                "Use WorkspaceScan.EnumerateFiles, or filter with WorkspaceScan.IsExcludedPath.");
        }
    }

    // ── 5. The scrolling contract of the chat list ────────────────────────────

    [Fact]
    public void TheChatList_KeepsTheContractTheAutoScrollerDependsOn()
    {
        // Three facts that must keep agreeing across a XAML file (project Inferpal) and an
        // in-process class (project Inferpal.InProc), with NO compiler link between them:
        //
        //   - the literal Tag, by which the two WPF class handlers recognise THE chat list among
        //     every ListBox living in devenv - renaming one side breaks no build, it simply turns
        //     scrolling off;
        //   - IsVirtualizing="False" and CanContentScroll="False", without which the ScrollViewer
        //     scrolls by ITEM (logical scrolling) and off-screen bubbles have no container:
        //     BringIntoView has nothing to bring, and ScrollToEnd aims at a wrong extent.
        //
        // All three fail the same way: the conversation stops following the stream, with no error,
        // no exception and no trace - the user concludes the model stopped answering. The comment
        // on ChatAutoScroller already STATED the agreement ("Must match the literal Tag set on the
        // chat ListBox in InferpalToolWindowContent.xaml"); nobody held it.
        var scroller = Path.Combine(RepoRoot(), "Inferpal.InProc", "GhostText", "ChatAutoScroller.cs");
        Assert.True(File.Exists(scroller), $"{scroller} does not exist - the rule checks nothing any more.");

        var tagConst = Regex.Match(CodeOnly(scroller), @"ChatListTag\s*=\s*""([^""]+)""");
        Assert.True(tagConst.Success, "ChatAutoScroller no longer exposes a ChatListTag literal: the rule derives nothing.");
        var tag = tagConst.Groups[1].Value;

        var xamlDir = Path.Combine(RepoRoot(), "Inferpal", "ToolWindow");
        var xamls   = Directory.EnumerateFiles(xamlDir, "*.xaml", SearchOption.TopDirectoryOnly).ToList();
        Assert.True(xamls.Count >= 2, $"Only {xamls.Count} XAML file(s) found: the rule reads nothing.");

        var element = new Regex(@"<[A-Za-z][^>]*?>", RegexOptions.Singleline);
        var matches = xamls
            .SelectMany(x => element.Matches(File.ReadAllText(x)).Select(m => (Xaml: x, El: m.Value)))
            .Where(e => Regex.IsMatch(e.El, $@"Tag\s*=\s*""{Regex.Escape(tag)}"""))
            .ToList();

        Assert.True(matches.Count == 1,
            $"{matches.Count} XAML element(s) carry Tag=\"{tag}\"; there must be exactly one. "
            + "Chat auto-scroll is a global WPF class handler filtered by that Tag: at zero it "
            + "attaches to nothing, at two it follows the wrong list - and neither says a word.");

        var chatList = matches[0].El;
        var missing = new[]
            {
                @"VirtualizingPanel\.IsVirtualizing\s*=\s*""False""",
                @"ScrollViewer\.CanContentScroll\s*=\s*""False""",
            }
            .Where(attr => !Regex.IsMatch(chatList, attr))
            .Select(attr => attr.Replace(@"\.", ".").Replace(@"\s*", string.Empty))
            .ToList();

        Assert.True(missing.Count == 0,
            $"The chat list ({Rel(matches[0].Xaml)}) lost: {string.Join(", ", missing)}. "
            + "With virtualization or logical scrolling on, off-screen bubbles have no container: "
            + "BringIntoView has nothing to bring and ScrollToEnd aims at a wrong extent - the "
            + "conversation silently stops following the stream.");
    }

    // ── 8. Who yields the GPU, and who must NEVER wait for it ─────────────────

    [Fact]
    public void EmbeddingLoopsYieldTheGpu_AndQueriesNeverWaitForIt()
    {
        // Two halves of one rule, with opposite failures, both silent:
        //
        //   - an embedding loop that does NOT yield steals the GPU from a chat turn in flight -
        //     the user sees a model stuttering or timing out, with no error anywhere;
        //   - a query that waits is a DEADLOCK: the agent already holds the chat lease for its
        //     whole run, and search_codebase / search_docs are called FROM that run. Waiting for
        //     the chat to go idle is waiting for yourself.
        //
        // The criterion is a SHAPE, not a list of files: embedding inside a loop is background
        // work (it yields); embedding outside a loop is answering someone who is waiting (it never
        // does).
        const string Embed = "GetEmbeddingAsync";
        const string Yield = "WaitForChatIdleAsync";

        var loops = 0;
        var oneShots = 0;
        var offenders = new List<string>();

        // We look for the CALL, not the word. node.ToString() also renders comments: the comment
        // documenting the wait ("Yield the shared GPU...") would have satisfied the first half,
        // and a header comment would have failed the second. False green and false red in the same
        // expression - the first version of both checks carried them.
        static bool Yields(SyntaxNode scope) =>
            scope.DescendantNodes().OfType<InvocationExpressionSyntax>().Any(i =>
                i.Expression is MemberAccessExpressionSyntax m && m.Name.Identifier.ValueText == Yield);

        foreach (var file in CoreSources("Services"))
        {
            var root = CSharpSyntaxTree.ParseText(File.ReadAllText(file), path: file).GetRoot();

            foreach (var call in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (call.Expression is not MemberAccessExpressionSyntax ma
                 || ma.Name.Identifier.ValueText != Embed) continue;

                var line = call.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                var loop = call.Ancestors()
                    .TakeWhile(a => a is not MethodDeclarationSyntax)
                    .FirstOrDefault(a => a is ForStatementSyntax or ForEachStatementSyntax or WhileStatementSyntax or DoStatementSyntax);

                if (loop is not null)
                {
                    loops++;
                    if (!Yields(loop))
                        offenders.Add($"{Rel(file)}({line}): embeds in a loop without yielding the GPU");
                    continue;
                }

                oneShots++;
                var scope = call.Ancestors().FirstOrDefault(a => a is MethodDeclarationSyntax or LocalFunctionStatementSyntax);
                if (scope is not null && Yields(scope))
                    offenders.Add($"{Rel(file)}({line}): a query waiting for the GPU - deadlock");
            }
        }

        // One witness per half: without both, a scan gone blind would pass while judging neither
        // the loops nor the queries.
        Assert.True(loops >= 2, $"Only {loops} in-loop embedding(s) found: the background half judges nothing.");
        Assert.True(oneShots >= 2, $"Only {oneShots} one-shot embedding(s) found: the query half judges nothing.");

        Assert.True(offenders.Count == 0,
            "GPU discipline broken. An embedding loop yields before every call "
            + "(GpuScheduler.WaitForChatIdleAsync); a query NEVER does - it is called from an agent "
            + "run already holding the chat lease, so waiting for the chat to be idle is waiting "
            + "for itself. Sites:"
            + Environment.NewLine + "  " + string.Join(Environment.NewLine + "  ", offenders));
    }

    // ── 7. The language is not changed by mutating the thread ─────────────────

    [Fact]
    public void NothingMutatesTheAmbientCulture()
    {
        // Strings.OverrideCulture exists for this: the language the user picked is consulted FIRST
        // in Get(), instead of forcing the thread culture. The reason is that the product lives in
        // processes it does not own - devenv for the in-process half, the Extensibility host for
        // the window - and writing an ambient culture there changes it for code that is not ours,
        // on a thread we do not pick. What breaks then is not our interface: it is the number and
        // date formatting of everything sharing that thread, with nothing thrown.
        //
        // Static on top of that (DefaultThreadCurrentCulture) and the whole process switches.
        // Measured at zero: free to lock, therefore locked now.
        string[] forbidden =
        [
            "CurrentUICulture", "CurrentCulture",
            "DefaultThreadCurrentUICulture", "DefaultThreadCurrentCulture",
        ];

        var offenders = new List<string>();
        var reads     = 0;

        foreach (var file in CoreSources("Services").Concat(CoreSources("Config"))
                                                    .Concat(CoreSources("Localization"))
                                                    .Concat(ViewModelSources())
                                                    .Concat(ProjectSources("Inferpal.Host"))
                                                    .Concat(ProjectSources("Inferpal.Fim"))
                                                    .Concat(ProjectSources("Inferpal.InProc")))
        {
            var root = CSharpSyntaxTree.ParseText(File.ReadAllText(file), path: file).GetRoot();

            // Witness: those properties really are READ somewhere. Without it, a rename on the BCL
            // side - or a scan gone blind - would make the rule green for nothing.
            reads += root.DescendantNodes().OfType<MemberAccessExpressionSyntax>()
                .Count(ma => forbidden.Contains(ma.Name.Identifier.ValueText));

            foreach (var assign in root.DescendantNodes().OfType<AssignmentExpressionSyntax>())
            {
                var target = assign.Left switch
                {
                    MemberAccessExpressionSyntax ma => ma.Name.Identifier.ValueText,
                    IdentifierNameSyntax id         => id.Identifier.ValueText,
                    _                               => null,
                };
                if (target is null || !forbidden.Contains(target)) continue;

                var line = assign.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                offenders.Add($"{Rel(file)}({line}): {assign.Left} = ...");
            }
        }

        Assert.True(reads >= 5,
            $"The scan found only {reads} ambient-culture read(s): the rule checks nothing any more.");

        Assert.True(offenders.Count == 0,
            "The ambient culture is written instead of being overridden. This process is not ours "
            + "(devenv, Extensibility host) and neither is the thread: go through "
            + "Strings.OverrideCulture, which Get() consults first. Sites:"
            + Environment.NewLine + "  " + string.Join(Environment.NewLine + "  ", offenders));
    }

    // ── 6. The types that cross the Remote UI boundary ────────────────────────

    [Fact]
    public void DataMemberProperties_CarryATypeTheBoundaryCanActuallyCross()
    {
        // A property exposed to devenv whose type does not cross is INVISIBLE on the VS side. No
        // error, no exception, no warning: the binding finds nothing and the element stays empty.
        // It is the most expensive failure shape in this repository to diagnose, because the code
        // looks right on both sides.
        //
        // The contract is a WHITELIST, and it was measured before being written: the stated
        // doctrine ("only string/bool/int/ObservableCollection<T>") was stricter than the product,
        // which also exposes 83 AsyncCommand, two double and one ChatMessageItem? (the scrolling
        // anchor). Forbidding what works is the rule people learn to work around.
        var files = ViewModelSources();

        var contracts = new HashSet<string>(StringComparer.Ordinal);
        var trees     = new List<(string File, SyntaxNode Root)>();
        foreach (var file in files)
        {
            var root = CSharpSyntaxTree.ParseText(File.ReadAllText(file), path: file).GetRoot();
            trees.Add((file, root));
            foreach (var type in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
                if (type.AttributeLists.SelectMany(a => a.Attributes)
                        .Any(a => a.Name.ToString() is "DataContract" or "DataContractAttribute"))
                    contracts.Add(type.Identifier.ValueText);
        }
        Assert.True(contracts.Count >= 8,
            $"Only {contracts.Count} [DataContract] type(s) found: the derivation is broken.");

        // The only shapes the boundary can carry. AsyncCommand is the SDK command type - it is how
        // an action crosses the boundary, there is no alternative.
        string[] primitives = ["string", "bool", "int", "double"];
        var shapes = new HashSet<string>(StringComparer.Ordinal);

        bool Crosses(TypeSyntax type)
        {
            var name = type.ToString().Replace(" ", string.Empty).TrimEnd('?');

            if (primitives.Contains(name))              { shapes.Add("primitive"); return true; }
            if (name == "AsyncCommand")                 { shapes.Add("command");   return true; }
            if (contracts.Contains(name))               { shapes.Add("contract");  return true; }

            var collection = Regex.Match(name, @"^ObservableCollection<([A-Za-z0-9_]+)>$");
            if (collection.Success
                && (primitives.Contains(collection.Groups[1].Value) || contracts.Contains(collection.Groups[1].Value)))
            {
                shapes.Add("collection");
                return true;
            }
            return false;
        }

        var offenders = new List<string>();
        var seen      = 0;
        foreach (var (file, root) in trees)
            foreach (var prop in root.DescendantNodes().OfType<PropertyDeclarationSyntax>())
            {
                if (!prop.AttributeLists.SelectMany(a => a.Attributes)
                        .Any(a => a.Name.ToString() is "DataMember" or "DataMemberAttribute")) continue;

                seen++;
                if (Crosses(prop.Type)) continue;

                var line = prop.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                offenders.Add($"{Rel(file)}({line}): {prop.Identifier.ValueText} is a {prop.Type}");
            }

        // Two witnesses, because two things can break: the enumeration (it no longer reads any
        // property) and the classifier (it accepts everything, or nothing). A "zero violation"
        // means nothing without both.
        Assert.True(seen > 100, $"Only {seen} [DataMember] propertie(s) read: the rule scans nothing.");
        foreach (var shape in new[] { "primitive", "command", "collection", "contract" })
            Assert.Contains(shape, shapes);

        Assert.True(offenders.Count == 0,
            "Property exposed to devenv with a type the Remote UI boundary cannot carry. It will be "
            + "INVISIBLE on the VS side: no error, no binding, the element stays empty. The only "
            + "shapes that cross are the primitives (string/bool/int/double), AsyncCommand, a "
            + "[DataContract] type, and an ObservableCollection of either of the last two. Sites:"
            + Environment.NewLine + "  " + string.Join(Environment.NewLine + "  ", offenders));
    }

    /// <summary>
    /// The witness of <see cref="CodeOnly"/>: it neutralizes comments in BOTH directions that
    /// matter, and shifts nothing.
    /// </summary>
    /// <remarks>
    /// Without it the repair would be invisible: the rules above are green before and after, since
    /// no source in the repository breaks them. That is exactly the failure mode this file exists
    /// to close - a rule that measures nothing is green for the worst possible reason.
    /// </remarks>
    [Fact]
    public void CodeOnly_NeutralizesComments_InBothDirections()
    {
        var path = Path.Combine(Path.GetTempPath(), $"inferpal-codeonly-{Guid.NewGuid():N}.cs");
        var source = string.Join(Environment.NewLine,
        [
            "class Subject",
            "{",
            "    // Tag=\"InferpalChatList\" quoted in prose",
            "    /// <summary>File.WriteAllText(path, text) in an XML comment</summary>",
            "    void Real() { Called(); }",
            "    /* Called();",
            "       Called(); */",
            "    void Other() { }",
            "}",
        ]);
        File.WriteAllText(path, source);
        try
        {
            var code = CodeOnly(path);

            // False RED: prose documenting a forbidden pattern must no longer carry it.
            Assert.DoesNotContain("InferpalChatList", code, StringComparison.Ordinal);
            Assert.DoesNotContain("File.WriteAllText", code, StringComparison.Ordinal);

            // False GREEN: a COMMENTED-OUT call no longer counts as present. The real one does.
            Assert.Equal(1, Regex.Matches(code, @"Called\(").Count);
            Assert.Contains("void Real()", code, StringComparison.Ordinal);
            Assert.Contains("void Other()", code, StringComparison.Ordinal);

            // Offsets are preserved: same length, same newlines - otherwise the line numbers in
            // the failure messages would point at the wrong line.
            Assert.Equal(source.Length, code.Length);
            Assert.Equal(source.Count(c => c == '\n'), code.Count(c => c == '\n'));
        }
        finally { File.Delete(path); }
    }

    // ── Plumbing ──────────────────────────────────────────────────────────────

    /// <summary>
    /// The text of a source file with its COMMENTS neutralized - replaced by spaces, length for
    /// length, newlines preserved: offsets, and therefore the line numbers of failure messages,
    /// stay exact.
    /// </summary>
    /// <remarks>
    /// A convention scan that reads raw text finds its patterns INSIDE comments, and that is paid
    /// for in both directions. False RED: the rule fails on the prose documenting the very defect
    /// it forbids. False GREEN: a rule requiring the presence of a call is satisfied by finding it
    /// COMMENTED OUT, i.e. disabled. Here the language is C#, so a syntax tree decides what a
    /// regex cannot. One reader per language, never two - this is the C# one, hence
    /// <c>internal</c>.
    /// </remarks>
    internal static string CodeOnly(string path)
    {
        var text   = File.ReadAllText(path);
        var buffer = text.ToCharArray();
        var root   = CSharpSyntaxTree.ParseText(text, path: path).GetRoot();

        foreach (var trivia in root.DescendantTrivia(descendIntoTrivia: true))
        {
            if (!trivia.IsKind(SyntaxKind.SingleLineCommentTrivia)
                && !trivia.IsKind(SyntaxKind.MultiLineCommentTrivia)
                && !trivia.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia)
                && !trivia.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia))
                continue;

            var span = trivia.FullSpan;
            for (var i = span.Start; i < span.End && i < buffer.Length; i++)
                if (buffer[i] != '\n' && buffer[i] != '\r')
                    buffer[i] = ' ';
        }
        return new string(buffer);
    }

    /// <summary>
    /// Every <c>.cs</c> file of one project of the repository, WITH ITS WITNESS - a renamed folder
    /// or a broken glob would otherwise make a rule pass by scanning nothing.
    /// </summary>
    /// <remarks>
    /// <c>internal</c> for the same reason as <see cref="CodeOnly"/>: one enumerator, therefore one
    /// witness. <c>LocalizationCompletenessTests</c> uses it to sweep the VS adapter for the
    /// <c>%Key%</c> tokens of the command table.
    /// </remarks>
    /// <summary>
    /// The VS adapter view models (<c>Inferpal\ToolWindow</c>), WITH ITS WITNESS - the same guard
    /// as <see cref="ProjectSources"/>.
    /// </summary>
    private static IReadOnlyList<string> ViewModelSources()
    {
        var dir = Path.Combine(RepoRoot(), "Inferpal", "ToolWindow");
        Assert.True(Directory.Exists(dir), $"The convention scan targets {dir}, which does not exist - the rule checks nothing any more.");

        var files = Directory.EnumerateFiles(dir, "*.cs", SearchOption.TopDirectoryOnly).ToList();
        Assert.NotEmpty(files);
        return files;
    }

    internal static IReadOnlyList<string> ProjectSources(string project)
    {
        var dir = Path.Combine(RepoRoot(), project);
        Assert.True(Directory.Exists(dir), $"The convention scan targets {dir}, which does not exist - the rule checks nothing any more.");

        var files = Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .ToList();
        Assert.NotEmpty(files);
        return files;
    }

    private static IEnumerable<string> ToolsSources() =>
        CoreSources(Path.Combine("Services", "Tools"));

    /// <summary>
    /// Where a regex meets input nobody in this repository controls. <c>Services\Tools</c> parses
    /// the workspace; <c>Services\Docs</c> parses HTML fetched from the open web, which is the
    /// least controlled input the product touches - and it was outside the rule until the
    /// post-1.6.0 review found the crawler running unbounded patterns over it, while its twin
    /// <c>FetchUrlTool</c> had been bounding the same ones since it was written.
    /// </summary>
    private static IEnumerable<string> UntrustedInputSources() =>
        ToolsSources().Concat(CoreSources(Path.Combine("Services", "Docs")));

    private static IEnumerable<string> CoreSources(string subdir) =>
        Directory.EnumerateFiles(
            Path.Combine(RepoRoot(), "Inferpal.Core", subdir), "*.cs", SearchOption.AllDirectories);

    private static string Rel(string path) =>
        Path.GetRelativePath(RepoRoot(), path);

    private static string Snippet(string source, int index) =>
        source.Substring(index, Math.Min(60, source.Length - index)).ReplaceLineEndings(" ");

    /// <summary>Repo root = first ancestor of the test bin folder containing README.md.</summary>
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "README.md")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    /// <summary>
    /// Content of the argument list opening at <paramref name="openParen"/>, with balanced
    /// parentheses, ignoring those inside string/char literals (regex patterns are full of
    /// unmatched parentheses) and the content of verbatim/interpolated strings.
    /// </summary>
    private static string? BalancedArguments(string source, int openParen)
    {
        Assert.True(openParen >= 0, "opening parenthesis not found after the call site");
        int depth = 0;
        for (int i = openParen; i < source.Length; i++)
        {
            char c = source[i];
            switch (c)
            {
                case '/': // comments: a "// function name(" would skew the count
                    if (i + 1 < source.Length && source[i + 1] == '/')
                    {
                        while (i < source.Length && source[i] != '\n') i++;
                    }
                    else if (i + 1 < source.Length && source[i + 1] == '*')
                    {
                        var close = source.IndexOf("*/", i + 2, StringComparison.Ordinal);
                        i = close < 0 ? source.Length : close + 1;
                    }
                    break;

                case '(': depth++; break;
                case ')':
                    if (--depth == 0) return source[(openParen + 1)..i];
                    break;

                case '\'': // char literal : '\'' ou '('
                    i++;
                    if (i < source.Length && source[i] == '\\') i++;
                    i++; // closes the quote
                    break;

                case '"':
                {
                    // Verbatim if an @ precedes (possibly $@ / @$) - "" escapes the quote.
                    bool verbatim = (i > 0 && source[i - 1] == '@') ||
                                    (i > 1 && source[i - 1] == '$' && source[i - 2] == '@');
                    i++;
                    while (i < source.Length)
                    {
                        if (verbatim && source[i] == '"' && i + 1 < source.Length && source[i + 1] == '"') { i += 2; continue; }
                        if (!verbatim && source[i] == '\\') { i += 2; continue; }
                        if (source[i] == '"') break;
                        i++;
                    }
                    break;
                }
            }
        }
        return null; // never closed - the call site reports file + snippet
    }
}
