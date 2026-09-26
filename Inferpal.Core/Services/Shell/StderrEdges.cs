namespace Inferpal.Services.Shell;

/// <summary>
/// The first and last lines a child process writes on stderr, bounded — what is said when it dies before answering.
/// </summary>
/// <remarks>
/// ⚠ stderr is where a server writes WHY it cannot start — a missing token, a bad path, a module it cannot import —
/// and the only place: drained and discarded, a server that died at startup reads "connection closed" or "a task was
/// canceled". Node and npm put the reason on the FIRST line (the stack follows), Python on the LAST (after
/// "Traceback"): the first lines and the last ones are kept, bounded, since a healthy server may log for hours.
/// Shared by the MCP stdio client and the LSP session.
/// </remarks>
internal sealed class StderrEdges
{
    private const int EdgeLines = 4;
    private const int LineChars = 200;

    private readonly object _lock = new();
    private readonly List<string> _head = [];
    private readonly Queue<string> _tail = new();
    private int _lines;

    /// <summary>Reads <paramref name="stderr"/> to its end, keeping its edges. Never throws.</summary>
    public async Task DrainAsync(TextReader stderr)
    {
        try
        {
            while (await stderr.ReadLineAsync().ConfigureAwait(false) is { } line)
                Keep(line);
        }
        catch { /* the process exited: its pipe is gone */ }
    }

    public void Keep(string line)
    {
        line = line.Trim();
        if (line.Length == 0) return;
        if (line.Length > LineChars) line = line[..LineChars] + "…";
        lock (_lock)
        {
            _lines++;
            if (_head.Count < EdgeLines) { _head.Add(line); return; }
            _tail.Enqueue(line);
            if (_tail.Count > EdgeLines) _tail.Dequeue();
        }
    }

    /// <summary>The kept lines on one line, the elided middle counted; empty when nothing was written.</summary>
    public override string ToString()
    {
        lock (_lock)
        {
            var skipped = _lines - _head.Count - _tail.Count;
            var parts   = new List<string>(_head);
            if (skipped > 0) parts.Add($"… {skipped} more line(s) …");
            parts.AddRange(_tail);
            return string.Join(" | ", parts);
        }
    }
}
