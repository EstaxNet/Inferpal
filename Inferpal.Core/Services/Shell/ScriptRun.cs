using System.Globalization;
using System.Text;

namespace Inferpal.Services.Shell;

/// <summary>
/// A script the agent writes when no tool does what it needs (pari 161): Python, PowerShell, bash or Node, written OUTSIDE
/// the repository — in a folder of the account — and run by <c>run_command</c> from the session's folder, under the same
/// rules and budget as a command. The card shows the script itself: what is approved is what runs.
/// </summary>
/// <remarks>
/// ⚠ What runs is what the card showed: every line break an interpreter honours (a lone CR, U+2028, U+2029, U+0085) is made
/// a plain line break BEFORE the card and the rules see the script, and the file that runs is written AFTER the answer, from
/// the approved text — never a file that waited on disk while the card did. ⚠ The interpreter is the machine's, never
/// installed: a language it lacks is refused BEFORE the card, naming the ones it has and what installs the one asked for. A
/// project's virtual environment is the repository's own program: named on the card, and run only as the repository's code
/// is (its owner's "No" holds). ⚠ What the rules judge is the line that runs it AND every line of the script; an "Always"
/// for scripts is not one for commands, nor the other way round.
/// </remarks>
internal static class ScriptRun
{
    /// <summary>The languages a script may be written in, and the extension its file takes.</summary>
    internal static readonly IReadOnlyDictionary<string, string> Extensions = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["python"] = ".py", ["powershell"] = ".ps1", ["bash"] = ".sh", ["node"] = ".js",
    };

    /// <summary>What an "Always" given on a script's card opens: scripts, never commands.</summary>
    internal const string GrantKey = "run_command:script";

    /// <summary>What the card's details start with, so it is drawn as a script: the marker, the language, the
    /// interpreter's name, the folder it runs in and its file, separated by tabs, then the script.</summary>
    /// <remarks>⚠ A <c>command</c> holding the marker is refused (<c>RunCommandTool</c>): the model could otherwise draw a
    /// script's card over a command.</remarks>
    internal const string Marker = "#!inferpal-script\t";

    /// <summary>How long a day's scripts are kept: long enough to read one again, short enough not to pile up.</summary>
    private static readonly TimeSpan KeptFor = TimeSpan.FromDays(7);

    /// <summary>The folder of the account the scripts are written in.</summary>
    internal static string Home => HomeOverride ?? Path.Combine(UserFolders.Of(Environment.SpecialFolder.LocalApplicationData), "Inferpal", "scripts");

    /// <summary>Set by the test suite: its own folder, never the account's.</summary>
    internal static string? HomeOverride { get; set; }

    /// <summary><paramref name="code"/> with every line break an interpreter honours made <c>\n</c>: the text the card, the
    /// rules and the interpreter all read the same way.</summary>
    internal static string Normalize(string code) =>
        code.Replace("\r\n", "\n").Replace('\r', '\n').Replace('\u2028', '\n').Replace('\u2029', '\n').Replace('\u0085', '\n');

    // Python run so that the project's own modules import as from a script in the session's folder: the script's file
    // lives outside it, and Python looks first in the script's folder. No quote in it: Windows PowerShell 5.1 loses them
    // on the way to a native program.
    private const string PythonLauncher =
        "import sys,os,runpy;sys.path.insert(0,os.getcwd());runpy.run_path(sys.argv[1],run_name=''.join(map(chr,[95,95,109,97,105,110,95,95])))";

    /// <summary>The interpreter that runs <paramref name="language"/> on this machine, the arguments before the file, its name
    /// for the card, and whether it is the repository's own (a virtual environment under the workspace); or <c>null</c>.</summary>
    internal static (string Exe, string[] Args, string Name, bool Repository)? Interpreter(string language, string workDir, string? root,
        Func<string, string?> onPath, Func<string, bool> exists, bool isWindows)
    {
        switch (language)
        {
            case "python":
                var python = Tools.RunTestsTool.ResolvePython(workDir, root, isWindows, onPath, exists,
                                                              Environment.GetEnvironmentVariable("VIRTUAL_ENV"));
                // The Store's "python" alias opens the Store, or hangs without a byte, when Python is not installed: never
                // taken from the PATH. A venv's interpreter is a path; the PATH's is a name, looked up.
                string? Usable(string? p) => p is null || p.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase) ? null : p;
                if (Path.IsPathRooted(python))
                {
                    var own = root is not null && Path.GetRelativePath(root, python) is var rel && !rel.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(rel);
                    return (python, ["-X", "utf8", "-c", PythonLauncher], own ? $"Python ({Path.GetRelativePath(root!, python)})" : "Python", own);
                }
                return (Usable(onPath(python)) ?? (isWindows ? Usable(onPath("py")) : null)) is { } found
                    ? (found, ["-X", "utf8", "-c", PythonLauncher], "Python", false) : null;
            case "powershell":
                if (onPath("pwsh") is { } pwsh) return (pwsh, ["-NoProfile", "-NonInteractive", "-File"], "PowerShell", false);
                if (!isWindows) return null;
                var windowsPs = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
                return exists(windowsPs) ? (windowsPs, ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File"], "Windows PowerShell", false) : null;
            case "bash":
                // Windows' own bash.exe (System32) starts WSL, another system with another file system: never it.
                if (onPath("bash") is { } bash && !(isWindows && bash.Contains(@"\System32\", StringComparison.OrdinalIgnoreCase)))
                    return (bash, [], "bash", false);
                if (isWindows)
                {
                    var local = Path.Combine(UserFolders.Of(Environment.SpecialFolder.LocalApplicationData), "Programs", "Git", "bin", "bash.exe");
                    return new[] { @"C:\Program Files\Git\bin\bash.exe", @"C:\Program Files (x86)\Git\bin\bash.exe", local }.FirstOrDefault(exists) is { } git
                        ? (git, [], "bash", false) : null;
                }
                return exists("/bin/bash") ? ("/bin/bash", [], "bash", false) : null;
            case "node":
                return onPath("node") is { } node ? (node, [], "Node", false) : null;
            default:
                return null;
        }
    }

    /// <summary>Why <paramref name="language"/> cannot run here: the languages this machine has, and what installs the one
    /// asked for. Said to the model, in English.</summary>
    internal static string Missing(string language, IEnumerable<string> available)
    {
        var install = language switch
        {
            "python"     => OperatingSystem.IsWindows() ? "python.org (with its py launcher), or `winget install Python.Python.3.12`" : OperatingSystem.IsMacOS() ? "`brew install python`" : "`sudo apt install python3`",
            "powershell" => "`winget install Microsoft.PowerShell` or the PowerShell docs for this system",
            "bash"       => OperatingSystem.IsWindows() ? "Git for Windows (its bash)" : "the system's package manager",
            "node"       => OperatingSystem.IsWindows() ? "`winget install OpenJS.NodeJS`" : OperatingSystem.IsMacOS() ? "`brew install node`" : "`sudo apt install nodejs`",
            _            => "",
        };
        var here = available.ToList();
        return $"Error: no {language} interpreter on this machine — nothing ran. "
             + (here.Count > 0 ? $"Write the script in {string.Join(" or ", here)} instead, which this machine has. " : "No script language is installed here. ")
             + (install.Length > 0 ? $"The person can install {language} with {install}." : "");
    }

    /// <summary>A new file of today's folder for a script in <paramref name="language"/>, not written yet; the folders older
    /// than a week are cleared.</summary>
    internal static string NewFile(string language)
    {
        var today = Path.Combine(Home, DateTime.UtcNow.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(today);
        Sweep();
        return Path.Combine(today, "script-" + Guid.NewGuid().ToString("N")[..8] + Extensions[language]);
    }

    /// <summary>Writes <paramref name="code"/> to <paramref name="file"/>: the copy the card opens before the answer, the file
    /// that runs after it.</summary>
    internal static Task WriteAsync(string file, string code, string language, CancellationToken ct) =>
        // With a byte order mark for PowerShell: Windows PowerShell 5.1 reads a script without one in the ANSI code page.
        File.WriteAllTextAsync(file, code, new UTF8Encoding(encoderShouldEmitUTF8Identifier: language == "powershell"), ct);

    /// <summary>The copy of <paramref name="file"/> the card opens in an editor tab: the script as shown, never the one run.</summary>
    internal static string ViewOf(string file) =>
        Path.Combine(Path.GetDirectoryName(file) ?? "", Path.GetFileNameWithoutExtension(file) + ".view" + Path.GetExtension(file));

    private static void Sweep()
    {
        try
        {
            foreach (var day in Directory.EnumerateDirectories(Home))
                if (DateTime.UtcNow - Directory.GetLastWriteTimeUtc(day) > KeptFor)
                    try { Directory.Delete(day, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Diagnostics.Swallow("ScriptRun.Sweep", ex); }
    }

    /// <summary>The line that runs the script in the session's shell, each part quoted for <paramref name="dialect"/>.</summary>
    internal static string CommandLine(ShellDialect dialect, string exe, IEnumerable<string> args, string file)
    {
        var parts = new[] { exe }.Concat(args).Append(file);
        return dialect == ShellDialect.PowerShell
            ? "& " + string.Join(' ', parts.Select(p => "'" + p.Replace("'", "''") + "'"))
            : string.Join(' ', parts.Select(p => "'" + p.Replace("'", "'\\''") + "'"));
    }

    /// <summary>The card's details: the marker line, then the script.</summary>
    internal static string Details(string language, string interpreter, string folder, string file, string code) =>
        $"{Marker}{language}\t{interpreter}\t{folder}\t{file}\n{code.TrimEnd('\n')}";

    /// <summary>The marker line's fields and the script, or <c>null</c> for details that are no script's.</summary>
    internal static (string Language, string Interpreter, string Folder, string File, string Code)? ReadDetails(string details)
    {
        if (!details.StartsWith(Marker, StringComparison.Ordinal)) return null;
        var newline = details.IndexOf('\n');
        var head = (newline < 0 ? details : details[..newline])[Marker.Length..].Split('\t');
        return head.Length == 4 ? (head[0], head[1], head[2], head[3], newline < 0 ? "" : details[(newline + 1)..]) : null;
    }

    /// <summary>The details as a person reads them where no card is drawn (the one-line prompt, an ACP client): a heading in
    /// place of the marker line.</summary>
    internal static string Readable(string details) =>
        ReadDetails(details) is { } s ? $"A script ({s.Interpreter}) run in {s.Folder}:\n{s.Code}" : details;
}
