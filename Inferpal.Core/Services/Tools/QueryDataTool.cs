using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using SQLitePCL;

namespace Inferpal.Services.Tools;

/// <summary>
/// Runs a read-only SQL query (SQLite) on a data file of the workspace: a <c>.db</c> / <c>.sqlite</c> database, or a CSV or
/// TSV file loaded in memory as the table <c>data</c> — to count, filter, sum or group rows a file too large to read holds.
/// </summary>
/// <remarks>
/// ⚠ Read-only by an AUTHORIZER, not by <c>query_only</c> (which a query may switch off): reads, selects, functions and the
/// schema pragmas pass, nothing else — a <c>PRAGMA hard_heap_limit</c> or <c>temp_store_directory</c> changes SQLite for the
/// WHOLE process (the code index, <c>@Docs</c>), an <c>ATTACH</c> or <c>VACUUM INTO</c> reaches another file. ⚠ A database is
/// opened as a private COPY: opened in place, even read-only, a WAL database writes its <c>-wal</c> and <c>-shm</c> files
/// into the workspace, and a pooled connection keeps the file locked after the call. ⚠ A query has a deadline (the engine's
/// progress handler): a cross join of two large tables would otherwise hold the run.
/// </remarks>
internal sealed class QueryDataTool : ITool
{
    private readonly Func<string?> _getRoot;

    public QueryDataTool(Func<string?> getRoot) => _getRoot = getRoot;

    /// <summary>The most rows a result shows.</summary>
    internal const int MaxRows = 200;

    /// <summary>The largest database file opened (copied first).</summary>
    internal const long MaxDatabaseBytes = 200L * 1024 * 1024;

    /// <summary>The largest CSV or TSV loaded: it is held in memory, parsed and copied into a table.</summary>
    internal const long MaxDelimitedBytes = 50L * 1024 * 1024;

    /// <summary>How long one query may run.</summary>
    internal static TimeSpan Deadline { get; set; } = TimeSpan.FromSeconds(20);

    private const int MaxCellChars = 200;
    private const int SqliteLimitAttached = 7;

    // SQLite authorizer action codes and answers.
    private const int SqliteOk = 0, SqliteDeny = 1, ActionPragma = 19, ActionRead = 20, ActionSelect = 21, ActionFunction = 31, ActionRecursive = 33;
    private static readonly HashSet<string> SchemaPragmas = new(StringComparer.OrdinalIgnoreCase)
        { "table_info", "table_xinfo", "index_list", "index_info", "index_xinfo", "foreign_key_list", "table_list" };

    private static readonly string[] Databases = [".db", ".sqlite", ".sqlite3"];
    private static readonly string[] Delimited = [".csv", ".tsv"];

    public string Name => "query_data";

    /// <inheritdoc/>
    public ToolEffect EffectOf(JsonElement args) => ToolEffect.Reads;

    public string Description =>
        "Runs a read-only SQL query (SQLite) on a data file: a .db or .sqlite database, or a CSV or TSV file loaded as the " +
        "table `data` whose columns are its header (their types are said). Use it to count, filter, sum or group rows " +
        "instead of reading a large data file. Only SELECT (and schema PRAGMAs such as table_info). Returns at most 200 rows.";

    public object Parameters => new
    {
        type = "object",
        properties = new
        {
            path = new { type = "string", description = "The data file: .csv, .tsv, .db, .sqlite." },
            sql  = new { type = "string", description = "The query, e.g. SELECT status, COUNT(*) FROM data GROUP BY status." },
        },
        required = new[] { "path", "sql" },
    };

