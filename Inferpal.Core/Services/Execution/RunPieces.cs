using System.Text;
using Inferpal.Services.Tools;

namespace Inferpal.Services.Execution;

/// <summary>One change of a file of a run: lines the run removed and added, between two lines it left alone.</summary>
/// <param name="Index">0-based, in the file's order: what the card and <c>/undo-run #run:n</c> name (as n + 1).</param>
/// <param name="BeforeLine">1-based line of the change in the file before the run.</param>
/// <param name="RunLine">1-based line of the change in the file as the run left it.</param>
internal sealed record RunPiece(int Index, int BeforeLine, int RunLine, IReadOnlyList<string> Removed, IReadOnlyList<string> Added);

/// <summary>
/// A file of a run cut into changes that are kept or undone one by one. The file's content is a function of which are
/// undone (<see cref="Compose"/>): each change shows its side from before the run if undone, the run's side otherwise.
/// </summary>
/// <remarks>
/// ⚠ A change is a block of changed lines between two unchanged ones — never merged with a neighbour through context, as
/// a git hunk is: two changes share no line, so undoing one cannot touch the other. Lines keep their terminators, so a
/// file is rebuilt byte for byte; <see cref="Of"/> refuses a file whose two ends it cannot rebuild exactly.
/// </remarks>
internal sealed class RunPieces
{
    /// <summary>Changed lines on either side past which a file is undone whole only: the line diff is O(m·n).</summary>
    internal const int MaxDifferingLines = 1000;

    private readonly IReadOnlyList<(IReadOnlyList<string> Same, RunPiece? Piece)> _segments;
    private readonly Encoding _encoding;
    private readonly byte[] _preamble;

    public IReadOnlyList<RunPiece> Pieces { get; }

    private RunPieces(IReadOnlyList<(IReadOnlyList<string>, RunPiece?)> segments, Encoding encoding, byte[] preamble)
    {
        _segments = segments;
        _encoding = encoding;
        _preamble = preamble;
        Pieces = [.. segments.Select(s => s.Item2).OfType<RunPiece>()];
    }

    /// <summary>Why a file is undone whole only (<see cref="Whole.Created"/>: the run created or deleted it).</summary>
    internal enum Whole { None, Binary, TooLarge, NotRebuilt, Created }

    /// <summary>
    /// The changes between <paramref name="before"/> and <paramref name="run"/> (the file as the run left it); <c>null</c>
    /// with the reason when the file is undone whole only — not text, too many lines differ, or the two ends cannot be
    /// rebuilt byte for byte (the run changed the file's encoding, say).
    /// </summary>
    public static RunPieces? Of(byte[] before, byte[] run, out Whole why)
    {
        if (TextFileEncoding.IsBinary(before) || TextFileEncoding.IsBinary(run)) { why = Whole.Binary; return null; }
        // Written back in the run's encoding: the version of the file the person has now.
        var encoding = TextFileEncoding.Of(run);
        var preamble = encoding.GetPreamble();
        if (preamble.Length == 0 || !run.AsSpan().StartsWith(preamble)) preamble = [];
        var oldLines = Lines(TextFileEncoding.Decode(before));
        var newLines = Lines(TextFileEncoding.Decode(run));
        if (Segments(oldLines, newLines) is not { } segments) { why = Whole.TooLarge; return null; }
        var pieces = new RunPieces(segments, encoding, preamble);
        // Nothing undone is the run's bytes, everything undone the bytes before it: else a change could not be given
        // back exactly, and the whole file is.
        if (!pieces.Compose(new HashSet<int>()).AsSpan().SequenceEqual(run)
            || !pieces.Compose(Enumerable.Range(0, pieces.Pieces.Count).ToHashSet()).AsSpan().SequenceEqual(before))
        {
            why = Whole.NotRebuilt;
            return null;
        }
        why = Whole.None;
        return pieces;
    }

