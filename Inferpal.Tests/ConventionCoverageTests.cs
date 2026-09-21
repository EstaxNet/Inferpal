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
            var source = CodeOnly(file);
            Assert.False(
                System.Text.RegularExpressions.Regex.IsMatch(source, @"(?<![\w.])File\.WriteAllText(Async)?\s*\("),
                $"{Rel(file)} writes a text file directly — File.WriteAllText emits UTF-8 with no " +
                "BOM whatever the target was (a VS BOM is stripped, UTF-16 transcoded in silence). " +
                "Use SafeFileWriter.WritePreservingAsync, or add a justified exemption here.");
        }
    }

    // ── 2. RegexBudget sous Services\Tools ────────────────────────────────────

    [Fact]
    public void ServiceRegexes_CarryAMatchTimeout()
    {
        // Two fixes from the post-1.6.1 review, and the second one is the lesson.
        //
        // SCOPE. The rule covered only Services\Tools + Services\Docs, on the grounds that this is
        // where untrusted content enters. But "untrusted" does not follow the folder split: the
        // MODEL's output is untrusted, and it arrives in Services\Execution (the subject of an
        // approval), Services\Agent (the inline call parser) and Services\Commands (the runner's
        // output). The scan therefore covers all of Services\**.
        //
        // TOOL. The scan itself was a regex, and it had the blind spot that matters: it recognised
        // `new Regex(…)` and `Regex Foo = new(…)`, never a `new(…)` element of a COLLECTION
        // (`private static readonly Regex[] HardDeny = [ new(…), … ]`). That is exactly the shape
        // of PermissionPolicy's two pattern sets — the denylist and the "opaque execution" tier —
        // which match the model's output on the approval path, and therefore had NO budget at all:
        // measured, 49 s on a 64 KB subject. A guard written in the same language as the defect it
        // hunts inherits its blind spots; this one now reads a syntax tree, where "a Regex is built
        // here" is a question with an exact answer.
        // A scan reports ALL its sites at once: failing on the first makes the list appear one run
        // at a time, and that is what turns a repair into a series of round trips.
        var offenders = new List<string>();
        foreach (var file in ServicesSources())
        {
            var root = CSharpSyntaxTree.ParseText(File.ReadAllText(file), path: file).GetRoot();

            foreach (var site in RegexSites(root))
                if (!site.Arguments.Any(a => a.Contains("RegexBudget") || a.Contains("Timeout")))
                    offenders.Add($"{Rel(file)}({site.Line}) : {site.Snippet}");
        }

        Assert.True(offenders.Count == 0,
            "These tools resolve a path against the process's working directory, which in Visual "
            + "Studio is not the project. Pass the base to PathSanitizer.Sanitize(path, root). Sites:"
            + Environment.NewLine + "  " + string.Join(Environment.NewLine + "  ", offenders));
    }

    /// <summary>
    /// Every site that builds a <c>Regex</c> or calls a static <c>Regex</c> method under
    /// <paramref name="root"/>. Three shapes, the third being the one that was missing:
    /// <c>new Regex(…)</c>, <c>Regex.IsMatch(…)</c>, and an implicit <c>new(…)</c> whose target
    /// type is declared <c>Regex</c> / <c>Regex[]</c> / a collection of <c>Regex</c>.
    /// </summary>
    private static IEnumerable<(int Line, string Snippet, IReadOnlyList<string> Arguments)> RegexSites(SyntaxNode root)
    {
        // Regex.Escape/Unescape match nothing: no budget to carry.
        string[] matching = ["IsMatch", "Match", "Matches", "Replace", "Split", "Count", "EnumerateMatches"];

        foreach (var node in root.DescendantNodes())
        {
            IReadOnlyList<string>? args = node switch
            {
                ObjectCreationExpressionSyntax oc when TypeIsRegex(oc.Type)
                    => ArgumentTexts(oc.ArgumentList),
                ImplicitObjectCreationExpressionSyntax ioc when DeclaredTypeMentionsRegex(ioc)
                    => ArgumentTexts(ioc.ArgumentList),
                InvocationExpressionSyntax inv
                    when inv.Expression is MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax { Identifier.Text: "Regex" } } ma
                         && matching.Contains(ma.Name.Identifier.Text)
                    => ArgumentTexts(inv.ArgumentList),
                _ => null,
            };
            if (args is null) continue;

            var line = node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            yield return (line, Squash(node.ToString()), args);
        }
    }

    private static bool TypeIsRegex(TypeSyntax type) =>
        type.ToString() is "Regex" or "System.Text.RegularExpressions.Regex";

    /// <summary>
    /// An implicit <c>new(…)</c> does not carry its type: it is read off the enclosing
    /// declaration — field, local variable or property. <c>Regex</c>, <c>Regex[]</c> and
    /// <c>List&lt;Regex&gt;</c> all count, an element of a Regex collection being a Regex.
    /// </summary>
    private static bool DeclaredTypeMentionsRegex(SyntaxNode node)
    {
        for (var current = node.Parent; current is not null; current = current.Parent)
        {
            var declared = current switch
            {
                VariableDeclarationSyntax v => v.Type.ToString(),
                PropertyDeclarationSyntax p => p.Type.ToString(),
                _ => null,
            };
            if (declared is null) continue;
            return declared == "Regex" || declared.StartsWith("Regex[") || declared.Contains("<Regex>");
        }
        return false;
    }

    private static IReadOnlyList<string> ArgumentTexts(BaseArgumentListSyntax? list) =>
        list is null ? [] : list.Arguments.Select(a => a.ToString()).ToList();

    private static string Squash(string text)
    {
        var single = text.ReplaceLineEndings(" ");
        return single.Length <= 90 ? single : single[..90] + "…";
    }

    // ── 3. AtomicFile sous Services\Persistence ───────────────────────────────

    [Fact]
    public void PersistenceStores_WriteThroughAtomicFile()
    {
        string[] exempt = ["AtomicFile.cs"]; // the funnel writes the staging file itself

        foreach (var file in CoreSources(Path.Combine("Services", "Persistence"))
                     .Where(f => !exempt.Contains(Path.GetFileName(f))))
        {
            var source = CodeOnly(file);
            Assert.False(
                System.Text.RegularExpressions.Regex.IsMatch(
                    source, @"(?<![\w.])File\.(WriteAllText|WriteAllBytes)(Async)?\s*\("),
                $"{Rel(file)} writes a store directly — File.WriteAll* truncates the target before " +
                "writing (a crash or a full disk = store lost, and the config file is shared " +
                "VS ↔ VS Code). Use AtomicFile.WriteAllText[Async]/WriteAllBytes.");
        }
    }

    // ── 4. WorkspaceScan sous Services\Tools ──────────────────────────────────

    [Fact]
    public void ServiceRecursiveEnumerations_RouteThroughWorkspaceScan()
    {
        // Whoever enumerates the workspace recursively goes THROUGH WorkspaceScan.EnumerateFiles:
        // mentioning it is no longer enough. Filtering by IsExcludedPath oneself left every site
        // with its own enumeration, hence its own failures — a single unlistable folder (a Docker
        // volume mounted in the repo, a locked junction to a Windows profile) stopped each walk,
        // and each failed differently (a false "directory not found", an exception, a silent
        // partial list). Read without comments: the funnel documents the shape it replaces.
        string[] exempt =
        [
            "WorkspaceScan.cs",        // the funnel itself
            // Deliberately walks the BUILD OUTPUT (bin/) to find the test assemblies there: the
            // workspace exclusions would be a contradiction in terms.
            "TestAssemblyLocator.cs",
        ];

        var offenders = ServicesSources()
            .Where(f => !exempt.Contains(Path.GetFileName(f)))
            .Where(f => CodeOnly(f).Contains("SearchOption.AllDirectories"))
            .Select(Rel)
            .ToList();

        Assert.True(offenders.Count == 0,
            "These tools resolve a path against the process's working directory, which in Visual "
            + "Studio is not the project. Pass the base to PathSanitizer.Sanitize(path, root). Sites:"
            + Environment.NewLine + "  " + string.Join(Environment.NewLine + "  ", offenders));
    }

    // ── 9. What the MODEL reads asserts no variable fact ──────────────────────

    [Fact]
    public void MutatingTools_GoThroughTheApprovalService()
    {
        // The repository's doctrine is written down: "destructive tools → mandatory approval +
        // snapshot". It was applied to a LIST (write_file, apply_diff, apply_edits, delete_file,
        // run_command), not to the property — and three tools that write were not on it:
        // insert_at_cursor and replace_selection (which change the open document, so they also
        // bypassed the permission rules and /undo-run) and update_memory, whose file is re-injected
        // into the system prompt of every later session. The scan asks the property: "does this
        // file have a write sink?" — in which case it must ask too.
        //
        // Write sinks recognised, all verified present in the folder at the time of writing.
        string[] writeSinks =
        [
            "SafeFileWriter.", "File.WriteAllText", "File.WriteAllBytes", "File.Delete",
            "Directory.Delete", "InsertAtCursorAsync", "ReplaceSelectionAsync",
        ];

        string[] exempt =
        [
            "SafeFileWriter.cs",    // the write funnel itself, called by the guarded tools
            "EditorWriteGate.cs",   // the gate: it is the one calling RequestApprovalAsync
            "RestoreFileTool.cs",   // guarded, but the call lives in the body — checked by hand below
        ];

        var offenders = new List<string>();
        foreach (var file in ToolsSources().Where(f => !exempt.Contains(Path.GetFileName(f))))
        {
            var source = CodeOnly(file);
            var sink   = writeSinks.FirstOrDefault(s => source.Contains(s, StringComparison.Ordinal));
            if (sink is null) continue;

            // Either the tool asks itself, or it goes through the shared gate.
            if (source.Contains("RequestApprovalAsync", StringComparison.Ordinal) ||
                source.Contains("EditorWriteGate.", StringComparison.Ordinal)) continue;

            offenders.Add($"{Rel(file)} (writes through '{sink}')");
        }

        Assert.True(offenders.Count == 0,
            "Tools that write without going through IApprovalService — approval is THE boundary " +
            "of the product, and a tool that dodges it also dodges the permission rules, the " +
            "force-prompt on repo-authored content, and /undo-run's snapshot:"
            + Environment.NewLine + "  " + string.Join(Environment.NewLine + "  ", offenders));
    }

    /// <summary>The only file exempt from the scan above must really ask: checked by name.</summary>
    [Fact]
    public void RestoreFileTool_StillAsks()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "Inferpal.Core", "Services", "Tools", "RestoreFileTool.cs"));
        Assert.Contains("RequestApprovalAsync", source, StringComparison.Ordinal);
    }

    // ── 6. Replacing the conversation waits for the turn to end ───────────────

    [Fact]
    public void ReplacingTheConversation_SettlesTheRunningTurnFirst()
    {
        // The host holds its turn slot for this; the Visual Studio VM —
        // the MAIN front-end — did it nowhere. Loading a session or clearing the chat while a turn
        // is running replaces _history under the agent loop: its answer lands in the freshly
        // restored conversation, and the render pass looks for a bubble that Messages.Clear() has
        // already removed (IndexOf = -1 ⇒ insert at -1, caught as an error bubble in the wrong
        // conversation).
        //
        // The VM cannot be instantiated without Visual Studio: the rule is therefore checked on the
        // text, like the script guards. Method granularity.
        var offenders = new List<string>();

        foreach (var file in ViewModelSources())
        {
            var root = CSharpSyntaxTree.ParseText(File.ReadAllText(file), path: file).GetRoot();

            foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
            {
                // The method that DEFINES the restoration is the point of application, not a caller.
                if (method.Identifier.Text is "RestoreConversation" or "SettleCurrentTurnAsync") continue;

                // ⚠ The CALLS, not the text. Written as `body.Contains("SettleCurrentTurnAsync")`,
                // this rule was green on a disarmed site: the COMMENT documenting the call contains
                // the word, and `method.ToString()` carries the trivia. The repository has already
                // paid for this shape twice ("REACHED" contained in "NOT REACHED", and a guard whose
                // comment quoted the pattern it looked for) — verified by breaking the site.
                var calls = method.DescendantNodes().OfType<InvocationExpressionSyntax>()
                    .Select(i => i.Expression switch
                    {
                        MemberAccessExpressionSyntax ma => ma.Name.Identifier.Text,
                        IdentifierNameSyntax id         => id.Identifier.Text,
                        _                               => string.Empty,
                    })
                    .ToList();

                var replaces = calls.Contains("RestoreConversation")
                    || method.DescendantNodes().OfType<InvocationExpressionSyntax>().Any(i =>
                           i.Expression is MemberAccessExpressionSyntax
                           {
                               Name.Identifier.Text: "Clear",
                               Expression: IdentifierNameSyntax { Identifier.Text: "Messages" },
                           });
                if (!replaces) continue;
                if (calls.Contains("SettleCurrentTurnAsync")) continue;

                offenders.Add($"{Rel(file)} : {method.Identifier.Text}");
            }
        }

        Assert.True(offenders.Count == 0,
            "These methods replace the conversation without letting the turn in flight unwind — " +
            "the agent loop holds the OLD list and will write its answer into the new one. " +
            "Call SettleCurrentTurnAsync() first:"
            + Environment.NewLine + "  " + string.Join(Environment.NewLine + "  ", offenders));
    }

    // ── 7. The model's arguments are read without trust ───────────────────────

    [Fact]
    public void ToolArguments_AreReadThroughToolArgs()
    {
        // ToolArgs was created by the review for exactly this: a 7B sends
        // « "top_k": "5" » and omits « query », so GetProperty/GetInt32 throw, and the model reads
        // "The given key was not present in the dictionary." — which names neither the tool nor the
        // argument. The helper was adopted in 2 tools out of 14: the post-1.6.1 review converted the
        // other 12, and this rule is what stops the next one from starting again on the throwing
        // shape.
        //
        // ⚠ And that list left out `.GetString()`: **23 sites**, against
        // ZERO for the two shapes it named. JsonElement.GetString() throws exactly like GetInt32()
        // — InvalidOperationException as soon as the element is not a string, so on « "path": 42 » —
        // and the model then reads "The requested operation requires an element of type 'String',
        // but the target element has type 'Number'", which names neither the tool nor the argument:
        // the very message this rule exists to remove. It is the shape defect this file documents
        // everywhere else: a sound rule, carried by an ENUMERATION whose list leaves out the
        // majority case.
        string[] throwing = ["args.GetProperty(", ".GetInt32()", ".GetBoolean()", ".GetString()"];
        string[] exempt   = ["ToolArgs.cs"];

        var offenders = new List<string>();
        foreach (var file in ToolsSources().Where(f => !exempt.Contains(Path.GetFileName(f))))
        {
            var source = CodeOnly(file);
            foreach (var form in throwing.Where(t => source.Contains(t, StringComparison.Ordinal)))
                offenders.Add($"{Rel(file)} : {form}");
        }

        Assert.True(offenders.Count == 0,
            "Argument reads that throw on a value written by the MODEL — go through ToolArgs "
            + "(args.Str / Trimmed / Keyword / Int / Bool), which degrades instead of throwing:"
            + Environment.NewLine + "  " + string.Join(Environment.NewLine + "  ", offenders));
    }

    // ── 8. A chat bubble is inserted themed ───────────────────────────────────

    [Fact]
    public void ChatBubbles_AreInsertedThroughTheThemingFunnel()
    {
        // Fifteen sites built the item INSIDE the call to Messages.Insert(...), which makes theming
        // impossible: there is no reference to theme. Those bubbles rendered in default colours
        // under a dark theme. The pre-1.6.0 review had repaired four by hand, in ChatTurn only —
        // hence the InsertThemed funnel, and this rule.
        var inline = new System.Text.RegularExpressions.Regex(
            @"Messages\.Insert\([^;]*?ChatMessageItem\.[A-Za-z]+Msg\(",
            System.Text.RegularExpressions.RegexOptions.Singleline);

        var offenders = new List<string>();
        foreach (var file in ViewModelSources())
        {
            var source = CodeOnly(file);
            foreach (System.Text.RegularExpressions.Match m in inline.Matches(source))
                offenders.Add($"{Rel(file)}({source[..m.Index].Count(c => c == '\n') + 1})");
        }

        Assert.True(offenders.Count == 0,
            "Bubbles inserted without going through InsertThemed: built inside the Insert call, "
            + "they cannot be themed and come out in light colours under a dark theme:" + Environment.NewLine + "  " + string.Join(Environment.NewLine + "  ", offenders));
    }

    // ── 9. Text shown to the user goes through Strings ────────────────────────

    [Fact]
    public void UserFacingChatText_GoesThroughStrings()
    {
        // Measured (review of the VS adapter). §17 had localized the PlanModeOn/
        // PlanModeOff pair and left the step-mode one, three lines above, in literal English — in
        // the VM AND in the host, where the neighbouring `/plan` returns Strings.PlanModeOn from the
        // same switch, under a comment claiming it was "deliberately English". Same pattern twice
        // more: the /fix-build loop had its two END messages localized and its two PROGRESS labels
        // hard-coded, and a file dialog passed its Title through Strings and not its Filter, one
        // line below. Nine users out of ten read English, and nothing anywhere said so — while the
        // VS Code front-end carries this rule in the header of its l10n.ts.
        (string Marker, int MessageArg)[] sinks =
        [
            ("ShowInfoAsync(",                 0),
            ("ChatMessageItem.AssistantMsg(",  0),
            ("ChatMessageItem.UserMsg(",       0),
            ("ChatMessageItem.StatusMsg(",     0),
            ("ChatMessageItem.ToolMsg(",       1),   // arg 0 = tool name: an identifier, not prose
            ("SlashCommandResult(",            1),   // arg 0 = ok, arg 2 = effects: same
        ];

        // NAMED exemptions, with their reason — that is the shape this repository gives a decision,
        // as opposed to a pattern that merely fails to see the site.
        string[] exempt =
        [
            // /test-build-banner: a diagnostic command whose audience is us. It names a signal file
            // and the in-proc -> OOP path; translating it would help nobody.
            "Test build-failure signal sent",
            // A git command name, not prose ("commit" is the four-letter word).
            "git commit",
            // The product name — the tool window title is not translated.
            "Inferpal",
        ];

        // A three-letter word is enough: the "Fix {0}" label of the /fix-build loop is one of them,
        // and the threshold of 4 let it through. Measure before choosing: going from 4 to 3 adds NO
        // false positive on the current tree, so nothing pays for that gain.
        // What stays under the threshold — "**", "…", "\n```" — is not prose.
        var word = new System.Text.RegularExpressions.Regex("[A-Za-z]{3,}");
        var sources = ViewModelSources()
            .Append(Path.Combine(RepoRoot(), "Inferpal.Host", "HostSlashCommands.cs"))
            .ToList();

        // Witness: the rule is worth nothing unless it really sees sinks. Renaming a method would
        // turn it green while measuring nothing — that is this file's failure mode.
        var sites = 0;
        var offenders = new List<string>();

        foreach (var file in sources)
        {
            Assert.True(File.Exists(file), $"Source not found, the rule guards nothing any more: {file}");
            var source = CodeOnly(file);

            foreach (var (marker, messageArg) in sinks)
            {
                var from = 0;
                while (true)
                {
                    var at = source.IndexOf(marker, from, StringComparison.Ordinal);
                    if (at < 0) break;
                    from = at + marker.Length;
                    sites++;

                    var args = BalancedArguments(source, at + marker.Length - 1);
                    if (args is null) continue;
                    var message = messageArg == 0 ? args : NthArgument(args, messageArg);

                    foreach (var literal in TopLevelLiterals(message))
                    {
                        var text = LiteralText(literal);
                        if (!word.IsMatch(text)) continue;
                        if (exempt.Any(e => literal.Contains(e, StringComparison.Ordinal))) continue;
                        offenders.Add($"{Rel(file)}({source[..at].Count(c => c == '\n') + 1}) : "
                                      + marker + " ← \"" + literal[..Math.Min(60, literal.Length)] + "\"");
                        break;
                    }
                }
            }

            // CurrentStep and a file dialog's properties: ASSIGNMENTS, not calls. CurrentStep often
            // lives in a lambda, so the statement stops at the ';' of level 0 OR at the parenthesis
            // closing the lambda.
            //
            // ⚠ Title/Filter were added afterwards: the first version of this rule looked only at
            // CHAT sinks, and the export dialog kept "Export Conversation" and its filter in hard
            // English, two lines from the sites it had just had corrected. A text sink is anything
            // the user reads.
            foreach (System.Text.RegularExpressions.Match m in
                     System.Text.RegularExpressions.Regex.Matches(source, @"(CurrentStep|Title|Filter)\s*="))
            {
                sites++;
                foreach (var literal in TopLevelLiterals(AssignedExpression(source, m.Index + m.Length)))
                {
                    var text = LiteralText(literal);
                    if (!word.IsMatch(text)) continue;
                    if (exempt.Any(e => literal.Contains(e, StringComparison.Ordinal))) continue;
                    offenders.Add($"{Rel(file)}({source[..m.Index].Count(c => c == '\n') + 1}) : "
                                  + m.Groups[1].Value + " ← \"" + literal[..Math.Min(60, literal.Length)] + "\"");
                    break;
                }
            }
        }

        Assert.True(sites >= 40, $"Only {sites} text sink(s) found — the rule measures nothing any more.");

        Assert.True(offenders.Count == 0,
            "Text shown to the user, hard-coded: it will never be translated, and nine users out "
            + "of ten will read it in English with nothing saying so. Add a key to the 10 .resx "
            + "files plus a property in Strings.cs:"
            + Environment.NewLine + "  " + string.Join(Environment.NewLine + "  ", offenders));
    }

    // ── 10. A WPF dialog goes through the shared STA thread ───────────────────

    /// <summary>
    /// No site under <c>Inferpal\ToolWindow</c> spins up its own thread to open a dialog.
    /// </summary>
    /// <remarks>
    /// Four sites did it by hand and did not do it the same way: two had
    /// <b>no</b> <c>try</c> at all, so an exception from <c>ShowDialog</c> left their
    /// <c>TaskCompletionSource</c> pending — the command waited forever, without a word — and on a
    /// <b>foreground</b> thread an unhandled exception terminates the process. The third did
    /// everything right, which proves the shape was known; none set <c>IsBackground</c>, although
    /// the two inline-edit windows have always done so. <c>StaDialog.RunAsync</c> is that funnel,
    /// and the rule stops the next site from starting over.
    /// </remarks>
    [Fact]
    public void WpfDialogs_GoThroughTheSharedStaThread()
    {
        // The funnel itself, and the two inline-edit windows, which own their thread for another
        // reason: they host an entire WPF window there, not a modal dialog.
        string[] exempt = ["StaDialog.cs", "InlineEditInputWindow.cs", "ClipboardHelper.cs"];

        var sites = 0;
        var offenders = new List<string>();
        foreach (var file in ViewModelSources().Where(f => !exempt.Contains(Path.GetFileName(f))))
        {
            var source = CodeOnly(file);
            foreach (System.Text.RegularExpressions.Match m in
                     System.Text.RegularExpressions.Regex.Matches(source, @"new\s+(System\.Threading\.)?Thread\s*\("))
            {
                sites++;
                offenders.Add($"{Rel(file)}({source[..m.Index].Count(c => c == '\n') + 1})");
            }
        }

        // Witness: the funnel exists and really does spin up a thread — otherwise the rule forbids
        // a shape with no replacement, and its green means nothing any more.
        var funnel = CodeOnly(Path.Combine(RepoRoot(), "Inferpal", "ToolWindow", "StaDialog.cs"));
        Assert.Contains("new Thread(", funnel, StringComparison.Ordinal);
        Assert.Contains("IsBackground", funnel, StringComparison.Ordinal);
        Assert.Contains("TrySetResult(default)", funnel, StringComparison.Ordinal);

        Assert.True(offenders.Count == 0,
            "A thread built by hand for a dialog: without StaDialog's try/finally, an exception "
            + "out of ShowDialog leaves the task hanging (the command waits forever) and, on a "
            + "foreground thread, takes the whole process down. Go through StaDialog.RunAsync:"
            + Environment.NewLine + "  " + string.Join(Environment.NewLine + "  ", offenders));
        Assert.Equal(0, sites);

        // ⚠ The exemption is from the FUNNEL, not from the property the funnel carries. Owning
        // your thread for another reason is a reason to build it yourself, never a reason to
        // build it in the FOREGROUND — and a foreground thread blocked on the clipboard, or on a
        // window that never closed, keeps Visual Studio from exiting: the user closes the IDE and
        // the process stays. `ClipboardHelper` held this exemption and had missed exactly that.
        // ⚠ Resolved from the adapter tree, not from ViewModelSources: two of the three live under
        // Commands/, so reading them through the view-model enumerator finds nothing and the rule
        // would pass without checking anyone.
        var owners = Directory
            .EnumerateFiles(Path.Combine(RepoRoot(), "Inferpal"), "*.cs", SearchOption.AllDirectories)
            .Where(f => exempt.Contains(Path.GetFileName(f)))
            .ToList();
        Assert.Equal(exempt.Length, owners.Count);   // witness: every exempted file was found

        foreach (var owner in owners)
        {
            var name = Path.GetFileName(owner);
            var text = CodeOnly(owner);
            Assert.Contains("new Thread(", text, StringComparison.Ordinal);   // witness: it owns one
            Assert.True(text.Contains("IsBackground", StringComparison.Ordinal),
                $"{name} owns its STA thread and does not mark it IsBackground: a thread of its own "
                + "is not a thread in the foreground, and the IDE will not exit while it is blocked.");
        }
    }

    // ── 11. A connection badge does not conclude from a status code ───────────

    [Fact]
    public void CheckConnection_ConfirmsThePayload_NotJustTheStatusCode()
    {
        // an LM Studio behind a reverse proxy answers HTTP 200 on
        // /api/tags — as on /anything — with the body
        // {"error":"Unexpected endpoint or method. (GET /api/tags)"}. An Ollama-typed client
        // pointed at it showed a GREEN badge while no turn could possibly complete.
        //
        // ProviderProbe has required the discriminating property since it existed and wrote the rule
        // down in black and white ("a bare status code is not enough"): the repository kept in code
        // what its own comment forbade — the pattern CLAUDE.md already names about test-suite
        // parsers ("exit 0 = green"). The rule therefore carries the PROPERTY — a connection probe
        // looks at what the server answered — and not today's two implementations.
        var sites = 0;
        var offenders = new List<string>();

        foreach (var file in CoreSources(Path.Combine("Services", "Inference")))
        {
            var root = CSharpSyntaxTree.ParseText(File.ReadAllText(file), path: file).GetRoot();

            foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>()
                         .Where(m => m.Identifier.ValueText == "CheckConnectionAsync"))
            {
                // The base's abstract declaration has no body: nothing to probe.
                if (method.Body is null && method.ExpressionBody is null) continue;

                sites++;
                var confirms = method.DescendantNodes().OfType<IdentifierNameSyntax>()
                    .Any(n => n.Identifier.ValueText == "ConfirmsBackendPayload");
                if (!confirms)
                    offenders.Add($"{Rel(file)}({method.GetLocation().GetLineSpan().StartLinePosition.Line + 1})");
            }
        }

        // Witness: the rule is worth nothing unless probes remain to be judged. Zero sites — a
        // renamed method, a moved file — would turn it green while looking at nothing.
        Assert.True(sites >= 2,
            $"The scan found only {sites} implementation(s) of CheckConnectionAsync under " +
            "Services\\Inference: the rule checks nothing any more.");

        Assert.True(offenders.Count == 0,
            "A connection probe concluding on the status code alone. A server — or a reverse "
            + "proxy — answering 200 on every route then gives a green badge while no chat turn "
            + "can complete. Go through ConfirmsBackendPayload, which requires the root property "
            + "signing the backend and traces what was observed:"
            + Environment.NewLine + "  " + string.Join(Environment.NewLine + "  ", offenders));
    }

    // ── 12. A tool that can degrade SAYS so when it returns nothing ───────────

    [Fact]
    public void ASearchToolThatEmbeds_ReportsWhenOnlyItsKeywordHalfRan()
    {
        // ProjectIndexService.SearchAsync skips its vector half when the embedding is null — model
        // not downloaded, backend off, circuit breaker open. The two tools then answered "No
        // relevant code found for …": a flat negative, which the model reads as "this code does not
        // exist". A missing capability rendered as a result.
        //
        // The rule carries the PROPERTY — a tool that embeds a query can lose its semantic half, so
        // it must be able to say so — and not today's two files. A third search tool will inherit it.
        var sites = 0;
        var offenders = new List<string>();

        foreach (var file in ToolsSources())
        {
            var source = CodeOnly(file);
            if (!source.Contains("GetEmbeddingAsync", StringComparison.Ordinal)) continue;

            sites++;
            if (!source.Contains("SearchDegradation", StringComparison.Ordinal))
                offenders.Add(Rel(file));
        }

        // Witness: the rule is worth nothing unless tools that embed remain. Zero sites — a renamed
        // method, a moved file — would turn it green while looking at nothing.
        Assert.True(sites >= 2,
            $"The scan found only {sites} tool(s) calling GetEmbeddingAsync under Services/Tools: " +
            "the rule checks nothing any more.");

        Assert.True(offenders.Count == 0,
            "A search tool that embeds a query without ever saying when only its lexical half "
            + "ran. Pass the nothing-found through SearchDegradation.Explain, which keeps the three "
            + "states apart (full search / semantics turned off by the user / embedding "
            + "unavailable):"
            + Environment.NewLine + "  " + string.Join(Environment.NewLine + "  ", offenders));
    }

    // ── 21. What the MODEL reads asserts no variable fact ─────────────────────

    /// <summary>The two facts the product knows to be variable, and that its text asserted.</summary>
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
        // ⚠ The rule reads the Description and Parameters PROPERTIES, not the file: `run_command`
        // must be able to name both dialects in the code that CHOOSES (ShellDialect.PowerShell,
        // "bash"), which is the whole point. What is forbidden is writing it hard where the model
        // reads it.
        var offenders   = new List<string>();
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
            "Text read by the model that names an editor or a shell. The same Core serves both "
            + "front-ends and three operating systems: state the fact instead of asserting it (the "
            + "dialect comes from ShellLauncher.Resolve, the editor is declared by the front-end):"
            + Environment.NewLine + "  " + string.Join(Environment.NewLine + "  ", offenders));
    }

    [Fact]
    public void TheBaseSystemPrompt_AssertsNeitherEditorNorShell()
    {
        // The SAME resource is served to both front-ends: Inferpal.Host passes it to VS Code as is.
        // "integrated into Visual Studio 2026" was therefore false for every VS Code user, in all
        // ten languages, and "PowerShell commands" false for every Linux/macOS machine. Both facts
        // are now built at run time by SystemPromptBuilder.EnvironmentFacts, where they are true.
        var dir      = Path.Combine(RepoRoot(), "Inferpal.Core", "Localization");
        var files    = Directory.EnumerateFiles(dir, "Strings*.resx").ToList();
        var checkedd = 0;
        var offenders = new List<string>();

        foreach (var file in files)
        {
            var m = Regex.Match(File.ReadAllText(file),
                                @"<data name=""SystemPrompt""[^>]*>\s*<value>(.*?)</value>",
                                RegexOptions.Singleline);
            if (!m.Success) continue;

            checkedd++;
            foreach (var fact in VariableFacts)
                if (m.Groups[1].Value.Contains(fact, StringComparison.Ordinal))
                    offenders.Add($"{Path.GetFileName(file)} : « {fact} »");
        }

        // Witness: all ten languages, otherwise a broken pattern would green the rule on zero files.
        Assert.True(checkedd == 10,
            $"The scan read {checkedd} SystemPrompt value(s) out of the 10 expected in {dir}: the rule checks nothing any more.");

        Assert.True(offenders.Count == 0,
            "The base system prompt asserts an editor or a shell. Both facts vary from one "
            + "front-end and one machine to the next: they belong to EnvironmentFacts, not to a "
            + "translation:" + Environment.NewLine + "  " + string.Join(Environment.NewLine + "  ", offenders));
    }

    // ── 23. What the USER reads does not assert the editor either ─────────────

    [Fact]
    public void SharedUiText_NamesNeitherAnEditorNorAShell()
    {
        // The user half of rule 21. That one closes what the MODEL reads; the same Core also serves
        // the text the USER reads, to BOTH front-ends, and nobody saw it.
        //
        // The channel is explicit, and its own comment sells it as a feature: `settings/strings`
        // serves the labels "straight from the same .resx resources as the Visual Studio settings
        // window. The VS Code settings webview renders them VERBATIM, so both editors share the
        // exact same wording in all 10 languages". That is true — and it is exactly why a label
        // naming ONE editor becomes false for the other.
        //
        // out of 731 entries, seven name an editor or a shell. FOUR are
        // served to both front-ends, hence false for one of them:
        //   · LabelLanguage    "(overrides Visual Studio)"                   -> VS Code
        //   · HintProvider     "Takes effect after reloading Visual Studio"  -> VS Code, and it
        //     names a REMEDY IT CANNOT APPLY (lesson of 1.6.6)
        //   · LabelCustomTools "name=powershell_command"                     -> VS Code + every
        //     Linux/macOS host. Fourth survival of the PowerShell assumption after ShellLauncher
        //, UserShellTool (pre-1.6.0) and run_command's description (09/08).
        //   · SlashHintRun     "run a PowerShell command"                    -> every non-Windows host
        //
        // ⚠ And THREE are legitimate, which is the heart of the rule: HintLanguage, LangAuto and
        // LabelToolCommand live only in the VS window (`InferpalSettingsData`), never in the schema
        // nor in the catalogue. The criterion is therefore not "this text names Visual Studio" but
        // "is this text SERVED TO BOTH". A rule on the word alone would redden three correct
        // strings — and a rule that gets it wrong is a rule that gets disarmed.
        var schema  = CodeOnly(Path.Combine(RepoRoot(), "Inferpal.Core", "Services", "Presentation", "SettingsSchema.cs"));
        var catalog = CodeOnly(Path.Combine(RepoRoot(), "Inferpal.Core", "Services", "SlashCommandRouter.cs"));

        // The resource names the host sends: the settings schema's literals, and the Strings.X of
        // the slash command catalogue (served by `command/list`).
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
                        offenders.Add($"{Path.GetFileName(file)} / {m.Groups[1].Value} : « {fact} »");
            }
        }

        // Two witnesses, because two things can break without a sound: collecting the shared names
        // (it finds none any more) and reading the .resx (it reads no values any more).
        Assert.True(shared.Count >= 40,
            $"Only {shared.Count} shared resource name(s) collected: the scan judges nothing.");
        Assert.True(scanned >= 400,
            $"Only {scanned} shared value(s) read in {files.Length} file(s): "
            + "the scan judges nothing any more.");

        Assert.True(offenders.Count == 0,
            "A text served to BOTH front-ends names an editor or a shell. `settings/strings` and "
            + "`command/list` hand those strings to VS Code verbatim, and the shell is resolved per "
            + "machine: the sentence is then false for the other half of the users. Reword it in "
            + "neutral terms (the editor, the shell) rather than naming ours:"
            + Environment.NewLine + "  " + string.Join(Environment.NewLine + "  ", offenders));
    }

    /// <summary>The text a model will read in a property: literals and the fixed parts of
    /// interpolated strings. Identifiers (<c>ShellDialect.PowerShell</c>) are not part of it.</summary>
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

    // ── 16. Clearing a bound collection loses its selection ───────────────────

    [Fact]
    public void TwoWayBoundCollections_AreNeverCleared()
    {
        // A Selector resets its bound property to null when the selected item leaves the
        // collection. ⚠ Re-filling it in the same method does NOT give it back: under Remote UI the
        // null write crosses the boundary and comes back AFTER the re-fill, which re-selects
        // nothing. That is the mechanism of issue #8, where the model list removed then re-added its
        // value in a single pass. Such a collection is updated in place, through
        // SelectionPreservingList.
        //
        // ⚠ The list of collections concerned is DERIVED FROM THE XAML — every element binding both
        // ItemsSource and SelectedItem — and not copied.
        //
        // NAMED exemption: Messages, whose SelectedItem is the scroll anchor (ScrollTarget) — a
        // stateless selection, which the chat repositions itself.
        string[] exempt = ["Messages"];
        var xamlDir = Path.Combine(RepoRoot(), "Inferpal", "ToolWindow");
        var xamls   = Directory.EnumerateFiles(xamlDir, "*.xaml", SearchOption.TopDirectoryOnly).ToList();
        Assert.True(xamls.Count >= 2, $"Only {xamls.Count} XAML file(s) found: the rule reads nothing.");

        var element = new Regex(@"<[A-Za-z][^>]*?>", RegexOptions.Singleline);
        var items   = new Regex(@"ItemsSource\s*=\s*""\{Binding\s+([A-Za-z0-9_]+)");
        var selected = new Regex(@"SelectedItem\s*=\s*""\{Binding\s+[A-Za-z0-9_]+");

        var bound = new HashSet<string>(StringComparer.Ordinal);
        foreach (var xaml in xamls)
        {
            var text = File.ReadAllText(xaml);
            foreach (Match el in element.Matches(text))
            {
                var m = items.Match(el.Value);
                if (m.Success && selected.IsMatch(el.Value)) bound.Add(m.Groups[1].Value);
            }
        }

        // Witness: the derivation must find something, otherwise the rule is green for nothing.
        Assert.True(bound.Count >= 4,
            $"Only {bound.Count} SelectedItem-bound property(ies) derived from the XAML -- the derivation is broken.");

        // Witness for the exemption: an exemption that names nothing bound any more is an open door.
        foreach (var name in exempt)
            Assert.True(bound.Contains(name), $"Exemption {name} no longer names any bound collection.");

        var offenders = new List<string>();
        var exemptClears = 0;
        foreach (var file in ViewModelSources())
        {
            var root = CSharpSyntaxTree.ParseText(File.ReadAllText(file), path: file).GetRoot();

            foreach (var clear in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (clear.Expression is not MemberAccessExpressionSyntax ma
                 || ma.Name.Identifier.ValueText != "Clear"
                 || clear.ArgumentList.Arguments.Count != 0) continue;

                var target = (ma.Expression as IdentifierNameSyntax)?.Identifier.ValueText;
                if (target is null || !bound.Contains(target)) continue;
                if (exempt.Contains(target)) { exemptClears++; continue; }

                var line = clear.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                offenders.Add($"{Rel(file)}({line}) : {target}.Clear()");
            }
        }

        // Witness for the scan: the chat really does clear Messages somewhere (/clear, restore).
        // Zero would mean the scan no longer sees any Clear, and the rule would be green for nothing.
        Assert.True(exemptClears >= 1, "No Messages.Clear() seen: the scan no longer detects Clear() calls.");

        Assert.True(offenders.Count == 0,
            "A collection whose XAML also binds SelectedItem is cleared: the Selector writes null "
            + "back into the bound property, and refilling it right away does not give the selection "
            + "back. Update in place (SelectionPreservingList). Sites:"
            + Environment.NewLine + "  " + string.Join(Environment.NewLine + "  ", offenders));
    }

    // ── 27. A path is resolved against the root it is checked against ─────────

    [Fact]
    public void ToolPaths_ResolveAgainstTheRootTheyAreCheckedAgainst()
    {
        // Sanitize(path) resolves a relative path against the PROCESS's current directory. Under
        // Visual Studio that is not the project: the out-of-process host keeps its start folder.
        // "src/Foo.cs" therefore pointed at another folder — refused as "outside the workspace root"
        // by the tools that check a root, and looked for in the wrong place, silently, by those that
        // do not (run_tests: "No test runner detected" on a solution full of tests). The same call
        // passed under VS Code, whose host starts in the workspace.
        // ⚠ The rule therefore covers EVERY call on an argument, not only those followed by
        // AssertUnderRoot: its first version looked only at those, and three tools were left out.
        var offenders   = new List<string>();
        var rootedCalls = 0;
        foreach (var file in ToolsSources())
        {
            var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(file), path: file).GetRoot();
            foreach (var call in tree.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (call.Expression is not MemberAccessExpressionSyntax
                    {
                        Expression: IdentifierNameSyntax { Identifier.ValueText: "PathSanitizer" },
                        Name.Identifier.ValueText: "Sanitize",
                    }) continue;

                if (call.ArgumentList.Arguments.Count == 2) { rootedCalls++; continue; }

                var line = call.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                offenders.Add($"{Rel(file)}({line}) : {call}");
            }
        }

        // Witness: the file tools really are read, otherwise "no site" means nothing.
        Assert.True(rootedCalls >= 14,
            $"Only {rootedCalls} Sanitize(path, root) call(s) read: the scan measures nothing any more.");

        Assert.True(offenders.Count == 0,
            "These tools resolve a path against the process's working directory, which in Visual "
            + "Studio is not the project. Pass the base to PathSanitizer.Sanitize(path, root). Sites:"
            + Environment.NewLine + "  " + string.Join(Environment.NewLine + "  ", offenders));
    }

    // ── 26. A property bound to SelectedItem is declared nullable ─────────────

    [Fact]
    public void SelectedItemBoundProperties_AreDeclaredNullable()
    {
        // The Selector writes null into the bound property as soon as the selected item leaves its
        // collection. Declared `string`, the property promises the compiler what the binding does
        // not keep: `agentModel.Trim()` compiled without a warning and threw a
        // NullReferenceException on every Save. Declared `string?`, every unguarded read becomes a
        // Release-build error. The compiler holds the SITES; this rule only holds the DECLARATION,
        // without which it sees nothing.
        //
        // ⚠ The list comes from the XAML, as for rule 16: it is the binding that makes the property
        // nullable, not its name.
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
        // would otherwise drop out of the rule with nothing going red.
        var missing = bound.Except(declared).ToList();
        Assert.True(missing.Count == 0,
            "Bound to SelectedItem in the XAML, not found in the view models: " + string.Join(", ", missing));

        Assert.True(offenders.Count == 0,
            "A property bound to SelectedItem is declared non-nullable: the Selector writes null "
            + "into it as soon as the item leaves its list, and a read such as `.Trim()` then throws "
            + "where the compiler could not warn. Declare it nullable. Sites:"
            + Environment.NewLine + "  " + string.Join(Environment.NewLine + "  ", offenders));
    }

    // ── 20. Who yields the GPU, and who must NEVER wait for it ────────────────

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

        // One witness per half: without both, a scan gone blind would go green while judging
        // neither the loops nor the queries.
        Assert.True(loops >= 2, $"Only {loops} in-loop embedding(s) found: the background half judges nothing.");
        Assert.True(oneShots >= 2, $"Only {oneShots} one-shot embedding(s) found: the query half judges nothing.");

        Assert.True(offenders.Count == 0,
            "GPU discipline broken. An embedding LOOP yields before every call "
            + "(GpuScheduler.WaitForChatIdleAsync); a QUERY never yields — it is called from an "
            + "agent run already holding the chat lease, so waiting for the chat to go idle is "
            + "waiting for itself. Sites:"
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
                offenders.Add($"{Rel(file)}({line}) : {assign.Left} = …");
            }
        }

        Assert.True(reads >= 5,
            $"The scan found only {reads} ambient-culture read(s): the rule checks nothing any more.");

        Assert.True(offenders.Count == 0,
            "The ambient culture is written instead of being overridden. This process is not ours "
            + "(devenv, the Extensibility host) and neither is the thread: go through "
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

            if (primitives.Contains(name))              { shapes.Add("primitif"); return true; }
            if (name == "AsyncCommand")                 { shapes.Add("commande"); return true; }
            if (contracts.Contains(name))               { shapes.Add("contrat");  return true; }

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
        // properties) and the classifier (it accepts everything, or nothing). A "zero violations"
        // means nothing without both.
        Assert.True(seen > 100, $"Only {seen} [DataMember] propertie(s) read: the rule scans nothing.");
        foreach (var shape in new[] { "primitif", "commande", "collection", "contrat" })
            Assert.Contains(shape, shapes);

        Assert.True(offenders.Count == 0,
            "A property exposed to devenv with a type the Remote UI boundary does not carry. It "
            + "will be INVISIBLE on the VS side: no error, no binding, the element stays empty. The "
            + "only shapes that cross are the primitives (string/bool/int/double), AsyncCommand, a "
            + "[DataContract] type, and an ObservableCollection of either of the last two. Sites:"
            + Environment.NewLine + "  " + string.Join(Environment.NewLine + "  ", offenders));
    }

    // ── 17. The chat list's scrolling contract ────────────────────────────────

    [Fact]
    public void TheChatList_KeepsTheContractTheAutoScrollerDependsOn()
    {
        // The three of them fail the same way: the conversation stops following the stream, with no
        // error, no exception, no trace — the user believes the model has stopped.
        //
        // The scroll never comes from a SelectedItem change: WPF has no BringIntoView in
        // Selector/ListBox, so the real scroll comes from the in-proc class handler
        // ChatAutoScroller, which finds the chat list by the literal Tag set in the XAML.
        //
        // ChatAutoScroller's comment ALREADY stated the agreement ("Must match the literal Tag set
        // on the chat ListBox in InferpalToolWindowContent.xaml"); nobody held it.
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
            + "The chat's auto-scroll is a global WPF class handler, filtered on that Tag: at zero "
            + "it hooks onto nothing, at two it follows the wrong list — in both cases without the "
            + "slightest error.");

        var chatList = matches[0].El;
        var missing = new[]
            {
                @"VirtualizingPanel\.IsVirtualizing\s*=\s*""False""",
                @"ScrollViewer\.CanContentScroll\s*=\s*""False""",
            }
            .Where(attr => !Regex.IsMatch(chatList, attr))
            .Select(attr => attr.Replace(@"\.", ".").Replace(@"\s*", string.Empty).Replace(@"""", "\""))
            .ToList();

        Assert.True(missing.Count == 0,
            $"The chat list ({Rel(matches[0].Xaml)}) lost: {string.Join(", ", missing)}. "
            + "With virtualization or logical scrolling on, off-screen bubbles have no container: "
            + "BringIntoView has nothing to bring and ScrollToEnd aims at a wrong extent - the "
            + "conversation silently stops following the stream.");
    }

    // ── 24. A solution is looked up by its EXTENSION, never by `*.sln` ────────

    [Fact]
    public void ASolutionIsNeverLookedUpByThe_sln_Pattern()
    {
        // Issue #9. `Directory.GetFiles(dir, "*.sln")` OFTEN returns the
        // `.slnx` too — through 8.3 short-name matching, which is configured PER VOLUME. The same
        // code found the solution on the maintainer's system volume and not on the reporter's
        // `G:\`: wrong workspace root, `read_file` refusing the project's paths, "No .sln file
        // found — cannot find .inferpal/context.md".
        //
        // ⚠ The rule exists because the first pass fixed the SIX sites in the Core and left the TWO
        // in the VS adapter — including the one that returns exactly the message above. A pattern
        // spread over two projects is not held in one's head; `SolutionFiles` is the funnel.
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

            // A file that names BOTH formats has done the work: it looks for project markers, not
            // for "the" solution, and the list is explicit. What the rule chases is the pattern
            // that DECIDES ALONE — the one that lets 8.3 answer.
            if (source.Contains("\"*.slnx\"", StringComparison.Ordinal)) continue;

            // Witness: the rule is worth nothing unless it really sees file lookups.
            sites += System.Text.RegularExpressions.Regex.Matches(source, @"(?:GetFiles|EnumerateFiles)\s*\(").Count;

            foreach (System.Text.RegularExpressions.Match m in
                     System.Text.RegularExpressions.Regex.Matches(source, "\"\\*\\.sln\""))
                offenders.Add($"{Rel(file)}({source[..m.Index].Count(c => c == '\n') + 1}) : \"*.sln\"");
        }

        Assert.True(sites >= 20,
            $"Only {sites} file lookup(s) found — the rule no longer measures anything.");

        Assert.True(offenders.Count == 0,
            "A solution looked up through the *.sln pattern gives an answer that depends on the "
            + "volume (8.3 short names): it finds .slnx files on one machine and not on another. "
            + "Go through Services/SolutionFiles, which filters on the extension. Sites:"
            + Environment.NewLine + "  " + string.Join(Environment.NewLine + "  ", offenders));
    }

    // ── 15. Ni `async void`, ni `.Wait()` ─────────────────────────────────────

    [Fact]
    public void AsyncCode_NeverSwallowsItsFailuresNorBlocks()
    {
        // Two shapes this repository has always forbidden in CLAUDE.md, and that nothing held.
        // They have no legitimate use here:
        //   `async void`  — the exception comes back to no await; it kills the process or vanishes,
        //                   and it is the purest shape of "something failed in silence";
        //   `.Wait()`     — blocks a thread on a task, which deadlocks as soon as a synchronization
        //                   context is in play (the VS window has one).
        //
        // ⚠ `.Result` is NOT in the rule, deliberately. its eight
        // occurrences are all legitimate — two `read.IsCompletedSuccessfully ? read.Result : …`
        // (no blocking possible) and six DTO properties called Result. No text tells those cases
        // apart from a real block, so the rule would live off its exemption list: exactly the
        // antipattern this file exists to avoid. Zero violations today, checked by hand; if that
        // ever changes it will show up in review, not here.
        var offenders = new List<string>();
        var sites     = 0;

        foreach (var file in CoreSources("Services").Concat(CoreSources("Config"))
                                                    .Concat(ViewModelSources())
                                                    .Concat(ProjectSources("Inferpal.Host"))
                                                    .Concat(ProjectSources("Inferpal.Fim"))
                                                    .Concat(ProjectSources("Inferpal.InProc")))
        {
            var root = CSharpSyntaxTree.ParseText(File.ReadAllText(file), path: file).GetRoot();

            // Witness: the rule is worth nothing unless async code remains to be judged.
            sites += root.DescendantNodes().OfType<MethodDeclarationSyntax>()
                .Count(m => m.Modifiers.Any(t => t.IsKind(SyntaxKind.AsyncKeyword)));

            foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
            {
                if (!method.Modifiers.Any(t => t.IsKind(SyntaxKind.AsyncKeyword))) continue;
                if (method.ReturnType is not PredefinedTypeSyntax pt || !pt.Keyword.IsKind(SyntaxKind.VoidKeyword)) continue;
                offenders.Add($"{Rel(file)}({method.GetLocation().GetLineSpan().StartLinePosition.Line + 1}) : async void {method.Identifier.ValueText}");
            }

            foreach (var inv in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (inv.Expression is not MemberAccessExpressionSyntax ma
                 || ma.Name.Identifier.ValueText != "Wait"
                 || inv.ArgumentList.Arguments.Count != 0) continue;
                offenders.Add($"{Rel(file)}({inv.GetLocation().GetLineSpan().StartLinePosition.Line + 1}) : .Wait()");
            }
        }

        Assert.True(sites >= 50,
            $"The scan found only {sites} async method(s): the rule checks nothing any more.");

        Assert.True(offenders.Count == 0,
            "These tools resolve a path against the process's working directory, which in Visual "
            + "Studio is not the project. Pass the base to PathSanitizer.Sanitize(path, root). Sites:"
            + Environment.NewLine + "  " + string.Join(Environment.NewLine + "  ", offenders));
    }

    // ── 14. No bubble label is a literal ──────────────────────────────────────

    [Fact]
    public void ChatBubbleLabels_NeverCarryALiteral()
    {
        // ⚠ `ChatMessageItem.UserMsg` carried `Label = "Vous"`. The bubble does not display it (the
        // template binds Label only for the "tool" role), so nothing signalled it — but the export
        // uses it as the header of every turn: a Japanese user received "Vous". The defect did not
        // live in what one looks at, it lived in what one takes away.
        //
        // ⚠ Rule 9 could not catch it: it reads the argument of a call that DISPLAYS, and this is a
        // property initializer. Fourth shape this file adds for the same doctrine — the text the
        // user reads comes from a resource, wherever it is written.
        var sites     = 0;
        var offenders = new List<string>();

        foreach (var file in ViewModelSources())
        {
            var root = CSharpSyntaxTree.ParseText(File.ReadAllText(file), path: file).GetRoot();

            foreach (var assignment in root.DescendantNodes().OfType<AssignmentExpressionSyntax>())
            {
                if (assignment.Left is not IdentifierNameSyntax name
                 || name.Identifier.ValueText is not ("Label" or "SubLabel")) continue;

                sites++;
                // Every string literal the VALUE can evaluate to, not only a bare one: a switch arm or a
                // ternary branch (`Label = role switch { "user" => "Vous" }`) is the same hard-coded
                // label one level deeper. An arm's PATTERN ("user") is matched, not displayed, and a
                // literal passed to a call (`RoleLabel("user")`) is a key: the call decides the text.
                foreach (var lit in assignment.Right.DescendantNodesAndSelf().OfType<LiteralExpressionSyntax>())
                {
                    if (!lit.IsKind(SyntaxKind.StringLiteralExpression) || lit.Token.ValueText.Length == 0) continue;
                    if (lit.Parent is ConstantPatternSyntax or ArgumentSyntax) continue;
                    offenders.Add($"{Rel(file)}({lit.GetLocation().GetLineSpan().StartLinePosition.Line + 1}) : \"{lit.Token.ValueText}\"");
                }
            }
        }

        // Witness: the rule is worth nothing unless labels remain to be judged.
        Assert.True(sites >= 4,
            $"The scan found only {sites} label assignment(s): the rule checks nothing any more.");

        Assert.True(offenders.Count == 0,
            "A hard-coded bubble label: it is rendered into the exported document, and therefore "
            + "read by users of all ten languages. Go through ConversationExporter.RoleLabel, which "
            + "takes it from the resources. Sites:"
            + Environment.NewLine + "  " + string.Join(Environment.NewLine + "  ", offenders));
    }

    // ── 13. What /diagnostics displays is in ENGLISH, like the whole channel ──

    [Fact]
    public void DiagnosticsDetails_AreWrittenInEnglish()
    {
        // The /diagnostics channel is read BY THE USER, in all ten languages, and it also carries
        // exception messages that nobody ever translates. The repository therefore settled this
        // long ago, by usage: every one of its details is in English. That is not written anywhere
        // else, and that is exactly why four FRENCH sentences could get in there in one evening —
        // caught by Dams, not by a test.
        //
        // ⚠ Rule 9 ("displayed text goes through Strings") could not catch it: it scans the VM and
        // the host, never Services/**. A convention that stops at a folder does not see the site one
        // writes elsewhere — the third time this file pays for it.
        //
        // ⚠ FOURTH time, and this time it is THIS rule that stopped at a folder: its scope covered
        // only the Core and the VM, so neither the FIM sidecar nor the in-proc. Two FRENCH details
        // lived there ("introuvable :" in FimSidecar, the framing sentence in FimRpcLoop) and it is
        // not this test that found them — it is the PORT to the public repository, where both were
        // translated into English. The channel is the same for the user, whichever project writes
        // into it.
        //
        // ⚠ And it must be said plainly: widening the scope would NOT have caught those two.
        // "introuvable :" and "En-tetes lus sans Content-Length exploitable" are written without a
        // single accented letter, and this test is a proxy on accents — checked by sabotage, it
        // stays GREEN on the exact word that was in the code. The scope is fixed because it was
        // wrong, not because it would have been enough: what found those sentences is the
        // comparison with the public repository, and nothing else could.
        //
        // ⚠ It is a PROXY, not a proof: it catches accented prose (French, Spanish,
        // Portuguese...), not French written without accents. It catches the mistake that was made,
        // and it claims no more. Non-ASCII punctuation (—, ≠, «, ») stays allowed: it is already in
        // existing English details.
        var sites = 0;
        var offenders = new List<string>();

        foreach (var file in CoreSources("Services").Concat(CoreSources("Config"))
                                    .Concat(ViewModelSources())
                                    .Concat(ProjectSources("Inferpal.Host"))
                                    .Concat(ProjectSources("Inferpal.Fim"))
                                    .Concat(ProjectSources("Inferpal.InProc")))
        {
            var root = CSharpSyntaxTree.ParseText(File.ReadAllText(file), path: file).GetRoot();

            foreach (var inv in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (inv.Expression is not MemberAccessExpressionSyntax ma
                 || ma.Name.Identifier.ValueText != "Record"
                 || !ma.Expression.ToString().Contains("Diagnostics")) continue;
                if (inv.ArgumentList.Arguments.Count < 2) continue;

                sites++;
                var detail = inv.ArgumentList.Arguments[1].Expression;
                var text = string.Concat(detail.DescendantNodesAndSelf()
                    .Select(n => n switch
                    {
                        LiteralExpressionSyntax lit          => lit.Token.ValueText,
                        InterpolatedStringTextSyntax interp   => interp.TextToken.ValueText,
                        _                                     => string.Empty,
                    }));

                var accented = new string([.. text.Where(c => char.IsLetter(c) && c > 127).Distinct()]);
                if (accented.Length > 0)
                    offenders.Add($"{Rel(file)}({inv.GetLocation().GetLineSpan().StartLinePosition.Line + 1}) : {accented}");
            }
        }

        // Witness: the rule is worth nothing unless calls remain to be judged.
        Assert.True(sites >= 10, $"The scan found only {sites} call(s) to Diagnostics.Record: the rule checks nothing any more.");

        Assert.True(offenders.Count == 0,
            "A /diagnostics detail written with accented letters: the channel is in English, it "
            + "is read by users of all ten languages, and a French sentence is as unreadable there "
            + "as anywhere else. Sites (with the offending letters):"
            + Environment.NewLine + "  " + string.Join(Environment.NewLine + "  ", offenders));
    }

    // ── 22. A model keyword is read through Keyword, never otherwise ──────────

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
            "A keyword written by the MODEL, read without normalisation. The code compares that "
            + "value against literals: Callers, Replace, or a value padded with spaces match none "
            + "of them, and the tool then returns a WRONG answer with no error — an empty report, "
            + "or a write other than the one asked for. Go through ToolArgs.Keyword:"
            + Environment.NewLine + "  " + string.Join(Environment.NewLine + "  ", offenders));
    }

    // ── Plomberie ─────────────────────────────────────────────────────────────

    /// <summary>
    /// The text of a source file with its COMMENTS neutralized - replaced by spaces, length for
    /// length, newlines preserved: offsets, and therefore the line numbers of failure messages,
    /// stay exact.
    /// </summary>
    /// <remarks>
    /// Without it the repair would be invisible: the ten rules above are green before as after,
    /// since no source in the repository is in breach. That is exactly the failure mode this file
    /// exists to close — a rule that measures nothing is green for the worst of reasons.
    /// </remarks>
    [Fact]
    public void CodeOnly_NeutralizesComments_InBothDirections()
    {
        var path = Path.Combine(Path.GetTempPath(), $"inferpal-codeonly-{Guid.NewGuid():N}.cs");
        var source = string.Join(Environment.NewLine,
        [
            "class Subject",
            "{",
            "    // Messages.Insert(Messages.Count - 2, ChatMessageItem.StreamingMsg(x));",
            "    /// <summary>File.WriteAllText(path, text) inside an XML comment</summary>",
            "    void Live() { Called(); }",
            "    /* Called();",
            "       Called(); */",
            "    void Other() { }",
            "}",
        ]);
        File.WriteAllText(path, source);
        try
        {
            var code = CodeOnly(path);

            // False RED: the prose documenting a forbidden pattern must no longer carry it.
            Assert.DoesNotContain("ChatMessageItem.StreamingMsg", code, StringComparison.Ordinal);
            Assert.DoesNotContain("File.WriteAllText", code, StringComparison.Ordinal);

            // False GREEN: a COMMENTED-OUT call no longer counts as present. The real call does.
            Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(code, "Called\\(").Count);
            Assert.Contains("void Live()", code, StringComparison.Ordinal);
            Assert.Contains("void Other()", code, StringComparison.Ordinal);

            // Offsets are preserved: same length, same newlines — otherwise the line numbers of
            // error messages would point at the wrong line.
            Assert.Equal(source.Length, code.Length);
            Assert.Equal(source.Count(c => c == '\n'), code.Count(c => c == '\n'));
        }
        finally { File.Delete(path); }
    }

    /// <summary>
    /// The text of a source file with its <b>comments neutralized</b> — replaced by spaces, length
    /// for length, newlines preserved: offsets, and therefore the line numbers of error messages,
    /// stay exact.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠ A convention scan that reads raw text finds its patterns <b>in the comments</b>, and that
    /// is paid for in both directions. In false <b>red</b>: the rule goes red on the prose that
    /// documents the defect it forbids — which happened <b>three times</b>/04, once
    /// on the very comment written to explain the fix the rule had just demanded. In false
    /// <b>green</b>: a rule requiring the presence of a call is happy to find it COMMENTED OUT,
    /// hence disabled.
    /// </para>
    /// <para>
    /// This is the same class as <c>Test-PublicParity</c>'s neutralizer, fixed, and
    /// the same lesson as rule 2's Roslyn pattern: <i>a guard written in the language of the defect
    /// it hunts inherits its blind spots</i>. Here the language is C#, so the syntax tree settles
    /// what a regex cannot.
    /// </para>
    /// <para>
    /// ⚠ One reader per language, never two: this is also what <c>SettingsSchemaDriftTests</c> calls
    /// for the C# sources it scans (hence <c>internal</c>). The other two languages have their own,
    /// because Roslyn does not read them — <c>DeployScriptGuardTests.NeutralizeScriptComments</c>
    /// for PowerShell, <c>SettingsSchemaDriftTests.NeutralizeTypeScriptComments</c> for TypeScript —
    /// and each carries its own witness.
    /// </para>
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

    private static IEnumerable<string> ToolsSources() =>
        CoreSources(Path.Combine("Services", "Tools"));

    /// <summary>The Core's whole Services folder — the scope of rules 2 and 4 since the post-1.6.1
    /// review: "untrusted input" and "disk walk" do not follow the sub-folder split, and a rule that
    /// stops at a folder does not see the site one moves into it.</summary>
    private static IEnumerable<string> ServicesSources() =>
        CoreSources("Services");

    /// <summary>
    /// Where a regex meets input nobody in this repository controls. <c>Services\Tools</c> parses
    /// the workspace; <c>Services\Docs</c> parses HTML fetched from the open web, which is the
    /// least controlled input the product touches — and it was outside the rule until the
    /// post-1.6.0 review found the crawler running unbounded patterns over it, while its twin
    /// <c>FetchUrlTool</c> had been bounding the same ones since it was written.
    /// </summary>
    private static IEnumerable<string> UntrustedInputSources() =>
        ToolsSources().Concat(CoreSources(Path.Combine("Services", "Docs")));

    /// <summary>
    /// Every <c>.cs</c> under <c>Inferpal.Core\&lt;subdir&gt;</c>, <b>with a positive witness</b>.
    /// </summary>
    /// <remarks>
    /// <c>internal</c> for the same reason as <see cref="CodeOnly"/>: one enumerator, therefore one
    /// witness. <c>LocalizationCompletenessTests</c> uses it to sweep the VS adapter for the
    /// <c>%Key%</c> tokens of the command table.
    /// </remarks>
    internal static IReadOnlyList<string> CoreSources(string subdir)
    {
        var dir = Path.Combine(RepoRoot(), "Inferpal.Core", subdir);
        Assert.True(Directory.Exists(dir), $"The convention scan targets {dir}, which does not exist — the rule checks nothing any more.");

        var files = Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories).ToList();
        Assert.NotEmpty(files);
        return files;
    }

    /// <summary>Same for the VS adapter: rules 6 and 8 scan <c>Inferpal\ToolWindow</c>.</summary>
    internal static IReadOnlyList<string> ViewModelSources()
    {
        var dir = Path.Combine(RepoRoot(), "Inferpal", "ToolWindow");
        Assert.True(Directory.Exists(dir), $"The convention scan targets {dir}, which does not exist — the rule checks nothing any more.");

        var files = Directory.EnumerateFiles(dir, "*.cs", SearchOption.TopDirectoryOnly).ToList();
        Assert.NotEmpty(files);
        return files;
    }

    internal static IReadOnlyList<string> ProjectSources(string project)
    {
        var dir = Path.Combine(RepoRoot(), project);
        Assert.True(Directory.Exists(dir), $"The convention scan targets {dir}, which does not exist — the rule checks nothing any more.");

        var files = Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .ToList();
        Assert.NotEmpty(files);
        return files;
    }

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

    /// <summary>The <paramref name="index"/>-th top-level argument of a list.</summary>
    private static string NthArgument(string args, int index)
    {
        var parts = new List<string>();
        var start = 0;
        var depth = 0;
        for (var i = 0; i < args.Length; i++)
        {
            var c = args[i];
            if (c == '"' || c == '\'') { i = SkipLiteral(args, i); continue; }
            if (c is '(' or '[' or '<') depth++;
            else if (c is ')' or ']' or '>') depth--;
            else if (c == ',' && depth == 0) { parts.Add(args[start..i]); start = i + 1; }
        }
        parts.Add(args[start..]);
        return index < parts.Count ? parts[index] : string.Empty;
    }

    /// <summary>
    /// The assigned expression, from <paramref name="afterEquals"/>: up to the top-level <c>;</c> or
    /// <c>,</c>, or up to the parenthesis closing the enclosing lambda — <c>CurrentStep</c> is most
    /// often written inside a <c>Post(() =&gt; CurrentStep = …)</c>.
    /// </summary>
    /// <remarks>
    /// ⚠ The comma was not in the first version, and the rule immediately accused the wrong site: an
    /// object-initializer member (<c>Title = X,</c>) ends with a comma, so the expression spilled
    /// over the following members and reported their literal under the first one's name.
    /// </remarks>
    private static string AssignedExpression(string source, int afterEquals)
    {
        var depth = 0;
        for (var i = afterEquals; i < source.Length; i++)
        {
            var c = source[i];
            if (c == '"' || c == '\'') { i = SkipLiteral(source, i); continue; }
            if (c is '(' or '[' or '{') depth++;
            else if (c is ')' or ']' or '}') { if (--depth < 0) return source[afterEquals..i]; }
            else if ((c == ';' || c == ',') && depth == 0) return source[afterEquals..i];
        }
        return source[afterEquals..];
    }

    /// <summary>
    /// The literals <b>written in</b> an expression, not those it passes to a nested call.
    /// </summary>
    /// <remarks>
    /// ⚠ Three shapes were measured before settling on this one, and the first two were wrong. "The
    /// literal right after the parenthesis" did not see <c>ShowInfoAsync(cond ? "…" : "…")</c> —
    /// that is, <b>the exact shape of the defect that gave birth to the rule</b>. "Every literal of
    /// the statement" counted 30 sites, 20 of which were protocol effect names
    /// (<c>stateChange</c>, <c>openFile</c>) and tool names, which are not displayed text. What
    /// remains: <c>Strings.SlashUsage("/commit-exec &lt;message&gt;")</c> passes a command name as a
    /// parameter, <c>HandleAsync(dir, "context.md", …)</c> a file name — a literal handed to a call
    /// is not the message.
    ///
    /// ⚠ A <b>grouping</b> parenthesis is not a call: <c>(ok ? "a" : "b") + x</c> is written in the
    /// expression. Only parentheses following an identifier count.
    /// </remarks>
    private static IEnumerable<string> TopLevelLiterals(string expression)
    {
        var callDepth = 0;
        var stack = new Stack<bool>();
        for (var i = 0; i < expression.Length; i++)
        {
            var c = expression[i];
            if (c == '"')
            {
                var end = SkipLiteral(expression, i);
                if (callDepth == 0) yield return expression[(i + 1)..Math.Min(end, expression.Length)];
                i = end;
                continue;
            }
            if (c == '\'') { i = SkipLiteral(expression, i); continue; }
            if (c is '(' or '[')
            {
                var j = i - 1;
                while (j >= 0 && char.IsWhiteSpace(expression[j])) j--;
                var isCall = j >= 0 && (char.IsLetterOrDigit(expression[j]) || expression[j] is '_' or ')' or ']' or '>');
                stack.Push(isCall);
                if (isCall) callDepth++;
            }
            else if (c is ')' or ']')
            {
                if (stack.Count > 0 && stack.Pop()) callDepth--;
            }
        }
    }

    /// <summary>
    /// The <b>text</b> of a literal: its interpolation holes removed, because they carry code, not
    /// prose.
    /// </summary>
    /// <remarks>
    /// ⚠ Seen red while writing the rule: <c>$"**{Strings.LabelModeChat}**"</c> contains no
    /// displayed word — but "Strings" and "LabelModeChat" are words, and the rule accused the one
    /// line in the file that was already doing exactly what it asks.
    /// </remarks>
    private static string LiteralText(string literal)
    {
        var sb = new System.Text.StringBuilder(literal.Length);
        for (var i = 0; i < literal.Length; i++)
        {
            if (literal[i] == '{')
            {
                if (i + 1 < literal.Length && literal[i + 1] == '{') { i++; continue; }   // {{ = accolade
                var depth = 1;
                i++;
                while (i < literal.Length && depth > 0)
                {
                    if (literal[i] == '{') depth++;
                    else if (literal[i] == '}') depth--;
                    i++;
                }
                i--;
                continue;
            }
            sb.Append(literal[i]);
        }
        return sb.ToString();
    }

    /// <summary>Index of the closing quote/apostrophe of a literal opened at <paramref name="at"/>.</summary>
    private static int SkipLiteral(string source, int at)
    {
        var quote = source[at];
        var verbatim = quote == '"' && ((at > 0 && source[at - 1] == '@') ||
                                        (at > 1 && source[at - 1] == '$' && source[at - 2] == '@'));
        for (var i = at + 1; i < source.Length; i++)
        {
            if (verbatim && source[i] == '"' && i + 1 < source.Length && source[i + 1] == '"') { i++; continue; }
            if (!verbatim && source[i] == '\\') { i++; continue; }
            if (source[i] == quote) return i;
            if (!verbatim && source[i] == '\n') return i;   // unterminated literal: do not devour what follows
        }
        return source.Length - 1;
    }

    /// <summary>
    /// A bubble inserted into <c>Messages</c> is THEMED: either through the <c>InsertThemed</c>
    /// funnel, or by an <c>ApplyItemTheme</c> on the same variable just before.
    /// </summary>
    /// <remarks>
    /// The neighbouring rule saw only ONE shape of bypass: the bubble built INSIDE the call to
    /// <c>Messages.Insert(...)</c>. The one built a line above and then inserted without theming
    /// escaped it — and that was the STREAMING bubble, i.e. the one the user watches for a whole
    /// answer.
    ///
    /// the defaults of <c>ChatMessageItem</c>'s colour fields are those of
    /// the DARK theme (<c>ThemeText = #D4D4D4</c>). Under a light theme, the unthemed bubble
    /// therefore rendered its text at <b>1.36:1</b> contrast on the <c>#F5F5F5</c> window
    /// background — the WCAG minimum for body text is 4.5:1, and the themed value gives 15.29:1.
    /// The answer was unreadable while it was generated, then appeared at once when
    /// <c>FinalizeStreamingBubble</c> themed it at the end of the turn.
    /// </remarks>
    [Fact]
    public void EveryInsertedBubble_IsThemed()
    {
        var insertion = new System.Text.RegularExpressions.Regex(
            @"Messages\.Insert\(\s*[^,]+,\s*([A-Za-z_][A-Za-z0-9_]*)\s*\)");

        var offenders = new List<string>();
        var seen = 0;
        foreach (var file in ViewModelSources())
        {
            var source = CodeOnly(file);
            var lines  = source.Split('\n');
            foreach (System.Text.RegularExpressions.Match m in insertion.Matches(source))
            {
                var name = m.Groups[1].Value;
                // The funnel itself: IT is what themes.
                if (name == "item" && source.Contains("private ChatMessageItem InsertThemed", StringComparison.Ordinal))
                    continue;
                seen++;
                var at     = source[..m.Index].Count(c => c == '\n');
                var window = string.Join("\n", lines[Math.Max(0, at - 25)..(at + 1)]);
                if (window.Contains($"ApplyItemTheme({name})", StringComparison.Ordinal)) continue;
                if (window.Contains("InsertThemed", StringComparison.Ordinal)) continue;
                offenders.Add($"{Rel(file)}({at + 1}) : {name}");
            }
        }

        // Witness: the rule is worth nothing unless insertions really remain to be guarded. Zero
        // would mean the VM has been rewritten and the rule scans nothing any more.
        Assert.True(seen >= 20, $"Only {seen} bubble insertion(s) read -- the rule measures nothing any more.");

        Assert.True(offenders.Count == 0,
            "A bubble inserted without theming: under a light theme it comes out at 1.36:1 of "
            + "contrast, where WCAG asks for 4.5:1. Go through InsertThemed:"
            + Environment.NewLine + "  " + string.Join(Environment.NewLine + "  ", offenders));
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

        // Witness: with no site replacing the conversation, the rule is green while measuring
        // nothing — this whole repository's failure mode. There are two: /clear and restore.
        Assert.True(seen >= 2, $"Only {seen} replacement site(s) read -- the rule measures nothing any more.");

        Assert.True(offenders.Count == 0,
            "These methods replace the conversation without resetting its counters: the token "
            + "total shown and exported, and the measurement the context pre-check decides on, then "
            + "describe the previous conversation. Call ResetTurnAccounting():"
            + Environment.NewLine + "  " + string.Join(Environment.NewLine + "  ", offenders));
    }

    // ── 28. A Stop button that is shown has something to cancel ───────────────

    [Fact]
    public void ShowingTheStopButton_GivesItSomethingToCancel()
    {
        // IsLoading = true turns the send button into Stop, and SendAsync then does one thing:
        // cancel _currentCts. A method that raises IsLoading without wiring _currentCts shows a Stop
        // that does nothing - and SettleCurrentTurnAsync, which cancels the same token before a
        // session is loaded, cannot stop it either.
        //
        // The subject is the ASSIGNMENT, read from the syntax tree (lambdas included: the flag is
        // raised on the VM context), not a list of commands.
        var offenders = new List<string>();
        var seen      = 0;

        foreach (var file in ViewModelSources())
        {
            var root = CSharpSyntaxTree.ParseText(File.ReadAllText(file), path: file).GetRoot();

            foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
            {
                var assignments = method.DescendantNodes().OfType<AssignmentExpressionSyntax>().ToList();

                var showsStop = assignments.Any(a =>
                    a.Left is IdentifierNameSyntax { Identifier.Text: "IsLoading" }
                    && a.Right.IsKind(SyntaxKind.TrueLiteralExpression));
                if (!showsStop) continue;
                seen++;

                var wiresCancel = assignments.Any(a =>
                    a.Left is IdentifierNameSyntax { Identifier.Text: "_currentCts" }
                    && !a.Right.IsKind(SyntaxKind.NullLiteralExpression));
                if (wiresCancel) continue;

                offenders.Add($"{Rel(file)} : {method.Identifier.Text}");
            }
        }

        // Witness: since rule 29, a single site raises the flag — BeginOwnedTurn — and it is the one
        // that must set _currentCts.
        Assert.True(seen >= 1,$"Only {seen} site(s) raising IsLoading read -- the rule measures nothing any more.");

        Assert.True(offenders.Count == 0,
            "These methods show the Stop button (IsLoading = true) with nothing for it to cancel: "
            + "SendAsync and SettleCurrentTurnAsync only cancel _currentCts. Wire a "
            + "CancellationTokenSource bound to _currentCts and pass its token:"
            + Environment.NewLine + "  " + string.Join(Environment.NewLine + "  ", offenders));
    }

    // ── 13. A property bound to SelectedItem is declared nullable ─────────────

    [Fact]
    public void SignalChannels_WriteThroughTheSignalFunnel()
    {
        // A signal channel is published through SignalFile.Write (staging + rename), never through
        // File.WriteAllText: the readers of this bus erase what they have just read
        // (VsBuildMonitor calls Clear() unconditionally after TryRead()), so a read landing on the
        // truncated file does not return "no signal", it LOSES the payload — a failed build with no
        // banner and no "Fix with AI".
        //
        // ⚠ THE SCOPE IS A PROPERTY, NOT A FOLDER. Rules 1 and 3 designate their subject by a
        // sub-folder (`Services\Tools`, `Services\Persistence`), and that is precisely the blind
        // spot this site went through: it writes a file, in a folder shared between processes, from
        // `Services\Signals`, which neither of them looks at. The subject here is "this file
        // resolves a path through the signal bus" — so it publishes into %TEMP%\Inferpal, so it owes
        // the atomicity guarantee, wherever it lives tomorrow.
        var offenders = new List<string>();
        var channels  = 0;
        var funnelled = 0;

        var writesDirectly = new Regex(
            @"(?<![\w.])File\.(WriteAllText|WriteAllBytes|WriteAllLines|AppendAllText|AppendAllLines)(Async)?\s*\(");

        foreach (var file in ServicesSources())
        {
            // The funnel itself is the only exemption: it is the one that writes the staging file.
            if (Path.GetFileName(file) == "SignalFile.cs") continue;

            var source = CodeOnly(file);

            // "This file publishes into the signals folder": it resolves a path there.
            if (!Regex.IsMatch(source, @"SignalFile\.(ScopedPathFor|KeyedPathFor|PathFor|Dir)\b")) continue;
            channels++;

            if (Regex.IsMatch(source, @"SignalFile\.Write\s*\(")) funnelled++;

            var direct = writesDirectly.Match(source);
            if (direct.Success)
                offenders.Add($"{Rel(file)} : {Snippet(source, direct.Index)}");
        }

        // Two witnesses, because two things can break in silence: collecting the channels (a moved
        // folder, a renamed path grammar) and recognising the funnel. "Zero offenders" is worth
        // nothing if the scan found no channel, nor if none of those found goes through the funnel —
        // that would be the generalised defect, rendered green.
        Assert.True(channels >= 8,
            $"Only {channels} signal channel(s) found: the rule judges nothing any more.");
        Assert.True(funnelled >= 8,
            $"Only {funnelled} channel(s) go through SignalFile.Write: the funnel has been "
            + "renamed or bypassed everywhere, and the rule no longer recognises what it enforces.");

        Assert.True(offenders.Count == 0,
            "This channel writes its signal file IN PLACE. File.WriteAll* truncates then fills, "
            + "so a reader in another process can land on an empty or half-written file — and the "
            + "readers of this bus erase what they have just read, which turns the tear into a "
            + "permanent loss. Go through SignalFile.Write (staging + rename, and it traces its "
            + "failure instead of swallowing it). Sites:"
            + Environment.NewLine + "  " + string.Join(Environment.NewLine + "  ", offenders));
    }

    // ── 29. A turn is taken and returned through the same funnel ──────────────

    [Fact]
    public void ATurnIsTakenAndReleased_ThroughOneFunnel()
    {
        // Taking the turn is three pieces of state that go together: IsLoading (the Stop button),
        // _currentCts (what Stop cancels) and _turnDone (what a session load and code actions await
        // before replacing the conversation). Releasing them must happen only while this turn still
        // owns them: a code action can cancel it and start the next one before its finally runs.
        // Every site that copied the gesture forgot part of it — /tdd and /fix-build had no _turnDone
        // and released unconditionally.
        var raisers    = new List<string>();
        var unreleased = new List<string>();
        var callers    = 0;
        var declared   = 0;

        static bool Invokes(SyntaxNode node, string name) =>
            node.DescendantNodes().OfType<InvocationExpressionSyntax>()
                .Any(i => i.Expression is IdentifierNameSyntax id && id.Identifier.Text == name);

        foreach (var file in ViewModelSources())
        {
            var root = CSharpSyntaxTree.ParseText(File.ReadAllText(file), path: file).GetRoot();

            foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
            {
                var name = method.Identifier.Text;
                if (name == "BeginOwnedTurn") { declared++; continue; }

                var raisesLoading = method.DescendantNodes().OfType<AssignmentExpressionSyntax>().Any(a =>
                    a.Left is IdentifierNameSyntax { Identifier.Text: "IsLoading" }
                    && a.Right.IsKind(SyntaxKind.TrueLiteralExpression));
                if (raisesLoading) raisers.Add($"{Rel(file)} : {name}");

                if (!Invokes(method, "BeginOwnedTurn")) continue;
                callers++;

                var releasedInFinally = method.DescendantNodes().OfType<FinallyClauseSyntax>()
                    .Any(f => Invokes(f, "EndOwnedTurn"));
                if (!releasedInFinally) unreleased.Add($"{Rel(file)} : {name}");
            }
        }

        Assert.True(declared == 1, $"BeginOwnedTurn is declared {declared} times -- the funnel no longer exists.");
        // Witness: the chat turn, /fix-build, /tdd, /commit and the long commands.
        Assert.True(callers >= 5, $"Only {callers} site(s) taking the turn read -- the rule measures nothing any more.");

        Assert.True(raisers.Count == 0,
            "These methods raise IsLoading themselves instead of going through BeginOwnedTurn — "
            + "a turn taken that way has no _turnDone, or is released without checking it is still owned:"
            + Environment.NewLine + "  " + string.Join(Environment.NewLine + "  ", raisers));
        Assert.True(unreleased.Count == 0,
            "These methods take the turn without releasing it in a finally (EndOwnedTurn): an "
            + "exception leaves the window loading, with a Stop button that has nothing left to cancel:"
            + Environment.NewLine + "  " + string.Join(Environment.NewLine + "  ", unreleased));
    }

    // ── 30. The system message is written in one place ────────────────────────

    [Fact]
    public void TheSystemMessage_IsWrittenInOnePlace()
    {
        // _history[0] was rewritten in seven places, each with its own recipe: the OODA summary
        // vanished at the first active-file change or the first /note, and the language persona at
        // the first plan-mode toggle. The composition lives in ApplySystemPrompt, and the persona's
        // language in a field BuildSystemPrompt reads itself.
        var writers  = new List<string>();
        var withArgs = new List<string>();
        var declared = 0;
        var refreshes = 0;

        foreach (var file in ViewModelSources())
        {
            var root = CSharpSyntaxTree.ParseText(File.ReadAllText(file), path: file).GetRoot();

            foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
            {
                var name = method.Identifier.Text;
                if (name == "ApplySystemPrompt") declared++;

                var writesSlot = method.DescendantNodes().OfType<AssignmentExpressionSyntax>().Any(a =>
                    a.Left is ElementAccessExpressionSyntax
                    {
                        Expression: IdentifierNameSyntax { Identifier.Text: "_history" },
                    } slot
                    && slot.ArgumentList.Arguments.Count == 1
                    && slot.ArgumentList.Arguments[0].Expression.ToString() == "0");
                if (writesSlot && name != "ApplySystemPrompt") writers.Add($"{Rel(file)} : {name}");

                foreach (var call in method.DescendantNodes().OfType<InvocationExpressionSyntax>())
                {
                    if (call.Expression is not IdentifierNameSyntax id) continue;
                    if (id.Identifier.Text == "RefreshSystemPrompt") refreshes++;
                    if (id.Identifier.Text == "BuildSystemPrompt" && call.ArgumentList.Arguments.Count > 0)
                        withArgs.Add($"{Rel(file)} : {name}");
                }
            }
        }

        Assert.True(declared == 1, $"ApplySystemPrompt is declared {declared} times -- the funnel no longer exists.");
        // Witness: plan, active file, /note, /rules, X-Ray, /template.
        Assert.True(refreshes >= 5, $"Only {refreshes} call(s) to RefreshSystemPrompt read -- the rule no longer measures anything.");

        Assert.True(writers.Count == 0,
            "These methods write _history[0] themselves: the OODA session summary and the persona are "
            + "lost there. Go through RefreshSystemPrompt / ApplySystemPrompt:"
            + Environment.NewLine + "  " + string.Join(Environment.NewLine + "  ", writers));
        Assert.True(withArgs.Count == 0,
            "These methods pass a language to BuildSystemPrompt: the persona only lives for that call, "
            + "and the next rebuild erases it. Set _personaLanguage instead:"
            + Environment.NewLine + "  " + string.Join(Environment.NewLine + "  ", withArgs));
    }

    // ── 32. A scan coverage counts what was READ ──────────────────────────────

    [Fact]
    public void ScanCoverage_CountsWhatWasRead_NotWhatWasTaken()
    {
        // `ScanCoverage.Take` can say one thing only: how many files the CAP let through. Its doc,
        // however, promised "how many were actually read". Between the two lives the file taken then
        // unreadable — a running build's lock, denied permissions, a file gone between the
        // enumeration and the read — which every tool swallowed in a per-file `catch` without
        // removing it from the count. Measured on a TWO-file workspace, far below the cap, with the
        // single dependant unreadable: `analyze_impact` returned "Layer 1 · Direct dependants (0)",
        // then "Risk: LOW ↳ No dependants detected — safe to refactor freely", with NO warning at
        // all — `ScanCoverage(2, 2)` is not partial.
        //
        // ⭐ The tell: `rename_symbol`, the sibling in the same folder, the one that WRITES, already
        // counted its unreadable files. The rule was written once and held by one reader out of four.
        //
        // ⚠ THE SUBJECT IS THE FILE, NOT THE METHOD. In `AnalyzeImpactTool` the `Take` lives in
        // `ExecuteAsync` and the `Swallow`s in two private scan methods: a per-method rule would
        // have exempted both sides and measured nothing. The property is "this file asserts a
        // coverage AND loses files in silence".
        var offenders = new List<string>();
        var subjects  = 0;
        var counting  = 0;

        foreach (var file in ServicesSources())
        {
            var source = CodeOnly(file);

            // "This file asserts a coverage": it builds one.
            if (!Regex.IsMatch(source, @"ScanCoverage\.Take\s*\(|new ScanCoverage\s*\(")) continue;
            // "… and it loses files in silence": it swallows a per-file error.
            if (!Regex.IsMatch(source, @"Diagnostics\.Swallow\s*\(")) continue;
            subjects++;

            // Give the count back: either through WithUnreadable, or through the constructor's 3rd
            // argument (the shape of `rename_symbol`, which held the rule before it existed).
            var feedsItBack =
                Regex.IsMatch(source, @"\.WithUnreadable\s*\(")
                || Regex.IsMatch(source, @"new ScanCoverage\s*\([^;]*,[^;]*,[^;)]+\)");

            if (feedsItBack) counting++;
            else offenders.Add(Rel(file));
        }

        // Witness: "zero offenders" is worth nothing if the scan found no subject — a moved folder,
        // `ScanCoverage` renamed, and the rule goes green while reading nothing. The second witness
        // is the other half: if nobody gives their count back any more, it is the recognition of the
        // gesture that is broken, not the repository that is clean.
        Assert.True(subjects >= 5,
            $"Only {subjects} file(s) build a scan coverage AND swallow a per-file error: "
            + "the rule judges nothing any more.");
        Assert.True(counting >= 5,
            $"Only {counting} file(s) report their loss count back: the gesture has been renamed "
            + "and the rule no longer recognises what it enforces.");

        Assert.True(offenders.Count == 0,
            "These files assert a scan coverage while swallowing per-file read errors without "
            + "counting them: `Scanned` then announces files nobody read, `IsPartial` stays false, "
            + "and `0 dependants` reads as `nothing depends on this`. Report them back through "
            + "ScanCoverage.WithUnreadable:"
            + Environment.NewLine + "  " + string.Join(Environment.NewLine + "  ", offenders));
    }
}
