using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// Every <c>Label*</c>/<c>Hint*</c> the Visual Studio settings window exposes must actually be
/// filled by <c>ApplyLabels()</c>.
/// </summary>
/// <remarks>
/// <para>
/// The 129 label/hint properties are <b>irreducible boilerplate</b>, and the review of 2026-08-07
/// left them alone on purpose: Remote UI binds XAML to named <c>[DataMember]</c> properties parsed
/// inside <c>devenv.exe</c>, so a collection or a reflective loop would have to be paid for with a
/// UI rewrite — and VS itself does not read initial values at DataContext assignment, which is why
/// <c>ApplyLabels()</c> exists at all.
/// </para>
/// <para>
/// What is <em>not</em> irreducible is the failure mode: add a setting, add its property, forget
/// its line in <c>ApplyLabels()</c>, and the window shows an empty label in all ten languages with
/// nothing failing. The compiler cannot see that; a reader will not notice one missing line among
/// 129 aligned ones. So the check is mechanical instead.
/// </para>
/// <para>
/// Source-level on purpose: the window's constructor takes VS SDK services, so it cannot be built
/// in a test host. Same precedent as <c>DocCountersTests</c> reading the README.
/// </para>
/// </remarks>
public class SettingsLabelCoverageTests
{
    private static string SettingsSource()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Inferpal.sln")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);

        return File.ReadAllText(Path.Combine(dir!, "Inferpal", "ToolWindow", "InferpalSettingsData.cs"));
    }

    [Fact]
    public void EveryLabelAndHintProperty_IsFilledByApplyLabels()
    {
        var source = SettingsSource();

        var declared = Regex.Matches(source, @"\[DataMember\] public string ((?:Label|Hint)\w+)")
                            .Select(m => m.Groups[1].Value)
                            .ToHashSet(StringComparer.Ordinal);

        var start = source.IndexOf("internal void ApplyLabels()", StringComparison.Ordinal);
        Assert.True(start > 0, "ApplyLabels() was renamed — this guard needs updating with it.");
        var body = source[start..];
        body = body[..body.IndexOf("\n    }", StringComparison.Ordinal)];

        var assigned = Regex.Matches(body, @"^\s*((?:Label|Hint)\w+)\s*=", RegexOptions.Multiline)
                            .Select(m => m.Groups[1].Value)
                            .ToHashSet(StringComparer.Ordinal);

        Assert.True(declared.Count > 100, $"only {declared.Count} label properties found — the regex drifted");

        var never = declared.Except(assigned).OrderBy(n => n, StringComparer.Ordinal).ToList();
        Assert.True(never.Count == 0,
            "These settings properties are never assigned, so the window shows them empty in all ten "
          + "languages:\n  " + string.Join("\n  ", never));
    }
}
