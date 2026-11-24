using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using Inferpal.Localization;

namespace Inferpal.Services.Tools;

internal class SearchInFilesTool : ITool
{
    private readonly Func<string?> _getWorkspaceRoot;
    private readonly Editor.OpenDocumentOverlay? _overlay;

    public SearchInFilesTool(Func<string?> getWorkspaceRoot, Editor.OpenDocumentOverlay? overlay = null)
    {
        _getWorkspaceRoot = getWorkspaceRoot;
        _overlay          = overlay;
    }

    public string Name => "search_in_files";
    /// <inheritdoc/>
    public ToolEffect EffectOf(JsonElement args) => ToolEffect.Reads;
    public string Description => "Searches for text or a regex pattern in files, the text of documents (PDF, Office, OpenDocument, Outlook .msg, event logs, Visio diagrams) included. Returns file:line:content.";
    public object Parameters => new
    {
        type = "object",
        properties = new
        {
            path         = new { type = "string", description = "Root directory of the search." },
            pattern      = new { type = "string", description = "Text or regular expression to search for." },
            file_pattern = new { type = "string", description = "File filter, e.g. *.cs (default: *)" }
        },
        required = new[] { "path", "pattern" }
    };

    public Task<string> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var root        = _getWorkspaceRoot();
        var path        = PathSanitizer.Sanitize(args.Str("path"), root);
        PathSanitizer.AssertUnderRoot(path, root);
        var search      = args.Str("pattern") ?? throw new ArgumentException("pattern is required.");
        var rawPattern  = args.Str("file_pattern");
        if (WorkspaceScan.NormalizeFilePattern(rawPattern) is not { } filePattern)
            return Task.FromResult(WorkspaceScan.InvalidPatternMessage("file_pattern", rawPattern));

        // ⚠ A FILE is searched, not refused: "Directory not found" for a path that exists read "this file does not
        // exist", on the most natural request there is — searching one file.
        var singleFile = File.Exists(path);
        if (!singleFile && !Directory.Exists(path))
            return Task.FromResult(ToolFailure.Said(Strings.DirNotFound(path)));
        var searchRoot = singleFile ? Path.GetDirectoryName(path)! : path;

        // ⚠ "Text OR a regular expression", so a pattern that parses as a regex may still be meant as text: read
        // only as a regex, `DoWork(x)` was "DoWorkx", `arr[i]` "arri", `obj?.Prop` an optional j — each missing the
        // text that IS in the file, and "no results" reads as "not used anywhere". Such a pattern matches both
        // ways: a real regex keeps its matches, and the literal text is never answered as absent.
        var (regex, literal) = Matchers(search);

        var results = new List<string>();

        // Lazy enumeration + the same artefact exclusions as the semantic index: GetFiles
        // materialises the whole tree (long seconds on a node project) and searches .git/,
        // node_modules/, bin/, obj/ — and a 100-result cap checked per FILE lets a single minified
        // file add tens of thousands of lines.
        // ⚠ "No results" is a CONCLUSION the model acts on — it stops looking. A walk that could
        // not start is not that answer, so this catch must not return it.
        var walkFailed = false;
        var walk = singleFile ? [path] : WorkspaceScan.EnumerateFiles(path, filePattern, out walkFailed);
        // ⚠ Sorted BEFORE the caps, like list_files and ScanCoverage.Take: the walk yields in file-system order —
        // arbitrary on POSIX — so "the first 100 matches" were a subset chosen by the volume, different between two
        // identical calls, and the notes below name "the first" of nothing.
        var files = walkFailed ? walk : walk.OrderBy(f => f, StringComparer.Ordinal);
        if (walkFailed)
            return Task.FromResult(
                $"Could not search '{path}': the directory could not be walked (permissions, or a "
                + "path the file system refused). Nothing was searched — this is NOT \"the pattern "
                + "is absent from the code\".");

