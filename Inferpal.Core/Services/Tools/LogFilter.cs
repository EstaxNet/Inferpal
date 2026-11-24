using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Inferpal.Services.Tools;

/// <summary>
/// A log read by level and time: <c>read_file</c> with <c>level</c>, <c>since</c> or <c>until</c>. An entry is a line that
/// opens with a time or a level (Serilog, NLog, log4net, Go's <c>log</c>, the <c>ILogger</c> console, ISO and syslog stamps),
/// with the lines under it that open with neither (a stack trace) — kept or dropped with it; or a structured record, a JSON
/// object or logfmt line (<see cref="Documents.StructuredLogLine"/>), shown as its time, level, message and exception, the records
/// that repeat a message already on the page counted rather than shown again.
/// </summary>
/// <remarks>
/// ⚠ What the filter could not judge is counted and said: lines before the first entry, entries with no level under a level
/// filter, entries with no time under a time filter, times compared without their date. Dropped silently, they let the model
/// conclude an error is absent from a log whose format it never saw recognised.
/// ⚠ A page is cut between two entries, and a <c>start_line</c> inside an entry is judged from that entry's header: cut
/// anywhere else, the next page took the rest of a stack trace for lines before the first entry, and a log whose last entry
/// was cut read as "no entry recognised".
/// </remarks>
internal static class LogFilter
{
    /// <summary>The levels, lowest first; each spelling the common formats write.</summary>
    private static readonly string[][] Levels =
    [
        ["trace", "trc", "trce", "verbose", "vrb", "finest", "finer"],
        ["debug", "dbg", "dbug", "fine"],
        ["info", "inf", "information", "notice"],
        ["warn", "wrn", "warning"],
        ["error", "err", "eror", "fail", "severe"],
        ["fatal", "ftl", "crit", "critical", "emerg", "alert"],
    ];

    /// <summary>The names the model may pass as <c>level</c>.</summary>
    internal static readonly string[] LevelNames = ["trace", "debug", "info", "warn", "error", "fatal"];

    private static readonly Dictionary<string, int> Rank = Levels
        .SelectMany((spellings, rank) => spellings.Select(s => (s, rank)))
        .ToDictionary(p => p.s, p => p.rank, StringComparer.OrdinalIgnoreCase);

    // Levels only a record's level field holds: in a text log, Go's "panic({0x4a6e20, …})" is a frame of a stack trace.
    private static readonly Dictionary<string, int> RecordOnly = new(StringComparer.OrdinalIgnoreCase)
        { ["panic"] = 5, ["dpanic"] = 5, ["emergency"] = 5 };

    /// <summary>The rank of a level a record's level field holds ("Warning", "ERR", "information", zap's "dpanic");
    /// <c>null</c> for no level.</summary>
    internal static int? RankOf(string word) =>
        Rank.TryGetValue(word.Trim(), out var rank) || RecordOnly.TryGetValue(word.Trim(), out rank) ? rank : null;

    /// <summary>The repeated messages listed under a page, the most repeated first; past them, counted.</summary>
    private const int MaxGroupsListed = 8;

