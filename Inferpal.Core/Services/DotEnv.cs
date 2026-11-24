namespace Inferpal.Services;

/// <summary>
/// A <c>.env</c> file as dotenv reads it: <c>KEY=value</c> lines, <c>export</c> allowed, <c>#</c> comments; a quoted value
/// ends at its closing quote (a comment may follow it), an unquoted one at <c>" #"</c>; a key set twice keeps its last
/// value. The one reader: a comment kept in a value goes out as part of an API key or a connection string.
/// </summary>
internal static class DotEnv
{
    /// <summary>The keys and values of <paramref name="text"/>.</summary>
    internal static Dictionary<string, string> Parse(string text)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (line.StartsWith("export ", StringComparison.Ordinal)) line = line[7..].TrimStart();
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            values[line[..eq].Trim()] = Value(line[(eq + 1)..].Trim());
        }
        return values;
    }

    private static string Value(string value)
    {
        if (value.Length >= 2 && value[0] is '"' or '\'' && value.IndexOf(value[0], 1) is var close and > 0) return value[1..close];
        var comment = value.IndexOf(" #", StringComparison.Ordinal);
        return comment >= 0 ? value[..comment].TrimEnd() : value;
    }
}
