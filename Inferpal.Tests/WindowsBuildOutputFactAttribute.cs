using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A test whose subject is an artefact only a <b>Windows build</b> produces — the net472 in-process
/// assembly, or the <c>extension.json</c> the VSIX build generates.
/// </summary>
/// <remarks>
/// <para>
/// ⚠ <b>This is not the same thing as the "UNDECIDED, not green" witness</b> that
/// <c>WalkCycleTests</c> carries, and the difference is what the two say about the machine. There,
/// a missing symbolic-link privilege is an <i>accident</i>: the test is supposed to run, so it goes
/// red and names what it could not measure. Here the artefact cannot exist <i>by design</i> — the
/// VSIX project only has a <c>net8.0-windows</c> target and the csproj drops it off Windows, which
/// is exactly what the CI's POSIX leg is for ("editor-agnostic net8.0 slice"). Red would mean "this
/// rule is broken" on a leg that was never asked the question.
/// </para>
/// <para>
/// ⚠ And the discriminator is the <b>platform</b>, never "the file is missing". Keyed on the file,
/// the rule would go green on Windows the day the build stopped producing it — the false green that
/// costs more than a red. On Windows the assertion stays hard, and the Windows leg of the CI is
/// where it is measured.
/// </para>
/// <para>
/// A skipped test is not a silent one: the reason below is printed by the runner, and it names the
/// build that would have to run for the rule to be judged.
/// </para>
/// </remarks>
public sealed class WindowsBuildOutputFactAttribute : FactAttribute
{
    public WindowsBuildOutputFactAttribute()
    {
        if (!System.OperatingSystem.IsWindows())
            Skip = "reads an artefact only a Windows build produces (the net472 in-process assembly "
                 + "or the generated extension.json): this leg builds the editor-agnostic net8.0 "
                 + "slice, so it is not asked the question. Judged by the Windows leg.";
    }
}
