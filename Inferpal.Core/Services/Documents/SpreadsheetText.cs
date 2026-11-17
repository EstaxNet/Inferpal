using System.Globalization;
using System.IO.Compression;
using System.Text;
using ExcelDataReader;

namespace Inferpal.Services.Documents;

/// <summary>
/// The text of a workbook — <c>.xlsx</c>, <c>.xlsm</c>, <c>.xlsb</c>, <c>.xls</c> — read by ExcelDataReader: each sheet under
/// its name, a row per line, its cells separated by tabs; numbers and dates written the same on every machine.
/// </summary>
/// <remarks>
/// ⚠ ExcelDataReader bounds nothing itself: a sheet that inflates to gigabytes is read to the end. So a zip whose parts
/// inflate past <see cref="MaxInflatedBytes"/> is refused before it is opened, and rows are read one by one under the
/// character cap and the deadline.
/// </remarks>
internal static class SpreadsheetText
{
    /// <summary>The most a workbook's parts may inflate to, together.</summary>
    internal const long MaxInflatedBytes = 256L * 1024 * 1024;

    static SpreadsheetText() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);   // an .xls names its code page

    /// <summary>An ExcelDataReader over <paramref name="bytes"/>, the code pages an <c>.xls</c> names registered first.</summary>
    internal static IExcelDataReader Open(byte[] bytes, string? password) =>
        ExcelReaderFactory.CreateReader(new MemoryStream(bytes), new ExcelReaderConfiguration { Password = password, LeaveOpen = false });

    internal static DocumentExtraction Extract(byte[] bytes, CancellationToken ct, string? password)
    {
        // A protected .xlsx is an encrypted OLE container; opened here, as for a DOCX, so the same card serves every format.
        // An .xls is an OLE container too, encrypted or not: ExcelDataReader opens it, with the password when one is given.
        if (OfficeCrypto.IsCompoundFile(bytes) && OfficeCrypto.IsEncryptedPackage(bytes))
        {
            // An encryption this reader does not open is said unreadable, never a password card no password can pass.
            if (!OfficeCrypto.Opens(bytes)) return new(string.Empty, 0, 0, DocumentProblem.Unreadable);
            if (password is null || OfficeCrypto.Decrypt(bytes, password, ct) is not { } package)
                return new(string.Empty, 0, 0, DocumentProblem.Encrypted);
            bytes = package;
        }
        if (!OfficeCrypto.IsCompoundFile(bytes)) RefuseInflatingPast(bytes);

        var text = new StringBuilder();
        var stoppedAt = 0;
        try
        {
            using var reader = Open(bytes, OfficeCrypto.IsCompoundFile(bytes) ? password : null);
            var sheet = 0;
            do
            {
                sheet++;
                text.Append("# ").Append(reader.Name).Append('\n');
                while (reader.Read())
                {
                    ct.ThrowIfCancellationRequested();
                    var line = new StringBuilder();
                    for (var i = 0; i < reader.FieldCount; i++)
                    {
                        if (i > 0) line.Append('\t');
                        line.Append(Cell(reader.GetValue(i)));
                    }
                    var row = line.ToString().TrimEnd('\t');
                    if (row.Length > 0) text.Append(row).Append('\n');
                    if (text.Length > PdfText.MaxChars) { stoppedAt = sheet; break; }
                }
                if (stoppedAt > 0) break;
            } while (reader.NextResult());
        }
        catch (ExcelDataReader.Exceptions.InvalidPasswordException)
        {
            return new(string.Empty, 0, 0, DocumentProblem.Encrypted);
        }
        catch (ExcelDataReader.Exceptions.ExcelReaderException ex)
        {
            Diagnostics.Swallow("SpreadsheetText.Extract", ex);
            return new(string.Empty, 0, 0, DocumentProblem.Unreadable);
        }
        var read = text.ToString().TrimEnd();
        // A workbook of empty sheets has only its sheet names: no text to read.
        if (read.Split('\n').All(l => l.StartsWith("# ", StringComparison.Ordinal)))
            return new(string.Empty, 0, 0, DocumentProblem.NoText);
        return new(read.Length > PdfText.MaxChars ? read[..PdfText.MaxChars] : read, 0, 0, DocumentProblem.None, stoppedAt);
    }

    /// <summary>A cell as text: a number and a date in the invariant culture, a date without a midnight time.</summary>
    internal static string Cell(object? value) => value switch
    {
        null => string.Empty,
        DateTime d => d.TimeOfDay == TimeSpan.Zero ? d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                                                   : d.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        double n => n.ToString("R", CultureInfo.InvariantCulture),
        bool b => b ? "TRUE" : "FALSE",
        _ => (Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty).Replace('\n', ' ').Replace('\t', ' '),
    };

    /// <summary>A zip whose parts would inflate past <see cref="MaxInflatedBytes"/>: refused before any library reads it.</summary>
    internal static void RefuseInflatingPast(byte[] bytes)
    {
        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        long total = 0;
        foreach (var entry in zip.Entries)
            if ((total += entry.Length) > MaxInflatedBytes)
                throw new DocumentLimitException("A document whose parts inflate past their size limit");
    }
}