    /// <summary>The file with the changes in <paramref name="undone"/> (by <see cref="RunPiece.Index"/>) given back.</summary>
    public byte[] Compose(IReadOnlySet<int> undone)
    {
        var text = new StringBuilder();
        foreach (var (same, piece) in _segments)
        {
            foreach (var line in same) text.Append(line);
            if (piece is not null)
                foreach (var line in undone.Contains(piece.Index) ? piece.Removed : piece.Added) text.Append(line);
        }
        return [.. _preamble, .. _encoding.GetBytes(text.ToString())];
    }

    /// <summary>Up to <paramref name="lines"/> unchanged lines on each side of change <paramref name="index"/>, to read it by.</summary>
    public (IReadOnlyList<string> Above, IReadOnlyList<string> Below) Context(int index, int lines)
    {
        // A change closes the segment that holds the lines before it; the next segment starts with the lines after it.
        var at = 0;
        while (_segments[at].Piece?.Index != index) at++;
        var above = _segments[at].Same;
        var below = _segments[at + 1].Same;
        return ([.. above.Skip(Math.Max(0, above.Count - lines))], [.. below.Take(lines)]);
    }

    /// <summary>Where each change starts (1-based) in the file with <paramref name="undone"/> given back.</summary>
    public IReadOnlyList<int> StartLines(IReadOnlySet<int> undone)
    {
        var starts = new List<int>();
        var line = 1;
        foreach (var (same, piece) in _segments)
        {
            line += same.Count;
            if (piece is null) continue;
            starts.Add(line);
            line += (undone.Contains(piece.Index) ? piece.Removed : piece.Added).Count;
        }
        return starts;
    }

    /// <summary>The lines of <paramref name="text"/>, each with its terminator (CRLF, LF or CR); the last may have none.</summary>
    internal static List<string> Lines(string text)
    {
        var lines = new List<string>();
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n' || text[i] == '\r' && (i + 1 == text.Length || text[i + 1] != '\n'))
            {
                lines.Add(text[start..(i + 1)]);
                start = i + 1;
            }
        }
        if (start < text.Length) lines.Add(text[start..]);
        return lines;
    }

    /// <summary>The run of lines both sides share, then each change: a longest common subsequence, ends trimmed first.</summary>
    private static List<(IReadOnlyList<string>, RunPiece?)>? Segments(List<string> old, List<string> @new)
    {
        var head = 0;
        while (head < old.Count && head < @new.Count && old[head] == @new[head]) head++;
        var tail = 0;
        while (tail < old.Count - head && tail < @new.Count - head && old[^(tail + 1)] == @new[^(tail + 1)]) tail++;
        var m = old.Count - head - tail;
        var n = @new.Count - head - tail;
        if (m > MaxDifferingLines || n > MaxDifferingLines) return null;

        var dp = new int[m + 1, n + 1];
        for (var i = m - 1; i >= 0; i--)
            for (var j = n - 1; j >= 0; j--)
                dp[i, j] = old[head + i] == @new[head + j] ? dp[i + 1, j + 1] + 1 : Math.Max(dp[i + 1, j], dp[i, j + 1]);

        var segments = new List<(IReadOnlyList<string>, RunPiece?)>();
        var same = old.GetRange(0, head);
        var removed = new List<string>();
        var added = new List<string>();
        var (oi, ni) = (0, 0);
        void Close()
        {
            if (removed.Count == 0 && added.Count == 0) return;
            // Lines of the change: where it starts on each side, counted from 1.
            var beforeLine = head + oi - removed.Count + 1;
            var runLine    = head + ni - added.Count + 1;
            segments.Add((same, new RunPiece(segments.Count, beforeLine, runLine, removed, added)));
            same = [];
            removed = [];
            added = [];
        }
        while (oi < m || ni < n)
        {
            if (oi < m && ni < n && old[head + oi] == @new[head + ni])
            {
                Close();
                same.Add(old[head + oi]);
                oi++;
                ni++;
            }
            else if (ni < n && (oi == m || dp[oi, ni + 1] >= dp[oi + 1, ni])) added.Add(@new[head + ni++]);
            else removed.Add(old[head + oi++]);
        }
        Close();
        same.AddRange(old.GetRange(old.Count - tail, tail));
        segments.Add((same, null));
        return segments;
    }
}
