using System.Text;

namespace Inferpal.Services.Execution;

/// <summary>
/// The one reader of a command line, for every control that reads one — the permission rules, the package check, the rule
/// an approval card proposes, the person's own shell tools: its steps as the shell runs them, every piece a shell might
/// read as a command, and whether it runs or writes more than its steps show.
/// </summary>
/// <remarks>
/// ⚠ Two readers that split one line differently let a command through the one that does not see it: an install inside
/// <c>$( )</c> that the package check does not read is never checked, and a PowerShell hashtable value the rules do not
/// read runs unapproved. And no single reading is safe for a refusal: a quote the reader takes otherwise than the shell
/// (an escape, a string across two lines, a comment, <c>--%</c>) hides the command after it — the rules judge every
/// reading (<see cref="Pieces"/> and <see cref="Read(string, bool)"/>), and "ambiguous ⇒ card" (<see cref="Hides"/>).
/// </remarks>
internal static class CommandLine
{
    /// <summary>A character that may start or hide another command: a line with one is not one plain command.</summary>
    internal static readonly char[] Metacharacters =
        [';', '&', '|', '`', '$', '<', '>', '(', ')', '{', '}', '\r', '\n', '\v', '\f', '\u0085', '\u2028', '\u2029'];

    /// <summary>How deep a subexpression inside a subexpression is read.</summary>
    private const int MaxDepth = 8;

    /// <summary>The longest line whose inner commands are read; a longer one is held by <see cref="Hides"/> alone.</summary>
    private const int MaxInnerLength = 64 * 1024;

    /// <summary>A step of a command line: its text as written, and whether it runs inside another step (a subexpression,
    /// a grouping, a block) — where a folder change does not say where the rest of the line runs.</summary>
    internal readonly record struct Step(string Text, bool Nested);

    /// <summary>The steps of <paramref name="line"/> in the dialect of this machine's shell (<see cref="Read(string, bool)"/>).</summary>
    internal static List<string> Steps(string line) => Steps(line, PosixShell());

    /// <inheritdoc cref="Read(string, bool)"/>
    internal static List<string> Steps(string line, bool posix) => [.. Read(line, posix).Select(s => s.Text)];

    /// <summary>The steps of <paramref name="line"/> in the dialect of this machine's shell.</summary>
    internal static List<Step> Read(string line) => Read(line, PosixShell());

    /// <summary>
    /// The steps of <paramref name="line"/> in the order the shell runs them: split at <c>&amp;&amp;</c>, <c>||</c>, <c>;</c>,
    /// <c>|</c>, a lone <c>&amp;</c> and line breaks — outside quotes and brackets, an escaped character taken as written —, as written
    /// (not trimmed); each preceded by the steps of the commands it runs INSIDE itself — a subexpression <c>$( )</c> or
    /// <c>@( )</c>, a grouping <c>( )</c>, a process substitution <c>&lt;( )</c>, a PowerShell block or hashtable <c>{ }</c>,
    /// backticks under a POSIX shell.
    /// </summary>
    /// <param name="line">The command line.</param>
    /// <param name="posix">A POSIX shell's reading (a backslash escapes, a backtick substitutes) rather than PowerShell's (a
    /// backtick escapes, a brace holds a block).</param>
    /// <remarks>⚠ Inside quotes a separator is part of an argument: split there, <c>pip install "x @ git+https://h/r.git#a&amp;egg=x"</c>
    /// reads as a bare <c>x</c>. Inside double quotes <c>$( )</c> still runs, as both shells read it.</remarks>
    internal static List<Step> Read(string line, bool posix) => Read(line, posix, 0);

    private static List<Step> Read(string line, bool posix, int depth)
    {
        var steps = new List<Step>();
        foreach (var piece in TopLevel(line, posix))
        {
            if (depth < MaxDepth && piece.Length <= MaxInnerLength)
                foreach (var inner in Inner(piece, posix))
                    steps.AddRange(Read(inner, posix, depth + 1));
            steps.Add(new Step(piece, depth > 0));
        }
        return steps;
    }

