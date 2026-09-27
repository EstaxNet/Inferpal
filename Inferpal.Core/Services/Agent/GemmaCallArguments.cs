using System.Text;
using System.Text.Json;

namespace Inferpal.Services.Agent;

/// <summary>
/// Converts the arguments of a Gemma 4 tool call — <c>{location:&lt;|"|&gt;Tokyo, JP&lt;|"|&gt;,days:3}</c> — to JSON.
/// </summary>
/// <remarks>
/// Gemma 4 writes its calls in its own syntax (Google's function-calling guide): keys bare, strings wrapped in the
/// <c>&lt;|"|&gt;</c> token, numbers and <c>true</c>/<c>false</c>/<c>null</c> bare, lists and objects in brackets. A string
/// is raw between its delimiters, so it may hold commas, colons and braces. <c>null</c> means "not this syntax":
/// the caller then keeps the call as unreadable, never as a call with no arguments.
/// </remarks>
internal static class GemmaCallArguments
{
    private const string Quote = "<|\"|>";

    /// <summary>The JSON object for <paramref name="text"/> (<c>{…}</c> in Gemma's syntax), or <c>null</c>.</summary>
    public static string? ToJson(string text)
    {
        var i    = 0;
        var json = new StringBuilder();
        if (!Value(text, ref i, json) || json.Length == 0 || json[0] != '{') return null;
        return Blanks(text, i) == text.Length ? json.ToString() : null;
    }

    private static bool Value(string s, ref int i, StringBuilder json)
    {
        i = Blanks(s, i);
        if (i >= s.Length) return false;
        if (s.AsSpan(i).StartsWith(Quote, StringComparison.Ordinal))
        {
            var end = s.IndexOf(Quote, i + Quote.Length, StringComparison.Ordinal);
            if (end < 0) return false;
            json.Append(JsonSerializer.Serialize(s[(i + Quote.Length)..end]));
            i = end + Quote.Length;
            return true;
        }
        if (s[i] == '{') return Container(s, ref i, json, '{', '}', keyed: true);
        if (s[i] == '[') return Container(s, ref i, json, '[', ']', keyed: false);

        // A bare scalar: up to the next separator. A number or true/false/null stays as it is; anything else is text.
        var start = i;
        while (i < s.Length && s[i] is not (',' or '}' or ']')) i++;
        var scalar = s[start..i].Trim();
        if (scalar.Length == 0) return false;
        json.Append(IsJsonScalar(scalar) ? scalar : JsonSerializer.Serialize(scalar));
        return true;
    }

    private static bool Container(string s, ref int i, StringBuilder json, char open, char close, bool keyed)
    {
        json.Append(open);
        i++;
        i = Blanks(s, i);
        if (i < s.Length && s[i] == close)
        {
            json.Append(close);
            i++;
            return true;
        }
        while (true)
        {
            if (keyed)
            {
                i = Blanks(s, i);
                string key;
                if (s.AsSpan(i).StartsWith(Quote, StringComparison.Ordinal))
                {
                    var end = s.IndexOf(Quote, i + Quote.Length, StringComparison.Ordinal);
                    if (end < 0) return false;
                    key = s[(i + Quote.Length)..end];
                    i   = end + Quote.Length;
                }
                else
                {
                    var start = i;
                    while (i < s.Length && s[i] is not (':' or ',' or '{' or '}' or '[' or ']')) i++;
                    key = s[start..i].Trim();
                }
                i = Blanks(s, i);
                if (key.Length == 0 || i >= s.Length || s[i] != ':') return false;
                i++;
                json.Append(JsonSerializer.Serialize(key)).Append(':');
            }
            if (!Value(s, ref i, json)) return false;
            i = Blanks(s, i);
            if (i >= s.Length) return false;
            if (s[i] == ',') { json.Append(','); i++; continue; }
            if (s[i] != close) return false;
            json.Append(close);
            i++;
            return true;
        }
    }

    private static bool IsJsonScalar(string text)
    {
        if (text is "true" or "false" or "null") return true;
        if (text[0] is not ('-' or (>= '0' and <= '9'))) return false;
        try
        {
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.ValueKind == JsonValueKind.Number;
        }
        catch (JsonException) { return false; }
    }

    private static int Blanks(string s, int i)
    {
        while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        return i;
    }
}
