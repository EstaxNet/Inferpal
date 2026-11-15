using System.Globalization;
using System.Text;
using MimeKit;
using OpenMcdf;

namespace Inferpal.Services.Documents;

/// <summary>
/// The text of an e-mail — <c>.eml</c> (MIME) by MimeKitLite, <c>.msg</c> (Outlook) by OpenMcdf: its subject, sender,
/// recipients and date, then its body (plain text, or HTML or RTF read as text), then the names of its attachments — named,
/// never read: an attached document is a file of its own.
/// </summary>
internal static class EmailText
{
    internal static DocumentExtraction Extract(byte[] bytes, string extension, CancellationToken ct)
    {
        try { return extension == ".msg" ? Msg(bytes, ct) : Eml(bytes, ct); }
        // A damaged file throws from inside the libraries, in their own types (a stream cut short, a broken chain):
        // said unreadable, never a failed tool.
        catch (Exception ex) when (ex is IOException or FormatException or ArgumentException or InvalidOperationException
                                       or NotSupportedException or IndexOutOfRangeException or OverflowException)
        {
            Diagnostics.Swallow("EmailText.Extract", ex);
            return new(string.Empty, 0, 0, DocumentProblem.Unreadable);
        }
    }

    private static DocumentExtraction Eml(byte[] bytes, CancellationToken ct)
    {
        var message = MimeMessage.Load(new MemoryStream(bytes), ct);
        var body = message.TextBody is { Length: > 0 } plain ? plain
                 : message.HtmlBody is { Length: > 0 } html ? Tools.FetchUrlTool.HtmlToText(html)
                 : string.Empty;
        var attachments = message.Attachments.Select(a => a is MimePart part ? part.FileName : (a as MessagePart)?.Message?.Subject)
                                             .Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n!).ToList();
        return Compose(message.Subject, message.From.ToString(), message.To.ToString(), message.Cc.ToString(),
                       message.Date == DateTimeOffset.MinValue ? null : message.Date, body, attachments, message.Bcc.ToString());
    }

    // ── Outlook .msg: MAPI properties stored as streams named __substg1.0_<id><type> ─────────────────────────────

    private static DocumentExtraction Msg(byte[] bytes, CancellationToken ct)
    {
        using var root = RootStorage.Open(DocText.Expandable(bytes));
        var codePage = Int(root, 0x3FDE) ?? Int(root, 0x3FFD) ?? 1252;
        string? Text(Storage storage, int id) => String(storage, id, codePage);

        var body = Text(root, 0x1000);
        if (string.IsNullOrWhiteSpace(body) && Binary(root, 0x1013) is { } htmlBytes)
            body = Tools.FetchUrlTool.HtmlToText(Decode(htmlBytes, codePage));
        if (string.IsNullOrWhiteSpace(body) && Binary(root, 0x1009) is { } compressed && CompressedRtf.Decompress(compressed) is { } rtf)
            body = RtfText.Extract(rtf, ct).Text;
        ct.ThrowIfCancellationRequested();

        var attachments = new List<string>();
        var to = new List<string>();
        var cc = new List<string>();
        var bcc = new List<string>();
        foreach (var entry in root.EnumerateEntries())
        {
            if (entry.Type != EntryType.Storage) continue;
            if (entry.Name.StartsWith("__attach_version1.0_", StringComparison.Ordinal))
            {
                var attachment = root.OpenStorage(entry.Name);
                if ((Text(attachment, 0x3707) ?? Text(attachment, 0x3704) ?? Text(attachment, 0x3001)) is { Length: > 0 } name) attachments.Add(name);
            }
            else if (entry.Name.StartsWith("__recip_version1.0_", StringComparison.Ordinal))
            {
                // A recipient: its name and its SMTP address (an Exchange address is a path, not an address to quote).
                var recipient = root.OpenStorage(entry.Name);
                var name = Text(recipient, 0x3001);
                var address = Text(recipient, 0x39FE) ?? Text(recipient, 0x3003);
                if (address is not null && address.Contains('/')) address = null;
                var shown = name is null ? address : address is null || address == name ? name : $"{name} <{address}>";
                if (shown is null) continue;
                (Int(recipient, 0x0C15, header: 8) switch { 2 => cc, 3 => bcc, _ => to }).Add(shown);
            }
        }
        var sender = Text(root, 0x0C1A) ?? Text(root, 0x0042);
        var senderAddress = Text(root, 0x5D01) ?? Text(root, 0x0C1F);
        var from = sender is null ? senderAddress : senderAddress is null || senderAddress.Contains('/') ? sender : $"{sender} <{senderAddress}>";
        return Compose(Text(root, 0x0037), from,
                       to.Count > 0 ? string.Join(", ", to) : Text(root, 0x0E04),
                       cc.Count > 0 ? string.Join(", ", cc) : Text(root, 0x0E03),
                       Time(root, 0x0039) ?? Time(root, 0x0E06),
                       body ?? string.Empty, attachments, bcc.Count > 0 ? string.Join(", ", bcc) : Text(root, 0x0E02));
    }

    private static string? String(Storage storage, int id, int codePage)
    {
        if (Read(storage, $"__substg1.0_{id:X4}001F") is { } unicode) return Encoding.Unicode.GetString(unicode).TrimEnd('\0');
        if (Read(storage, $"__substg1.0_{id:X4}001E") is { } ansi) return Decode(ansi, codePage).TrimEnd('\0');
        return null;
    }

    private static byte[]? Binary(Storage storage, int id) => Read(storage, $"__substg1.0_{id:X4}0102");

    private static byte[]? Read(Storage storage, string name)
    {
        try
        {
            using var stream = storage.OpenStream(name);
            if (stream.Length > DocumentText.MaxBytes) throw new DocumentLimitException("An e-mail property past its size limit");
            var bytes = new byte[stream.Length];
            stream.ReadExactly(bytes);
            return bytes;
        }
        catch (FileNotFoundException) { return null; }
    }

    /// <summary>A fixed-size property (an integer, a time), read from a properties stream: a header — 32 bytes for the
    /// message, 8 for a recipient or an attachment —, then 16 bytes per property: tag, flags, value.</summary>
    private static byte[]? Fixed(Storage storage, int id, int type, int header)
    {
        if (Read(storage, "__properties_version1.0") is not { } props) return null;
        var tag = (uint)((id << 16) | type);
        for (var at = header; at + 16 <= props.Length; at += 16)
            if (BitConverter.ToUInt32(props, at) == tag) return props[(at + 8)..(at + 16)];
        return null;
    }

    private static int? Int(Storage storage, int id, int header = 32) =>
        Fixed(storage, id, 0x0003, header) is { } v ? BitConverter.ToInt32(v, 0) : null;

    private static DateTimeOffset? Time(RootStorage root, int id)
    {
        if (Fixed(root, id, 0x0040, 32) is not { } v) return null;
        var ticks = BitConverter.ToInt64(v, 0);
        return ticks <= 0 || ticks > DateTime.MaxValue.ToFileTimeUtc() ? null : DateTimeOffset.FromFileTime(ticks).ToUniversalTime();
    }

    static EmailText() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    private static string Decode(byte[] bytes, int codePage)
    {
        try { return Encoding.GetEncoding(codePage).GetString(bytes); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException) { return Encoding.Latin1.GetString(bytes); }
    }

    private static DocumentExtraction Compose(string? subject, string? from, string? to, string? cc, DateTimeOffset? date,
                                              string body, IReadOnlyList<string> attachments, string? bcc)
    {
        var text = new StringBuilder();
        void Header(string name, string? value) { if (!string.IsNullOrWhiteSpace(value)) text.Append(name).Append(": ").Append(value.Trim()).Append('\n'); }
        Header("Subject", subject);
        Header("From", from);
        Header("To", to);
        Header("Cc", cc);
        Header("Bcc", bcc);
        if (date is { } d) Header("Date", d.ToString("yyyy-MM-dd HH:mm zzz", CultureInfo.InvariantCulture));
        // Runs of blank lines (a plain body converted from HTML or RTF is full of them) cost the window, not the reader.
        text.Append('\n').Append(System.Text.RegularExpressions.Regex.Replace(body.Replace("\r\n", "\n").Trim(), @"\n{3,}", "\n\n")).Append('\n');
        if (attachments.Count > 0) text.Append('\n').Append("Attachments (not included): ").Append(string.Join(", ", attachments)).Append('\n');
        var read = text.ToString().Trim();
        if (read.Length == 0) return new(string.Empty, 0, 0, DocumentProblem.NoText);
        return new(read.Length > PdfText.MaxChars ? read[..PdfText.MaxChars] : read, 0, 0, DocumentProblem.None,
                   read.Length > PdfText.MaxChars ? 1 : 0);
    }
}

