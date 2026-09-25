using System.Text;

namespace Inferpal.Services.Rag;

/// <summary>
/// Formats the auto-retrieved RAG context block injected into a chat turn's prompt. Pure/testable:
/// the VM does the retrieval (shadow cache or a search) and passes the ranked chunks here. Chunks
/// whose file is already attached are skipped (to avoid duplicating content), and the block is
/// capped to a character budget and a maximum number of chunks.
/// </summary>
/// <remarks>
/// ⚠ <b>This block SAYS what it is.</b> It is a handful of top matches from the semantic index,
/// injected without anyone asking, under a heading that reads "Relevant code" — a model that takes
/// it for a search answers "this symbol is only used in X" about a repository it was shown three
/// snippets of. <c>search_codebase</c>, the explicit path over the same index, prints its mode and
/// its index footer; the automatic one said nothing at all.
/// ⚠ And the cuts are named the way the rest of the product names them: a chunk cut mid-line used
/// to end on a bare <c>…</c> <b>inside the code fence</b>, which reads as a syntax error rather
/// than a truncation, and a chunk dropped by the budget left no trace whatsoever.
/// </remarks>
internal static class RagAutoContext
{
    public const int DefaultBudgetChars = 1500;
    public const int DefaultMaxChunks   = 3;
    private const int MaxChunkChars     = 600;

    private const string Header = "## Relevant code (auto-retrieved for this question)";

    /// <param name="results">Ranked (chunk, score) results, best first.</param>
    /// <param name="attachedPaths">Source file paths already injected as attachments — their chunks are skipped.</param>
    /// <param name="notYetReindexed">
    /// Files changed since the index read them (<see cref="ProjectIndexService.NotYetReindexed"/>).
    /// ⚠ Their chunks are the version from BEFORE the change: the file saved a second ago and asked
    /// about now — the most relevant file there is — came back as "relevant code" in its old form.
    /// Left out, and said: unlike an attached file, their current content is nowhere in the prompt.
    /// </param>
    public static string Build(
        IReadOnlyList<RagHit> results,
        ISet<string> attachedPaths,
        int budget = DefaultBudgetChars,
        int maxChunks = DefaultMaxChunks,
        ISet<string>? notYetReindexed = null)
    {
        if (results is null || results.Count == 0) return string.Empty;

        var sb      = new StringBuilder();
        int used    = 0;
        int count   = 0;
        int skipped = 0;          // already attached: their content IS in the prompt
        int stale   = 0;          // changed since indexed: their content is NOT
        bool capped = false;      // a retrieved chunk did not make it in

        foreach (var chunk in results.Select(r => r.Chunk))
        {
            if (count >= maxChunks) { capped = true; break; }
            if (chunk.FilePath is { Length: > 0 } fp && attachedPaths.Contains(fp)) { skipped++; continue; }
            if (chunk.FilePath is { Length: > 0 } sp && notYetReindexed?.Contains(sp) == true) { stale++; continue; }

            var body = chunk.Content.Length > MaxChunkChars
                ? SafeTruncate.Truncate(chunk.Content, MaxChunkChars) + "\n…(truncated)"
                : chunk.Content;
            var block = $"### {chunk.RelPath}:{chunk.StartLine}-{chunk.EndLine}\n```\n{body}\n```\n";

            // Stop before exceeding the budget, but always include at least one chunk.
            if (count > 0 && used + block.Length > budget) { capped = true; break; }

            sb.Append(block).Append('\n');
            used += block.Length;
            count++;
        }

        if (count == 0)
            return stale == 0
                ? string.Empty
                : Header + "\n" + $"_{stale} matching snippet(s) are in files changed since they were indexed — "
                  + "left out; `read_file` shows them as they are now._";

        // One line, and only the causes that fired. It costs ~20 tokens on a block capped at
        // ~375, and it is what keeps "here is the relevant code" from being read as "here is all
        // of it".
        var note = capped
            ? $"_{count} of {results.Count - skipped - stale} retrieved snippets — the rest did not fit. "
            : $"_Top {count} match(es) of the semantic index. ";
        if (stale > 0)
            note += $"{stale} snippet(s) of files changed since they were indexed were left out — `read_file` shows them as they are now. ";
        note += "This is a sample, not a search: call `search_codebase` to look further._";

        return Header + "\n" + note + "\n\n" + sb.ToString().TrimEnd();
    }

    /// <summary>Most chunks of one file that a Visual Studio auto-attach chip carries.</summary>
    public const int ChipMaxChunks = 3;

    /// <summary>
    /// The content of a Visual Studio auto-attach chip (<c>🔮 File.cs</c>): the file's best chunks, in the order
    /// given, preceded by what they are when they do not cover the whole file.
    /// </summary>
    /// <remarks>
    /// ⚠ The chip reaches the model as <c>[Attached: 🔮 File.cs]</c>, which reads as the file. Without the line
    /// that names the excerpts, the model lists the methods it was shown as everything the file defines — "the
    /// class defines two static methods" — and the user writes again what the rest of the file already holds;
    /// with it, the same model answers "at least". A file the chunks cover entirely says nothing: it IS whole.
    /// </remarks>
    /// <param name="totalLines">The file's line count, or <c>null</c> when unknown — then the note is kept,
    /// since nothing proves the file is whole.</param>
    public static string ChipContent(string fileName, IReadOnlyList<RagChunk> chunks, int? totalLines)
    {
        var joined = string.Join("\n\n...\n\n", chunks.Select(c => c.Content));
        if (chunks.Count == 0 || Covers(chunks, totalLines)) return joined;

        var ranges = string.Join(", ", chunks.Select(c => $"{c.StartLine}-{c.EndLine}"));
        return $"Excerpts of {fileName} picked by semantic search (lines {ranges}), not the whole file: "
             + "what is not shown here may still be in it — read_file reads it all.\n\n" + joined;
    }

    /// <summary><see cref="ChipContent(string, IReadOnlyList{RagChunk}, int?)"/>, the line count read from disk.</summary>
    public static string ChipContent(string path, IReadOnlyList<RagChunk> chunks)
    {
        int? lines;
        try { lines = Tools.TextFileEncoding.ReadLines(path).Count; }
        catch (Exception ex)
        {
            Diagnostics.Swallow("RagAutoContext.ChipContent", ex);
            lines = null;
        }
        return ChipContent(Path.GetFileName(path), chunks, lines);
    }

    /// <summary>Whether the chunks, overlaps allowed, span every line from 1 to <paramref name="totalLines"/>.</summary>
    private static bool Covers(IReadOnlyList<RagChunk> chunks, int? totalLines)
    {
        if (totalLines is not { } total) return false;
        var next = 1;
        foreach (var c in chunks.OrderBy(c => c.StartLine))
        {
            if (c.StartLine > next) return false;
            next = Math.Max(next, c.EndLine + 1);
        }
        return next > total;
    }
}
