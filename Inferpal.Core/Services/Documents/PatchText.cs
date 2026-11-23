using System.Globalization;
using System.IO;
using System.Text;

namespace Inferpal.Services.Documents;

/// <summary>
/// A patch — <c>git diff</c>, <c>git format-patch</c> (its subject and author), <c>diff -u</c> — read as its files and
/// their blocks, and applied to a file's text block by block (<c>apply_edits</c> with <c>patch</c>).
/// </summary>
/// <remarks>
/// ⚠ A block is placed only where its context and removed lines stand exactly, trailing white space aside: at its line,
/// or shifted to the nearest place that holds them. Never fuzzy — a block placed on lines that only look alike changes
/// the wrong code. A block whose result is already there is "already applied", never applied twice. Never throws for a
/// malformed patch: what it cannot read is not a block.
/// </remarks>
internal static class PatchText
{
    private const int MaxChars = DocumentText.ReportMaxChars;
    private const int JudgedFileBytes = 4_000_000;

    internal enum FileKind { Modify, Add, Delete, Rename, Copy, Binary, ModeOnly }

    internal sealed record Hunk(int OldStart, int OldCount, int NewStart, int NewCount, string Heading,
                                IReadOnlyList<(char Op, string Text)> Lines, bool OldNoEol, bool NewNoEol)
    {
        /// <summary>The lines the file holds before: context and removed.</summary>
        public List<string> Before => [.. Lines.Where(l => l.Op != '+').Select(l => l.Text)];
        /// <summary>The lines it holds after: context and added.</summary>
        public List<string> After => [.. Lines.Where(l => l.Op != '-').Select(l => l.Text)];
        public int Added => Lines.Count(l => l.Op == '+');
        public int Removed => Lines.Count(l => l.Op == '-');
        /// <summary>The block as the patch writes it, for a refusal that hands it back.</summary>
        public string Text => $"@@ -{OldStart},{OldCount} +{NewStart},{NewCount} @@{(Heading.Length > 0 ? " " + Heading : "")}\n"
                            + string.Join("\n", Lines.Select(l => l.Op + l.Text));
    }

    internal sealed record PatchFile(string? OldPath, string? NewPath, FileKind Kind, IReadOnlyList<Hunk> Hunks, bool GitPrefixed,
                                     bool ModeChanged = false)
    {
        /// <summary>The path the patch changes, as written (no <c>a/</c>/<c>b/</c> prefix of a git patch).</summary>
        public string Path => (Kind == FileKind.Delete ? OldPath : NewPath ?? OldPath) ?? "";
    }

    internal sealed record Patch(IReadOnlyList<PatchFile> Files, IReadOnlyList<string> Subjects, string? Author, string? Date);

