using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// Test classes that flip <c>Strings.OverrideCulture</c> (through <c>Strings.ApplyLanguage</c>).
/// </summary>
/// <remarks>
/// ⚠ That switch is <b>process-wide</b>, and deliberately so: the product refuses to mutate
/// <c>Thread.CurrentUICulture</c>. In tests it means a class that goes Japanese for one assertion
/// does it for <b>every</b> other class running at the same moment - even when it restores cleanly
/// in a <c>finally</c>. Measured: two assertions added to <see cref="ConversationExporterTests"/>
/// turned <c>ArenaTests</c> red, which compares a localized sentence, and the run before had passed
/// by sheer scheduling luck. A test that fails one run in two costs more than the defect it guards.
/// <para>Same pattern as the shell collection and the signal collection.</para>
/// </remarks>
public static class CultureSerialCollection
{
    public const string Name = "culture-serial";
}

[CollectionDefinition(CultureSerialCollection.Name, DisableParallelization = true)]
public class CultureSerialCollectionDefinition { }
