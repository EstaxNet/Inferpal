using System.Text.Json;
using Inferpal.Services.Agent;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A Gemma 4 call that writes its strings in ASCII quotes, JSON-style, instead of its <c>&lt;|"|&gt;</c> token.
/// </summary>
/// <remarks>
/// ⚠ Read as a bare scalar, <c>path:"src/Shop/Cart.cs"</c> kept its quotes: <c>read_file</c> answered "File not found:
/// …/\"src/Shop/Cart.cs\"", <c>run_command</c> ran a program named <c>"dotnet test …"</c>, and a quoted KEY
/// (<c>"pattern":</c>) became an argument no tool reads. Gemma 4 12B wrote it in 41 of its 456 calls on the bench, in 26
/// of 132 runs. The calls below are rebuilt from what the parser made of them — <c>"\"src/Shop/Cart.cs\""</c> can only
/// have been read from <c>"src/Shop/Cart.cs"</c>.
/// </remarks>
public class GemmaQuotedArgumentsTests
{
    private static JsonElement Args(string text)
    {
        var (calls, cleaned) = InlineToolCallParser.TryParse(text);
        var call = Assert.Single(calls!);
        Assert.Null(call.Function.UnparsedArguments);
        Assert.DoesNotContain("tool_call", cleaned);
        return call.Function.Arguments;
    }

    [Fact]
    public void AQuotedPath_IsThePath()
    {
        var args = Args("<|tool_call>call:read_file{path:\"src/Shop/Cart.cs\"}<tool_call|>");

        Assert.Equal("src/Shop/Cart.cs", args.GetProperty("path").GetString());
    }

    [Fact]
    public void AQuotedKey_IsTheKey()
    {
        var args = Args("<|tool_call>call:search_in_files{path:\"/ws\",\"pattern\":\"ApplyDiscount\"}<tool_call|>");

        Assert.Equal("/ws", args.GetProperty("path").GetString());
        Assert.Equal("ApplyDiscount", args.GetProperty("pattern").GetString());
    }

    [Fact]
    public void AQuotedCommand_IsTheCommand()
    {
        var args = Args("<|tool_call>call:run_command{command:\"dotnet test /ws/Shop.slnx\"}<tool_call|>");

        Assert.Equal("dotnet test /ws/Shop.slnx", args.GetProperty("command").GetString());
    }

    /// <summary>Written JSON-style, the string is read JSON-style: its escapes mean what they mean in JSON, and the
    /// separators inside it are text.</summary>
    [Fact]
    public void AQuotedString_ReadsItsEscapes_AndKeepsItsPunctuation()
    {
        var args = Args("<|tool_call>call:apply_diff{path:\"a.cs\",old_content:\"return x / 10m;\\n    }\",new_content:\"a, b: {c} \\\"d\\\"\"}<tool_call|>");

        Assert.Equal("return x / 10m;\n    }", args.GetProperty("old_content").GetString());
        Assert.Equal("a, b: {c} \"d\"", args.GetProperty("new_content").GetString());
    }

    /// <summary>A backslash JSON does not know is the model's text, not an escape: a Windows path stays whole.</summary>
    [Fact]
    public void AnEscapeJsonDoesNotKnow_StaysAsWritten()
    {
        var args = Args("<|tool_call>call:read_file{path:\"C:\\src\\a.cs\"}<tool_call|>");

        Assert.Equal("C:\\src\\a.cs", args.GetProperty("path").GetString());
    }

    /// <summary>⚠ Reference arms: Gemma's own syntax is unchanged — a token-delimited string keeps the quotes it holds,
    /// and a bare value is still text.</summary>
    [Fact]
    public void GemmasOwnSyntax_IsUnchanged()
    {
        var quoted = Args("<|tool_call>call:search{query:<|\"|>say \"hi\"<|\"|>}<tool_call|>");
        var bare   = Args("<|tool_call>call:read_file{path:src/a.cs}<tool_call|>");

        Assert.Equal("say \"hi\"", quoted.GetProperty("query").GetString());
        Assert.Equal("src/a.cs", bare.GetProperty("path").GetString());
    }
}
