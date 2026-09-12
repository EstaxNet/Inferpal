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
//   9. Variable facts    - what the MODEL reads (a tool description, the base system prompt)
//                          asserts neither an editor nor a shell: one Core serves both Visual
//                          Studio and VS Code, and the shell has been resolved per machine since
//                          §23. The prompt said "Visual Studio 2026 ... PowerShell" in all TEN
//                          languages, and run_command's description said PowerShell on the
//                          published linux-x64 / darwin-arm64 packages.
//  11. Shared UI text   - a label the host serves to BOTH front-ends names neither an editor
//                          nor a shell. `settings/strings` and `command/list` render these
//                          strings verbatim in VS Code, and the shell is resolved per machine,
//                          so naming ours makes the sentence false for the other half of the
//                          users. The criterion is "is it served to both", never "does it say
//                          Visual Studio": three strings live only in the VS window and are
//                          right to name it.
//  10. Model keywords    - an argument the code COMPARES against literals is read through
//                          ToolArgs.Keyword, never any other way. This is the half the "read
//                          model arguments without trusting them" rule does not cover: that one
//                          closes the reads that THROW, this one the reads that return a WRONG
//                          ANSWER with no error - 'Callers' does not match "callers", and the
//                          report comes out with no sections at all.
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

    // ── 9. What the MODEL reads asserts no variable fact ──────────────────────

    /// <summary>The two facts the product knows to be variable, and that its own text asserted.</summary>
    private static readonly string[] VariableFacts = ["Visual Studio", "PowerShell", "powershell"];

    [Fact]
    public void ModelFacingToolText_NamesNeitherAnEditorNorAShell()
    {
        // A tool description is not prose: it is the SPECIFICATION the model reads to decide how
        // to write its call. `run_command` announced "Runs a PowerShell command", and the shell has
        // been resolved per machine since §23 — on the linux-x64 and darwin-arm64 packages
        // published since 1.5.0, that shell is /bin/bash. The model wrote Get-ChildItem and
        // $env:FOO, bash refused them, and the agent spent its iteration budget rediscovering by
        // trial and error what the tool could have said in one sentence. Same family for the
        // editor: get_solution_info and get_open_editors named Visual Studio to the VS Code
        // front-end.
        //
        // ⚠ The rule reads the Description and Parameters PROPERTIES, not the file: run_command
        // must be able to name both dialects in the code that CHOOSES between them — that is the
        // whole point. What is forbidden is writing the fact down where the model reads it.
        var offenders    = new List<string>();
        var descriptions = 0;

        foreach (var file in ToolsSources())
        {
            var root = CSharpSyntaxTree.ParseText(File.ReadAllText(file), path: file).GetRoot();

            foreach (var property in root.DescendantNodes().OfType<PropertyDeclarationSyntax>())
            {
                var name = property.Identifier.ValueText;
                if (name is not ("Description" or "Parameters")) continue;
                if (name == "Description") descriptions++;

                foreach (var text in ModelFacingText(property))
                    foreach (var fact in VariableFacts)
                        if (text.Contains(fact, StringComparison.Ordinal))
                            offenders.Add($"{Rel(file)} : {name} → « {text.Trim()} »");
            }
        }

        // Witness: the rule is only worth something if it actually read tool descriptions. A moved
        // folder or a renamed property turns "no offender" back into "nothing was read".
        Assert.True(descriptions >= 20,
            $"The scan read only {descriptions} Description propertie(s) under Services/Tools: the rule no longer checks anything.");

        Assert.True(offenders.Count == 0,
            "Model-facing text naming an editor or a shell. One Core serves both front-ends and "
            + "three operating systems: state the fact instead of asserting it (the dialect comes "
            + "from ShellLauncher.Resolve, the editor is declared by the front-end):"
            + Environment.NewLine + "  " + string.Join(Environment.NewLine + "  ", offenders));
    }

    [Fact]
    public void TheBaseSystemPrompt_AssertsNeitherEditorNorShell()
    {
        // The SAME resource is served to both front-ends: Inferpal.Host hands it to VS Code
        // verbatim. "integrated in Visual Studio 2026" was therefore false for every VS Code user,
        // in all ten languages, and "PowerShell commands" false for every Linux/macOS machine. Both
        // facts are now built at runtime by SystemPromptBuilder.EnvironmentFacts, where they hold.
        var dir       = Path.Combine(RepoRoot(), "Inferpal.Core", "Localization");
        var files     = Directory.EnumerateFiles(dir, "Strings*.resx").ToList();
        var inspected = 0;
        var offenders = new List<string>();

        foreach (var file in files)
        {
            var m = Regex.Match(File.ReadAllText(file),
                                @"<data name=""SystemPrompt""[^>]*>\s*<value>(.*?)</value>",
                                RegexOptions.Singleline);
            if (!m.Success) continue;

            inspected++;
            foreach (var fact in VariableFacts)
                if (m.Groups[1].Value.Contains(fact, StringComparison.Ordinal))
                    offenders.Add($"{Path.GetFileName(file)} : « {fact} »");
        }

        // Witness: all ten languages, otherwise a broken pattern would make the rule green on zero files.
        Assert.True(inspected == 10,
            $"The scan read {inspected} SystemPrompt value(s) out of the 10 expected in {dir}: the rule no longer checks anything.");

        Assert.True(offenders.Count == 0,
            "The base system prompt asserts an editor or a shell. Both facts vary from one "
            + "front-end and one machine to the next: they belong to EnvironmentFacts, not to a "
            + "translation:" + Environment.NewLine + "  " + string.Join(Environment.NewLine + "  ", offenders));
    }

    /// <summary>The text a model will read inside a property: literals and the fixed parts of
    /// interpolated strings. Identifiers (<c>ShellDialect.PowerShell</c>) are not.</summary>
    private static IEnumerable<string> ModelFacingText(PropertyDeclarationSyntax property)
    {
        foreach (var node in property.DescendantNodes())
        {
            if (node is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.StringLiteralExpression))
                yield return literal.Token.ValueText;
            else if (node is InterpolatedStringTextSyntax interpolated)
                yield return interpolated.TextToken.ValueText;
        }
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

    // ── 11. What the USER reads does not assert the editor either ────────────

    [Fact]
    public void SharedUiText_NamesNeitherAnEditorNorAShell()
    {
        // The user-facing half of the "variable facts" rule. That one closes what the MODEL reads;
        // the same Core also serves the text the USER reads, to BOTH front-ends, and nobody looked.
        //
        // The channel is explicit, and its own comment sells it as a feature: `settings/strings`
        // serves the labels "straight from the same .resx resources as the Visual Studio settings
        // window. The VS Code settings webview renders them VERBATIM, so both editors share the
        // exact same wording in all 10 languages." True -- and exactly why a label naming ONE
        // editor becomes false for the other.
        //
        // Measured 2026-09-09: of 731 entries, seven name an editor or a shell. FOUR are served to
        // both front-ends and are therefore false for one of them:
        //   . LabelLanguage    "(overrides Visual Studio)"                  -> VS Code
        //   . HintProvider     "Takes effect after reloading Visual Studio" -> VS Code, and it
        //     names a REMEDY THEY CANNOT PERFORM
        //   . LabelCustomTools "name=powershell_command"                    -> VS Code and every
        //     Linux/macOS host. Fourth survival of the PowerShell assumption.
        //   . SlashHintRun     "run a PowerShell command"                   -> every non-Windows host
        //
        // And THREE are legitimate, which is the heart of the rule: HintLanguage, LangAuto and
        // LabelToolCommand live only in the VS window, never in the schema or the catalogue. The
        // criterion is therefore not "this text says Visual Studio" but "is this text SERVED TO
        // BOTH". A rule on the word alone would redden three correct strings -- and a rule that is
        // wrong is a rule people learn to disarm.
        var schema  = CodeOnly(Path.Combine(RepoRoot(), "Inferpal.Core", "Services", "Presentation", "SettingsSchema.cs"));
        var catalog = CodeOnly(Path.Combine(RepoRoot(), "Inferpal.Core", "Services", "SlashCommandRouter.cs"));

        // The resource names the host actually sends: string literals of the settings schema, and
        // the Strings.X of the slash-command catalogue (served through `command/list`).
        var shared = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in Regex.Matches(schema, "\"(Label|Hint|Title|Tab)[A-Za-z0-9]+\""))
            shared.Add(m.Value.Trim('"'));
        foreach (Match m in Regex.Matches(catalog, @"Strings\.(\w+)"))
            shared.Add(m.Groups[1].Value);

        var resxDir = Path.Combine(RepoRoot(), "Inferpal.Core", "Localization");
        var files   = Directory.GetFiles(resxDir, "Strings*.resx");

        var offenders = new List<string>();
        var scanned   = 0;
        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            foreach (Match m in Regex.Matches(text, @"<data name=""(\w+)""[^>]*><value>(.*?)</value>",
                                              RegexOptions.Singleline))
            {
                if (!shared.Contains(m.Groups[1].Value)) continue;
                scanned++;
                foreach (var fact in VariableFacts)
                    if (m.Groups[2].Value.Contains(fact, StringComparison.OrdinalIgnoreCase))
                        offenders.Add($"{Path.GetFileName(file)} / {m.Groups[1].Value} : '{fact}'");
            }
        }

        // Two witnesses, because two things can break silently: collecting the shared names (it
        // finds none any more) and reading the .resx (it reads no values any more).
        Assert.True(shared.Count >= 40,
            $"Only {shared.Count} shared resource name(s) collected: the scan judges nothing.");
        Assert.True(scanned >= 400,
            $"Only {scanned} shared value(s) read across {files.Length} file(s): the scan judges nothing.");

        Assert.True(offenders.Count == 0,
            "A text served to BOTH front-ends names an editor or a shell. `settings/strings` and "
            + "`command/list` render these strings verbatim in VS Code, and the shell is resolved "
            + "per machine, so the sentence is false for the other half of the users. Reword in "
            + "neutral terms ('the editor', 'shell') rather than naming ours:"
            + Environment.NewLine + "  " + string.Join(Environment.NewLine + "  ", offenders));
    }

    // ── 10. A model keyword is read through Keyword, never any other way ──────

    [Fact]
    public void ModelKeywords_AreReadThroughToolArgsKeyword()
    {
        // The "read model arguments without trusting them" rule closes the argument reads that
        // THROW. This one closes the other half: the reads that return a WRONG ANSWER, with no
        // error and no trace.
        //
        // Measured 2026-09-09: nine sites read a keyword written by the model, each deciding its
        // own normalisation - four trimmed and lower-cased, one lower-cased only, three did
        // neither. And ToolArgs.Keyword, written for exactly this and whose comment claimed "the
        // shape every action/mode uses", had ONE caller in the whole repository: its own unit test.
        //
        // What the three unnormalised sites cost - never an exception, always an answer the model
        // has no way to doubt:
        //   . direction: "Callers"  -> trace_dependency rendered a report with NEITHER the Callers
        //                              section NOR the Callees one. The model concludes "no caller".
        //   . bridges:   " all"     -> trace_nexus walked the WHOLE workspace to conclude there are
        //                              no bridges between the languages.
        //   . mode:      "Replace"  -> update_memory APPENDED instead of overwriting, in the file
        //                              re-injected into every later session's system prompt.
        //
        // The criterion is a PROPERTY, not a list of argument names: "the code compares this value
        // against a literal" - so a mode/action/direction added tomorrow inherits the rule without
        // anyone remembering to enrol it.
        //
        // What it does NOT see, said rather than implied: the comparison must live in the SAME
        // method as the read. Two of the nine compare elsewhere - run_command hands its `action` to
        // HandleAction (a parameter there), apply_diff/apply_edits hand their `occurrence` to
        // ApplyDiffMatcher (another file, which normalises on its own). Following those would need
        // a semantic model and would make the rule brittle; it judges 7, and a site moved out of
        // its reach drops out of the counter - which is what the witness watches.
        string[] readers = ["TryGetProperty", "Str", "Trimmed", "Keyword"];

        var keywordArgs = 0;
        var offenders   = new List<string>();

        // We look for the CALL and the LITERAL, never the word: node.ToString() also renders
        // comments, which is both a false green and a false red waiting to happen.
        static bool ReadsAnArgument(SyntaxNode init, string[] readers, out bool viaKeyword)
        {
            var calls = init.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>()
                .Select(i => (i.Expression as MemberAccessExpressionSyntax)?.Name.Identifier.ValueText)
                .Where(n => n is not null)
                .ToList();
            viaKeyword = calls.Contains("Keyword");
            return calls.Any(n => readers.Contains(n!));
        }

        static bool ComparedToALiteral(SyntaxNode scope, string name)
        {
            static bool IsString(SyntaxNode? n) =>
                n is LiteralExpressionSyntax l && l.IsKind(SyntaxKind.StringLiteralExpression);
            static bool Is(SyntaxNode? n, string name) =>
                n is IdentifierNameSyntax id && id.Identifier.ValueText == name;

            foreach (var node in scope.DescendantNodes())
            {
                switch (node)
                {
                    // `x switch { "a" => ...` and `switch (x) { case "a":`
                    case SwitchExpressionSyntax se when Is(se.GoverningExpression, name):
                    case SwitchStatementSyntax ss when Is(ss.Expression, name):
                        if (node.DescendantNodes().Any(IsString)) return true;
                        break;

                    // `x is "a" or "b"`
                    case IsPatternExpressionSyntax ip when Is(ip.Expression, name):
                        if (ip.Pattern.DescendantNodesAndSelf().Any(IsString)) return true;
                        break;

                    // `x == "a"` / `x != "a"`
                    case BinaryExpressionSyntax be
                        when be.IsKind(SyntaxKind.EqualsExpression) || be.IsKind(SyntaxKind.NotEqualsExpression):
                        if ((Is(be.Left, name) && IsString(be.Right)) || (Is(be.Right, name) && IsString(be.Left)))
                            return true;
                        break;

                    // `x.Equals("a")` - Contains/StartsWith are deliberately out: looking for a
                    // substring is not choosing from a closed set.
                    case InvocationExpressionSyntax inv
                        when inv.Expression is MemberAccessExpressionSyntax m
                          && m.Name.Identifier.ValueText == "Equals" && Is(m.Expression, name):
                        if (inv.ArgumentList.Arguments.Any(a => IsString(a.Expression))) return true;
                        break;
                }
            }
            return false;
        }

        foreach (var file in ToolsSources().Where(f => Path.GetFileName(f) != "ToolArgs.cs"))
        {
            var root = CSharpSyntaxTree.ParseText(File.ReadAllText(file), path: file).GetRoot();

            foreach (var scope in root.DescendantNodes()
                                      .Where(n => n is MethodDeclarationSyntax or LocalFunctionStatementSyntax))
            {
                // The two ways of naming what was just read: `var x = args....` and
                // `if (args.... is { } x)`. Knowing only the first would leave a hole the very
                // shape of this defect can come back through.
                var declared = new List<(string Name, SyntaxNode Init, int Line)>();

                foreach (var v in scope.DescendantNodes().OfType<VariableDeclaratorSyntax>())
                    if (v.Initializer is { } init)
                        declared.Add((v.Identifier.ValueText, init.Value,
                                      init.GetLocation().GetLineSpan().StartLinePosition.Line + 1));

                foreach (var ip in scope.DescendantNodes().OfType<IsPatternExpressionSyntax>())
                    if (ip.Pattern.DescendantNodesAndSelf().OfType<SingleVariableDesignationSyntax>().FirstOrDefault()
                        is { } d)
                        declared.Add((d.Identifier.ValueText, ip.Expression,
                                      ip.GetLocation().GetLineSpan().StartLinePosition.Line + 1));

                foreach (var (name, init, line) in declared)
                {
                    if (!ReadsAnArgument(init, readers, out var viaKeyword)) continue;
                    if (!ComparedToALiteral(scope, name)) continue;

                    keywordArgs++;
                    if (!viaKeyword)
                        offenders.Add($"{Rel(file)}({line}) : '{name}' compared against literals "
                                    + "without going through ToolArgs.Keyword");
                }
            }
        }

        // The witness. It covers BOTH halves of the scan at once, because the counter is their
        // intersection: a broken read detector and a broken comparison detector both return zero,
        // and a zero with no witness would be green.
        Assert.True(keywordArgs >= 6,
            $"Only {keywordArgs} model keyword(s) found: the rule no longer judges anything "
            + "(argument read or comparison against a literal - the counter is their intersection).");

        Assert.True(offenders.Count == 0,
            "A keyword written by the MODEL is read without normalisation. The code compares this "
            + "value against literals: 'Callers', 'Replace' or a value with spaces around it match "
            + "none of them, and the tool then returns a wrong answer WITH NO ERROR - an empty "
            + "report, or a write different from the one asked for. Go through ToolArgs.Keyword:"
            + Environment.NewLine + "  " + string.Join(Environment.NewLine + "  ", offenders));
    }

    // ── 12. A solution is looked up by its EXTENSION, never by `*.sln` ────

    [Fact]
    public void ASolutionIsNeverLookedUpByThe_sln_Pattern()
    {
        // Issue #9, measured 2026-09-10. `Directory.GetFiles(dir, "*.sln")` OFTEN returns `.slnx`
        // files as well — through 8.3 short-name matching, which is configured PER VOLUME. The same
        // code found the solution on the maintainer's system volume and not on the reporter's
        // `G:\`: wrong workspace root, `read_file` refusing the project's own paths, and
        // "No .sln file found — cannot find .inferpal/context.md".
        //
        // ⚠ The rule exists because the first pass fixed the SIX Core sites and left the TWO in
        // the VS adapter — including the one that renders exactly the message above. A pattern
        // spread over two projects is not held in one's head; SolutionFiles is the funnel.
        var offenders = new List<string>();
        var sites     = 0;

        foreach (var file in CoreSources("Services").Concat(ViewModelSources())
                                                    .Concat(ProjectSources("Inferpal.Host"))
                                                    .Concat(ProjectSources("Inferpal.InProc")))
        {
            // The funnel class is the only place allowed to name the format, and its text documents
            // the trap precisely: it exempts itself, by name.
            if (Path.GetFileName(file).Equals("SolutionFiles.cs", StringComparison.OrdinalIgnoreCase))
                continue;

            var source = CodeOnly(file);

            // A file that names BOTH formats has done the work: it is looking for project markers,
            // not for "the" solution, and the list is explicit. What the rule is after is the
            // pattern that DECIDES ALONE — the one that lets 8.3 answer.
            if (source.Contains("\"*.slnx\"", StringComparison.Ordinal)) continue;

            // Witness: the rule is only worth something if it actually sees file lookups.
            sites += System.Text.RegularExpressions.Regex.Matches(source, @"(?:GetFiles|EnumerateFiles)\s*\(").Count;

            foreach (System.Text.RegularExpressions.Match m in
                     System.Text.RegularExpressions.Regex.Matches(source, "\"\\*\\.sln\""))
                offenders.Add($"{Rel(file)}({source[..m.Index].Count(c => c == '\n') + 1}) : \"*.sln\"");
        }

        Assert.True(sites >= 20,
            $"Only {sites} file lookup(s) found — the rule no longer measures anything.");

        Assert.True(offenders.Count == 0,
            "A solution looked up with the « *.sln » pattern gives an answer that depends on the "
            + "volume (8.3 short names): it finds .slnx files on one machine and not on another. "
            + "Go through Services/SolutionFiles, which filters on the extension. Sites:"
            + Environment.NewLine + "  " + string.Join(Environment.NewLine + "  ", offenders));
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
    // ── 25. Replacing the conversation resets its counters ────────────────────

    [Fact]
    public void ReplacingTheConversation_ResetsTheTurnAccounting()
    {
        // The counters describe the conversation on screen: the session token total goes into the
        // header AND into the exported conversation, and the previous turn's prompt size is what
        // ContextManager decides compaction on. Kept from one conversation to the next, they
        // describe the one just left - so either a short conversation just opened gets compacted
        // (turns thrown away for nothing), or a long one is left unbounded and the backend cuts off
        // its head in silence.
        //
        // Only /clear did it; restoring did not. The VS Code host holds the property since it
        // existed (HostSession.History zeroes LastPromptTokens on assignment): it was the MAIN
        // front-end that lacked it.
        //
        // The subject is not a list of names: it is the site that REPLACES the conversation
        // (Messages.Clear()), the same structural signature as rule 6.
        var offenders = new List<string>();
        var seen      = 0;

        foreach (var file in ViewModelSources())
        {
            var root = CSharpSyntaxTree.ParseText(File.ReadAllText(file), path: file).GetRoot();

            foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
            {
                // The CALLS, not the text: the comment documenting the rule mentions
                // Messages.Clear() and ResetTurnAccounting (rule 6, verified by breaking the site).
                var calls = method.DescendantNodes().OfType<InvocationExpressionSyntax>().ToList();

                var replaces = calls.Any(i =>
                    i.Expression is MemberAccessExpressionSyntax
                    {
                        Name.Identifier.Text: "Clear",
                        Expression: IdentifierNameSyntax { Identifier.Text: "Messages" },
                    });
                if (!replaces) continue;
                seen++;

                if (calls.Any(i => i.Expression is IdentifierNameSyntax { Identifier.Text: "ResetTurnAccounting" }))
                    continue;

                offenders.Add($"{Rel(file)} : {method.Identifier.Text}");
            }
        }

        // Witness: with no site replacing the conversation the rule is green while measuring
        // nothing - this repo's failure mode. There are two: /clear and restoring.
        Assert.True(seen >= 2, $"Only {seen} replacement site(s) read -- the rule no longer measures anything.");

        Assert.True(offenders.Count == 0,
            "These methods replace the conversation without resetting its counters: the token total "
            + "shown and exported, and the measurement the pre-send context check decides on, then "
            + "describe the previous conversation. Call ResetTurnAccounting():"
            + Environment.NewLine + "  " + string.Join(Environment.NewLine + "  ", offenders));
    }

    // ── 13. A property bound to SelectedItem is declared nullable ─────────────

    [Fact]
    public void SelectedItemBoundProperties_AreDeclaredNullable()
    {
        // The Selector writes null into the bound property as soon as the selected item leaves its
        // collection. Declared `string`, the property promises the compiler what the binding does
        // not keep: `agentModel.Trim()` compiled without a warning and threw a
        // NullReferenceException on every Save. Declared `string?`, every unguarded read becomes an
        // error of the Release build. The compiler holds the SITES; this rule only holds the
        // DECLARATION, without which it sees nothing.
        //
        // ⚠ The list comes from the XAML: it is the binding that makes the property nullable, not
        // its name.
        var xamlDir = Path.Combine(RepoRoot(), "Inferpal", "ToolWindow");
        var selected = new Regex(@"SelectedItem\s*=\s*""\{Binding\s+([A-Za-z0-9_]+)");
        var bound = new HashSet<string>(StringComparer.Ordinal);
        foreach (var xaml in Directory.EnumerateFiles(xamlDir, "*.xaml", SearchOption.TopDirectoryOnly))
            foreach (Match m in selected.Matches(File.ReadAllText(xaml)))
                bound.Add(m.Groups[1].Value);

        // Witness: the derivation must find at least the model lists.
        Assert.True(bound.Count >= 8,
            $"Only {bound.Count} SelectedItem-bound property(ies) derived from the XAML -- the derivation is broken.");

        var declared = new HashSet<string>(StringComparer.Ordinal);
        var offenders = new List<string>();
        foreach (var file in ViewModelSources())
        {
            var root = CSharpSyntaxTree.ParseText(File.ReadAllText(file), path: file).GetRoot();
            foreach (var property in root.DescendantNodes().OfType<PropertyDeclarationSyntax>())
            {
                var name = property.Identifier.ValueText;
                if (!bound.Contains(name)) continue;
                declared.Add(name);
                if (property.Type is NullableTypeSyntax) continue;

                var line = property.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                offenders.Add($"{Rel(file)}({line}) : {property.Type} {name}");
            }
        }

        // Second witness: every binding finds its property. A property renamed on one side only
        // would otherwise leave the rule without anything turning red.
        var missing = bound.Except(declared).ToList();
        Assert.True(missing.Count == 0,
            "Bound to SelectedItem in the XAML, not found in the view models: " + string.Join(", ", missing));

        Assert.True(offenders.Count == 0,
            "A property bound to SelectedItem is declared non-nullable: the Selector writes null into it "
            + "when the item leaves its list, and a read such as `.Trim()` then throws an exception the "
            + "compiler could not report. Declare it nullable. Sites:"
            + Environment.NewLine + "  " + string.Join(Environment.NewLine + "  ", offenders));
    }
}
