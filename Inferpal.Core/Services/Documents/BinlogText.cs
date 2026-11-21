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
    /// <summary>What is said when no <c>dotnet</c> runs on this machine: the log cannot be replayed without the SDK.</summary>
    internal const string NoSdk =
        "This MSBuild binary log is read by replaying it with the .NET SDK (`dotnet msbuild`), and `dotnet` does not run on "
        + "this machine: install the .NET SDK to read it.";

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

    /// <summary>The replayed text of the log held in <paramref name="bytes"/>.</summary>
    /// <param name="ct">The reader's deadline or the person's cancel: the replay is killed, its tree with it.</param>
    internal static DocumentExtraction Extract(byte[] bytes, CancellationToken ct, int maxChars = PdfText.MaxChars)
    {
        // A folder of this read's own (rule 39): the log copied there, the replay's text written beside it.
        var dir = Directory.CreateTempSubdirectory("inferpal-binlog-");
        try
        {
            var log  = Path.Combine(dir.FullName, "build.binlog");
            var text = Path.Combine(dir.FullName, "replay.log");
            File.WriteAllBytes(log, bytes);
            var psi = new ProcessStartInfo
            {
                FileName = "dotnet", WorkingDirectory = dir.FullName, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            };
            foreach (var arg in new[] { "msbuild", log, "-noconlog", "-nologo",
                                        $"-flp:v=minimal;performancesummary;encoding=utf-8;logfile={text}" })
                psi.ArgumentList.Add(arg);
            // The replay's own words in English, as every dotnet the agent reads; the log's messages stay as written.
            psi.Environment["DOTNET_CLI_UI_LANGUAGE"] = "en";
            psi.Environment["VSLANG"] = "1033";
            psi.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
            psi.Environment["DOTNET_NOLOGO"] = "1";

            Process process;
            try { process = ChildProcess.Start(psi); }
            catch (Win32Exception) { return new(NoSdk, 0, 0, DocumentProblem.None); }
            string stderr;
            using (process)
            {
                // No token on the reads: the pipes end when the process does, killed or not.
                _ = process.StandardOutput.ReadToEndAsync();
                var err = process.StandardError.ReadToEndAsync();
                while (!process.WaitForExit(100))
                {
                    if (!ct.IsCancellationRequested) continue;
                    try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
                    break;
                }
                process.WaitForExit();
                ct.ThrowIfCancellationRequested();
                // The process has exited: the read is complete, nothing waits.
                stderr = err.IsCompletedSuccessfully ? err.Result : string.Empty;
            }
            // An SDK older than the one that wrote the log, or a damaged file: dotnet says why on its own error stream.
            if (!File.Exists(text) || new FileInfo(text).Length == 0)
                return string.IsNullOrWhiteSpace(stderr)
                    ? new(string.Empty, 0, 0, DocumentProblem.Unreadable)
                    : new("The .NET SDK on this machine could not replay this binary log: " + stderr.Trim(), 0, 0, DocumentProblem.None);
            return Summarize(File.ReadAllText(text, Encoding.UTF8), maxChars);
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