/// <summary>
/// Outlook's compressed RTF (MS-OXRTFCP): an LZ77 variant over a 4 KB dictionary that starts with a fixed RTF prefix.
/// Bounded by the size its header declares.
/// </summary>
internal static class CompressedRtf
{
    private const string Prefix =
        "{\\rtf1\\ansi\\mac\\deff0\\deftab720{\\fonttbl;}{\\f0\\fnil \\froman \\fswiss \\fmodern \\fscript \\fdecor MS Sans SerifSymbolArialTimes New RomanCourier{\\colortbl\\red0\\green0\\blue0\r\n\\par \\pard\\plain\\f0\\fs20\\b\\i\\u\\tab\\tx";

    internal static byte[]? Decompress(byte[] data)
    {
        if (data.Length < 16) return null;
        var rawSize = BitConverter.ToInt32(data, 4);
        var magic = BitConverter.ToUInt32(data, 8);
        if (rawSize < 0 || rawSize > DocumentText.MaxBytes) return null;
        if (magic == 0x414C454D) return data[16..Math.Min(data.Length, 16 + rawSize)];   // "MELA": stored uncompressed
        if (magic != 0x75465A4C) return null;                                            // "LZFu"
        var dictionary = new byte[4096];
        var prefix = Encoding.ASCII.GetBytes(Prefix);
        prefix.CopyTo(dictionary, 0);
        var write = prefix.Length;
        var output = new List<byte>(rawSize);
        var at = 16;
        while (at < data.Length && output.Count < rawSize)
        {
            var control = data[at++];
            for (var bit = 0; bit < 8 && at < data.Length && output.Count < rawSize; bit++)
            {
                if ((control & (1 << bit)) == 0)
                {
                    var b = data[at++];
                    output.Add(b);
                    dictionary[write] = b;
                    write = (write + 1) % 4096;
                    continue;
                }
                if (at + 1 >= data.Length) return output.ToArray();
                var reference = (data[at] << 8) | data[at + 1];
                at += 2;
                var offset = reference >> 4;
                var length = (reference & 0xF) + 2;
                if (offset == write) return output.ToArray();   // the end marker
                for (var i = 0; i < length; i++)
                {
                    var b = dictionary[(offset + i) % 4096];
                    output.Add(b);
                    dictionary[write] = b;
                    write = (write + 1) % 4096;
                }
            }
        }
        return output.ToArray();
    }
}
