
namespace Inferpal.Services.Bench;

/// <summary>One navigation question and its unambiguous ground truth.</summary>
/// <param name="Symbol">A type declared in exactly one file of the workspace.</param>
/// <param name="ExpectedPath">That file, repository-relative with forward slashes.</param>
internal sealed record ContextBenchQuestion(string Symbol, string ExpectedPath);

/// <summary>
/// The question set of the <b>context bench</b> (roadmap §12/§11): "where does X live?" asked of
/// the agent, to measure what the repository map and sub-agents actually buy in tool calls and
/// tokens — not just what they cost.
/// </summary>
/// <remarks>
/// <para>
/// Questions are <b>generated from the workspace being benched</b> rather than frozen like
/// <see cref="BenchTasks"/>: a fixed question set can only ever be about one repository, and the
/// number that matters to a user is the one measured on their own code. Generation is pure and
/// deterministic — same scan, same questions — so two arms of a comparison are always asked
/// exactly the same thing.
/// </para>
/// <para>
/// Ground truth is only taken from symbols declared in <b>exactly one</b> file. A name declared
/// twice (a partial class, a test double sharing its subject's name) has no single right answer,
/// and grading it would measure the grader, not the model.
/// </para>
/// </remarks>
internal static class ContextBenchTasks
{
    /// <summary>Default question count — enough to be indicative, short enough to run twice.</summary>
    public const int DefaultCount = 8;

    /// <summary>
    /// Short names collide across ecosystems and read as noise in a prompt; requiring a few
    /// characters keeps the questions about navigation rather than about guessing.
    /// </summary>
    private const int MinSymbolLength = 6;

    /// <summary>Instruction appended to every question: a bare path is trivial to grade.</summary>
    public const string AnswerInstruction =
        "Answer with the repository-relative path of that file and nothing else.";

    /// <summary>Builds the question text sent to the agent.</summary>
    public static string Prompt(ContextBenchQuestion q) =>
        $"Which file of this repository declares `{q.Symbol}`? {AnswerInstruction}";

    /// <summary>Symbols bundled into one compound question (roadmap §11 bench).</summary>
    public const int SymbolsPerCompoundQuestion = 3;

    /// <summary>
    /// Builds a compound question: several unrelated symbols at once, so answering means several
    /// separate searches whose file content all lands in the context window.
    /// </summary>
    /// <remarks>
    /// A single-hop question does not exercise delegation at all — one search answers it and there
    /// is nothing worth handing to a sub-agent. This shape is what makes the §11 measurement
    /// meaningful, and the gate says so in advance.
    /// </remarks>
    public static string CompoundPrompt(IReadOnlyList<ContextBenchQuestion> parts) =>
        "Locate each of these declarations in this repository:\n"
        + string.Join("\n", parts.Select(p => $"- `{p.Symbol}`"))
        + "\n\nAnswer with one line per item, formatted `symbol -> repository/relative/path`, "
        + "and nothing else.";