        var seen = 0;
        var documentWatch = new System.Diagnostics.Stopwatch();
        var tally = new Tally();
        foreach (var file in files)
        {
            if (ct.IsCancellationRequested) break;
            if (results.Count >= MaxResults) break;
            seen++;

            try
            {
                var relPath = file[searchRoot.Length..].TrimStart('\\', '/');
                List<string> lines;
                var headerLines = 0;
                // ⚠ The unsaved buffer when the editor holds one, as read_file shows it: searched on disk only, a
                // symbol the user just typed came back "no results" — read "not defined" — from the very file
                // read_file showed it in.
                if (_overlay is not null && _overlay.TryGetUnsaved(file, out var buffered))
                {
                    lines = [.. buffered.ReplaceLineEndings("\n").Split('\n')];
                    if (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
                }
                else if (Documents.DocumentText.ToolsRead(file))
                {
                    // A PDF or DOCX is searched in its extracted text — read_file's, so a line reported here is the line it
                    // shows. Extraction is bounded per search: past its time, the rest are counted and said, never "no match".
                    if (documentWatch.Elapsed > DocumentSearchBudget) { tally.DocumentsNotSearched++; continue; }
                    // An MSBuild binary log is read by replaying it (a dotnet run of up to a minute): a search never starts one,
                    // it searches a log already read and names the others.
                    if (file.EndsWith(".binlog", StringComparison.OrdinalIgnoreCase) && !Documents.AttachedDocuments.IsRead(file))
                    { tally.BuildLogsNotReplayed++; continue; }
                    documentWatch.Start();
                    var extracted = Documents.AttachedDocuments.ForModel(file, ct);
                    documentWatch.Stop();
                    if (extracted is null) { tally.DocumentsWithoutText++; continue; }
                    // Read in part (a long event log keeps its newest events): what was left out is not searched.
                    if (Documents.AttachedDocuments.Read(file, ct).StoppedAt > 0) tally.DocumentsInPart.Add(relPath);
                    tally.DocumentsSearched++;
                    lines = [.. extracted.Split('\n')];
                    // Inferpal's own header ("[Text extracted from …: images are not here.]") is not the document: never
                    // a match, its lines kept so the numbers stay read_file's.
                    headerLines = lines.IndexOf(string.Empty) + 1;
                }
                else
                {
                    // A multi-megabyte file (a dump, a bundle) was loaded whole for a few truncated matches
                    // at best. Skipped — and said, since a silent skip reads as "not in the code".
                    if (new FileInfo(file).Length > MaxSearchFileBytes) { tally.SkippedLarge++; continue; }
                    // Decoded like the file an edit will rewrite: a line shown with "�" could not be quoted back.
                    lines = TextFileEncoding.ReadLines(file);
                    // A binary file's "lines" are noise, NULs included: said once, as grep does, never shown.
                    if (TextFileEncoding.IsBinaryFile(file))
                    {
                        if (lines.Any(l => regex.IsMatch(l) || literal?.IsMatch(l) == true))
                            results.Add($"{relPath}: binary file matches");
                        continue;
                    }
                }
                for (int i = headerLines; i < lines.Count && results.Count < MaxResults; i++)
                {
                    if (!regex.IsMatch(lines[i]) && literal?.IsMatch(lines[i]) != true) continue;
                    var line = lines[i].Trim();
                    if (line.Length > MaxLineChars) line = line[..MaxLineChars] + "…";   // a minified line is not a result
                    results.Add($"{relPath}:{i + 1}: {line}");
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { tally.Unreadable++; Diagnostics.Swallow("SearchInFilesTool.ReadFile", ex); }
        }

        // ⚠ No file searched is not "no match": a filter for a language the project does not hold (`*.cs` in a
        // JavaScript project) selects nothing, and "No results" read as "the text is not in the code".
        if (seen == 0 && !ct.IsCancellationRequested)
        {
            var none = filePattern == "*"
                ? $"No file was found under '{path}' — nothing was searched."
                : $"No file under '{path}' matches file_pattern '{rawPattern}', so nothing was searched — this is NOT "
                  + "\"the text is absent\". Leave file_pattern out to search every file.";
            // A folder the walk could not list may be exactly where the files are.
            if (WorkspaceScan.FirstWalkGap(path, root) is { } noneGap) none += $"\n({noneGap.Sentence()})";
            return Task.FromResult(none);
        }

        // What reaches the model is what fits its context: past that the loop would cut the MIDDLE of the matches.
        var shown = ToolOutputBudget.LinesThatFit(results);
        var notes = Notes(results, shown, tally, singleFile ? null : WorkspaceScan.FirstWalkGap(path, root));
        return Task.FromResult((results.Count == 0 ? Strings.NoResults : string.Join("\n", results.Take(shown))) + notes);
    }

    /// <summary>The most characters of a matching line shown: a minified line is not a result.</summary>
    private const int MaxLineChars = 400;

    /// <summary>The files named where the matches that did not fit the context are.</summary>
    private const int FilesNamed = 20;

    /// <summary>The documents named that were searched only in part.</summary>
    private const int DocumentsNamed = 5;

    /// <summary>What the walk could not search, counted as it goes, said under the matches.</summary>
    private sealed class Tally
    {
        public int SkippedLarge, Unreadable, DocumentsSearched, DocumentsWithoutText, DocumentsNotSearched, BuildLogsNotReplayed;
        public List<string> DocumentsInPart { get; } = [];
    }

    /// <summary>The pattern as a regular expression, and as its literal text when it parses as a regex too ("Text OR a
    /// regular expression": <c>DoWork(x)</c> is meant as text as often as not); a pattern that does not parse is its text.</summary>
    private static (Regex Regex, Regex? Literal) Matchers(string search)
    {
        try
        {
            var regex = new Regex(search, RegexOptions.IgnoreCase | RegexOptions.Compiled, RegexBudget.Default);
            return (regex, Regex.Escape(search) != search
                ? new Regex(Regex.Escape(search), RegexOptions.IgnoreCase | RegexOptions.Compiled, RegexBudget.Default)
                : null);
        }
        catch { return (new Regex(Regex.Escape(search), RegexOptions.IgnoreCase | RegexOptions.Compiled, RegexBudget.Default), null); }
    }

    /// <summary>Every reason the answer may be incomplete, each on its line under the matches.</summary>
    private static string Notes(List<string> results, int shown, Tally tally, WorkspaceScan.WalkGap? gap)
    {
        // Every reason the answer may be incomplete, said. The size skip already was; the RESULT
        // CAP was not, and it is the one that shapes a conclusion — a model asking "where is this
        // used?" reads exactly a hundred lines as the whole list and refactors on it.
        var notes = new System.Text.StringBuilder();
        if (shown < results.Count)
        {
            // Each result starts with its relative path and a colon ("src/a.cs:12: …", "img.bin: binary file matches").
            var elsewhere = results.Skip(shown).Select(r => r[..Math.Max(0, r.IndexOf(':'))])
                                   .Where(f => f.Length > 0).Distinct().ToList();
            notes.Append($"\n(showing the first {shown} of {results.Count} match(es) found — the rest would not fit the "
                       + $"context; they are in: {string.Join(", ", elsewhere.Take(FilesNamed))}"
                       + (elsewhere.Count > FilesNamed ? $", +{elsewhere.Count - FilesNamed} more file(s)" : "")
                       + " — search those files, or narrow the pattern)");
        }
        if (results.Count >= MaxResults)
            notes.Append($"\n(stopped at the first {MaxResults} match(es) — narrow the path or the "
                       + "pattern to see the rest; this is NOT the complete list)");
        if (tally.SkippedLarge > 0)
            notes.Append($"\n({tally.SkippedLarge} file(s) larger than {MaxSearchFileBytes / (1024 * 1024)} MB were not searched.)");
        if (tally.Unreadable > 0)
            notes.Append($"\n({tally.Unreadable} file(s) could not be read and were not searched.)");
        if (tally.DocumentsSearched > 0 && results.Count > 0)
            notes.Append($"\n({tally.DocumentsSearched} PDF/DOCX file(s) were searched in their extracted text; read_file shows them at "
                       + "the same line numbers.)");
        if (tally.DocumentsInPart.Count > 0)
            notes.Append($"\n({tally.DocumentsInPart.Count} document(s) were searched only in part — their text is cut where read_file "
                       + $"cuts it (an event log keeps its newest events): {string.Join(", ", tally.DocumentsInPart.Take(DocumentsNamed))}"
                       + (tally.DocumentsInPart.Count > DocumentsNamed ? $", +{tally.DocumentsInPart.Count - DocumentsNamed} more" : "")
                       + ". No match there is NOT \"absent from them\".)");
        if (tally.DocumentsWithoutText > 0)
            notes.Append($"\n({tally.DocumentsWithoutText} PDF/DOCX file(s) give no text to search (a scan, images, a password, or "
                       + "too large): read_file on one says which.)");
        if (tally.BuildLogsNotReplayed > 0)
            notes.Append($"\n({tally.BuildLogsNotReplayed} MSBuild binary log(s) (.binlog) were not searched: a search does not replay "
                       + "them — read_file on one replays it, and a search after that includes it. This is NOT \"absent from them\".)");
        if (tally.DocumentsNotSearched > 0)
            notes.Append($"\n({tally.DocumentsNotSearched} PDF/DOCX file(s) were not searched: reading documents stopped after "
                       + $"{DocumentSearchBudget.TotalSeconds:0} s — search one by its path, or narrow file_pattern. This is NOT "
                       + "\"absent from them\".)");
        // ⚠ The last reason, and the only one whose files are never even ENUMERATED: a folder the
        // walk could not list, or one it will not follow because it is a link. No count above can
        // express it — those files are absent from every total — so it names the folder instead.
        // The sentence belongs to WalkGap and is never written here: "cannot be listed" and "is a
        // link, not followed" send the reader to two different places.
        if (gap is { } walkGap)
            notes.Append($"\n({walkGap.Sentence()})");
        return notes.ToString();

    }

    /// <summary>Time one search spends extracting PDF and DOCX text (each read once per version of the file, then kept):
    /// a folder of a few hundred documents would otherwise hold the tool for minutes.</summary>
    internal static readonly TimeSpan DocumentSearchBudget = TimeSpan.FromSeconds(10);

    /// <summary>Largest file read line by line — past it the file is skipped and counted.</summary>
    internal const long MaxSearchFileBytes = 8 * 1024 * 1024;

    /// <summary>
    /// Matches returned at most. Reaching it is SAID: a truncated list read as a complete one is how
    /// a model concludes a symbol is used in exactly a hundred places.
    /// </summary>
    internal const int MaxResults = 100;
}
