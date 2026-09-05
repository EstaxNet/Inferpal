using System.Linq;
using Inferpal.Services;
using Inferpal.Services.Execution;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A permission rule the product could not read was dropped <b>without a word</b>.
/// </summary>
/// <remarks>
/// <c>ParseLine</c> returns <c>null</c> for a malformed line as well as for an invalid regular
/// expression, and "skipped rather than throwing, so one bad line never disables the whole ruleset"
/// is the right arbitration - what was missing was SAYING so. A user writing
/// <c>deny run_command *.env</c> (an invalid pattern: <c>*</c> cannot open one) believes they put a
/// restriction in place; it does not exist.
///
/// This is not a security hole: the real boundary remains the approval prompt, where a human reads
/// the raw command. The defect is that a stated intent disappeared without a trace.
///
/// And the file already showed the right behaviour THREE LINES above: an <c>allow</c> rule coming
/// from the workspace overlay is refused <b>and recorded</b>. The rule was written for one branch
/// and not for its neighbour.
/// </remarks>
[Collection("Diagnostics")]
public class PermissionRuleSilenceTests
{
    private static string[] PermissionNotes() =>
        [.. Diagnostics.Snapshot().Where(e => e.Context == "Permission").Select(e => e.Detail)];

    [Fact]
    public void AMalformedRuleLine_IsRecorded()
    {
        Diagnostics.Clear();

        var rules = PermissionPolicy.ParseRules("deny run_command rm -rf /\nthis is not a rule");

        Assert.Single(rules);                                        // the good line still applies
        Assert.Contains(PermissionNotes(), d => d.Contains("this is not a rule"));
    }

    /// <summary>The most misleading case: the line has the right SHAPE, only the pattern is invalid.</summary>
    [Fact]
    public void AnInvalidRegex_IsRecorded()
    {
        Diagnostics.Clear();

        var rules = PermissionPolicy.ParseRules("deny run_command *.env");

        Assert.Empty(rules);
        Assert.Contains(PermissionNotes(), d => d.Contains("*.env"));
    }

    /// <summary>Reference arm: blank lines and comments are not errors. Reporting them would be
    /// noise on an ordinary file, and a noisy channel stops being read.</summary>
    [Fact]
    public void BlankLinesAndComments_AreNotReported()
    {
        Diagnostics.Clear();

        var rules = PermissionPolicy.ParseRules("# a comment\n\n   \ndeny run_command rm -rf /");

        Assert.Single(rules);
        Assert.Empty(PermissionNotes());
    }

    /// <summary>
    /// The worse of the two: an overlay with broken JSON produced <b>zero</b> deny rules, silently.
    /// A project shipping its <c>permissions.json</c> to restrict itself lost every restriction with
    /// nobody able to know.
    /// </summary>
    [Fact]
    public void AnOverlayThatIsNotValidJson_IsRecorded()
    {
        Diagnostics.Clear();

        var rules = PermissionPolicy.ParseJsonOverlay("{ \"rules\": [ \"deny * a\" ");   // truncated

        Assert.Empty(rules);
        Assert.NotEmpty(PermissionNotes());
    }

    [Fact]
    public void AnOverlayRuleThatDoesNotParse_IsRecorded()
    {
        Diagnostics.Clear();

        var rules = PermissionPolicy.ParseJsonOverlay("{ \"rules\": [\"deny * \\\\.env$\", \"nonsense here\"] }");

        Assert.Single(rules);
        Assert.Contains(PermissionNotes(), d => d.Contains("nonsense here"));
    }

    /// <summary>Reference arm: a fully valid overlay says nothing.</summary>
    [Fact]
    public void AValidOverlay_ReportsNothing()
    {
        Diagnostics.Clear();

        var rules = PermissionPolicy.ParseJsonOverlay("{ \"rules\": [\"deny * \\\\.env$\"] }");

        Assert.Single(rules);
        Assert.Empty(PermissionNotes());
    }

    /// <summary>
    /// The settings panel NAMES them at save time: <c>/diagnostics</c> answers whoever thinks to
    /// open it, and nobody does after writing a rule they believe they just put in place.
    /// </summary>
    [Fact]
    public void DroppedRules_AreCountedForTheSettingsPanel()
    {
        PermissionPolicy.ParseRules(
            "deny run_command rm -rf /\ndeny run_command *.env\nnot a rule", out var dropped);

        Assert.Equal(2, dropped.Count);
        Assert.Contains(dropped, d => d.Contains("*.env"));
    }

    /// <summary>Reference arm: what the parser skips NORMALLY does not count. Otherwise the panel
    /// would announce lost rules to anyone who comments a line out.</summary>
    [Fact]
    public void BlankAndCommentedRules_AreNotCounted()
    {
        PermissionPolicy.ParseRules("# disabled\n\n   \ndeny run_command rm -rf /", out var dropped);

        Assert.Empty(dropped);
    }
}
