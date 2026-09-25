using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Inferpal.Services.Shell;

/// <summary>
/// What Windows PowerShell wrote to stderr, as text: its CLIXML stream decoded, progress records dropped.
/// </summary>
/// <remarks>
/// ⚠ Every shell call goes through <c>-EncodedCommand</c> (immune to quoting), and Windows PowerShell then takes its
/// caller for another PowerShell: it writes its non-output streams to stderr as CLIXML — a <c>#&lt; CLIXML</c> header
/// line, then one <c>&lt;Objs&gt;</c> element per line. <c>-OutputFormat Text</c> does not change it. Read raw, every
/// command came back to the model with a "[stderr]" section — the look of a failure — holding ~700 characters of XML
/// for a start-up progress record ("Preparing modules for first use"), and a real cmdlet error arrived as
/// <c>&lt;S S="Error"&gt;Get-Item : …_x000D__x000A_&lt;/S&gt;</c>, one element per line of the message.
/// ⚠ Line by line, so a streamed reader (background jobs) decodes each line as it comes. A native program's own stderr
/// lines are interleaved between the header and the XML, and are kept; an element that does not parse is kept as is —
/// decoding must never lose what the command said.
/// </remarks>
internal static class PowerShellStderr
{
    private const string Header = "#< CLIXML";

    /// <summary>The whole of a captured stderr, decoded.</summary>
    public static string Decode(string stderr)
    {
        if (!stderr.Contains(Header, StringComparison.Ordinal)) return stderr;

        var sb = new StringBuilder();
        foreach (var line in stderr.Replace("\r\n", "\n").Split('\n'))
            sb.Append(DecodeLine(line) is { } text ? text.EndsWith('\n') ? text : text + "\n" : string.Empty);
        return sb.ToString().TrimEnd('\n');
    }

    /// <summary>One stderr line, decoded: <c>null</c> when nothing is left to show (the header, a progress-only
    /// element).</summary>
    public static string? DecodeLine(string? line)
    {
        if (line is null) return null;
        var trimmed = line.Trim();
        if (trimmed == Header) return null;
        if (!trimmed.StartsWith("<Objs", StringComparison.Ordinal)) return line;

        try
        {
            var sb = new StringBuilder();
            foreach (var element in XElement.Parse(trimmed).Elements())
            {
                var stream = (string?)element.Attribute("S");
                if (string.Equals(stream, "progress", StringComparison.OrdinalIgnoreCase)) continue;

                // Strings carry the error/warning/verbose/debug text, CR/LF escaped as _x000D__x000A_. Any other
                // object is kept by its ToString, when it has one.
                var text = element.Name.LocalName == "S"
                    ? element.Value
                    : element.Elements().FirstOrDefault(e => e.Name.LocalName == "ToString")?.Value;
                if (string.IsNullOrEmpty(text)) continue;

                text = XmlConvert.DecodeName(text);
                if (stream is { } s && !s.Equals("Error", StringComparison.OrdinalIgnoreCase)
                    && element.Name.LocalName == "S")
                    text = s.ToUpperInvariant() + ": " + text;
                sb.Append(text);
            }
            var decoded = sb.ToString().Replace("\r\n", "\n").TrimEnd('\n');
            return decoded.Length == 0 ? null : decoded;
        }
        catch (XmlException)
        {
            return line;   // not what it looked like: never lose it
        }
    }
}