    /// <summary>The patch <paramref name="text"/> holds; <c>null</c> when it holds no file diff.</summary>
    internal static Patch? Parse(string text)
    {
        if (text.Length > MaxChars) return null;
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var files = new List<PatchFile>();
        var subjects = new List<string>();
        string? author = null, date = null;

        string? oldPath = null, newPath = null;
        var kind = FileKind.Modify;
        var git = false;
        var open = false;
        var modeChanged = false;
        var hunks = new List<Hunk>();

        void Close()
        {
            if (!open) return;
            if (kind == FileKind.Modify && hunks.Count == 0 && oldPath is not null && newPath is not null && oldPath != newPath)
                kind = FileKind.Rename;
            else if (kind == FileKind.Modify && hunks.Count == 0 && git) kind = FileKind.ModeOnly;
            files.Add(new PatchFile(oldPath, newPath, kind, [.. hunks], git, modeChanged));
            open = false; oldPath = newPath = null; kind = FileKind.Modify; git = false; modeChanged = false; hunks = [];
        }

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            {
                // format-patch's mail headers, before the diff of each commit (a block's lines never reach here).
                if (line.StartsWith("Subject: ", StringComparison.Ordinal))
                {
                    var subject = line["Subject: ".Length..].Trim();
                    while (i + 1 < lines.Length && lines[i + 1].StartsWith(' ')) subject += " " + lines[++i].Trim();
                    subjects.Add(System.Text.RegularExpressions.Regex.Replace(subject, @"^\[PATCH[^\]]*\]\s*", ""));
                    continue;
                }
                if (author is null && line.StartsWith("From: ", StringComparison.Ordinal)) { author = line[6..].Trim(); continue; }
                if (date is null && line.StartsWith("Date: ", StringComparison.Ordinal)) { date = line[6..].Trim(); continue; }
            }
            if (line.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                Close();
                open = true; git = true;
                if (GitHeaderPaths(line["diff --git ".Length..]) is var (a, b)) { oldPath = a; newPath = b; }
                continue;
            }
            if (open && git && hunks.Count == 0)
            {
                if (line.StartsWith("new file mode", StringComparison.Ordinal)) { kind = FileKind.Add; oldPath = null; continue; }
                if (line.StartsWith("deleted file mode", StringComparison.Ordinal)) { kind = FileKind.Delete; newPath = null; continue; }
                if (line.StartsWith("rename from ", StringComparison.Ordinal)) { oldPath = Unquote(line[12..]); kind = FileKind.Rename; continue; }
                if (line.StartsWith("rename to ", StringComparison.Ordinal)) { newPath = Unquote(line[10..]); kind = FileKind.Rename; continue; }
                // A copy keeps its source: its blocks make a NEW file from the source's text, never an edit of the source.
                if (line.StartsWith("copy from ", StringComparison.Ordinal)) { oldPath = Unquote(line[10..]); kind = FileKind.Copy; continue; }
                if (line.StartsWith("copy to ", StringComparison.Ordinal)) { newPath = Unquote(line[8..]); kind = FileKind.Copy; continue; }
                if (line.StartsWith("old mode ", StringComparison.Ordinal) || line.StartsWith("new mode ", StringComparison.Ordinal))
                { modeChanged = true; continue; }
                if (line.StartsWith("Binary files ", StringComparison.Ordinal) || line == "GIT binary patch") { kind = FileKind.Binary; continue; }
            }
            if (line.StartsWith("--- ", StringComparison.Ordinal) && i + 1 < lines.Length && lines[i + 1].StartsWith("+++ ", StringComparison.Ordinal))
            {
                // A plain diff starts its file here; a git one has already started it.
                if (!(open && git && hunks.Count == 0)) { Close(); open = true; }
                var from = FilePath(line[4..], git);
                var to = FilePath(lines[++i][4..], git);
                if (from is null && kind is not (FileKind.Rename or FileKind.Copy)) kind = FileKind.Add;
                if (to is null && kind is not (FileKind.Rename or FileKind.Copy)) kind = FileKind.Delete;
                if (from is not null) oldPath = from; else oldPath = null;
                if (to is not null) newPath = to; else newPath = null;
                continue;
            }
            if (open && line.StartsWith("@@ ", StringComparison.Ordinal) && ReadHunk(lines, ref i) is { } hunk)
            {
                hunks.Add(hunk);
                continue;
            }
        }
        Close();
        return files.Count == 0 ? null : new Patch(files, subjects, author, date);
    }

    /// <summary>The two paths of a <c>diff --git a/x b/x</c> line, prefixes removed; quoted paths read.</summary>
    private static (string, string)? GitHeaderPaths(string rest)
    {
        if (rest.StartsWith('"'))
        {
            var end = QuotedEnd(rest);
            if (end < 0 || end + 2 > rest.Length) return null;
            return (StripPrefix(Unquote(rest[..(end + 1)])), StripPrefix(Unquote(rest[(end + 2)..])));
        }
        // Same path on both sides is the common case: both halves are equal, so the split is the middle space.
        var middle = rest.Length / 2;
        if (rest.Length % 2 == 1 && rest[middle] == ' ' && StripPrefix(rest[..middle]) == StripPrefix(rest[(middle + 1)..]))
            return (StripPrefix(rest[..middle]), StripPrefix(rest[(middle + 1)..]));
        var mid = rest.IndexOf(" b/", StringComparison.Ordinal);
        return mid < 0 ? null : (StripPrefix(rest[..mid]), StripPrefix(rest[(mid + 1)..]));
    }

    /// <summary>The index of the quote that closes the quoted path <paramref name="s"/> opens; -1 when none does.</summary>
    private static int QuotedEnd(string s)
    {
        for (var k = 1; k < s.Length; k++)
        {
            if (s[k] == '\\') { k++; continue; }
            if (s[k] == '"') return k;
        }
        return -1;
    }

    // git's prefixes: a/ b/, and with diff.mnemonicPrefix c/ w/ i/ o/.
    private static string StripPrefix(string path) =>
        path.Length > 2 && path[1] == '/' && path[0] is 'a' or 'b' or 'c' or 'w' or 'i' or 'o' ? path[2..] : path;

    /// <summary>A path as git quotes it (<c>"caf\303\251.txt"</c>: C escapes, octal bytes of UTF-8) read back; any other
    /// text as it is.</summary>
    internal static string Unquote(string path)
    {
        if (path.Length < 2 || path[0] != '"' || path[^1] != '"') return path;
        var bytes = new List<byte>();
        for (var k = 1; k < path.Length - 1; k++)
        {
            var c = path[k];
            if (c != '\\' || k + 1 >= path.Length - 1) { bytes.AddRange(Encoding.UTF8.GetBytes(c.ToString())); continue; }
            var next = path[++k];
            if (next is >= '0' and <= '7' && k + 2 < path.Length - 1)
            {
                bytes.Add((byte)Convert.ToInt32(path.Substring(k, 3), 8));
                k += 2;
                continue;
            }
            bytes.AddRange(Encoding.UTF8.GetBytes(next switch { 't' => "\t", 'n' => "\n", 'r' => "\r", 'a' => "\a", 'b' => "\b", 'f' => "\f", 'v' => "\v", _ => next.ToString() }));
        }
        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    /// <summary>The path of a <c>---</c>/<c>+++</c> line: its date after a tab removed; <c>null</c> for <c>/dev/null</c>.</summary>
    private static string? FilePath(string field, bool git)
    {
        var path = field.StartsWith('"') && QuotedEnd(field) is > 0 and var end ? Unquote(field[..(end + 1)]) : field.Split('\t')[0].TrimEnd();
        if (path == "/dev/null") return null;
        return git ? StripPrefix(path) : path;
    }

    private static Hunk? ReadHunk(string[] lines, ref int i)
    {
        var m = System.Text.RegularExpressions.Regex.Match(lines[i], @"^@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@ ?(.*)$");
        if (!m.Success) return null;
        int Int(System.Text.RegularExpressions.Group g, int fallback) =>
            g.Success && int.TryParse(g.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : fallback;
        int oldStart = Int(m.Groups[1], 0), oldCount = Int(m.Groups[2], 1), newStart = Int(m.Groups[3], 0), newCount = Int(m.Groups[4], 1);
        var body = new List<(char, string)>();
        int oldSeen = 0, newSeen = 0;
        bool oldNoEol = false, newNoEol = false;
        char last = ' ';
        while (i + 1 < lines.Length && (oldSeen < oldCount || newSeen < newCount || lines[i + 1].StartsWith('\\')))
        {
            var line = lines[i + 1];
            if (line.StartsWith('\\'))
            {
                // "\ No newline at end of file" marks the line before it.
                if (last != '+') oldNoEol = true;
                if (last != '-') newNoEol = true;
                i++;
                continue;
            }
            // An empty line inside a block is a context line whose leading space an editor removed.
            var op = line.Length == 0 ? ' ' : line[0];
            if (op is not (' ' or '+' or '-')) break;
            var text = line.Length == 0 ? "" : line[1..];
            if (op != '+') oldSeen++;
            if (op != '-') newSeen++;
            body.Add((op, text));
            last = op;
            i++;
        }
        return new Hunk(oldStart, oldCount, newStart, newCount, m.Groups[5].Value.Trim(), body, oldNoEol, newNoEol);
    }

    // ── Applying ──────────────────────────────────────────────────────────────

    internal enum Outcome { Applied, Shifted, AlreadyApplied, Rejected }

    internal sealed record HunkResult(Hunk Hunk, Outcome Outcome, int Offset);

    /// <summary>
    /// <paramref name="original"/> with the blocks of <paramref name="file"/> that apply; each block's outcome. The text
    /// keeps the file's own line breaks and its final one, unless the patch says otherwise.
    /// </summary>
    internal static (string Content, IReadOnlyList<HunkResult> Results) Apply(string original, PatchFile file)
    {
        var crlf = original.Contains("\r\n", StringComparison.Ordinal);
        var text = crlf ? original.Replace("\r\n", "\n", StringComparison.Ordinal) : original;
        var endsWithBreak = text.Length == 0 || text.EndsWith('\n');
        var lines = text.Length == 0 ? new List<string>() : [.. (endsWithBreak ? text[..^1] : text).Split('\n')];
        var results = new List<HunkResult>();
        var delta = 0;     // lines added less removed by the blocks placed so far
        var floor = 0;     // a block is never placed above the one before it

        foreach (var hunk in file.Hunks)
        {
            var before = hunk.Before;
            var after = hunk.After;
            // A block with no old line inserts after line OldStart; its place is that line.
            var expected = Math.Max(0, (hunk.OldCount == 0 ? hunk.OldStart : hunk.OldStart - 1) + delta);
            // Context shorter on one side than the other only happens at an edge of the file: the block sits there.
            var lead = hunk.Lines.TakeWhile(l => l.Op == ' ').Count();
            var trail = hunk.Lines.Reverse().TakeWhile(l => l.Op == ' ').Count();
            var anchor = lead < trail && hunk.OldStart <= 1 ? Anchor.Start : trail < lead ? Anchor.End : Anchor.None;
            // A block whose old lines are blank, braces or nothing at all stands in many places: never shifted, only at its line.
            var shiftable = before.Count(l => l.Trim().Length >= 3) >= 2;
            var at = Find(lines, before, expected, floor, anchor, shiftable);
            // ⚠ Its result first: a block that adds lines at an edge still finds its old lines once applied, and would be
            // placed again. Already applied when its new lines stand nearer to its place than its old ones.
            var done = after.Count > 0 && !after.SequenceEqual(before) ? Find(lines, after, expected, floor, anchor, shiftable) : -1;
            // A block with no old line "finds" itself anywhere: its new lines at the same place are the only evidence.
            if (done >= 0 && (at < 0 || Math.Abs(done - expected) < Math.Abs(at - expected) || before.Count == 0 && done == at))
            {
                results.Add(new HunkResult(hunk, Outcome.AlreadyApplied, done - expected));
                delta += after.Count - before.Count;
                floor = done + after.Count;
                continue;
            }
            if (at < 0)
            {
                results.Add(new HunkResult(hunk, Outcome.Rejected, 0));
                continue;
            }
            // Context lines keep the file's own text (its trailing spaces): only what the block adds comes from the patch.
            var replaced = new List<string>(after.Count);
            var j = at;
            foreach (var (op, line) in hunk.Lines)
            {
                if (op == ' ') replaced.Add(lines[j++]);
                else if (op == '-') j++;
                else replaced.Add(line);
            }
            lines.RemoveRange(at, before.Count);
            lines.InsertRange(at, replaced);
            results.Add(new HunkResult(hunk, at == expected ? Outcome.Applied : Outcome.Shifted, at - expected));
            delta += after.Count - before.Count;
            floor = at + after.Count;
            // The block's own word on the end of the file: the last line it leaves has, or has not, its line break.
            if (at + after.Count == lines.Count)
            {
                if (hunk.NewNoEol) endsWithBreak = false;
                else if (hunk.OldNoEol) endsWithBreak = true;
            }
        }
        var joined = string.Join("\n", lines) + (endsWithBreak && lines.Count > 0 ? "\n" : "");
        return (crlf ? joined.Replace("\n", "\r\n", StringComparison.Ordinal) : joined, results);
    }

    private enum Anchor { None, Start, End }

    /// <summary>Where <paramref name="block"/> stands in <paramref name="lines"/>, at or after <paramref name="floor"/>,
    /// nearest to <paramref name="expected"/> — only at the file's edge for an <paramref name="anchor"/>ed block, only at
    /// <paramref name="expected"/> for one that is not <paramref name="shiftable"/>; trailing white space ignored; -1 when
    /// nowhere.</summary>
    private static int Find(List<string> lines, List<string> block, int expected, int floor, Anchor anchor, bool shiftable)
    {
        var last = lines.Count - block.Count;
        if (last < floor) return -1;
        if (anchor != Anchor.None)
        {
            var edge = anchor == Anchor.Start ? 0 : last;
            return edge >= floor && Matches(lines, block, edge) ? edge : -1;
        }
        if (!shiftable) return expected >= floor && expected <= last && Matches(lines, block, expected) ? expected : -1;
        expected = Math.Clamp(expected, floor, last);
        for (var d = 0; d <= Math.Max(expected - floor, last - expected); d++)
        {
            if (expected + d <= last && Matches(lines, block, expected + d)) return expected + d;
            if (d > 0 && expected - d >= floor && Matches(lines, block, expected - d)) return expected - d;
        }
        return -1;
    }

    private static bool Matches(List<string> lines, List<string> block, int at)
    {
        for (var k = 0; k < block.Count; k++)
            if (!string.Equals(lines[at + k].TrimEnd(), block[k].TrimEnd(), StringComparison.Ordinal)) return false;
        return true;
    }

    /// <summary>The words for one outcome, as the summary and the tool say them.</summary>
    internal static string Said(HunkResult r) => r.Outcome switch
    {
        Outcome.Applied => "applies",
        Outcome.Shifted => $"applies {Math.Abs(r.Offset)} line{(Math.Abs(r.Offset) == 1 ? "" : "s")} {(r.Offset > 0 ? "lower" : "higher")}",
        Outcome.AlreadyApplied => "already applied",
        _ => "does NOT apply: its context is not in the file",
    };

    // ── Where a patch path lands ──────────────────────────────────────────────

    /// <summary>
    /// The workspace file a patch path names: the existing one for a file it changes (the path as written, else with its
    /// first folder removed — <c>diff -ru a/ b/</c>), the one to create for a file it adds; <c>Why</c> instead when it
    /// cannot be touched.
    /// </summary>
    /// <remarks>⚠ A patch path is relative: an absolute path or one that climbs (<c>..</c>) is refused before any disk is
    /// asked about it (asking can be a connection). Then judged as every written path is: under the root, outside the
    /// ignore files.</remarks>
    internal static (string? Path, string? Why) Locate(PatchFile file, string? root)
    {
        if (string.IsNullOrEmpty(root)) return (null, "no workspace is open");
        // A rename's content lives under its old name. A plain diff of two names (foo.c.orig → foo.c) changes the new one:
        // both are tried, the new first, as GNU patch prefers it.
        string?[] names = file.Kind switch
        {
            FileKind.Add => [file.NewPath],
            FileKind.Rename => [file.OldPath],
            _ when !file.GitPrefixed && file.NewPath is not null && file.OldPath != file.NewPath => [file.NewPath, file.OldPath],
            _ => [file.OldPath ?? file.NewPath],
        };
        var candidates = new List<string>();
        foreach (var written in names.OfType<string>())
        {
            var segments = written.Split('/', '\\');
            if (System.IO.Path.IsPathRooted(written) || written.Contains(':', StringComparison.Ordinal) || segments.Contains(".."))
                return (null, $"'{written}' is not a path inside the workspace");
            var stripped = segments.Length > 1 ? string.Join('/', segments[1..]) : null;
            // A plain `diff -ru a/ b/` names its folders: tried without them too, a new file placed without them.
            if (file.Kind == FileKind.Add)
                candidates.Add(!file.GitPrefixed && stripped is not null && segments[0] is "a" or "b" ? stripped : written);
            else
            {
                candidates.Add(written);
                if (!file.GitPrefixed && stripped is not null) candidates.Add(stripped);
            }
        }
        if (candidates.Count == 0) return (null, "the patch names no path");
        try
        {
            foreach (var candidate in candidates)
            {
                var full = Tools.PathSanitizer.Sanitize(candidate, root);
                Tools.PathSanitizer.AssertUnderRoot(full, root);
                // Never asked of the disk when it leads to another machine: the question is a connection to it.
                if (Execution.NetworkPath.LeadsOutside(full, root)) return (null, $"'{candidate}' leads outside the workspace");
                if (file.Kind == FileKind.Add || File.Exists(full))
                    return Governance.ContentExclusions.Of(full) is { } excluded
                        ? (null, "kept out of AI tools by " + excluded.Named) : (full, null);
            }
            return (null, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            return (null, Diagnostics.RootMessage(ex));
        }
    }

    /// <summary>What <see cref="Summarize"/> judges each file against: the workspace under <paramref name="root"/>, a file
    /// with unsaved changes in the editor by its buffer.</summary>
    internal static Func<PatchFile, (string? Path, string? Text, string? Why)> Workspace(string? root, Editor.OpenDocumentOverlay? overlay) => file =>
    {
        var (path, why) = Locate(file, root);
        if (why is not null) return (null, null, why);
        if (path is null || !File.Exists(path)) return (null, null, null);
        if (file.Kind == FileKind.Add) return (path, null, null);
        try
        {
            // Read to judge, on a plain read_file: a file past the cap is said, not read whole.
            if (new FileInfo(path).Length > JudgedFileBytes) return (null, null, "too large to check here");
            if (Tools.TextFileEncoding.IsBinaryFile(path)) return (null, null, "it is a binary file");
            return (path, overlay is not null && overlay.TryGetUnsaved(path, out var buffered) ? buffered : Tools.TextFileEncoding.ReadText(path), null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (null, null, "it cannot be read (" + Diagnostics.RootMessage(ex) + ")");
        }
    };

    // ── Reading ───────────────────────────────────────────────────────────────

    /// <summary>
    /// A <c>.patch</c> or <c>.diff</c> read as its files and blocks, each block judged against the workspace when
    /// <paramref name="resolve"/> finds its file, then its text within <paramref name="budget"/>; <c>null</c> for any
    /// other file.
    /// </summary>
    /// <param name="resolve">The file a patch path names in the workspace, with its text; <c>null</c> when it is not
    /// there, the reason in place of the text when it cannot be judged (excluded, unreadable).</param>
    internal static string? Summarize(string name, string text, int budget, Func<PatchFile, (string? Path, string? Text, string? Why)>? resolve,
                                      CancellationToken ct = default)
    {
        var lower = name.ToLowerInvariant();
        if (!lower.EndsWith(".patch", StringComparison.Ordinal) && !lower.EndsWith(".diff", StringComparison.Ordinal)) return null;
        if (Parse(text) is not { } patch) return null;
        var sb = new StringBuilder();
        var added = patch.Files.Sum(f => f.Hunks.Sum(h => h.Added));
        var removed = patch.Files.Sum(f => f.Hunks.Sum(h => h.Removed));
        sb.Append(CultureInfo.InvariantCulture,
            $"Patch: {patch.Files.Count} file{(patch.Files.Count == 1 ? "" : "s")}, +{added} −{removed}");
        if (patch.Subjects.Count > 0)
            sb.Append(CultureInfo.InvariantCulture, $"; {patch.Subjects.Count} commit{(patch.Subjects.Count == 1 ? "" : "s")}: ")
              .Append(string.Join("; ", patch.Subjects.Take(5).Select(s => "\"" + s + "\""))).Append(patch.Subjects.Count > 5 ? "; …" : "");
        if (patch.Author is { } author) sb.Append(", by ").Append(author);
        if (patch.Date is { } date) sb.Append(", ").Append(date);
        sb.Append(".\n");
        var notApplying = 0;
        var unlisted = 0;
        // A file a series changes twice is judged, the second time, on what the first commit made of it — as apply_edits does.
        var running = new Dictionary<string, string>(PathComparer.Default);
        foreach (var file in patch.Files)
        {
            ct.ThrowIfCancellationRequested();
            if (sb.Length > budget / 2) { unlisted++; continue; }
            var mark = file.Kind switch
            {
                FileKind.Add => "A", FileKind.Delete => "D", FileKind.Rename => "R", FileKind.Copy => "C", FileKind.Binary => "B",
                FileKind.ModeOnly => "m", _ => "M",
            };
            var label = file.Kind is FileKind.Rename or FileKind.Copy && file.OldPath != file.NewPath ? $"{file.OldPath} → {file.NewPath}" : file.Path;
            sb.Append(CultureInfo.InvariantCulture,
                $"  {mark} {label}  +{file.Hunks.Sum(h => h.Added)} −{file.Hunks.Sum(h => h.Removed)}, {file.Hunks.Count} block{(file.Hunks.Count == 1 ? "" : "s")}");
            sb.Append(file.Kind switch
            {
                FileKind.Add => " (new file)",
                FileKind.Delete => " (deleted)",
                FileKind.Copy => " (a copy: the source stays as it is)",
                FileKind.Binary => " (binary: its content is not in this text)",
                FileKind.ModeOnly => " (mode only)",
                _ => file.ModeChanged ? " (and its mode)" : "",
            });
            if (resolve is not null && file.Kind is not (FileKind.Copy or FileKind.Binary or FileKind.ModeOnly))
            {
                var (path, content, why) = resolve(file);
                if (path is not null && running.TryGetValue(path, out var earlier)) content = earlier;
                if (why is not null) sb.Append(" — not checked: ").Append(why);
                else if (file.Kind == FileKind.Add)
                {
                    if (path is not null) sb.Append(" — already exists in the workspace");
                }
                else if (path is null) sb.Append(" — not in the workspace");
                else if (content is not null && file.Hunks.Count > 0 && file.Kind is FileKind.Modify or FileKind.Rename)
                {
                    var (next, results) = Apply(content, file);
                    running[path] = next;
                    notApplying += results.Count(r => r.Outcome == Outcome.Rejected);
                    sb.Append(" — in the workspace: ").Append(string.Join("; ", results.Select((r, k) =>
                        $"block {k + 1} (line {r.Hunk.OldStart}) {Said(r)}")));
                }
            }
            sb.Append('\n');
        }
        if (unlisted > 0)
            sb.Append(CultureInfo.InvariantCulture, $"  [{unlisted} more file{(unlisted == 1 ? "" : "s")} not listed]\n");
        if (resolve is not null)
            sb.Append(notApplying > 0
                ? $"{notApplying} block{(notApplying == 1 ? "" : "s")} no longer appl{(notApplying == 1 ? "ies" : "y")}: the files changed since the patch was made.\n"
                : "");
        sb.Append("To apply it: apply_edits with patch set to this file's path; each changed file is on the approval card, and any "
                + "block that does not apply is left out and named.\n\n");
        var page = budget - sb.Length - 120;
        if (page <= 0) return sb.ToString().TrimEnd();
        if (text.Length <= page) return sb.Append(text).ToString().TrimEnd();
        var cut = text.LastIndexOf('\n', Math.Min(page, text.Length - 1));
        var shown = cut > 0 ? text[..cut] : text[..page];
        var linesShown = shown.Count(c => c == '\n') + 1;
        return sb.Append(shown).Append(CultureInfo.InvariantCulture,
            $"\n[Patch text cut after line {linesShown}: read_file with start_line={linesShown + 1} continues]").ToString();
    }
}
