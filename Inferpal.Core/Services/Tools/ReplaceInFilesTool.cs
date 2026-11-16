using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Inferpal.Localization;

namespace Inferpal.Services.Tools;

/// <summary>
/// Replaces a text (or a regular expression) in every file of a folder that holds it, in one call and one approval: the
/// change a model would otherwise make with one edit call per file, each carrying the old and the new text.
/// </summary>
/// <remarks>
/// ⚠ All or nothing, like <c>rename_symbol</c>: every file is backed up before any is written, and a write that fails
/// puts back the files already written (<see cref="SafeFileWriter.WriteAllOrRollBackAsync"/>) — half a replacement across a
/// code base is the state nobody can review. Past <see cref="MaxFiles"/> files nothing is written: the call is narrowed,
/// never cut. ⚠ The files are the walk's (<see cref="WorkspaceScan.EnumerateFiles(string, string, out bool)"/>): the
/// ignore files' exclusions, generated folders and the size cap are the search's, and what was not read is said.
/// </remarks>
internal sealed class ReplaceInFilesTool : ITool
{
    private readonly IApprovalService _approval;
    private readonly FileHistoryService _history;
    private readonly Func<string?> _getRoot;
    private readonly Editor.OpenDocumentOverlay? _overlay;

    public ReplaceInFilesTool(IApprovalService approval, FileHistoryService history, Func<string?> getRoot,
                              Editor.OpenDocumentOverlay? overlay = null)
    {
        _approval = approval;
        _history  = history;
        _getRoot  = getRoot;
        _overlay  = overlay;
    }

    private static readonly Regex LoneLineFeed = new(@"(?<!\r)\n", RegexOptions.CultureInvariant, RegexBudget.Default);

    /// <summary>The most files one call changes — within what the approval judges path by path, other names included
    /// (<c>ApprovalServiceBase.PathForms</c>).</summary>
    internal const int MaxFiles = 64;

    /// <summary>Files the approval card shows a diff of; the rest are counted.</summary>
    private const int MaxFilesInPrompt = 8;

    private const long MaxFileBytes = 8 * 1024 * 1024;

    public string Name => "replace_in_files";

    /// <summary>Every file it writes is backed up first: the run follows it without watching the work tree.</summary>
    public bool NeedsWorkTreeWatch => false;

    /// <inheritdoc/>
    public ToolEffect EffectOf(JsonElement args) => ToolEffect.Writes;

    public string Description =>
        "Replaces a text in every file of a folder that contains it, in one call and one approval — instead of one edit per " +
        "file. Exact, case-sensitive text by default (line breaks follow each file's own); regex=true for a regular " +
        "expression ($1 in the replacement, $$ for a literal $; $ and \\n mean a line end in every file). Not syntax-checked: build or test " +
        "after it. All or nothing; dry_run=true lists the files and counts without writing.";

    public object Parameters => new
    {
        type = "object",
        properties = new
        {
            find         = new { type = "string",  description = "The text (or regular expression) to replace." },
            replace      = new { type = "string",  description = "What replaces it (may be empty)." },
            path         = new { type = "string",  description = "Folder (or file) to change (default: the workspace)." },
            file_pattern = new { type = "string",  description = "File filter, e.g. *.cs (default: *)." },
            regex        = new { type = "boolean", description = "Read find as a regular expression (default: false)." },
            dry_run      = new { type = "boolean", description = "List what would change without writing (default: false)." },
        },
        required = new[] { "find", "replace" },
    };

    private sealed record Hit(string Path, int Count, string Old, string New);

    public async Task<string> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var workspace = _getRoot();
        if (string.IsNullOrEmpty(workspace)) throw new NoWorkspaceRootException();
        var find    = args.Str("find") ?? throw new ArgumentException("find is required.");
        var replace = args.Str("replace") ?? throw new ArgumentException("replace is required.");
        if (find.Length == 0) return "Error: find is empty: say the text to replace.";
        var path = args.Str("path") is { Length: > 0 } p ? PathSanitizer.Sanitize(p, workspace) : workspace;
        PathSanitizer.AssertUnderRoot(path, workspace);
        var rawPattern = args.Str("file_pattern");
        if (WorkspaceScan.NormalizeFilePattern(rawPattern) is not { } filePattern)
            return WorkspaceScan.InvalidPatternMessage("file_pattern", rawPattern);
        var useRegex = args.Bool("regex", false);
        var dryRun   = args.Bool("dry_run", false);