    // A level word near the start of a line, alone or bracketed: "[12:00:01 ERR]", "2026-11-17 10:00:01.123 ERROR", "fail: App[0]".
    private static readonly Regex LevelWord = new(
        @"(?<![\w.-])(" + string.Join("|", Levels.SelectMany(l => l)) + @")(?![\w.-])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexBudget.Default);

    // A date and time (ISO, or Go's 2026/11/17), or a time alone (Serilog's console), or a syslog stamp ("Nov 17 10:52:01").
    private static readonly Regex Stamp = new(
        @"^\W{0,2}(?:(?<date>\d{4}[-/]\d{2}[-/]\d{2})[ T])?(?<time>\d{2}:\d{2}(?::\d{2})?)|^(?<mon>[A-Z][a-z]{2}) +(?<day>\d{1,2}) (?<time2>\d{2}:\d{2}:\d{2})",
        RegexOptions.CultureInvariant, RegexBudget.Default);

    /// <summary>How far into a line its level is looked for: past it, a word is the message's, not the entry's.</summary>
    private const int LevelWindow = 48;

    /// <summary>How far back a <c>start_line</c> inside an entry looks for its header, and for the last date before it.</summary>
    private const int LookBack = 20_000;

    /// <summary>A moment the model names: a time of day, or a date and a time; <paramref name="WholeMinute"/> when written
    /// without seconds — "until 10:30" takes 10:30:45.</summary>
    internal readonly record struct Moment(DateOnly? Date, TimeOnly Time, bool WholeMinute);

    /// <summary>Reads <paramref name="text"/> as the model passed it ("10:30", "2026-11-17 10:30:00"); <c>null</c> if it is neither.</summary>
    internal static Moment? ParseMoment(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var t = text.Trim().Replace('T', ' ');
        var minute = Regex.IsMatch(t, @"^(\d{4}-\d{2}-\d{2} )?\d{2}:\d{2}$", RegexOptions.None, RegexBudget.Default);
        string[] dated = ["yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm", "yyyy-MM-dd HH:mm:ss.FFFFFFF"];
        if (DateTime.TryParseExact(t, dated, CultureInfo.InvariantCulture, DateTimeStyles.None, out var at))
            return new(DateOnly.FromDateTime(at), TimeOnly.FromDateTime(at), minute);
        string[] timed = ["HH:mm:ss", "HH:mm", "HH:mm:ss.FFFFFFF"];
        return TimeOnly.TryParseExact(t, timed, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time) ? new(null, time, minute) : null;
    }

    private readonly record struct Header(int? Level, DateOnly? Date, TimeOnly? Time, Documents.StructuredLogLine? Record = null);

    /// <summary>The entry <paramref name="line"/> opens, if any; in a log of <paramref name="records"/>, a JSON or logfmt
    /// record is one.</summary>
    /// <remarks>⚠ Records only where most of the log's first lines are records: in a text log, a JSON body quoted under an
    /// entry, or a sentence that holds "level=error", stays a line of that entry.</remarks>
    private static Header? HeaderOf(string line, bool records)
    {
        // A JSON object is read as one before anything else: its text holds times and level words of its own. A line of a
        // record log that is no record (a last record cut short by a crash) is an entry nothing can judge — said, not folded
        // into the record before it.
        if (records && line.TrimStart().StartsWith('{'))
            return Documents.StructuredLogLine.Read(line) is { } json ? new(json.Level, json.Date, json.Time, json) : new Header(null, null, null);
        DateOnly? date = null;
        TimeOnly? time = null;
        var m = Stamp.Match(line);
        if (m.Success)
        {
            if (m.Groups["date"].Success && DateOnly.TryParseExact(m.Groups["date"].Value.Replace('/', '-'), "yyyy-MM-dd",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
                date = d;
            var tv = m.Groups["time"].Success ? m.Groups["time"].Value : m.Groups["time2"].Value;
            if (TimeOnly.TryParseExact(tv, ["HH:mm:ss", "HH:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)) time = t;
        }
        var head = line.Length > LevelWindow ? line[..LevelWindow] : line;
        var lm = LevelWord.Match(head);
        // A level with no time opens an entry only at the very start ("fail: App[0]", "[ERROR] …"): further in, it is a
        // word of a stack trace or of a message.
        if (time is null && !(lm.Success && lm.Index <= 1))
            return records && Documents.StructuredLogLine.Read(line) is { } logfmt ? new(logfmt.Level, logfmt.Date, logfmt.Time, logfmt) : null;
        return new(lm.Success ? Rank[lm.Value] : null, date, time);
    }

    /// <summary>
    /// The entries of <paramref name="text"/> at <paramref name="minLevel"/> or above, between <paramref name="since"/> and
    /// <paramref name="until"/>, from line <paramref name="startLine"/> to <paramref name="endLine"/> (0: the end), each line
    /// under its number in the file, cut between two entries at <paramref name="budget"/> characters with the line to read on from.
    /// </summary>
    internal static string Apply(string text, string name, string? minLevel, Moment? since, Moment? until, int startLine, int endLine, int budget)
    {
        var min = minLevel is null ? (int?)null : Rank[minLevel];
        var lines = text.Split('\n');
        var total = lines.Length > 0 && lines[^1].Length == 0 ? lines.Length - 1 : lines.Length;
        var first = Math.Max(1, startLine) - 1;
        var last = endLine > 0 ? Math.Min(endLine, total) : total;
        var timed = since is not null || until is not null;
        var records = Documents.StructuredLogLine.FormatOf(text, minRecords: 1, maxLine: int.MaxValue) is not null;

        // A start inside an entry is judged from the entry's header, and the last date before it carries on.
        var from = first;
        for (var j = first; j >= 0 && j > first - LookBack && j < total; j--)
            if (HeaderOf(lines[j].TrimEnd('\r'), records) is not null) { from = j; break; }
        DateOnly? lastDate = null;
        if (timed)
            for (var j = from - 1; j >= 0 && j > from - LookBack; j--)
                if (HeaderOf(lines[j].TrimEnd('\r'), records) is { Date: { } d }) { lastDate = d; break; }

        var sb = new StringBuilder();
        int entries = 0, kept = 0, beforeFirst = 0, noLevel = 0, noTime = 0, undated = 0, epochJudged = 0;
        int? cutAt = null, rolledAt = null, entryLine = null;
        var entryStart = 0;
        bool keep = false, repeat = false;
        TimeOnly? lastTime = null;
        // The records shown on this page by what makes them the same message; a record that repeats one is counted, not shown.
        var groups = new Dictionary<string, Repeated>(StringComparer.Ordinal);
        string? groupOpened = null;
        (Documents.StructuredLogLine Record, Repeated Group)? hidden = null;
        for (var i = from; i < last; i++)
        {
            var line = lines[i].TrimEnd('\r');
            var header = HeaderOf(line, records);
            if (header is { } h)
            {
                entries++;
                (repeat, groupOpened, hidden) = (false, null, null);
                lastDate = h.Date ?? lastDate;
                // An undated time that goes back by more than an hour: the log went past midnight.
                if (h.Date is null && h.Time is { } now && lastTime is { } before && now < before.AddHours(-1)) rolledAt ??= i + 1;
                lastTime = h.Time ?? lastTime;
                keep = true;
                if (min is { } floor)
                {
                    if (h.Level is null) { noLevel++; keep = false; }
                    else if (h.Level < floor) keep = false;
                }
                if (keep && timed)
                {
                    if (h.Time is null) { noTime++; keep = false; }
                    else
                    {
                        var date = h.Date ?? lastDate;
                        if (date is null && (since?.Date is not null || until?.Date is not null)) undated++;
                        if (h.Record is { Epoch: true }) epochJudged++;
                        if (since is { } s && Compare(date, h.Time.Value, s) < 0) keep = false;
                        if (until is { } u && Compare(date, h.Time.Value, u with { Time = u.WholeMinute ? u.Time.Add(TimeSpan.FromSeconds(59.999)) : u.Time }) > 0)
                            keep = false;
                    }
                }
                if (keep) { kept++; entryLine = i + 1; entryStart = sb.Length; }
                if (keep && i >= first && h.Record is { GroupKey: { } key } record)
                {
                    if (groups.TryGetValue(key, out var seen)) { seen.Count++; seen.Last = i + 1; repeat = true; hidden = (record, seen); }
                    else groups[groupOpened = key] = new Repeated(record.Template, i + 1);
                }
            }
            else if (entries == 0) { beforeFirst++; continue; }
            if (!keep || i < first) continue;
            string shown;
            if (repeat)
            {
                if (header is not null) continue;   // a repeat of a message already on the page: counted below
                // A line under a repeated record (a stack trace the runtime wrote raw) is no repeat: shown, with its record.
                repeat = false;
                hidden!.Value.Group.Count--;
                shown = $"{entryLine}: {hidden.Value.Record.Render()}\n{i + 1}: {line}\n";
            }
            else shown = header?.Record is { } r ? $"{i + 1}: {r.Render()}\n" : $"{i + 1}: {line}\n";
            if (sb.Length + shown.Length > budget)
            {
                if (sb.Length == 0)
                {
                    // One line larger than a page: shown cut, said.
                    sb.Append(shown.AsSpan(0, Math.Max(0, budget - 120))).Append($" [… line {i + 1} cut: {shown.Length} characters]\n");
                    cutAt = i + 2;
                }
                else if (entryStart > 0 && entryLine is { } headerLine)
                {
                    // Cut between two entries: the one being written goes whole to the next page.
                    sb.Length = entryStart;
                    kept--;
                    if (groupOpened is not null) groups.Remove(groupOpened);
                    cutAt = headerLine;
                }
                else cutAt = i + 1;   // one entry longer than a page: the next page goes on inside it
                break;
            }
            sb.Append(shown);
        }

        if (entries == 0)
            return $"[{name}: no log entry recognised (no line opens with a time or a level, and none is a JSON or logfmt record "
                 + "with one) — read it without level/since/until, or use search_in_files]";
        var notes = new List<string>();
        if (beforeFirst > 0) notes.Add($"{beforeFirst} line(s) before the first entry not judged");
        if (noLevel > 0) notes.Add($"{noLevel} entr{(noLevel == 1 ? "y" : "ies")} with no level left out");
        if (noTime > 0) notes.Add($"{noTime} entr{(noTime == 1 ? "y" : "ies")} with no time left out");
        if (undated > 0) notes.Add($"{undated} entr{(undated == 1 ? "y" : "ies")} with no date compared by time of day only");
        if (rolledAt is { } rolled && timed) notes.Add($"the times go back past midnight at line {rolled}: entries are compared by time of day, whatever their day");
        if (epochJudged > 0) notes.Add($"{epochJudged} entr{(epochJudged == 1 ? "y" : "ies")} timed by an epoch number, read as UTC");
        var what = string.Join(", ", new[]
        {
            minLevel is null ? null : $"level {minLevel} or above",
            since is null ? null : $"since {Show(since.Value)}",
            until is null ? null : $"until {Show(until.Value)}",
        }.Where(s => s is not null));
        var range = (first == 0 ? "" : $" from line {first + 1}") + (endLine > 0 ? $" to line {last}" : "");
        var repeats = groups.Values.Sum(g => g.Count - 1);
        var footer = cutAt is { } next
            ? $"[{name}: {kept - repeats} matching entries shown{(repeats > 0 ? $" and {repeats} repeat(s) counted below" : "")}{range} ({what}), "
              + $"each line under its number; more follow — call read_file with the same filter and start_line={next}]"
            : $"[{name}: {kept} of {entries} entries match ({what}){range}, each line under its number; {(endLine > 0 && last < total ? $"line {last} reached" : "the end of the file")}]";
        if (notes.Count > 0) footer += $"\n[{string.Join("; ", notes)}]";
        var repeated = groups.Values.Where(g => g.Count > 1).OrderByDescending(g => g.Count).ThenBy(g => g.First).ToList();
        if (repeated.Count > 0)
        {
            var listed = repeated.Take(MaxGroupsListed).Select(g => $"{g.Count} × \"{SafeTruncate.Truncate(g.Template, 120)}\" (shown at line {g.First}, "
                                                                        + $"repeated up to line {g.Last})");
            footer += $"\n[messages repeated on this page, each shown once: {string.Join("; ", listed)}"
                      + (repeated.Count > MaxGroupsListed ? $"; and {repeated.Count - MaxGroupsListed} more repeated message(s)" : "")
                      + " — read one with start_line and end_line on its line]";
        }
        return sb.Length == 0 ? footer : sb + footer;
    }

    /// <summary>A message shown once on a page: its template, the line it was shown at, how many records carry it, the last.</summary>
    private sealed class Repeated(string template, int first)
    {
        public string Template { get; } = template;
        public int First { get; } = first;
        public int Count { get; set; } = 1;
        public int Last { get; set; } = first;
    }

    private static string Show(Moment m) => (m.Date is { } d ? d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + " " : "")
                                            + m.Time.ToString(m.WholeMinute ? "HH:mm" : "HH:mm:ss", CultureInfo.InvariantCulture);

    // An entry with no date of its own is compared by its time of day; so is a moment named without a date.
    private static int Compare(DateOnly? date, TimeOnly time, Moment m) =>
        date is { } d && m.Date is { } md ? (d, time).CompareTo((md, m.Time)) : time.CompareTo(m.Time);
}
