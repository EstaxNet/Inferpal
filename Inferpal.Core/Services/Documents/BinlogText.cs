using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace Inferpal.Services.Documents;

/// <summary>
/// An MSBuild binary log (<c>.binlog</c>) read as what a person looks for in it: its errors and warnings, the projects it
/// built, and the projects, targets and tasks that took longest. Replayed by the .NET SDK on this machine
/// (<c>dotnet msbuild &lt;log&gt;</c>), the program that writes the format: no reader is shipped (one would bring MSBuild's
/// own assemblies, which the Visual Studio package leaves out), and the format follows the SDK's.
/// </summary>
internal static class BinlogText
{
    /// <summary>Why a log is not read when no <c>dotnet</c> runs on this machine (<see cref="DocumentProblem.NoReader"/>).</summary>
    internal const string NoSdk =
        "it is read by replaying it with the .NET SDK, and `dotnet` does not run on this machine: install the .NET SDK";

    /// <summary>Why a log is not read when the SDK here would not replay it, followed by what dotnet said.</summary>
    internal const string NotReplayed = "the .NET SDK on this machine could not replay it (a newer SDK may have written it): ";

    /// <summary>The first line of a replayed log: what follows and in which order.</summary>
    internal const string Heading =
        "MSBuild binary log, replayed by the .NET SDK on this machine: its errors, its warnings, the projects it built, then "
        + "what took longest.";

    /// <summary>How many of the slowest projects, targets and tasks are listed.</summary>
    internal const int SlowestShown = 10;

    /// <summary>How many errors, and how many warnings, are listed; the rest is counted.</summary>
    internal const int DiagnosticsShown = 200;

    private static readonly Regex Diagnostic = new(@":\s(error|warning)\s+[A-Za-z]*\d*\s*:", RegexOptions.None, RegexBudget.Default);
    private static readonly Regex Output     = new(@"^\s+\S.*\s->\s\S", RegexOptions.None, RegexBudget.Default);
    private static readonly Regex Timed      = new(@"^\s*(\d+)\s+ms\s+(.+?)\s+(\d+)\s+calls?\s*$", RegexOptions.None, RegexBudget.Default);

    /// <summary>The replayed text of the log held in <paramref name="bytes"/> (an entry of an archive): written to a file of
    /// its own first, the replay reading a file.</summary>
    /// <param name="ct">The reader's deadline or the person's cancel: the replay is killed, its tree with it.</param>
    internal static DocumentExtraction Extract(byte[] bytes, CancellationToken ct, int maxChars = PdfText.MaxChars)
    {
        var dir = Directory.CreateTempSubdirectory("inferpal-binlog-");
        try
        {
            var log = Path.Combine(dir.FullName, "build.binlog");
            File.WriteAllBytes(log, bytes);
            return ExtractFile(log, ct, maxChars);
        }
        finally
        {
            try { dir.Delete(recursive: true); } catch { /* cleanup */ }
        }
    }

    /// <summary>The replayed text of the log at <paramref name="path"/>, read where it is: never copied, never held whole.</summary>
    /// <param name="ct">The reader's deadline or the person's cancel: the replay is killed, its tree with it.</param>
    internal static DocumentExtraction ExtractFile(string path, CancellationToken ct, int maxChars = PdfText.MaxChars)
    {
        // A folder of this read's own (rule 39) for the replay's text.
        var dir = Directory.CreateTempSubdirectory("inferpal-binlog-");
        try
        {
            var text = Path.Combine(dir.FullName, "replay.log");
            var psi = new ProcessStartInfo
            {
                FileName = "dotnet", WorkingDirectory = dir.FullName,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            };
            foreach (var arg in new[] { "msbuild", path, "-noconlog", "-nologo",
                                        $"-flp:v=minimal;performancesummary;encoding=utf-8;logfile={text}" })
                psi.ArgumentList.Add(arg);
            // The replay's own words in English, as every dotnet the agent reads; the log's messages stay as written.
            psi.Environment["DOTNET_CLI_UI_LANGUAGE"] = "en";
            psi.Environment["VSLANG"] = "1033";
            psi.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
            psi.Environment["DOTNET_NOLOGO"] = "1";

            ChildProcessResult run;
            // ⚠ Through ChildProcess.RunAsync, never a loop of its own: it drains both pipes before it returns and bounds the
            // wait on a child that refuses to die. Waited for here, on the document reader's own thread (no synchronization
            // context), which DocumentText's deadline bounds and its cancel ends.
            try { run = ChildProcess.RunAsync(psi, null, ct).GetAwaiter().GetResult(); }
            catch (Win32Exception) { return new(NoSdk, 0, 0, DocumentProblem.NoReader); }
            ct.ThrowIfCancellationRequested();

            var written = File.Exists(text) ? File.ReadAllText(text, Encoding.UTF8) : string.Empty;
            // ⚠ A replay that read the log always writes its performance summary. One that could not (a damaged file, a log
            // from a newer MSBuild) EXITS 0, says why on the console — stdout, not stderr — and leaves an empty log: read as a
            // summary, that is "no error, no warning", a clean build that never was.
            if (!written.Contains("Performance Summary:", StringComparison.Ordinal))
            {
                var said = (run.Stdout.Trim() + "\n" + run.Stderr.Trim()).Trim();
                return new(NotReplayed + (said.Length == 0 ? $"exit code {run.ExitCode}" : said.Length > 2000 ? said[..2000] : said),
                           0, 0, DocumentProblem.NoReader);
            }
            return Summarize(written, maxChars);
        }
        finally
        {
            try { dir.Delete(recursive: true); } catch { /* cleanup */ }
        }
    }