        Regex? regex = null;
        if (useRegex && System.Text.RegularExpressions.Regex.IsMatch(replace, @"(?<!\$)(?:\$\$)*\$[_`'+]", RegexOptions.None, RegexBudget.Default))
            return "Error: the replacement holds $_, $`, $' or $+ — in a regular expression's replacement these insert the whole " +
                   "file or its edges. Write $$ for a literal $, or pass regex=false.";
        if (useRegex)
        {
            try { regex = new Regex(find, RegexOptions.CultureInvariant | RegexOptions.Multiline, RegexBudget.Default); }
            catch (ArgumentException ex) { return $"Error: '{find}' is not a valid regular expression ({ex.Message}). Pass regex=false to replace it as text."; }
        }

        var singleFile = File.Exists(path);
        if (!singleFile && !Directory.Exists(path)) return Strings.DirNotFound(path);
        var walkFailed = false;
        var walk = singleFile ? [path] : WorkspaceScan.EnumerateFiles(path, filePattern, out walkFailed);
        if (walkFailed)
            return $"Could not walk '{path}' (permissions, or a path the file system refused): nothing was replaced.";

        var hits = new List<Hit>();
        int scanned = 0, skippedLarge = 0, unreadable = 0, skippedBinary = 0, generated = 0, outside = 0, matching = 0;
        foreach (var file in walk.OrderBy(f => f, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                // A document's text is extracted, not its bytes: replaced there, the file would be written back as text.
                if (Documents.DocumentText.ToolsRead(file) || Documents.ChatImages.Handles(file)) { skippedBinary++; continue; }
                if (new FileInfo(file).Length > MaxFileBytes) { skippedLarge++; continue; }
                if (TextFileEncoding.IsBinaryFile(file)) { skippedBinary++; continue; }
                var old = _overlay is not null && _overlay.TryGetUnsaved(file, out var buffered)
                    ? buffered
                    : await TextFileEncoding.ReadTextAsync(file, ct);
                scanned++;
                int count;
                string changed;
                if (regex is not null)
                {
                    // A file all in CRLF is matched as the model reads it, in LF ($ before the break, \n for a break), and
                    // written back in CRLF. A file with mixed breaks is matched as it is: converting it would touch lines the
                    // replacement does not.
                    var crlf = old.Contains("\r\n", StringComparison.Ordinal) && !LoneLineFeed.IsMatch(old);
                    var text = crlf ? old.Replace("\r\n", "\n", StringComparison.Ordinal) : old;
                    count = regex.Matches(text).Count;
                    var result = count > 0 ? regex.Replace(text, replace.Replace("\r\n", "\n", StringComparison.Ordinal)) : text;
                    changed = crlf ? result.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal) : result;
                }
                else
                {
                    // A model writes \n: in a CRLF file the text it means has \r\n, and what it writes keeps the file's breaks.
                    var eol = LineEndings.Dominant(old);
                    var findHere = LineEndings.ToEol(find, eol);
                    count = Occurrences(old, findHere);
                    changed = count > 0 ? old.Replace(findHere, LineEndings.ToEol(replace, eol), StringComparison.Ordinal) : old;
                }
                if (count == 0 || string.Equals(changed, old, StringComparison.Ordinal)) continue;
                // A generated file is rewritten by its generator: the tools that edit by hand leave it, and say so.
                if (GeneratedFiles.Of(file, old) is not null) { generated++; continue; }
                // ⚠ The walk lists a linked FILE (it only stops at linked folders): written through, it would change a file
                // outside the workspace — ~/.bashrc behind docs/notes.md. Checked on the files that would change only.
                try { PathSanitizer.AssertUnderRoot(file, workspace); }
                catch (ArgumentException) { outside++; continue; }
                // Past the cap, counted only: the call is refused, and holding every text to say so is no use.
                if (++matching > MaxFiles) continue;
                hits.Add(new Hit(file, count, old, changed));
            }
            catch (RegexMatchTimeoutException)
            {
                return $"Error: the regular expression took too long on '{Shown(file, workspace)}': nothing was replaced. Simplify it.";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { unreadable++; }
        }

        var gaps = Gaps(skippedLarge, unreadable, skippedBinary, generated, outside)
                 + (singleFile ? string.Empty : WorkspaceScan.FirstWalkGap(path, workspace) is { } gap ? "; " + gap.Sentence() : string.Empty);
        if (hits.Count == 0)
            return $"No file under '{Shown(path, workspace)}' contains {(useRegex ? "a match of" : "the text")} '{find}' "
                 + $"({scanned} file(s) read){gaps}. Nothing was changed.";
        if (matching > MaxFiles)
            return $"Error: {matching} files would change, more than {MaxFiles} in one call. Nothing was changed: narrow path "
                 + "or file_pattern and call again.";

