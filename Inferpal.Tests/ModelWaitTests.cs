using Inferpal.Localization;
using Inferpal.Services.Inference;
using Inferpal.Services.Presentation;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// The wait card says what the server does, how long against what, and when it stops — the bound said from the start,
/// never only at the cut; a bar only against a measured read; past it, a warning measured against the bound.
/// </summary>
[Collection(CultureSerialCollection.Name)]
public sealed class ModelWaitTests : IDisposable
{
    public ModelWaitTests() => Strings.ApplyLanguage("en");

    public void Dispose() => Strings.ApplyLanguage(null);

    private static string D(int seconds) => RunSummary.Duration(TimeSpan.FromSeconds(seconds));

    [Fact]
    public void NothingMeasured_SaysTheTokensAndTheBound_WithNoBar()
    {
        var wait = new ModelWait(18_400, TimeSpan.FromSeconds(42), null, TimeSpan.FromMinutes(10));

        Assert.Equal(Strings.WaitReading, wait.Title);
        Assert.Equal(D(42), wait.Times);
        Assert.Equal(Strings.WaitDetailUnmeasured(18_400, D(600)), wait.Detail);
        Assert.Null(wait.Fraction);
        Assert.False(wait.Longer);
    }

    [Fact]
    public void WithinTheMeasuredRead_IsMeasuredAgainstIt_AndSaysTheBound()
    {
        var wait = new ModelWait(18_400, TimeSpan.FromSeconds(40), TimeSpan.FromSeconds(80), TimeSpan.FromMinutes(10));

        Assert.Equal(Strings.WaitTimesOfAbout(D(40), D(80)), wait.Times);
        Assert.Equal(Strings.WaitDetail(18_400, D(80), D(600)), wait.Detail);
        Assert.Contains(D(600), wait.Detail, StringComparison.Ordinal);   // the bound, from the start
        Assert.Equal(0.5, wait.Fraction);
        Assert.False(wait.Longer);
    }

    [Fact]
    public void PastTheMeasuredRead_IsAWarning_MeasuredAgainstTheBound()
    {
        var wait = new ModelWait(18_400, TimeSpan.FromSeconds(150), TimeSpan.FromSeconds(80), TimeSpan.FromMinutes(10));

        Assert.True(wait.Longer);
        Assert.Equal(Strings.WaitReadingLonger, wait.Title);
        Assert.Equal(Strings.WaitTimesOfBound(D(150), D(600)), wait.Times);
        Assert.Equal(Strings.WaitDetailLonger(D(80), D(600)), wait.Detail);
        Assert.Equal(0.25, wait.Fraction);
    }

    [Fact]
    public void TheBar_NeverRunsPastItsEnd()
    {
        Assert.Equal(1, new ModelWait(1, TimeSpan.FromMinutes(12), TimeSpan.FromSeconds(80), TimeSpan.FromMinutes(10)).Fraction);
    }

    /// <summary>A sign of life (headers, a keep-alive) restarts the silence the server is judged on: past the bound, the
    /// time alone, never "3 min of 2 min".</summary>
    [Fact]
    public void PastTheBound_SaysTheTimeAlone()
    {
        var wait = new ModelWait(2_300, TimeSpan.FromMinutes(3), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2));

        Assert.Equal(D(180), wait.Times);
        Assert.Equal(1, wait.Fraction);
    }

    /// <summary>A read under a second measured nothing a person can read: no warning that "it usually takes 0 s".</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(400)]
    public void AReadUnderASecond_IsNotMeasured(int milliseconds)
    {
        var wait = new ModelWait(2_300, TimeSpan.FromSeconds(9), TimeSpan.FromMilliseconds(milliseconds), TimeSpan.FromMinutes(5));

        Assert.False(wait.Longer);
        Assert.Null(wait.Fraction);
        Assert.Equal(Strings.WaitDetailUnmeasured(2_300, D(300)), wait.Detail);
    }
}
