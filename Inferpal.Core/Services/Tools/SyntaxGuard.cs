using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Inferpal.Services.Tools;

/// <summary>
/// Refuses an edit that ADDS a parse error, before it is written and before the approval card: C# by Roslyn, Razor by the
/// balance of its braces, Python by the machine's interpreter, ES and CommonJS modules (<c>.mjs</c>, <c>.cjs</c>) by
/// <c>node --check</c>. The model is told where and what, and sends the edit again.
/// </summary>
/// <remarks>
/// ⚠ Parse errors only, never semantic ones: a type moved to another file leaves CS0101 or CS0246 between two edits,
/// a legitimate state. ⚠ Added only: a file that did not parse before is not this edit's doing, and is let through.
/// ⚠ No refusal on a doubt: a checker missing, slow or answering something else lets the edit through. ⚠ Never a loop:
/// the checker can be older than the project's language (a C# or Python version it does not know), so the same edit
/// sent again after a refusal IN THE SAME RUN is written; outside a run no pass is offered, and the loop policy counts
/// the repeats (<see cref="OffersPass"/>). ⚠ A <c>.js</c> file is not checked: whether it parses depends on the project
/// (ES modules or not, JSX, Flow), which a copy checked on its own does not carry.
/// </remarks>
internal static class SyntaxGuard
{
    /// <summary>How long a Python or JavaScript check may take before the edit is let through unchecked.</summary>
    internal static TimeSpan Deadline { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>A parse error: its line (1-based) and what the parser says.</summary>
    internal readonly record struct ParseError(int Line, string Message);

    // The edits refused, by run, file and content: the same one sent again IN THE SAME RUN is the model insisting, and is
    // let through. ⚠ Keyed by run: otherwise a Regenerate, another ACP thread or a /task inherits the pass and writes the
    // broken edit unjudged. Outside a run nothing is remembered: every edit is judged.
    private static readonly ConcurrentDictionary<string, byte> RefusedOnce = new(StringComparer.Ordinal);

    // A checker that could not start or did not answer in time is not asked again in this process: every edit would
    // wait for it (on Windows, the Store's "python" alias can hang without a byte).
    private static volatile bool _pythonUnavailable, _nodeUnavailable;

    /// <summary>What every refusal says, in whatever a tool wraps it (<c>apply_edits</c>'s localized "no changes").</summary>
    internal const string RefusalMarker = "Refused: this edit leaves a syntax error in ";

    /// <summary>Whether a tool's result carries this guard's refusal, alone or inside a batch's (whether the same call sent
    /// again is what the refusal asks for: <see cref="OffersPass"/>).</summary>
    internal static bool Refused(string toolResult) => toolResult.Contains(RefusalMarker, StringComparison.Ordinal);

    /// <summary>What a refusal that offers the pass says: only within a run.</summary>
    internal const string PassOffered =
        " If the code is valid for this project (a newer language version than the check knows), send the same edit "
        + "again and it will be written.";

    /// <summary>Whether a tool's result is a refusal that lets the same edit through when it is sent again — never outside
    /// a run, where the loop policy must count the repeats.</summary>
    internal static bool OffersPass(string toolResult) =>
        Refused(toolResult) && toolResult.Contains(PassOffered, StringComparison.Ordinal);

    /// <summary>Forgets the refusals and the checkers found unavailable (tests).</summary>
    internal static void ResetForTests()
    {
        RefusedOnce.Clear();
        _pythonUnavailable = _nodeUnavailable = false;
    }

    /// <summary>
    /// Why the edit of <paramref name="path"/> from <paramref name="original"/> (<c>null</c>: a new file) to
    /// <paramref name="proposed"/> is refused, or <c>null</c>: it adds no parse error, nothing could say, or the model
    /// sent the same edit again after its refusal.
    /// </summary>
    /// <param name="root">The workspace root, to name the file by its path in the refusal.</param>
    /// <param name="runId">The run the edit belongs to (<see cref="Execution.FileHistoryService.CurrentRunId"/>); the
    /// same edit sent again passes only within it.</param>
    public static async Task<string?> RefusalAsync(string path, string? original, string proposed, string? root,
                                                   string? runId, CancellationToken ct)
    {
        if (await FirstErrorAsync(path, proposed, ct) is not { } error) return null;
        if (original is not null && await FirstErrorAsync(path, original, ct) is not null) return null;

        if (runId is not null)
        {
            var key = runId + "\n" + path + "\n" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(proposed)));
            if (RefusedOnce.TryRemove(key, out _)) return null;
            if (RefusedOnce.Count > 256) RefusedOnce.Clear();
            RefusedOnce[key] = 0;
        }
        return $"{RefusalMarker}{Shown(path, root)} at line {error.Line} ({error.Message}). "
               + "Nothing was written: correct the edit and send it again."
               + (runId is null ? string.Empty : PassOffered);
    }

    private static string Shown(string path, string? root)
    {
        if (string.IsNullOrEmpty(root)) return path;
        var relative = Path.GetRelativePath(root, path);
        return relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative)
            ? path
            : relative.Replace('\\', '/');
    }

    /// <summary>The first parse error of <paramref name="text"/> as <paramref name="path"/>'s language reads it, or
    /// <c>null</c>: none, a language not checked, or no answer from its parser.</summary>
    internal static async Task<ParseError?> FirstErrorAsync(string path, string text, CancellationToken ct) =>
        !Checks(path) ? null : Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".cs"                 => CSharpError(text),
            ".razor" or ".cshtml" => RazorBraceError(text),
            ".py"                 => await PythonErrorAsync(text, ct),
            ".mjs" or ".cjs"      => await NodeErrorAsync(Path.GetExtension(path).ToLowerInvariant(), text, ct),
            _                     => null,
        };

    /// <summary>Whether <see cref="FirstErrorAsync"/> judges <paramref name="path"/>'s language at all: a caller asks
    /// before reading a file for nothing. ⚠ The one list: it gates the switch below, so a language added there alone is
    /// never reached.</summary>
    internal static bool Checks(string path) =>
        ChecksInProcess(path) || Path.GetExtension(path).ToLowerInvariant() is ".py" or ".mjs" or ".cjs";

    /// <summary>Whether <paramref name="path"/>'s language is judged without starting a process (Roslyn, the Razor brace
    /// count): what a gesture checks on a click, where an interpreter's cold start would make the click wait — and, past
    /// the deadline, turn that interpreter off for the session.</summary>
    internal static bool ChecksInProcess(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".cs" or ".razor" or ".cshtml";

    // File-based programs (.NET 10's `#:package`, `#:sdk`) read as the SDK reads them.
    private static readonly CSharpParseOptions CSharpOptions = new CSharpParseOptions(LanguageVersion.Preview)
        .WithFeatures([new KeyValuePair<string, string>("FileBasedProgram", "true")]);

    // Not syntax: `#error` in a branch the project's symbols would turn off (they are not known here), and a file-based
    // program's directive in a file the parser does not take for one.
    private static readonly HashSet<string> NotSyntax = new(StringComparer.Ordinal) { "CS1029", "CS9298" };

    private static ParseError? CSharpError(string text)
    {
        var tree = CSharpSyntaxTree.ParseText(text, CSharpOptions);
        var d = tree.GetDiagnostics().FirstOrDefault(x => x.Severity == DiagnosticSeverity.Error && !NotSyntax.Contains(x.Id));
        return d is null ? null
            : new ParseError(d.Location.GetLineSpan().StartLinePosition.Line + 1,
                             $"{d.Id}: {d.GetMessage(CultureInfo.InvariantCulture)}");
    }

    /// <summary>
    /// The first brace of a Razor file that closes nothing, or the first one never closed — braces inside double-quoted
    /// strings, C# character literals and comments (<c>@* *@</c>, <c>&lt;!-- --&gt;</c>, <c>/* */</c>, <c>//</c>) aside.
    /// </summary>
    /// <remarks>A single quote opens no string: in markup it is an apostrophe far more often than a C# character literal,
    /// and a lone one would swallow the rest of the file. Only a literal of one character (<c>'{'</c>, <c>'\''</c>) is
    /// skipped. A double quote opens a string only when it closes on its line (<c>27" monitor</c> is text), or as a
    /// verbatim string (<c>@"C:\Temp\"</c>). <c>//</c> after a <c>:</c> is an address, not a comment; a <c>/*</c> never
    /// closed is text (<c>src/*.cs</c>). <c>&lt;script&gt;</c> and <c>&lt;style&gt;</c> are another language, and
    /// skipped whole.</remarks>
    internal static ParseError? RazorBraceError(string text)
    {
        var open = new Stack<int>();
        var line = 1;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\n') { line++; continue; }
            if (Skip(text, ref i, ref line, "@*", "*@") || Skip(text, ref i, ref line, "<!--", "-->")
                || Skip(text, ref i, ref line, "/*", "*/", closedOnly: true)
                || Skip(text, ref i, ref line, "<script", "</script>", closedOnly: true, ignoreCase: true)
                || Skip(text, ref i, ref line, "<style", "</style>", closedOnly: true, ignoreCase: true))
                continue;
            if (c == '/' && i + 1 < text.Length && text[i + 1] == '/' && (i == 0 || text[i - 1] != ':'))
            {
                var eol = text.IndexOf('\n', i);
                if (eol < 0) break;
                i = eol - 1;
                continue;
            }
            if (c == '\'' && CharLiteralLength(text, i) is > 0 and var length)
            {
                i += length - 1;
                continue;
            }
            if (c == '"' && StringEnd(text, i, verbatim: i > 0 && text[i - 1] == '@') is > 0 and var end)
            {
                line += Count(text, i, end, '\n');
                i = end;
                continue;
            }
            if (c == '{') open.Push(line);
            else if (c == '}')
            {
                if (open.Count == 0) return new ParseError(line, "a '}' that closes nothing");
                open.Pop();
            }
        }
        return open.Count > 0 ? new ParseError(open.Last(), "a '{' that is never closed") : null;
    }

    /// <summary>The closing quote of the string opened at <paramref name="at"/> — on its line, past <c>\"</c> escapes; a
    /// verbatim one past <c>""</c>, on any line — or -1: the quote is text.</summary>
    private static int StringEnd(string text, int at, bool verbatim)
    {
        for (var k = at + 1; k < text.Length; k++)
        {
            if (verbatim)
            {
                if (text[k] != '"') continue;
                if (k + 1 < text.Length && text[k + 1] == '"') { k++; continue; }
                return k;
            }
            if (text[k] == '\n') return -1;
            if (text[k] == '\\') { k++; continue; }
            if (text[k] == '"') return k;
        }
        return -1;
    }

    /// <summary>The length of the C# character literal at <paramref name="at"/> (<c>'x'</c>, <c>'\x'</c>), or 0.</summary>
    private static int CharLiteralLength(string text, int at)
    {
        if (at + 2 < text.Length && text[at + 1] != '\\' && text[at + 1] != '\n' && text[at + 2] == '\'') return 3;
        if (at + 3 < text.Length && text[at + 1] == '\\' && text[at + 3] == '\'') return 4;
        return 0;
    }

    private static bool Skip(string text, ref int i, ref int line, string start, string end, bool closedOnly = false,
                             bool ignoreCase = false)
    {
        var comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (string.Compare(text, i, start, 0, start.Length, comparison) != 0) return false;
        var close = text.IndexOf(end, i + start.Length, comparison);
        if (close < 0 && closedOnly) return false;
        var stop  = close < 0 ? text.Length : close + end.Length;
        line += Count(text, i, stop, '\n');
        i = stop - 1;
        return true;
    }

    private static int Count(string text, int from, int to, char c)
    {
        var n = 0;
        for (var k = from; k < to && k < text.Length; k++) if (text[k] == c) n++;
        return n;
    }

    private const string CheckedName = "check";

    /// <summary>
    /// A checker by its absolute path: the user's active <c>VIRTUAL_ENV</c> for Python, else the PATH; <c>null</c> when
    /// there is none.
    /// </summary>
    /// <remarks>⚠ This runs before the approval card: never an executable the repository or another user could place —
    /// no project <c>.venv</c>, no shared temporary folder, no bare name (which Windows looks up in the current folder,
    /// the workspace, first), no relative PATH entry.</remarks>
    private static string? Checker(string name)
    {
        var windows = OperatingSystem.IsWindows();
        if (name == "python" && Environment.GetEnvironmentVariable("VIRTUAL_ENV") is { Length: > 0 } venv
            && Path.IsPathRooted(venv)
            && Path.Combine(venv, windows ? Path.Combine("Scripts", "python.exe") : Path.Combine("bin", "python")) is var active
            && File.Exists(active))
            return active;
        string[] names = name == "python" ? (windows ? ["python.exe"] : ["python3", "python"]) : [windows ? name + ".exe" : name];
        foreach (var candidate in names)
            if (Shell.ShellLauncher.FindOnPath(candidate) is { } found && Path.IsPathRooted(found))
                return found;
        return null;
    }

    /// <summary>The first syntax error Python reports, by the machine's interpreter (<see cref="Checker"/>).</summary>
    private static async Task<ParseError?> PythonErrorAsync(string text, CancellationToken ct)
    {
        if (_pythonUnavailable) return null;
        if (Checker("python") is not { } python) { _pythonUnavailable = true; return null; }
        var result = await RunOnCopyAsync(text, ".py", file =>
        {
            var psi = new ProcessStartInfo(python);
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add("import ast,sys; ast.parse(open(sys.argv[1], encoding='utf-8').read(), sys.argv[1])");
            psi.ArgumentList.Add(file);
            return psi;
        }, ct);
        if (result is not { TimedOut: false } r) { _pythonUnavailable = true; return null; }
        if (r.Succeeded) return null;
        var kind = Find(r.Stderr, @"^(?:SyntaxError|IndentationError|TabError): .*$", RegexOptions.Multiline);
        // The checked file's own frame: the traceback names the -c script and ast.py first.
        var at   = Find(r.Stderr, Regex.Escape(CheckedName + ".py") + @""", line (\d+)");
        return kind is not null && at is not null
            ? new ParseError(int.Parse(at.Groups[1].Value, CultureInfo.InvariantCulture), kind.Value.Trim())
            : null;
    }

    private static async Task<ParseError?> NodeErrorAsync(string extension, string text, CancellationToken ct)
    {
        if (_nodeUnavailable) return null;
        if (Checker("node") is not { } node) { _nodeUnavailable = true; return null; }
        var result = await RunOnCopyAsync(text, extension, file =>
        {
            var psi = new ProcessStartInfo(node);
            psi.ArgumentList.Add("--check");
            psi.ArgumentList.Add(file);
            return psi;
        }, ct);
        if (result is not { TimedOut: false } r) { _nodeUnavailable = true; return null; }
        if (r.Succeeded) return null;
        var kind = Find(r.Stderr, @"^SyntaxError: .*$", RegexOptions.Multiline);
        var at   = Find(r.Stderr, Regex.Escape(CheckedName + extension) + @":(\d+)");
        return kind is not null && at is not null
            ? new ParseError(int.Parse(at.Groups[1].Value, CultureInfo.InvariantCulture), kind.Value.Trim())
            : null;
    }

    /// <summary>A match in a checker's output, which is not ours: bounded, and a match past its budget is no answer.</summary>
    private static Match? Find(string input, string pattern, RegexOptions options = RegexOptions.None)
    {
        try
        {
            var match = Regex.Match(input, pattern, options, RegexBudget.Default);
            return match.Success ? match : null;
        }
        catch (RegexMatchTimeoutException) { return null; }
    }

    /// <summary>Runs a checker on a temporary copy of the text (never the workspace's file); <c>null</c> when it could
    /// not start.</summary>
    private static async Task<ChildProcessResult?> RunOnCopyAsync(string text, string extension,
                                                                  Func<string, ProcessStartInfo> checker, CancellationToken ct)
    {
        var dir  = Path.Combine(Path.GetTempPath(), "inferpal-syntax", Guid.NewGuid().ToString("N"));
        var file = Path.Combine(dir, CheckedName + extension);
        try
        {
            Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(file, text, ct);
            return await ChildProcess.RunAsync(checker(file), Deadline, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Diagnostics.Swallow("SyntaxGuard", ex);
            return null;
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}