    /// <summary>
    /// The replay's text (minimal verbosity with its performance summary) as the reader looks for it: errors first,
    /// warnings, projects built, then the slowest projects, targets and tasks. Each list is bounded and its rest counted.
    /// </summary>
    internal static DocumentExtraction Summarize(string replay, int maxChars = PdfText.MaxChars)
    {
        var lines    = replay.ReplaceLineEndings("\n").Split('\n');
        var errors   = new List<string>();
        var warnings = new List<string>();
        var outputs  = new List<string>();
        var seen     = new HashSet<string>(StringComparer.Ordinal);
        var section  = string.Empty;
        var timed    = new Dictionary<string, List<(long Ms, string Name, int Calls)>>(StringComparer.Ordinal);
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            if (line.EndsWith("Performance Summary:", StringComparison.Ordinal)) { section = line.Trim(); continue; }
            if (section.Length > 0 && Timed.Match(line) is { Success: true } t)
            {
                if (!timed.TryGetValue(section, out var list)) timed[section] = list = [];
                list.Add((long.Parse(t.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture), t.Groups[2].Value.Trim(),
                          int.Parse(t.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture)));
                continue;
            }
            if (Diagnostic.Match(line) is { Success: true } d)
            {
                // A diagnostic is logged once per project that reports it, often twice: said once.
                if (!seen.Add(line.Trim())) continue;
                (d.Groups[1].Value == "error" ? errors : warnings).Add(line.Trim());
                continue;
            }
            if (section.Length == 0 && Output.IsMatch(line)) outputs.Add(line.Trim());
        }

        var sb = new StringBuilder(Heading).Append('\n');
        void List(string title, List<string> items)
        {
            sb.Append('\n').Append("## ").Append(title).Append(" (").Append(items.Count).Append(")\n");
            if (items.Count == 0) { sb.Append("(none)\n"); return; }
            foreach (var i in items.Take(DiagnosticsShown)) sb.Append(i).Append('\n');
            if (items.Count > DiagnosticsShown) sb.Append("… +").Append(items.Count - DiagnosticsShown).Append(" more not listed\n");
        }
        List("Errors", errors);
        List("Warnings", warnings);
        List("Projects built", outputs);
        foreach (var (key, title) in new[]
                 {
                     ("Project Performance Summary:", "Slowest projects"),
                     ("Target Performance Summary:", "Slowest targets"),
                     ("Task Performance Summary:", "Slowest tasks"),
                 })
        {
            if (!timed.TryGetValue(key, out var list) || list.Count == 0) continue;
            sb.Append('\n').Append("## ").Append(title).Append(" (of ").Append(list.Count).Append(")\n");
            foreach (var (ms, name, calls) in list.OrderByDescending(x => x.Ms).Take(SlowestShown))
                sb.Append(ms.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(" ms  ").Append(name)
                  .Append(" (").Append(calls).Append(calls == 1 ? " call" : " calls").Append(")\n");
        }

        var text = sb.ToString().TrimEnd();
        return text.Length > maxChars
            ? new(text[..maxChars], 0, 0, DocumentProblem.None, StoppedAt: 1)
            : new(text, 0, 0, DocumentProblem.None);
    }
}
