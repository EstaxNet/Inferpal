using System.Globalization;
using Inferpal.Services;
using Inferpal.Services.Persistence;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A date written into a file, or into a file's name, is written the same on every machine: Gregorian year, ':' between
/// hours and minutes.
/// </summary>
/// <remarks>
/// They were formatted in the machine's culture: a Thai calendar dated a note 2569 and named a session 2569-…, out of
/// order with every other; a Finnish one wrote <c>09.30</c>. The display of a date keeps the user's culture — only what
/// is stored is fixed.
/// </remarks>
[Collection(CultureSerialCollection.Name)]
public sealed class PersistedDatesCultureTests
{
    private static readonly DateTime At = new(2026, 10, 15, 9, 30, 12, DateTimeKind.Local);

    private static T In<T>(string culture, Func<T> body)
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo(culture);
        try { return body(); }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Theory]
    [InlineData("th-TH")]
    [InlineData("fi-FI")]
    [InlineData("en-US")]   // reference arm: the ordinary culture writes what it always wrote
    public void ANote_ASessionName_AndALogLine_AreWrittenTheSameEverywhere(string culture)
    {
        Assert.Equal("- [2026-10-15 09:30] keep the cache\n", In(culture, () => NotesStore.FormatLine("keep the cache", At)));
        Assert.Equal("2026-10-15_0930_My_Title", In(culture, () => SessionManager.SessionFileName(At, "My_Title")));
        Assert.StartsWith("2026-10-15 09:30:12 [Ctx]", In(culture, () => new DiagnosticEntry(At, "Ctx", "detail").ToLine()));
    }
}
