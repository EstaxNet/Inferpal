using System.Text.Json;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// Reading model-written arguments must degrade, never throw (review 2026-08-07).
/// </summary>
public class ToolArgsTests
{
    private static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement;

    [Theory]
    [InlineData("""{"q":"hello"}""", "hello")]
    [InlineData("""{"q":"  hello  "}""", "hello")]
    [InlineData("""{"q":"   "}""", null)]
    [InlineData("""{"q":null}""", null)]
    [InlineData("""{"q":42}""", null)]      // wrong type is absent, not a crash
    [InlineData("""{}""", null)]
    public void Trimmed_DegradesToNull(string json, string? expected) =>
        Assert.Equal(expected, Args(json).Trimmed("q"));

    [Theory]
    [InlineData("""{"n":5}""", 5)]
    [InlineData("""{"n":"5"}""", 5)]        // ⚠ the shape small local models actually send
    [InlineData("""{"n":"abc"}""", 9)]
    [InlineData("""{"n":true}""", 9)]
    [InlineData("""{"n":2.5}""", 9)]
    [InlineData("""{}""", 9)]
    public void Int_TakesTheStringFormAndFallsBackOtherwise(string json, int expected) =>
        Assert.Equal(expected, Args(json).Int("n", 9));

    [Theory]
    [InlineData("""{"b":true}""", true)]
    [InlineData("""{"b":"true"}""", true)]
    [InlineData("""{"b":false}""", false)]
    [InlineData("""{"b":"nope"}""", true)]
    [InlineData("""{}""", true)]
    public void Bool_TakesTheStringFormAndFallsBackOtherwise(string json, bool expected) =>
        Assert.Equal(expected, Args(json).Bool("b", fallback: true));

    [Fact]
    public void Keyword_IsTrimmedAndLowered() =>
        Assert.Equal("list", Args("""{"action":"  LIST "}""").Keyword("action"));

    [Fact]
    public void NonObjectArguments_AreNotACrash()
    {
        // A model that answers with a bare string or array instead of an arguments object.
        Assert.Null(Args("\"oops\"").Trimmed("q"));
        Assert.Equal(9, Args("[1,2]").Int("n", 9));
        Assert.True(Args("null").Bool("b", fallback: true));
    }
}
