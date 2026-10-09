using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Inferpal.Services.Rag;

namespace Inferpal.Services.Tools;

/// <summary>
/// Project-wide symbol rename: replaces every occurrence of an identifier across source files.
/// Uses Roslyn syntax analysis for C# (zero false matches inside strings or comments) and
/// word-boundary regex for other languages.  Supports a dry-run preview before committing.
/// </summary>
internal sealed class RenameSymbolTool : ITool
{
    private readonly IApprovalService   _approval;
    private readonly FileHistoryService _history;
    private readonly Func<string?>      _getRoot;

    private readonly Editor.OpenDocumentOverlay? _overlay;

    public RenameSymbolTool(IApprovalService approval, FileHistoryService history, Func<string?> getRoot,
                            Editor.OpenDocumentOverlay? overlay = null)
    {
        _approval = approval;
        _history  = history;
        _getRoot  = getRoot;
        _overlay  = overlay;
    }

    public string Name => "rename_symbol";

    // ⚠ No "every occurrence across all source files". This tool builds a ScanCoverage — files it
    // could not read, files past the size cap, a folder it could not list — precisely because it
    // CANNOT promise that, and the description is read BEFORE the report: a first claim is harder
    // to dislodge than a footnote. What the description may state is the CONTRACT (all-or-nothing),
    // which is a fact about this tool; how much of the tree was reached is a fact about the world.
    public string Description =>
        "Use this for ANY rename of a method, class, property, function or variable, instead of editing each file by " +
        "hand: it renames every occurrence — callers and tests included — in one call, after the user approves the " +
        "diff. Renames an identifier across the source files found under 'root'. Uses Roslyn for C# (no false " +
        "matches in strings or comments; when the name designates several symbols it lists them and renames nothing " +
        "until declaring_file says which) and word-boundary regex for other languages. The report states " +
        "what was actually scanned — files it could not read, folders it could not list — and the write " +
        "is all-or-nothing: nothing is changed if any file cannot be. dry_run=true only previews, and writes nothing.";

    public object Parameters => new
    {
        type = "object",
        properties = new
        {
            root = new
            {
                type        = "string",
                description = "Root directory to search in. Defaults to the solution root when omitted."
            },
            old_name = new
            {
                type        = "string",
                description = "Current identifier name (exact, case-sensitive)."
            },
            new_name = new
            {
                type        = "string",
                description = "New identifier name to replace it with."
            },
            file_pattern = new
            {
                type        = "string",
                description = "Restrict rename to files matching this glob pattern (e.g. '*.cs', '*.ts'). Defaults to all supported source extensions."
            },
            dry_run = new
            {
                type        = "boolean",
                description = "true (default) = preview only, no changes written. false = apply after approval."
            },
            declaring_file = new
            {
                type        = "string",
                description = "C#: when old_name names several symbols (the tool lists them and renames nothing), the file declaring the one to rename."
            },
            declaring_line = new
            {
                type        = "integer",
                description = "C#: with declaring_file, the line of that declaration — when one file declares several symbols of that name."
            }
        },
        required = new[] { "old_name", "new_name" }
    };

    // ── ExecuteAsync ───────────────────────────────────────────────────────────

    public async Task<string> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var workspace   = _getRoot();
        var root        = PathSanitizer.Sanitize(args.Str("root") ?? workspace, workspace);
        // rename_symbol WRITES files — keep it inside the workspace like write_file/apply_diff.
        PathSanitizer.AssertUnderRoot(root, workspace);
        var oldName     = args.Trimmed("old_name") ?? string.Empty;
        var newName     = args.Trimmed("new_name") ?? string.Empty;
        var filePattern = args.Str("file_pattern");
        // ⚠ Applies by default: the approval prompt already shows the human every changed line. A preview first cost a
        // second call, and the second call is where a model lost the rename — it switched to editing by hand.
        var dryRun      = args.Bool("dry_run", false);
        var declaringFile = args.Trimmed("declaring_file");
        int? declaringLine = args.Has("declaring_line") ? args.Int("declaring_line", 0) : null;