    public async Task<string> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var root = _getRoot();
        if (string.IsNullOrEmpty(root)) throw new NoWorkspaceRootException();
        var path = PathSanitizer.Sanitize(args.Str("path"), root);
        PathSanitizer.AssertUnderRoot(path, root);
        var sql = args.Str("sql")?.Trim();
        if (string.IsNullOrEmpty(sql)) return "Error: sql is empty: write the query.";
        var name = Path.GetFileName(path);
        if (!File.Exists(path)) return $"Error: '{name}' does not exist.";
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is ".xlsx" or ".xls" or ".xlsb" or ".ods")
            return $"'{name}' is a spreadsheet: query_data reads CSV, TSV and SQLite databases. Read the sheet with read_file, " +
                   "or ask the user to export it as CSV.";
        var database = Databases.Contains(ext);
        if (!database && !Delimited.Contains(ext))
            return $"'{name}' is not a data file query_data reads (.csv, .tsv, .db, .sqlite).";
        var limit = database ? MaxDatabaseBytes : MaxDelimitedBytes;
        if (new FileInfo(path).Length > limit)
            return $"'{name}' is larger than {limit / (1024 * 1024)} MB: it is not loaded. Ask the user for a smaller extract.";

        return await Task.Run(() => database ? QueryCopy(path, sql, ct) : QueryDelimited(path, ext == ".tsv" ? '\t' : '\0', sql, ct), ct);
    }

    /// <summary>The database queried through a private copy (its <c>-wal</c> and <c>-journal</c> too): nothing is written
    /// next to the original, and nothing keeps it locked.</summary>
    private static string QueryCopy(string path, string sql, CancellationToken ct)
    {
        if (!File.Exists(path + "-wal") && !File.Exists(path + "-journal"))
        {
            // Nothing pending beside it: opened in place as immutable — no lock, no -shm, nothing written. A -wal or a hot
            // journal would be ignored by immutable (stale results without a word): those go through the copy.
            try
            {
                using var direct = new SqliteConnection(new SqliteConnectionStringBuilder
                    { DataSource = new Uri(path).AbsoluteUri + "?immutable=1", Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
                direct.Open();
                return Run(direct, sql, loaded: null, ct);
            }
            catch (SqliteException ex) { return $"Error: '{Path.GetFileName(path)}' could not be opened as a SQLite database ({ex.Message})."; }
        }
        var temp = Directory.CreateTempSubdirectory("inferpal-query-").FullName;
        try
        {
            var copy = Path.Combine(temp, Path.GetFileName(path));
            File.Copy(path, copy);
            foreach (var companion in new[] { "-wal", "-journal" })
                if (File.Exists(path + companion)) File.Copy(path + companion, copy + companion);
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = copy, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
            connection.Open();
            return Run(connection, sql, loaded: null, ct);
        }
        catch (SqliteException ex) { return $"Error: '{Path.GetFileName(path)}' could not be opened as a SQLite database ({ex.Message})."; }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    private static string QueryDelimited(string path, char delimiter, string sql, CancellationToken ct)
    {
        using var connection = new SqliteConnection("Data Source=:memory:;Pooling=False");
        connection.Open();
        string loaded;
        try { loaded = Load(connection, path, delimiter, ct); }
        catch (SqliteException ex) { return $"'{Path.GetFileName(path)}' could not be loaded: {ex.Message}."; }
        catch (FormatException ex) { return $"'{Path.GetFileName(path)}' could not be loaded: {ex.Message}."; }
        return Run(connection, sql, loaded, ct);
    }

    private static string Run(SqliteConnection connection, string sql, string? loaded, CancellationToken ct)
    {
        var handle = connection.Handle!;
        raw.sqlite3_limit(handle, SqliteLimitAttached, 0);
        raw.sqlite3_set_authorizer(handle, (_, action, p0, _, _, _) => action switch
        {
            ActionSelect or ActionRead or ActionFunction or ActionRecursive => SqliteOk,
            ActionPragma when SchemaPragmas.Contains(p0.utf8_to_string() ?? string.Empty) => SqliteOk,
            _ => SqliteDeny,
        }, null);
        var until = DateTime.UtcNow + Deadline;
        var timedOut = false;
        raw.sqlite3_progress_handler(handle, 10_000,
            _ => { if (ct.IsCancellationRequested || DateTime.UtcNow > until) { timedOut = !ct.IsCancellationRequested; return 1; } return 0; }, null);
        var sb = new StringBuilder();
        if (loaded is not null) sb.AppendLine(loaded);
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            using var reader = command.ExecuteReader();
            if (reader.FieldCount == 0) return sb.Append("The query returned no columns.").ToString();
            sb.AppendLine(string.Join(",", Enumerable.Range(0, reader.FieldCount).Select(i => Quoted(reader.GetName(i)))));
            int shown = 0, cut = 0;
            var more = false;
            while (reader.Read())
            {
                if (shown >= MaxRows || sb.Length > ToolOutputBudget.ListChars) { more = true; break; }
                sb.AppendLine(string.Join(",", Enumerable.Range(0, reader.FieldCount).Select(i => Cell(reader, i, ref cut))));
                shown++;
            }
            if (shown == 0) sb.AppendLine("(no rows)");
            if (cut > 0) sb.AppendLine($"({cut} cell(s) cut at {MaxCellChars} characters, marked …)");
            if (more) sb.AppendLine($"(more rows than the {shown} shown: aggregate, filter or add LIMIT/OFFSET)");
            return sb.ToString().TrimEnd();
        }
        catch (SqliteException) when (timedOut)
        {
            return $"Error: the query ran past {Deadline.TotalSeconds:0} s and was stopped. Narrow it (a WHERE, a LIMIT).";
        }
        catch (SqliteException) when (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException(ct);
        }
        catch (SqliteException ex)
        {
            // SQLITE_AUTH (23): the authorizer refused it; SQLITE_ERROR "too many attached": an ATTACH.
            var why = ex.SqliteErrorCode == 23 || ex.Message.Contains("not authorized", StringComparison.OrdinalIgnoreCase)
                    || ex.Message.Contains("too many attached", StringComparison.OrdinalIgnoreCase)
                ? "query_data only reads this file: SELECT and schema PRAGMAs pass; a statement that writes, changes a setting or reaches another file does not."
                : ex.Message;
            return $"Error: {why}";
        }
        finally
        {
            raw.sqlite3_progress_handler(handle, 0, null, null);
        }
    }

    private static string Quoted(string text) =>
        text.Contains(',') || text.Contains('"') || text.Contains('\n') || text.Contains('\r') ? "\"" + text.Replace("\"", "\"\"") + "\"" : text;

    private static string Cell(SqliteDataReader reader, int i, ref int cut)
    {
        if (reader.IsDBNull(i)) return "NULL";
        // A BLOB's length, never its bytes: a 900 MB blob would be copied into the editor's process to print its size.
        if (reader.GetFieldType(i) == typeof(byte[])) return $"<{reader.GetBytes(i, 0, null, 0, 0)} bytes>";
        var value = reader.GetValue(i);
        // A text that reads NULL is quoted, once, so it is never taken for SQL's NULL.
        if (value is string { } literal && literal == "NULL") return "\"NULL\"";
        var text = value switch
        {
            double d => d.ToString(CultureInfo.InvariantCulture),
            _        => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
        };
        if (text.Length > MaxCellChars) { text = text[..MaxCellChars] + "…"; cut++; }
        return Quoted(text);
    }

    /// <summary>
    /// Loads a delimited file into the table <c>data</c>, and says what it loaded: the columns and their types, the rows,
    /// and any field past the header's columns (dropped). A column is INTEGER or REAL only when every value in it reads
    /// back as written — <c>007</c>, a phone number or a 25-digit identifier stays TEXT —; a semicolon file reads a decimal
    /// comma. <paramref name="delimiter"/> \0: the header's own (comma or semicolon).
    /// </summary>
    private static string Load(SqliteConnection connection, string path, char delimiter, CancellationToken ct)
    {
        var text = TextFileEncoding.ReadText(path);
        var separator = delimiter == '\0' ? Guess(text) : delimiter;
        var decimalComma = separator == ';';
        var records = Records(text, separator, ct).ToList();
        if (records.Count == 0) throw new FormatException("it is empty");
        var header = records[0];
        var names = new List<string>();
        for (var i = 0; i < header.Count; i++)
        {
            var n = header[i].Trim();
            if (n.Length == 0) n = $"col{i + 1}";
            var unique = n;
            for (var k = 2; names.Contains(unique, StringComparer.OrdinalIgnoreCase); k++) unique = $"{n}_{k}";
            names.Add(unique);
        }
        var body = records.Skip(1).ToList();
        var extra = body.Count(r => r.Count > names.Count);
        var types = names.Select((_, c) => TypeOf(body, c, decimalComma)).ToList();
        using (var create = connection.CreateCommand())
        {
            create.CommandText = "CREATE TABLE data (" + string.Join(", ", names.Select((n, c) => $"\"{n.Replace("\"", "\"\"")}\" {types[c]}")) + ")";
            create.ExecuteNonQuery();
        }
        using (var tx = connection.BeginTransaction())
        using (var insert = connection.CreateCommand())
        {
            insert.CommandText = "INSERT INTO data VALUES (" + string.Join(", ", names.Select((_, c) => $"$p{c}")) + ")";
            var parameters = names.Select((_, c) => insert.Parameters.Add($"$p{c}", SqliteType.Text)).ToList();
            var row = 0;
            foreach (var r in body)
            {
                if ((++row & 1023) == 0) ct.ThrowIfCancellationRequested();
                for (var c = 0; c < names.Count; c++)
                {
                    var v = c < r.Count ? r[c] : string.Empty;
                    parameters[c].Value = v.Trim().Length == 0 ? DBNull.Value
                        : types[c] == "INTEGER" ? long.Parse(v.Trim(), CultureInfo.InvariantCulture)
                        : types[c] == "REAL" ? Real(v.Trim(), decimalComma)!.Value
                        : v;
                }
                insert.ExecuteNonQuery();
            }
            tx.Commit();
        }
        var said = new StringBuilder($"'{Path.GetFileName(path)}' loaded as the table `data` ({body.Count} row(s)); columns: ");
        said.Append(string.Join(", ", names.Select((n, c) => $"{n} {types[c]}")));
        if (decimalComma && types.Contains("REAL")) said.Append(" (decimal commas read as numbers)");
        said.Append('.');
        if (extra > 0) said.Append($" {extra} row(s) had more fields than the header: the extra fields were left out.");
        return said.ToString();
    }

    private static string TypeOf(List<List<string>> body, int c, bool decimalComma)
    {
        var values = body.Select(r => c < r.Count ? r[c].Trim() : string.Empty).Where(v => v.Length > 0).ToList();
        if (values.Count == 0) return "TEXT";
        // A number is a number only when it reads back as written: "007", "+33…" and 25-digit identifiers stay TEXT.
        if (values.All(v => long.TryParse(v, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n)
                            && n.ToString(CultureInfo.InvariantCulture) == v))
            return "INTEGER";
        if (values.All(v => Real(v, decimalComma) is not null)) return "REAL";
        return "TEXT";
    }

    /// <summary>A finite decimal number without a leading zero or a plus sign, or <c>null</c>.</summary>
    private static double? Real(string v, bool decimalComma)
    {
        var s = decimalComma && !v.Contains('.') ? v.Replace(',', '.') : v;
        if (s.StartsWith('+') || s.Length > 1 && s[0] == '0' && s[1] != '.' || s.StartsWith("-0", StringComparison.Ordinal) && s.Length > 2 && s[2] != '.')
            return null;
        // Digits alone: a number when an integer holds them as written (20 beside 10.5), an identifier otherwise — as a
        // double a 25-digit identifier would lose its digits.
        if (s.TrimStart('-').All(char.IsAsciiDigit))
            return long.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var whole)
                   && whole.ToString(CultureInfo.InvariantCulture) == s ? whole : null;
        return double.TryParse(s, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent,
                               CultureInfo.InvariantCulture, out var d) && double.IsFinite(d) ? d : null;
    }

    /// <summary>The delimiter of a CSV: a semicolon when the first line holds more of them than commas (a European Excel
    /// export), a comma otherwise.</summary>
    private static char Guess(string text)
    {
        var first = text.AsSpan(0, Math.Max(0, text.IndexOf('\n') is var n and >= 0 ? n : text.Length));
        return first.Count(';') > first.Count(',') ? ';' : ',';
    }

    /// <summary>The records of a delimited text (RFC 4180: quoted fields, doubled quotes, line breaks inside quotes);
    /// blank lines are no records.</summary>
    internal static IEnumerable<List<string>> Records(string text, char delimiter, CancellationToken ct)
    {
        var field = new StringBuilder();
        var record = new List<string>();
        var quoted = false;
        var count = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else field.Append(c);
                continue;
            }
            if (c == '"' && field.Length == 0) { quoted = true; continue; }
            if (c == delimiter) { record.Add(field.ToString()); field.Clear(); continue; }
            if (c is '\r' or '\n')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                record.Add(field.ToString()); field.Clear();
                if (record.Count > 1 || record[0].Length > 0) yield return record;
                record = [];
                if ((++count & 1023) == 0) ct.ThrowIfCancellationRequested();
                continue;
            }
            field.Append(c);
        }
        if (field.Length > 0 || record.Count > 0)
        {
            record.Add(field.ToString());
            if (record.Count > 1 || record[0].Length > 0) yield return record;
        }
    }
}