        var total = hits.Sum(h => h.Count);
        var sb = new StringBuilder();
        sb.AppendLine($"replace_in_files: '{find}' → '{replace}', {total} occurrence(s) in {hits.Count} file(s) ({scanned} read){gaps}:");
        foreach (var h in hits) sb.AppendLine($"- {Shown(h.Path, workspace)} — {h.Count}");

        foreach (var h in hits)
        {
            if (FileTarget.UnsavedRefusal(_overlay, h.Path) is { } unsaved)
            {
                if (dryRun) { sb.AppendLine($"(Not appliable yet: {unsaved})"); break; }
                return unsaved;
            }
            if (FileTarget.EncodingRefusal(h.Path, h.New) is { } cannotHold) return cannotHold;
        }
        if (dryRun) return sb.Append("Dry run — no file was changed. Call again without dry_run to apply.").ToString();

        var details = new StringBuilder(Strings.ReplaceConfirm(OnCard(find), OnCard(replace), hits.Count, total));
        foreach (var h in hits.Take(MaxFilesInPrompt))
        {
            details.Append("\n\n### ").Append(Shown(h.Path, workspace));
            if (CodeActions.DiffComputer.ComputeText(h.Old, h.New, maxLines: 20) is { } diff) details.Append('\n').Append(diff);
        }
        if (hits.Count > MaxFilesInPrompt) details.Append("\n\n").Append(Strings.ReplaceMoreFiles(hits.Count - MaxFilesInPrompt));
        // Every file the call writes is the subject: a deny rule or an instruction file among them is judged on its path.
        if (!await _approval.RequestApprovalAsync(Name, details.ToString(), ct, subject: string.Join("\n", hits.Select(h => h.Path))))
            return Strings.DiffCancelled;
        if (await FileTarget.ChangedSinceApprovalAsync(_overlay, hits.Select(h => new ApprovedFile(h.Path, h.Old)), ct) is { } typedMeanwhile)
            return typedMeanwhile;

        foreach (var h in hits)
        {
            var (saved, _) = await _history.BackUpBeforeChangeAsync(h.Path, ct);
            if (!saved) return _history.BackupFailedMessage(h.Path);
        }
        var write = await SafeFileWriter.WriteAllOrRollBackAsync([.. hits.Select(h => (h.Path, h.New, h.Old))]);
        if (write.Ok) return sb.Append($"Applied to {hits.Count} file(s). /undo-run or restore_file puts them back.").ToString();
        return write.Stuck.Count == 0
            ? $"Error: nothing was replaced: writing {Shown(write.FailedPath!, workspace)} failed ({write.Error}), and every file already written was put back."
            : $"Error: writing {Shown(write.FailedPath!, workspace)} failed ({write.Error}), and "
              + string.Join(", ", write.Stuck.Select(s => Shown(s, workspace))) + " could not be put back — restore them with restore_file.";
    }

    private static int Occurrences(string text, string find)
    {
        var count = 0;
        for (var at = text.IndexOf(find, StringComparison.Ordinal); at >= 0; at = text.IndexOf(find, at + find.Length, StringComparison.Ordinal))
            count++;
        return count;
    }

    /// <summary>A text as the card shows it between backticks: one line, bounded, its own backticks made harmless.</summary>
    private static string OnCard(string text)
    {
        var line = text.Replace("\r", "").Replace('\n', '⏎').Replace('`', '\'');
        return line.Length > 80 ? line[..79] + "…" : line;
    }

    private static string Gaps(int large, int unreadable, int binary, int generated, int outside)
    {
        var parts = new List<string>();
        if (outside > 0) parts.Add($"{outside} linked file(s) leading outside the workspace left as they are");
        if (large > 0) parts.Add($"{large} over {MaxFileBytes / (1024 * 1024)} MB not read");
        if (unreadable > 0) parts.Add($"{unreadable} unreadable");
        if (binary > 0) parts.Add($"{binary} binary, image or document file(s) not searched");
        if (generated > 0) parts.Add($"{generated} generated file(s) left as they are (change their source)");
        return parts.Count == 0 ? string.Empty : "; " + string.Join(", ", parts);
    }

    private static string Shown(string path, string workspace) => Governance.ContentExclusions.Shown(path, workspace);
}