        if (string.IsNullOrEmpty(oldName) || string.IsNullOrEmpty(newName))
            return "old_name and new_name are required.";
        if (oldName == newName)
            return "old_name and new_name are identical — nothing to do.";
        if (QualifiedRefusal(oldName, newName) is { } qualified)
            return qualified;
        if (!IsValidIdentifier(newName))
            return $"'{newName}' is not a valid identifier name.";
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
            return $"Directory not found: '{root}'. Provide a valid 'root' parameter.";
        // This tool WRITES the files the pattern designates: a directory part would leave the root.
        if (filePattern is not null && WorkspaceScan.NormalizeFilePattern(filePattern) is null)
            return WorkspaceScan.InvalidPatternMessage("file_pattern", filePattern);

        // ── Enumerate files ────────────────────────────────────────────────────
        // ⚠ The enumeration DROPS files, and this tool WRITES: a source past
        // CodeChunker.MaxFileSizeBytes (200 kB — a generated Reference.cs, a bundled script) was
        // skipped in silence, so a rename came back "Applied to 12 file(s)" while a thirteenth kept
        // the old name, and "No occurrences found" read as "the symbol does not exist". What was
        // NOT looked at travels with the result, like in every other scanning tool.
        var (files, skippedBySize) = EnumerateSourceFiles(root, filePattern);
        if (files.Count == 0)
            return $"No source files found under '{root}'.";

        // ── Scan for occurrences ───────────────────────────────────────────────
        // C# renames are resolved by the compiler when a workspace is known: the syntactic path
        // below rewrites EVERY identifier token spelled like the target, so renaming a method
        // called `Handle` would also rewrite the dozen unrelated `Handle` methods of other types —
        // and this tool writes files. Semantics narrows that to the symbol actually asked for.
        var plan          = TryPlanRename(oldName, root, declaringFile, declaringLine, ct);
        if (plan is { Candidates.Count: > 1 })
            return Ambiguous(oldName, plan.Candidates);
        // ⚠ A narrowing that designates nothing is refused, never dropped: falling through to the text-based path
        // renamed EVERY symbol spelled `old_name` — both Handle methods the model had just picked between — because the
        // file it named was written "./src/Cart.cs" or the line was the identifier's, not the declaration's.
        if ((declaringFile is not null || declaringLine is not null) && plan is not { Spans.Count: > 0 })
            return NarrowingMatchedNothing(oldName, declaringFile, declaringLine, indexAvailable: plan is not null);
        var semanticSpans = plan is { Spans.Count: > 0 } ? plan.Spans : null;

        // ⚠ A root narrowed below the workspace hides every caller outside it: the narrowed compilation does not contain
        // them, so the rename covered the folder, left them on the old name, and the build broke under "Applied to 4
        // file(s)". The symbol is resolved over the whole workspace too; references outside the root refuse the rename,
        // before anything is written.
        if (semanticSpans is not null && NarrowerThanWorkspace(root, workspace)
            && ReferencesOutside(oldName, root, workspace, declaringFile, declaringLine, ct) is { Count: > 0 } outside)
            return OutsideRootRefusal(oldName, root, workspace, outside);

        var hits            = new List<(string FilePath, int Count, string OldContent, string NewContent)>();
        var stale           = new List<string>();
        int totalOccurrences = 0;
        int unreadable       = 0;

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var content = await TextFileEncoding.ReadTextAsync(file, ct);

                var isCSharp = Path.GetExtension(file).Equals(".cs", StringComparison.OrdinalIgnoreCase);
                string newContent;
                int    count;
                if (semanticSpans is not null && isCSharp)
                {
                    bool isStale;
                    (newContent, count, isStale) = ApplySpans(content, semanticSpans.GetValueOrDefault(file), oldName, newName);
                    if (isStale) { stale.Add(file); continue; }
                }
                else
                {
                    (newContent, count) = isCSharp
                        ? RenameInCSharp(content, oldName, newName)
                        : RenameWithRegex(content, oldName, newName);
                }