    /// <summary>
    /// Groups the generated questions into compound ones of
    /// <see cref="SymbolsPerCompoundQuestion"/> parts. Consecutive picks come from different
    /// folders (see <see cref="Build"/>), so a bundle spans the repository rather than one corner.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<ContextBenchQuestion>> BuildCompound(
        IReadOnlyList<ScannedFile> files, int questionCount)
    {
        var flat = Build(files, questionCount * SymbolsPerCompoundQuestion);
        return flat
            .Select((q, i) => (q, i))
            .GroupBy(x => x.i / SymbolsPerCompoundQuestion)
            .Where(g => g.Count() == SymbolsPerCompoundQuestion)   // no half bundle
            .Select(g => (IReadOnlyList<ContextBenchQuestion>)g.Select(x => x.q).ToList())
            .ToList();
    }

    /// <summary>Scores a compound answer part by part; the caller aggregates over symbols.</summary>
    public static int ScoreCompound(IReadOnlyList<ContextBenchQuestion> parts, string? answer) =>
        parts.Count(p => Score(p, answer));

    /// <summary>
    /// Picks up to <paramref name="count"/> questions, spread across folders so the set measures
    /// navigation of the whole repository rather than of its biggest directory.
    /// </summary>
    public static IReadOnlyList<ContextBenchQuestion> Build(
        IReadOnlyList<ScannedFile> files, int count = DefaultCount)
    {
        if (count <= 0) return [];

        // symbol → the files declaring it; anything with more than one owner is discarded below.
        var owners = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var file in files.OrderBy(f => f.RelPath, StringComparer.Ordinal))
            foreach (var symbol in file.Symbols)
            {
                if (symbol.Length < MinSymbolLength) continue;
                if (!owners.TryGetValue(symbol, out var list)) owners[symbol] = list = [];
                if (!list.Contains(file.RelPath, StringComparer.Ordinal)) list.Add(file.RelPath);
            }

        var unique = owners
            .Where(kv => kv.Value.Count == 1)
            .Select(kv => new ContextBenchQuestion(kv.Key, kv.Value[0]))
            .ToList();

        // Hard questions first: a type whose name IS its file name is nearly free — one filename
        // search answers it, and the first measurement showed exactly that (6/6 in both arms for
        // 7 tool calls, a ceiling with no room left for extra context to prove anything). Asking
        // about types that live in a differently-named file forces a real content search.
        var hard = unique.Where(q => !IsNamedAfterItsFile(q)).ToList();
        var easy = unique.Where(IsNamedAfterItsFile).ToList();

        // Small or convention-strict repositories may not have enough: top up rather than return
        // an empty set, and let the caller see the count it actually got.
        var pool = hard.Count >= count ? hard : [.. hard, .. easy];

        // Round-robin over folders: taking the first N alphabetically would ask eight questions
        // about the same directory and call it a measure of the repository.
        var byFolder = pool
            .GroupBy(q => Folder(q.ExpectedPath), StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => g.OrderBy(q => q.Symbol, StringComparer.Ordinal).ToList())
            .ToList();

        var picked = new List<ContextBenchQuestion>();
        for (var round = 0; picked.Count < count; round++)
        {
            var addedThisRound = false;
            foreach (var folder in byFolder)
            {
                if (round >= folder.Count) continue;
                picked.Add(folder[round]);
                addedThisRound = true;
                if (picked.Count == count) break;
            }
            if (!addedThisRound) break;   // exhausted every folder
        }

        return picked;
    }

    /// <summary>
    /// True when the answer names the expected file <b>by its full repository-relative path</b>.
    /// Lenient about shape (slash style, backticks, surrounding prose), strict about the path.
    /// </summary>
    /// <remarks>
    /// <b>The bare file name is deliberately not accepted</b>, and this is the difference between a
    /// measurement and a placebo. In most C# repositories the type <c>Foo</c> lives in
    /// <c>Foo.cs</c>, so a model can "answer" from the question alone without reading anything —
    /// the first run of this bench scored 3/6 with <b>zero tool calls</b> for exactly that reason.
    /// Requiring the folder forces the model either to navigate or to have read it in the
    /// repository map, which is precisely the thing being measured.
    /// </remarks>
    public static bool Score(ContextBenchQuestion question, string? answer)
    {
        if (string.IsNullOrWhiteSpace(answer)) return false;

        var normalized = MarkdownParser.StripThinkTags(answer)
                                       .Replace('\\', '/')
                                       .Replace("`", "")
                                       .ToLowerInvariant();
        var expected = question.ExpectedPath.Replace('\\', '/').ToLowerInvariant();

        return normalized.Contains(expected, StringComparison.Ordinal);
    }

    /// <summary>
    /// True when the symbol is simply the name of its own file (<c>WorkspaceSymbolScanner</c> in
    /// <c>WorkspaceSymbolScanner.cs</c>) — the case a single filename lookup answers.
    /// </summary>
    private static bool IsNamedAfterItsFile(ContextBenchQuestion q)
    {
        var stem = FileName(q.ExpectedPath);
        var dot  = stem.LastIndexOf('.');
        if (dot > 0) stem = stem[..dot];
        return string.Equals(stem, q.Symbol, StringComparison.OrdinalIgnoreCase);
    }

    private static string Folder(string relPath)
    {
        var i = relPath.LastIndexOf('/');
        return i < 0 ? string.Empty : relPath[..i];
    }

    private static string FileName(string relPath)
    {
        var i = relPath.LastIndexOf('/');
        return i < 0 ? relPath : relPath[(i + 1)..];
    }
}
