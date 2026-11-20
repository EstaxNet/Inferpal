using System.Text.RegularExpressions;
using Inferpal.Services.Agent;
using Inferpal.Services.Governance;
using Inferpal.Services.Shell;

namespace Inferpal.Services.Prompting;

/// <summary>
/// One or two lines that ride with a question when it calls for a tool parameter models do not find on their own: the
/// repeated measure of <c>run_command</c> when the question asks for speed, the level and time filter of <c>read_file</c>
/// when it looks for a log's errors by time. One reader for both front-ends — the Visual Studio view model and the host.
/// </summary>
/// <remarks>
/// <para>
/// The descriptions already name these parameters, and models skip them all the same: they time a program once by hand,
/// or search a log by hour and miss an entry. Said at the moment the question needs them, they are used.
/// </para>
/// <para>
/// ⚠ With the question, never in the system prompt (a server that caches the prompt's prefix would read the whole
/// conversation again), and only for a tool the turn offers, judged on the registry the turn sends (<see cref="OfferedBy"/>).
/// ⚠ Injected BEFORE the active-file block: that block runs to <c>## User request</c> and is rebuilt in place after a
/// compaction — a hint after it was taken for part of it and deleted. Said with every question that calls for it, never
/// deduplicated against the history: a compaction can drop the question that carried it. Written for the model:
/// English, not localized; the words that trigger it are the person's, in the interface's ten languages. A false positive
/// costs a line; a miss is the defect it exists for.
/// </para>
/// </remarks>
internal static class ToolHints
{
    /// <summary>The block's heading.</summary>
    public const string Header = "## Tool hint";

    /// <summary>The repeated measure, for a question that asks for speed or for how long a program takes.</summary>
    internal static readonly string Measure =
        $"To measure how long the program takes, or compare its speed before and after a change, run it with "
        + $"run_command's `repeat` parameter "
        + $"({RepeatedRun.MinRepeat} to {RepeatedRun.MaxRepeat} runs after a warm-up): it reports the median and the "
        + "spread of the runs. One run alone is noise.";

    /// <summary>The log filter, for a question that looks for a log's entries by level or time.</summary>
    internal const string LogFilter =
        "read_file reads only the entries of a log that match: `level` (for example \"error\") and `since`/`until` (a "
        + "time such as \"10:30\"). On a long log, use them instead of reading it whole or searching it with a command.";

    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    /// <summary>Asks for speed, in the ten languages. ⚠ Not "quickly", in any language (a request made in a hurry),
    /// "optimistic" or "оптимизм", a keyboard "accelerator", or a method "trop longue".</summary>
    private static readonly Regex Speed = new(
        @"\b(faster|speed\s*(it\s*)?up|too\s+slow|slow(er|s)?\b|performance|optimi[sz](e|es|ed|ing|ation)\b|quicker|takes too long|accelerate\b)"
        + @"|plus rapide|rapidit|acc[ée]l[èé]r|\blent(e|s|es|eur)?\b|optimis(e|er|ez|ation|é)|met trop de temps|prend trop de temps"
        + @"|schneller|langsam|beschleunig|optimier"
        + @"|m[aá]s r[aá]pid|\blent[oa]s?\b|aceler|optimiz"
        + @"|pi[uù] (veloc|rapid)|velocizz|ottimizz"
        + @"|быстрее|медлен|ускор|оптимизир|оптимизац"
        + @"|もっと速く|速くして|速くなる|遅[いくす]|高速化|最適化"
        + @"|더\s*빠르|빨라지|느[려리림]|속도|최적화"
        + @"|szybc|\bwoln|przyspiesz|optymaliz"
        + @"|更快|加快|加速|慢|优化|性能",
        Options, RegexBudget.Default);