                if (count > 0)
                {
                    hits.Add((file, count, content, newContent));
                    totalOccurrences += count;
                }
            }
            catch (OperationCanceledException) { throw; }
            // Unreadable here means NOT EXAMINED: counted, never folded into the total the report
            // claims to have scanned.
            catch (Exception ex) { unreadable++; Diagnostics.Swallow("RenameSymbolTool.Read", ex); }
        }

        // ⚠ The compiler's spans are offsets into each file as the index last parsed it. A file edited
        // since (the file watcher refreshes the index after its debounce) would take the rename at the
        // wrong offsets — corrupted code. Refuse rather than guess.
        if (stale.Count > 0)
            return $"Error: the C# index is out of date for {stale.Count} file(s) changed in the last seconds ("
                 + string.Join(", ", stale.Take(5).Select(f => Path.GetRelativePath(root, f)))
                 + "); nothing was renamed. Retry in a few seconds.";

        // Coverage of the whole pass: what the walk found, minus what was too large to open and
        // what refused to be read.
        // ⚠ The unreadable count is its own member, not a subtraction from Scanned: folded into
        // Scanned it came out as the CAP sentence ("only N of M files were scanned (cap)"), which
        // sends the reader to narrow a budget when the fix is a lock or a permission.
        // ⚠ And the folder that could not be LISTED, the worst of the three causes here: this tool
        // WRITES. An invisible folder does not produce an incomplete report, it produces a PARTIAL
        // rename — the calls inside it keep the old name and the code no longer compiles.
        var coverage = new ScanCoverage(files.Count + skippedBySize, files.Count, unreadable)
            .WithGap(WorkspaceScan.FirstWalkGap(root, root));
        var partial  = coverage.IsIncomplete ? "\n" + coverage.Warning() : string.Empty;

        if (hits.Count == 0)
            return $"No occurrences of `{oldName}` found in {files.Count - unreadable} scanned file(s)."
                 + partial;

        // The compiler could not resolve the symbol: every identifier spelled like it gets renamed.
        var textBased = semanticSpans is null
                        && hits.Any(h => h.FilePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase));
        var textBasedWarning =
            $"⚠ Text-based rename: the compiler could not resolve `{oldName}`, so EVERY identifier spelled "
            + "that way is renamed — unrelated symbols included. Check the changes.";

        // ── Preview report ─────────────────────────────────────────────────────
        var sb = new StringBuilder();
        sb.AppendLine($"## rename_symbol: `{oldName}` → `{newName}`");
        sb.AppendLine($"Found **{totalOccurrences}** occurrence(s) in **{hits.Count}** file(s) (scanned {files.Count - unreadable}):");
        if (coverage.IsIncomplete) sb.AppendLine(coverage.Warning());
        if (textBased) sb.AppendLine(textBasedWarning);
        sb.AppendLine();

        foreach (var (f, count, _, _) in hits.OrderBy(h => h.FilePath))
            sb.AppendLine($"- `{Path.GetRelativePath(root, f)}` — {count} occurrence(s)");

        // A name one of the files cannot hold in its own encoding is said now, the dry run included: applied, it would
        // be written as another character.
        foreach (var (f, _, _, newContent) in hits)
        {
            if (FileTarget.UnsavedRefusal(_overlay, f) is { } unsaved) return unsaved;
            if (FileTarget.EncodingRefusal(f, newContent) is { } cannotHold) return cannotHold;
        }

        if (dryRun)
        {
            sb.AppendLine();
            sb.AppendLine("*Dry run — no files modified. Call again with `dry_run: false` to apply.*");
            return sb.ToString().TrimEnd();
        }

        // ── Apply with approval ────────────────────────────────────────────────
        // The prompt is where the human decides: the changed lines, not only a count.
        var details = new StringBuilder(
            $"Rename `{oldName}` → `{newName}` in {hits.Count} file(s) ({totalOccurrences} occurrence(s))");
        if (textBased) details.Append("\n\n").Append(textBasedWarning);
        foreach (var (f, _, oldContent, newContent) in hits.OrderBy(h => h.FilePath).Take(MaxFilesInPrompt))
        {
            details.Append("\n\n### ").Append(Path.GetRelativePath(root, f));
            if (DiffComputer.ComputeText(oldContent, newContent, maxLines: 20) is { } diff)
                details.Append('\n').Append(diff);
        }
        if (hits.Count > MaxFilesInPrompt)
            details.Append($"\n\n… and {hits.Count - MaxFilesInPrompt} more file(s).");

        // Subject for permission rules = every affected file path (newline-separated) so allow/deny
        // path patterns are matched against the actual files, not the localized summary. A deny rule
        // matching ANY affected path blocks the whole rename (the policy short-circuits on first match).
        var subject = string.Join("\n", hits.Select(h => h.FilePath));
        if (!await _approval.RequestApprovalAsync("rename_symbol", details.ToString(), ct, subject: subject))
            return "Rename cancelled by user.";
        if (await FileTarget.UnsavedSinceApprovalAsync(_overlay, hits.Select(h => h.FilePath), ct) is { } typedMeanwhile)
            return typedMeanwhile;

        // Back up every file before writing any: a backup that cannot be saved stops the rename untouched.
        foreach (var (filePath, _, _, _) in hits)
        {
            var (saved, _) = await _history.BackUpBeforeChangeAsync(filePath, ct);
            if (!saved) return FileHistoryService.BackupFailedMessage(filePath);
        }

        // Approved and backed up: a Stop no longer interrupts the writes halfway through the rename.
        // ⚠ And a write that FAILS puts back the files already written. Collecting the error and
        // carrying on left the symbol renamed in eight files out of nine, under a line that read
        // "Applied with 1 error(s)" — a partially applied rename is the one refactor whose half
        // state never compiles. Same funnel as apply_edits, which already owed the model this.
        var write = await SafeFileWriter.WriteAllOrRollBackAsync(
            [.. hits.Select(h => (h.FilePath, h.NewContent, h.OldContent))]);

        sb.AppendLine();
        if (write.Ok)
            // The coverage line is already in the header of this same report — saying it twice in
            // the text the model reads is noise, and noise is how a warning stops being read.
        {
            sb.AppendLine($"✅ Applied to {hits.Count} file(s). Use `restore_file` to undo individual files.");
            // The text-based path has no compiler to find the callers outside a narrowed root: it says where the old
            // name is left (the compiler-resolved path refused above instead).
            if (NarrowerThanWorkspace(root, workspace))
                sb.Append(RenameIntent.StaleNote((oldName, newName), workspace, hits[0].FilePath).TrimStart('\n')).AppendLine();
        }
        else
        {
            var failed = Path.GetRelativePath(root, write.FailedPath!);
            sb.AppendLine(write.Stuck.Count == 0
                ? $"❌ Nothing was renamed: writing {failed} failed ({write.Error}). "
                  + "Every file already written was put back unchanged."
                : $"❌ Writing {failed} failed ({write.Error}), and "
                  + string.Join(", ", write.Stuck.Select(s => Path.GetRelativePath(root, s)))
                  + " could not be put back — restore them with `restore_file`.");
        }

        return sb.ToString().TrimEnd();
    }

    // ── Semantic rename (C#, when a workspace is known) ────────────────────────

    /// <summary>
    /// Tokens to rewrite per file, resolved by the compiler — or null when that is not possible
    /// (no workspace, symbol unresolved, index failure), in which case the caller falls back to
    /// the syntactic path.
    /// </summary>
    /// <summary>Whether <paramref name="root"/> is a folder strictly inside <paramref name="workspace"/>.</summary>
    private static bool NarrowerThanWorkspace(string root, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] string? workspace)
    {
        if (string.IsNullOrEmpty(workspace)) return false;
        var sep = Path.DirectorySeparatorChar;
        var r = Path.GetFullPath(root).TrimEnd(sep, '/');
        var w = Path.GetFullPath(workspace).TrimEnd(sep, '/');
        return !PathComparer.Default.Equals(r, w) && r.StartsWith(w + sep, PathComparer.Comparison);
    }

    private static bool IsUnder(string file, string folder) =>
        Path.GetFullPath(file).StartsWith(Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar, '/') + Path.DirectorySeparatorChar,
                                          PathComparer.Comparison);

    /// <summary>The files outside <paramref name="root"/> that reference the symbol, resolved over the whole workspace;
    /// when the workspace names several symbols this way, every file outside the root that spells the name.</summary>
    private static List<string> ReferencesOutside(
        string oldName, string root, string workspace, string? declaringFile, int? declaringLine, CancellationToken ct)
    {
        var wide = TryPlanRename(oldName, workspace, declaringFile, declaringLine, ct);
        if (wide is { Spans.Count: > 0 })
            return [.. wide.Spans.Keys.Where(f => !IsUnder(f, root)).OrderBy(f => f, StringComparer.Ordinal)];
        return [.. (RenameIntent.FilesStillNaming(workspace, ".cs", oldName) ?? []).Where(f => !IsUnder(f, root))
                   .OrderBy(f => f, StringComparer.Ordinal)];
    }

    private static string OutsideRootRefusal(string oldName, string root, string workspace, List<string> outside)
    {
        var named = string.Join(", ", outside.Take(5).Select(f => Path.GetRelativePath(workspace, f)))
                  + (outside.Count > 5 ? $" and {outside.Count - 5} more" : string.Empty);
        return $"Error: `{oldName}` is also used outside '{Path.GetRelativePath(workspace, root)}': {named}. Renaming inside "
             + "that folder only would leave those on the old name and break the build; nothing was renamed. Call again "
             + "without 'root' to rename across the whole workspace (with declaring_file when the name designates several "
             + "symbols).";
    }

    private static Lsp.RenamePlan? TryPlanRename(
        string oldName, string root, string? declaringFile, int? declaringLine, CancellationToken ct)
    {
        try
        {
            // Nothing resolved (empty spans): the caller falls back rather than silently rename nothing at all.
            return Lsp.CSharpSemanticIndex.ForWorkspace(root).PlanRename(oldName, declaringFile, declaringLine, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Diagnostics.Swallow("RenameSymbolTool.Semantic", ex);
            return null;
        }
    }

    /// <summary>Candidates listed in an ambiguity refusal before the rest is counted.</summary>
    private const int MaxListedCandidates = 20;

    /// <summary>
    /// The refusal when <paramref name="oldName"/> names several symbols: each one, and how to say which — nothing
    /// renamed.
    /// </summary>
    /// <summary>The model narrowed the rename to a declaration, and none matches: nothing is renamed.</summary>
    private static string NarrowingMatchedNothing(string oldName, string? declaringFile, int? declaringLine, bool indexAvailable)
    {
        var asked = string.Join(" and ", new[]
        {
            declaringFile is null ? null : $"declaring_file '{declaringFile}'",
            declaringLine is null ? null : $"declaring_line {declaringLine}",
        }.Where(s => s is not null));
        return indexAvailable
            ? $"Error: no declaration of `{oldName}` matches {asked}; nothing was renamed. The line is the one where the "
              + "declaration starts, attributes included. Call again without them to list the declarations of "
              + $"`{oldName}` with their files and lines."
            : $"Error: {asked} need the C# compiler's view of the workspace, which is not available here; nothing was "
              + $"renamed. Without them, every identifier spelled `{oldName}` would be renamed — check that preview first.";
    }

    internal static string Ambiguous(string oldName, IReadOnlyList<(string Symbol, Lsp.SymbolLocation Location)> candidates)
    {
        var sb = new StringBuilder(
            $"'{oldName}' names {candidates.Count} different symbols — nothing was renamed. Say which one with " +
            "declaring_file (and declaring_line when one file declares several):\n");
        foreach (var (symbol, location) in candidates.Take(MaxListedCandidates))
            sb.Append($"- {symbol} — {location.RelPath}:{location.Line}\n");
        if (candidates.Count > MaxListedCandidates)
            sb.Append($"- … and {candidates.Count - MaxListedCandidates} more\n");
        return sb.ToString().TrimEnd();
    }

    /// <summary>Files whose changed lines the approval prompt shows; the rest are counted.</summary>
    private const int MaxFilesInPrompt = 20;

    /// <summary>Replaces the given spans, back to front so earlier offsets stay valid.</summary>
    /// <returns><c>Stale</c> when a span no longer covers <paramref name="oldName"/> in
    /// <paramref name="content"/> — the file changed since the index parsed it. Nothing is replaced
    /// then: replacing at the old offsets writes the new name over unrelated code.</returns>
    internal static (string NewContent, int Count, bool Stale) ApplySpans(
        string content,
        IReadOnlyList<Microsoft.CodeAnalysis.Text.TextSpan>? spans,
        string oldName,
        string newName)
    {
        if (spans is null || spans.Count == 0) return (content, 0, false);

        foreach (var span in spans)
        {
            if (span.End > content.Length) return (content, 0, true);
            var text = content.AsSpan(span.Start, span.Length);
            var covers = text.SequenceEqual(oldName.AsSpan())
                         || (text.Length == oldName.Length + 1 && text[0] == '@' && text[1..].SequenceEqual(oldName.AsSpan()));
            if (!covers) return (content, 0, true);
        }

        var sb = new StringBuilder(content);
        foreach (var span in spans.OrderByDescending(s => s.Start))
        {
            sb.Remove(span.Start, span.Length);
            sb.Insert(span.Start, newName);
        }
        return (sb.ToString(), spans.Count, false);
    }

    // ── Roslyn rename (C# only) ────────────────────────────────────────────────

    private static (string newContent, int count) RenameInCSharp(
        string content, string oldName, string newName)
    {
        var root = CSharpSyntaxTree.ParseText(content).GetRoot();

        var tokens = root.DescendantTokens()
            .Where(t => t.IsKind(SyntaxKind.IdentifierToken) && t.ValueText == oldName)
            .ToList();

        if (tokens.Count == 0) return (content, 0);

        var newRoot = root.ReplaceTokens(tokens, (original, _) =>
            SyntaxFactory.Identifier(
                original.LeadingTrivia,
                newName,
                original.TrailingTrivia));

        return (newRoot.ToFullString(), tokens.Count);
    }

    // ── Regex rename (other languages) ─────────────────────────────────────────

    private static (string newContent, int count) RenameWithRegex(
        string content, string oldName, string newName)
    {
        // Build a word-boundary pattern for this specific name
        var pattern = $@"(?<!\w){Regex.Escape(oldName)}(?!\w)";
        int count   = 0;
        var result  = Regex.Replace(content, pattern, _ => { count++; return newName; },
                                    RegexOptions.None, RegexBudget.Default);
        return (result, count);
    }

    // ── File enumeration ───────────────────────────────────────────────────────

    /// <summary>
    /// The source files to scan, and how many were left out <b>because of their size</b>.
    /// </summary>
    /// <remarks>
    /// The size filter is not a detail of the walk: this tool rewrites what it finds, so a file it
    /// never opened keeps the old name while the report says the rename is done. The number comes
    /// back so the caller can say it. Files skipped for an unsupported extension are not counted —
    /// they were never candidates.
    /// </remarks>
    private static (List<string> Files, int SkippedBySize) EnumerateSourceFiles(string rootDir, string? filePattern)
    {
        var result = new List<string>();
        var skipped = 0;
        try
        {
            // ONE walk filtered by extension — the per-extension loop walked the whole tree once for
            // every supported language.
            foreach (var f in WorkspaceScan.EnumerateFiles(rootDir, filePattern ?? "*"))
            {
                if (!CodeChunker.SupportedExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
                    continue;
                try
                {
                    if (new FileInfo(f).Length < CodeChunker.MaxFileSizeBytes) result.Add(f);
                    else skipped++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Gone or unreadable between the walk and the stat: this file, not the rest of the
                    // scan — but it counts as not looked at, like an oversized one.
                    skipped++;
                    Diagnostics.Swallow("RenameSymbolTool.ScanFile", ex);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Diagnostics.Swallow("RenameSymbolTool.Scan", ex); }
        return (result, skipped);
    }

    private static bool IsValidIdentifier(string name) =>
        !string.IsNullOrEmpty(name) &&
        (char.IsLetter(name[0]) || name[0] == '_') &&
        name.All(c => char.IsLetterOrDigit(c) || c == '_');

    /// <summary>
    /// The refusal of a QUALIFIED name (<c>Cart.ComputeTotal</c>, <c>Shop::Cart</c>), naming the bare identifiers to
    /// send instead; <c>null</c> when neither name is one.
    /// </summary>
    /// <remarks>
    /// ⚠ "'Cart.CalculateTotal' is not a valid identifier name" names no remedy: a model that wrote the member the
    /// way the user asked for it reads it as a problem with the name the USER chose, blames the request and stops.
    /// Never renamed on the model's behalf — an ill-written call does not become another operation.
    /// </remarks>
    internal static string? QualifiedRefusal(string oldName, string newName)
    {
        string oldBare = Bare(oldName), newBare = Bare(newName);
        if (oldBare == oldName && newBare == newName) return null;
        if (!IsValidIdentifier(oldBare) || !IsValidIdentifier(newBare)) return null;   // not a qualified name: the checks below speak
        var qualified = newBare != newName ? newName : oldName;
        if (oldBare == newBare)
            return $"'{qualified}' is a qualified name, and both names end on `{oldBare}`: rename_symbol renames an "
                 + "identifier, it does not move a member to another type. Nothing was renamed.";
        return $"'{qualified}' is a qualified name: old_name and new_name take the identifier alone — here old_name "
             + $"`{oldBare}`, new_name `{newBare}`. For C#, the compiler finds which symbol it is (declaring_file picks one "
             + "when several share the name). Nothing was renamed.";

        static string Bare(string name)
        {
            int dot = name.LastIndexOf('.'), colons = name.LastIndexOf("::", StringComparison.Ordinal);
            return name[Math.Max(dot < 0 ? 0 : dot + 1, colons < 0 ? 0 : colons + 2)..];
        }
    }
}