    /// <summary>
    /// Every piece of <paramref name="line"/> a shell might read as a command: split at <c>;</c>, <c>|</c>, <c>&amp;</c> and
    /// line breaks wherever they stand, quotes or not.
    /// </summary>
    /// <remarks>What a REFUSAL judges besides <see cref="Read(string, bool)"/>: a quote this reader takes otherwise than the
    /// shell (an escape, a string across two lines, a comment) never hides a command from a <c>deny</c>. A fragment of a
    /// quoted argument matches no <c>allow</c>: such a line goes to the card, the direction a misreading may err in.</remarks>
    internal static string[] Pieces(string line) => line.Split([';', '|', '&', '\r', '\n']);

    private static bool PosixShell() => Shell.ShellLauncher.Resolve().Dialect != Shell.ShellDialect.PowerShell;

    /// <summary>The top-level steps of <paramref name="line"/>, quote- and escape-aware, as written.</summary>
    /// <remarks>⚠ A separator inside brackets belongs to what they hold: cut there, <c>echo $(rm -rf src; true)</c> leaves a
    /// bracket that never closes, <see cref="Inner"/> reads nothing, and a <c>deny</c> misses the <c>rm</c> it sees in
    /// <c>echo $(rm -rf src)</c>. A line whose brackets do not close is read without them: nothing is lost to a stray one.</remarks>
    private static List<string> TopLevel(string line, bool posix, bool brackets = true)
    {
        var steps = new List<string>();
        var step = new StringBuilder();
        char? quote = null;
        var depth = 0;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (IsEscape(c, quote, posix) && i + 1 < line.Length)
            {
                step.Append(c).Append(line[++i]);
                continue;
            }
            if (quote is { } open) { if (c == open) quote = null; }
            else if (c is '"' or '\'') quote = c;
            else if (brackets && posix && c == '`' && line.IndexOf('`', i + 1) is > 0 and var close)
            {
                step.Append(line, i, close - i);   // what backticks hold, the closing one appended below
                i = close;
                c = line[i];
            }
            else if (brackets && (c == '(' || (!posix && c == '{' && (i == 0 || line[i - 1] != '$')))) depth++;
            else if (brackets && depth > 0 && (c == ')' || (!posix && c == '}'))) depth--;
            else if (depth == 0 && c is '&' or '|' or ';' or '\r' or '\n') { steps.Add(step.ToString()); step.Clear(); continue; }
            step.Append(c);
        }
        steps.Add(step.ToString());
        return brackets && depth > 0 ? TopLevel(line, posix, brackets: false) : steps;
    }

    /// <summary>Whether <paramref name="c"/> escapes the next character: PowerShell's backtick (outside single quotes), a
    /// POSIX backslash (outside single quotes).</summary>
    private static bool IsEscape(char c, char? quote, bool posix) =>
        quote != '\'' && (posix ? c == '\\' : c == '`');

    /// <summary>The text of each command <paramref name="line"/> runs inside itself, in order; a hashtable entry's
    /// <c>name=</c> taken off, so that its value reads as the command it is. Stops at a bracket that never closes — the
    /// line goes to the card (<see cref="Hides"/>), and the scan stays linear.</summary>
    private static IEnumerable<string> Inner(string line, bool posix)
    {
        char? quote = null;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (IsEscape(c, quote, posix)) { i++; continue; }
            if (quote == '\'') { if (c == '\'') quote = null; continue; }
            if (posix && c == '`')
            {
                var close = line.IndexOf('`', i + 1);
                if (close < 0) yield break;
                yield return line[(i + 1)..close];
                i = close;
                continue;
            }
            var subexpression = c == '$' && i + 1 < line.Length && line[i + 1] == '(';
            if (quote == '"')
            {
                if (c == '"') quote = null;
                else if (subexpression)
                {
                    if (Closing(line, i + 1, posix) is not { } end) yield break;
                    yield return line[(i + 2)..end];
                    i = end;
                }
                continue;
            }
            if (c is '\'' or '"') { quote = c; continue; }
            // A brace holds a command in PowerShell only (a block, a hashtable) — never `${variable}`; in a POSIX shell it
            // runs nothing a `$(` or a backtick does not already show.
            var block = c == '{' && !posix && (i == 0 || line[i - 1] != '$');
            if (c == '(' || block)
            {
                if (Closing(line, i, posix) is not { } last) yield break;
                var text = line[(i + 1)..last];
                yield return block ? WithoutEntryNames(text) : text;
                i = last;
            }
        }
    }

    /// <summary>The index of the bracket that closes the one at <paramref name="open"/>, quotes and escapes respected; null
    /// when none.</summary>
    private static int? Closing(string line, int open, bool posix)
    {
        var (opening, closing) = line[open] == '{' ? ('{', '}') : ('(', ')');
        var depth = 0;
        char? quote = null;
        for (var i = open; i < line.Length; i++)
        {
            var c = line[i];
            if (IsEscape(c, quote, posix)) { i++; continue; }
            if (quote is { } q) { if (c == q) quote = null; continue; }
            if (c is '"' or '\'') { quote = c; continue; }
            if (c == opening) depth++;
            else if (c == closing && --depth == 0) return i;
        }
        return null;
    }

    /// <summary>Each <c>;</c>-separated entry of a PowerShell block or hashtable, and, for an entry <c>name=value</c>, its
    /// value alone too.</summary>
    private static string WithoutEntryNames(string text) =>
        string.Join(";", text.Split(';').SelectMany(entry =>
        {
            var trimmed = entry.TrimStart();
            var equals = trimmed.IndexOf('=');
            return equals > 0 && trimmed[..equals].Trim().All(ch => char.IsLetterOrDigit(ch) || ch is '_' or '-' or '"' or '\'')
                ? new[] { entry, trimmed[(equals + 1)..] }
                : [entry];
        }));

    /// <summary>
    /// Whether <paramref name="line"/> runs or writes more than its steps show: a subexpression, a grouping, a backtick, a
    /// redirection, and under PowerShell a block or a hashtable — outside quotes. Inside double quotes only <c>$(</c> and
    /// the backtick count (both shells still expand them there); inside single quotes nothing does:
    /// <c>git commit -m "feat(ui): x"</c> is one plain command.
    /// </summary>
    /// <remarks>⚠ Whatever makes the quotes read otherwise than this scan reads them counts too: a typographic quote
    /// (PowerShell takes <c>‘ ’ “ ”</c> for quotes) and, under a POSIX shell (<paramref name="posix"/>), a backslash before a
    /// quote and <c>$'…'</c> — bash reads <c>\' $(rm -rf src) \'</c> with the <c>$(</c> outside quotes. Under PowerShell a
    /// backslash is no escape: <c>"C:\repo\"</c> is a plain folder. ⚠ PowerShell runs a hashtable's values
    /// (<c>@{a=Remove-Item src}</c>): a brace there is not plain; in a POSIX shell it runs nothing (<c>HEAD@{1}</c>).</remarks>
    internal static bool Hides(string line, bool posix)
    {
        var quote = '\0';
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c is >= '\u2018' and <= '\u201E') return true;
            if (quote == '\'') { if (c == '\'') quote = '\0'; continue; }
            if (posix && i + 1 < line.Length && (c == '\\' && line[i + 1] is '\'' or '"' || c == '$' && line[i + 1] == '\''))
                return true;
            if (c == '`' || (c == '$' && i + 1 < line.Length && line[i + 1] == '(')) return true;
            if (quote == '"') { if (c == '"') quote = '\0'; continue; }
            if (c is '\'' or '"') { quote = c; continue; }
            if (c is '(' or '>') return true;
            if (!posix && c == '{' && i > 0 && line[i - 1] == '$')
            {
                // `${env:X}`: a variable's name, read to its closing brace.
                var close = line.IndexOf('}', i + 1);
                if (close < 0) return true;
                i = close;
                continue;
            }
            if (!posix && c is '{' or '}') return true;
        }
        return false;
    }
}