    /// <summary>Asks how long a program takes, in the ten languages: a duration to measure, not only a speed to gain.
    /// ⚠ Never "runtime" or "Laufzeit" alone (a runtime error), "measure" without time (a panel's width), "time it" (every
    /// time it crashes), "response time" (a timeout to set) or a project named Benchmark. Read across line breaks: a
    /// pasted question breaks lines anywhere.</summary>
    private static readonly Regex Duration = new(
        @"\bhow long (does|do|did|will|would)\b.{0,60}\btake|\bhow long it takes|\bhow much time\b|\bhow fast (does|do|is|will)\b"
        + @"|\bbenchmark (it|this|that|the|my|our)\b|\b(execution|running) time\b|\bmeasur\w*\b.{0,30}\b(time|duration|how long|how fast)\b"
        + @"|combien (de temps|dure)|temps d.ex[ée]cution|dur[ée]e d.ex[ée]cution|chronom[eè]tr|mesur\w*\b.{0,30}\b(temps|dur[ée]e)\b"
        + @"|wie (lange|viel zeit)|ausf[üu]hrungszeit|zeit (messen|stoppen)|dauer messen"
        + @"|cu[aá]nto (tarda|tiempo|dura)|tiempo de ejecuci[oó]n|medir el tiempo"
        + @"|quanto (tempo|dura|ci mette)|tempo di esecuzione|misura\w*\b.{0,20}\btempo\b"
        + @"|сколько времени|как долго|время выполнения|замер\w* врем"
        + @"|(どのくらい|どれくらい|どれだけ)(の)?時間|実行時間|所要時間|時間を(計|測)"
        + @"|얼마나 걸|실행 시간|소요 시간|시간을 측정"
        + @"|ile czasu|jak d[łl]ugo|czas wykonania|zmierz czas"
        + @"|多长时间|多久|耗时|执行时间|运行时间|测量时间",
        Options | RegexOptions.Singleline, RegexBudget.Default);

    /// <summary>Errors or warnings: what a log filter by level selects.</summary>
    private static readonly Regex Errors = new(
        @"\b(errors?|exceptions?|warnings?|fail(ed|s|ures?|ing)?|crash(ed|es)?)\b|erreur|avertissement|plant[ée]|[ée]chou"
        + @"|fehler|warnung|errore|errori|avvis|ошибк|исключени|エラー|例外|警告|오류|에러|예외|경고|błęd|wyjąt|ostrzeż|错误|异常|警告",
        Options, RegexBudget.Default);

    /// <summary>A time of day, or a log named: what makes it a question for the filter. ⚠ Not "since" or "after" alone
    /// ("the errors since my last commit" is a build), a position in pasted output (<c>app.ts:12:34</c>, <c>line 12: 34</c>),
    /// the verb "log in", or a word that merely holds the letters — katakana and hangul words end in ログ/로그 (プログラム,
    /// ダイアログ, 블로그), and "registro" or "Protokoll" name other things too.</summary>
    private static readonly Regex LogOrTime = new(
        @"(?<![:\d.])\b\d{1,2}(:\d{2}(:\d{2})?|\s*h\s*\d{2})\b(?!:\d)|\b\d{1,2}\s*(am|pm|uhr|h)\b|\b\d{1,2}[:.]\d{2}\s*(am|pm|uhr)\b"
        + @"|\d{1,2}\s*(時|시|点)"
        + @"|\.log\b|\.clef\b|\blogs?\b(?!\s+(in|on|out)\b)|\bjournal|protokolldatei|\blogdatei|\bлог\b|\bлоги|журнал"
        + @"|(?<![゠-ヿ])ログ|(?<![가-힣])로그|dziennik|日志",
        Options, RegexBudget.Default);

    /// <summary>Which tools a turn offers, as it will send them: none with the tools off (a code action too), Plan's
    /// read-only ones, or the held mode's (<see cref="ContextManager.NextTurnTools"/>). Read only when a question calls
    /// for a hint: most never do, and the registry and the mode are read from disk.</summary>
    public static Func<string, bool> OfferedBy(IToolRegistry tools, bool toolsEnabled, bool planMode, Func<ChatMode?> mode)
    {
        if (!toolsEnabled) return _ => false;
        var names = new Lazy<HashSet<string>>(() => ContextManager.NextTurnTools(tools, planMode, mode()).Definitions
            .Select(d => d.Function.Name).ToHashSet(StringComparer.Ordinal));
        return name => names.Value.Contains(name);
    }

    /// <summary>
    /// The block for <paramref name="question"/>, or <c>null</c> when it calls for none or the turn does not offer the
    /// tool it would name.
    /// </summary>
    /// <param name="question">What the person typed.</param>
    /// <param name="offered">Whether this turn offers a tool, by name (<see cref="OfferedBy"/>); asked only on a match.</param>
    public static string? For(string? question, Func<string, bool> offered)
    {
        if (string.IsNullOrWhiteSpace(question)) return null;
        var lines = new List<string>(2);
        try
        {
            if ((RegexBudget.IsMatch(Speed, question) || RegexBudget.IsMatch(Duration, question)) && offered("run_command"))
                lines.Add(Measure);
            if (RegexBudget.IsMatch(Errors, question) && RegexBudget.IsMatch(LogOrTime, question) && offered("read_file"))
                lines.Add(LogFilter);
        }
        catch (RegexMatchTimeoutException) { return null; }   // a question too long to read in time: no hint
        return lines.Count == 0 ? null : Header + "\n\n" + string.Join("\n\n", lines);
    }
}
