using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// Code comments are in English — including the ones written without a single accent.
/// </summary>
/// <remarks>
/// <para>
/// ⚠ Two detectors watched this rule and <b>neither could see this class</b>. The accent proxy of
/// <c>ConventionCoverageTests</c> says so in its own words ("it catches accented prose, not French
/// written without accents"), and what did catch those was the comparison with the public clone —
/// which stopped being a detector the day the two trees became identical, because a French comment
/// now sits in both, identically. A safety net removed by an unrelated change.
/// </para>
/// <para>
/// ⚠ Measured when this test was written: <b>seven</b> live strays in shipped code, all of the same
/// shape — an English sentence whose last clause stayed French ("instead of re-embedding
/// <i>le fichier entier</i>", "written by the model <i>pour list_files et search_in_files</i>").
/// The translation pass ended at the line break.
/// </para>
/// <para>
/// ⚠ Word list, not a language model: every word here is one an English technical comment does not
/// contain. Quoted text is exempt — a comment may quote product text that IS French, which is what
/// <c>DiffComputerTests</c> does about the sentence that was the defect.
/// </para>
/// </remarks>
public sealed class CommentLanguageTests
{
    /// <summary>French function words that carry no accent and no English homograph.</summary>
    private static readonly string[] FrenchWords =
    [
        "avec", "dans", "sinon", "donc", "ainsi", "chaque", "fichier", "fichiers", "aucun",
        "aucune", "lorsque", "puisque", "afin", "toujours", "sont", "elle", "vers le", "entre les",
        "le fichier", "la ligne", "les lignes", "du modele", "de la", "de ligne", "recopie",
    ];

    /// <summary>
    /// French function words. Two DISTINCT ones on a line is the second signal: English technical
    /// prose carries at most one by accident ("de facto", "et al."), French prose cannot avoid two.
    /// </summary>
    /// <remarks>
    /// ⚠ A word list alone is not enough, and the sabotage said so: written without
    /// <c>de ligne</c>, this test stayed green on <c>// ── Saut de ligne ──</c>, the very line that
    /// opened the hunt. A list catches what someone thought of; this signal catches French.
    /// ⚠ Measured on the whole repository: threshold 2 found four more French lines and <b>zero</b>
    /// false positives, threshold 3 found nothing. The threshold is the one that separates, not the
    /// one that feels safest — a detector whose output is noise ends up disarmed.
    /// </remarks>
    private static readonly string[] FrenchStopWords =
    [
        "de", "le", "la", "les", "un", "une", "des", "du", "et", "dans", "pour", "avec", "sur",
        "que", "qui", "ne", "pas", "est", "sont", "ce", "ces", "aux", "au", "en", "par", "plus",
        "son", "sa", "ses", "dont", "donc",
    ];

    /// <summary>
    /// French by decision, and never ported: translating them is the translation-without-a-reader
    /// this repository stopped doing. Named in CLAUDE.md as the two exceptions.
    /// </summary>
    private static readonly string[] FrenchByDesign =
    [
        Path.Combine("Inferpal.Tests", "DeployScriptGuardTests.cs"),
        Path.Combine("Inferpal.Tests", "Probes") + Path.DirectorySeparatorChar,
    ];

    [Fact]
    public void NoFrenchLeftInAComment_EvenWithoutAccents()
    {
        var root = RepoRoot();
        var rx   = new Regex(@"\b(" + string.Join('|', FrenchWords) + @")\b",
                             RegexOptions.IgnoreCase, TimeSpan.FromSeconds(5));
        var stop = new Regex(@"\b(" + string.Join('|', FrenchStopWords) + @")\b",
                             RegexOptions.IgnoreCase, TimeSpan.FromSeconds(5));

        var scanned = 0;
        var offenders = new List<string>();

        foreach (var file in Sources(root))
        {
            if (FrenchByDesign.Any(x => file.Contains(x, StringComparison.OrdinalIgnoreCase))) continue;
            scanned++;

            var n = 0;
            foreach (var raw in File.ReadLines(file))
            {
                n++;
                var line = raw.TrimStart();
                if (!(line.StartsWith("//", StringComparison.Ordinal) ||
                      line.StartsWith('*'))) continue;

                // ⚠ Quoted text is the product's own, in whatever language it ships — and a
                // comment quotes French precisely when it documents a defect about French. Three
                // shapes carry a quotation: the double quote, the guillemets, and the markup
                // emphasis a doc comment uses (<i>, <c>). ⚠ A quotation that opens on one line and
                // closes on the next leaves an UNBALANCED quote here, so everything after the last
                // odd one is dropped too: DiffComputerTests quotes "Fichier trop grand pour" across
                // a line break, and a reader that only pairs quotes would report it.
                var prose = Regex.Replace(line, @"<(i|c|b)>.*?</\1>", " ", RegexOptions.None, TimeSpan.FromSeconds(5));
                prose = Regex.Replace(prose, "\"[^\"]*\"|«[^»]*»", " ", RegexOptions.None, TimeSpan.FromSeconds(5));
                var odd = prose.IndexOf('"');
                if (odd >= 0) prose = prose[..odd];
                odd = prose.IndexOf('<');   // an emphasis left open by the same line break
                if (odd >= 0 && !prose[odd..].Contains('>')) prose = prose[..odd];
                var hit     = rx.Match(prose);
                var density = stop.Matches(prose).Select(x => x.Value.ToLowerInvariant()).Distinct().Take(2).Count();
                if (hit.Success || density >= 2)
                    offenders.Add($"{Path.GetRelativePath(root, file)}({n}): "
                                + (hit.Success ? $"…{hit.Value}… " : "[two French function words] ")
                                + prose.Trim()[..Math.Min(70, prose.Trim().Length)]);
            }
        }

        // Witness: a rule that reads no file is green for the wrong reason.
        Assert.True(scanned > 200, $"only {scanned} source file(s) were read — the scan checks nothing");

        Assert.True(offenders.Count == 0,
            "A code comment still carries French. The accent proxy cannot see these, and the "
            + "public clone no longer can either — the two trees are identical, so the sentence is "
            + "in both. Sites:" + Environment.NewLine + "  "
            + string.Join(Environment.NewLine + "  ", offenders));
    }

    private static IEnumerable<string> Sources(string root)
    {
        foreach (var project in new[] { "Inferpal.Core", "Inferpal", "Inferpal.Host",
                                        "Inferpal.InProc", "Inferpal.Fim", "Inferpal.Tests" })
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, project), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") ||
                file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")) continue;
            yield return file;
        }

        var ts = Path.Combine(root, "vscode", "src");
        if (Directory.Exists(ts))
            foreach (var file in Directory.EnumerateFiles(ts, "*.ts", SearchOption.AllDirectories))
                yield return file;
    }

    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "Inferpal.Core")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return dir!;
    }
}
