using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Inferpal.Services.Documents;

/// <summary>
/// A test report read as its result: VSTest's <c>.trx</c>, or JUnit XML (pytest, Maven, Gradle, jest-junit,
/// go-junit-report) — the counts, then each failed test with its message and stack, without running the suite again.
/// </summary>
/// <remarks>
/// ⚠ The file stays a text file: this result stands in for its first page only, and <c>start_line</c> reads the XML itself,
/// at the line numbers <c>search_in_files</c> gives. Read as a page, a report of 300 tests shows hundreds of green results
/// before its first red one, or none at all under the read cap. ⚠ A JUnit report is known by its root element, never by
/// its <c>.xml</c> extension alone.
/// </remarks>
internal static class TestReportText
{
    /// <summary>The largest report read as its result: past it, its XML is paged as it is (a tree of it costs several times
    /// its size).</summary>
    private const int MaxReportChars = 8_000_000;

    /// <summary>How many lines of a message, then of a stack, are shown for one failed test, and how long a line.</summary>
    private const int MessageLines = 12, StackLines = 10, LineChars = 300;

    /// <summary>The result of the report <paramref name="xml"/> read from <paramref name="name"/>, within
    /// <paramref name="budget"/> characters; <c>null</c> when it is not a test report.</summary>
    internal static string? Summarize(string name, string xml, int budget)
    {
        var extension = Path.GetExtension(name).ToLowerInvariant();
        if (extension is not (".trx" or ".xml") || xml.Length > MaxReportChars) return null;
        var head = xml.Length > 4096 ? xml[..4096] : xml;
        if (!head.Contains("<TestRun", StringComparison.Ordinal) && !head.Contains("<testsuite", StringComparison.Ordinal))
            return null;

        XElement root;
        try
        {
            using var reader = XmlReader.Create(new StringReader(xml),
                new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            root = XElement.Load(reader);
        }
        catch (XmlException ex)
        {
            Diagnostics.Swallow("TestReportText.Parse", ex);
            return null;
        }

        var report = root.Name.LocalName switch
        {
            "TestRun" => Trx(root),
            "testsuites" or "testsuite" => JUnit(root),
            _ => null,
        };
        return report is null ? null : Render(name, report, budget);
    }

    private sealed record Failure(string Test, string Kind, string Message, string Stack);

    private sealed record Report(string Format, int Total, int Passed, int Failed, int Errors, int Skipped, List<Failure> Failures);

    private static Report Trx(XElement run)
    {
        var results = run.Descendants().Where(e => e.Name.LocalName == "UnitTestResult")
            // A data-driven test holds its rows as inner results: the rows are the tests.
            .Where(e => !e.Descendants().Any(d => d.Name.LocalName == "UnitTestResult")).ToList();
        int passed = 0, failed = 0, errors = 0, skipped = 0;
        var failures = new List<Failure>();
        foreach (var r in results)
        {
            var outcome = (string?)r.Attribute("outcome") ?? string.Empty;
            switch (outcome)
            {
                // Every outcome VSTest writes: a test that passed with a warning, or before the run was aborted, passed.
                case "Passed" or "PassedButRunAborted" or "Warning" or "Completed": passed++; continue;
                case "NotExecuted" or "Inconclusive" or "Pending" or "Disconnected" or "InProgress": skipped++; continue;
                case "Failed": failed++; break;
                default: errors++; break;                       // Error, Timeout, Aborted, NotRunnable
            }
            var error = r.Descendants().FirstOrDefault(e => e.Name.LocalName == "ErrorInfo");
            failures.Add(new((string?)r.Attribute("testName") ?? "?", outcome == "Failed" ? "failed" : outcome.ToLowerInvariant(),
                             Child(error, "Message"), Child(error, "StackTrace")));
        }
        return new("VSTest .trx", results.Count, passed, failed, errors, skipped, failures);
    }

    private static Report JUnit(XElement root)
    {
        var cases = root.DescendantsAndSelf().Where(e => e.Name.LocalName == "testcase").ToList();
        int passed = 0, failed = 0, errors = 0, skipped = 0;
        var failures = new List<Failure>();
        foreach (var c in cases)
        {
            var failure = c.Elements().FirstOrDefault(e => e.Name.LocalName == "failure");
            var error = c.Elements().FirstOrDefault(e => e.Name.LocalName == "error");
            if (failure is null && error is null)
            {
                if (c.Elements().Any(e => e.Name.LocalName == "skipped")) skipped++;
                else passed++;
                continue;
            }
            var bad = failure ?? error!;
            if (failure is not null) failed++; else errors++;
            var className = (string?)c.Attribute("classname");
            var test = string.IsNullOrEmpty(className) ? (string?)c.Attribute("name") ?? "?" : className + "." + (string?)c.Attribute("name");
            var message = ((string?)bad.Attribute("message") ?? string.Empty).Trim();
            var body = bad.Value.Trim();
            // pytest and others repeat the message as the body's last line: shown once.
            failures.Add(new(test, failure is not null ? "failed" : "error", message, body == message ? string.Empty : body));
        }
        return new("JUnit XML", cases.Count, passed, failed, errors, skipped, failures);
    }

    private static string Child(XElement? parent, string name) =>
        parent?.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value.Trim() ?? string.Empty;

    private static string Render(string name, Report r, int budget)
    {
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.Append(inv, $"[Test report {name} ({r.Format}): {r.Total} test(s) — {r.Passed} passed, {r.Failed} failed");
        if (r.Errors > 0) sb.Append(inv, $", {r.Errors} in error");
        if (r.Skipped > 0) sb.Append(inv, $", {r.Skipped} not run");
        sb.Append(". This is the report's result; read its XML with start_line=1.]\n");
        if (r.Failures.Count == 0)
        {
            sb.Append(r.Total == 0 ? "\nThe report holds no test.\n" : "\nNo test failed.\n");
            return sb.ToString();
        }

        var shown = 0;
        foreach (var f in r.Failures)
        {
            var entry = new StringBuilder();
            entry.Append(inv, $"\n{shown + 1}. {Line(f.Test)} — {f.Kind}\n");
            AppendLines(entry, f.Message, MessageLines);
            AppendLines(entry, f.Stack, StackLines);
            // ⚠ Cut between two tests, and the rest counted: a list that stops without saying so reads as all the reds.
            var room = budget - 200 - sb.Length;
            if (entry.Length > room && shown > 0) break;
            // The first one shows even alone over the budget — cut, and said.
            if (entry.Length > room) { entry.Length = Math.Max(0, room - 40); entry.Append("\n   … (cut to fit this read)\n"); }
            sb.Append(entry);
            shown++;
        }
        if (shown < r.Failures.Count)
            sb.Append(inv, $"\n{r.Failures.Count - shown} more failed test(s) are not shown here: search_in_files finds them in the XML (<ErrorInfo in a .trx, <failure or <error in JUnit).\n");
        return sb.ToString();
    }

    /// <summary>A line at most <see cref="LineChars"/> long: a serialized object or a snapshot diff writes megabytes on one.</summary>
    private static string Line(string line) =>
        line.Length <= LineChars ? line : string.Create(CultureInfo.InvariantCulture, $"{line[..LineChars]}… ({line.Length - LineChars} more characters)");

    /// <summary>The first <paramref name="max"/> non-blank lines of <paramref name="text"/>, indented, and how many more.</summary>
    private static void AppendLines(StringBuilder into, string text, int max)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n').Where(l => l.Trim().Length > 0).ToList();
        foreach (var line in lines.Take(max)) into.Append("   ").Append(Line(line.TrimEnd())).Append('\n');
        if (lines.Count > max) into.Append(CultureInfo.InvariantCulture, $"   … {lines.Count - max} more line(s)\n");
    }
}
