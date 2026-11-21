using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Inferpal.Services.Databases;

/// <summary>
/// Reads a database for <c>query_database</c>: the account's rights, the tables, a table's structure, the rows of one read —
/// each in a transaction that keeps nothing.
/// </summary>
/// <remarks>
/// ⚠ The second lock: PostgreSQL and MySQL/MariaDB run the query in <c>START TRANSACTION READ ONLY</c>, SQL Server in a
/// transaction always rolled back — a function of the person's that writes is stopped there, which the analysis cannot see.
/// What a transaction cannot stop (<c>dblink</c>, <c>COPY … TO PROGRAM</c>) the analysis stops first
/// (<see cref="SqlGuard"/>). ⚠ The session is set to read text as the analysis does: PostgreSQL's
/// <c>standard_conforming_strings</c> on, MySQL without <c>NO_BACKSLASH_ESCAPES</c> nor <c>ANSI_QUOTES</c> — otherwise the
/// server would end a string where the analysis did not, and run what it took for a string.
/// </remarks>
internal static class DatabaseReader
{
    /// <summary>How long one query may run, on the server's own clock.</summary>
    internal const int QuerySeconds = 30;

    /// <summary>The most rows read before the query is stopped: what is shown is what the answer's budget holds
    /// (<see cref="Tools.ToolOutputBudget.ListChars"/>), and this ceiling only spares the server a table read whole.</summary>
    internal const int MaxRowsRead = 10_000;

    private const int MaxCellChars = 200;

    private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(1);

    private static readonly Regex GrantLine = new(@"^GRANT\s+(?<privileges>.+?)\s+ON\s+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                                                   MatchTimeout);

    // The MySQL modes that make a "…" a name (ANSI_QUOTES, alone or within a composite mode) or a backslash a plain
    // character (NO_BACKSLASH_ESCAPES): the analysis reads neither.
    private static readonly HashSet<string> ForeignModes = new(StringComparer.OrdinalIgnoreCase)
        { "ANSI_QUOTES", "NO_BACKSLASH_ESCAPES", "ANSI", "ORACLE", "MSSQL", "DB2", "POSTGRESQL", "MAXDB", "SQLSERVER" };

    /// <summary>Opens a target's connection; replaced by tests.</summary>
    internal static Func<DatabaseTarget, CancellationToken, Task<DbConnection>> Open { get; set; } = OpenAsync;

    private static async Task<DbConnection> OpenAsync(DatabaseTarget target, CancellationToken ct)
    {
        DbConnection connection = target.Engine switch
        {
            DatabaseEngine.Postgres  => new Npgsql.NpgsqlConnection(target.ConnectionString),
            DatabaseEngine.SqlServer => new Microsoft.Data.SqlClient.SqlConnection(target.ConnectionString),
            _                        => new MySqlConnector.MySqlConnection(target.ConnectionString),
        };
        try
        {
            await connection.OpenAsync(ct);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    /// <summary>What the account may do, where the engine shows it; <see cref="AccountRights.Unknown"/> when it does not.</summary>
    /// <remarks>"Can only read" is said only where no write right was found at any level the engine answers for: the
    /// database, a schema, a table (PostgreSQL, SQL Server), every grant line (MySQL — a role granted there leaves it unknown).</remarks>
    public static async Task<AccountRights> RightsAsync(DbConnection connection, DatabaseEngine engine, CancellationToken ct)
    {
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandTimeout = 10;
            if (engine == DatabaseEngine.MySql)
            {
                command.CommandText = "SHOW GRANTS FOR CURRENT_USER()";
                var grants = new List<string>();
                await using (var reader = await command.ExecuteReaderAsync(ct))
                    while (await reader.ReadAsync(ct)) grants.Add(reader.GetString(0));
                if (grants.Count == 0) return AccountRights.Unknown;
                string[] writes = ["ALL", "INSERT", "UPDATE", "DELETE", "CREATE", "DROP", "ALTER", "FILE", "SUPER", "EXECUTE", "TRIGGER", "EVENT", "GRANT OPTION", "INDEX", "REFERENCES"];
                var unknown = false;
                foreach (var grant in grants)
                {
                    // The privileges between GRANT and ON: never the account's or the database's name.
                    if (GrantLine.Match(grant) is not { Success: true } line) { unknown = true; continue; }   // a role granted
                    var privileges = line.Groups["privileges"].Value.ToUpperInvariant().Split(',').Select(p => p.Trim());
                    if (privileges.Any(p => writes.Any(w => p == w || p.StartsWith(w + " ", StringComparison.Ordinal)))) return AccountRights.CanWrite;
                    if (grant.Contains("WITH GRANT OPTION", StringComparison.OrdinalIgnoreCase)) return AccountRights.CanWrite;
                }
                return unknown ? AccountRights.Unknown : AccountRights.ReadOnly;
            }
            command.CommandText = engine == DatabaseEngine.Postgres
                ? "SELECT CASE WHEN current_setting('transaction_read_only') = 'on' THEN 0 " +
                  "WHEN (SELECT rolsuper OR rolcreaterole OR rolcreatedb FROM pg_roles WHERE rolname = current_user) THEN 1 " +
                  "WHEN EXISTS (SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace " +
                  "WHERE c.relkind IN ('r','p','v','m') AND n.nspname NOT IN ('pg_catalog','information_schema') " +
                  "AND has_table_privilege(c.oid, 'INSERT,UPDATE,DELETE,TRUNCATE')) THEN 1 " +
                  "WHEN EXISTS (SELECT 1 FROM pg_namespace n WHERE n.nspname NOT IN ('pg_catalog','information_schema') " +
                  "AND n.nspname NOT LIKE 'pg\\_%' AND has_schema_privilege(n.oid, 'CREATE')) THEN 1 " +
                  "WHEN has_database_privilege(current_database(), 'CREATE') THEN 1 ELSE 0 END"
                : "SELECT CASE WHEN IS_SRVROLEMEMBER('sysadmin') = 1 OR IS_ROLEMEMBER('db_owner') = 1 OR IS_ROLEMEMBER('db_datawriter') = 1 " +
                  "OR IS_ROLEMEMBER('db_ddladmin') = 1 OR EXISTS (SELECT 1 FROM fn_my_permissions(NULL, 'DATABASE') " +
                  "WHERE permission_name IN ('INSERT','UPDATE','DELETE','ALTER','CONTROL','CREATE TABLE','EXECUTE')) " +
                  // A grant on a schema or a table, to the account or a role it is in (public included).
                  "OR EXISTS (SELECT 1 FROM sys.database_permissions p WHERE p.state IN ('G','W') " +
                  "AND p.permission_name IN ('INSERT','UPDATE','DELETE','ALTER','CONTROL','TAKE OWNERSHIP','EXECUTE') " +
                  "AND (p.grantee_principal_id = DATABASE_PRINCIPAL_ID() OR p.grantee_principal_id = 0 " +
                  "OR p.grantee_principal_id IN (SELECT r.role_principal_id FROM sys.database_role_members r " +
                  "WHERE r.member_principal_id = DATABASE_PRINCIPAL_ID()))) THEN 1 ELSE 0 END";
            return Convert.ToInt32(await command.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture) == 1 ? AccountRights.CanWrite : AccountRights.ReadOnly;
        }
        catch (Exception ex) when (ex is DbException or InvalidOperationException or InvalidCastException or FormatException
                                   or RegexMatchTimeoutException)
        {
            Diagnostics.Swallow("DatabaseReader.Rights", ex);
            return AccountRights.Unknown;
        }
    }

    /// <summary>The tables (and views) the account sees, one per line.</summary>
    public static Task<string> TablesAsync(DbConnection connection, DatabaseEngine engine, CancellationToken ct) =>
        ReadAsync(connection, engine, engine switch
        {
            DatabaseEngine.Postgres => "SELECT table_schema, table_name, table_type FROM information_schema.tables " +
                                       "WHERE table_schema NOT IN ('pg_catalog','information_schema') ORDER BY 1, 2",
            DatabaseEngine.SqlServer => "SELECT TABLE_SCHEMA, TABLE_NAME, TABLE_TYPE FROM INFORMATION_SCHEMA.TABLES ORDER BY 1, 2",
            _ => "SELECT TABLE_SCHEMA, TABLE_NAME, TABLE_TYPE FROM information_schema.tables WHERE TABLE_SCHEMA = DATABASE() ORDER BY 1, 2",
        }, parameter: null, ct);

    /// <summary>The structure of <paramref name="table"/> (<c>schema.table</c> or <c>table</c>): its columns, keys and
    /// constraints, indexes, and a view's definition — in sections, each a result of its own.</summary>
    public static async Task<string> DescribeAsync(DbConnection connection, DatabaseEngine engine, string table, CancellationToken ct)
    {
        var dot = table.LastIndexOf('.');
        string Strip(string s) => s.Trim().Trim('"', '[', ']', '`');
        var (schema, name) = dot > 0 ? (Strip(table[..dot]), Strip(table[(dot + 1)..])) : (null, Strip(table));
        var filter = schema is null ? "" : " AND {0} = @schema";
        // The names go as parameters: never spliced into the text.
        var sections = engine switch
        {
            DatabaseEngine.Postgres => new[]
            {
                ("Columns", "SELECT table_schema, column_name, data_type, is_nullable, column_default FROM information_schema.columns " +
                            "WHERE table_name = @name" + string.Format(CultureInfo.InvariantCulture, filter, "table_schema") + " ORDER BY table_schema, ordinal_position"),
                ("Keys and constraints", "SELECT n.nspname AS schema, con.conname AS name, pg_get_constraintdef(con.oid) AS definition " +
                            "FROM pg_constraint con JOIN pg_class c ON c.oid = con.conrelid JOIN pg_namespace n ON n.oid = c.relnamespace " +
                            "WHERE c.relname = @name" + string.Format(CultureInfo.InvariantCulture, filter, "n.nspname") + " ORDER BY 1, 2"),
                ("Indexes", "SELECT schemaname, indexname, indexdef FROM pg_indexes WHERE tablename = @name" +
                            string.Format(CultureInfo.InvariantCulture, filter, "schemaname") + " ORDER BY 1, 2"),
                ("Definition (a view)", "SELECT table_schema, view_definition FROM information_schema.views WHERE table_name = @name" +
                            string.Format(CultureInfo.InvariantCulture, filter, "table_schema")),
            },
            DatabaseEngine.SqlServer => new[]
            {
                ("Columns", "SELECT TABLE_SCHEMA, COLUMN_NAME, DATA_TYPE, CHARACTER_MAXIMUM_LENGTH, IS_NULLABLE, COLUMN_DEFAULT FROM INFORMATION_SCHEMA.COLUMNS " +
                            "WHERE TABLE_NAME = @name" + string.Format(CultureInfo.InvariantCulture, filter, "TABLE_SCHEMA") + " ORDER BY TABLE_SCHEMA, ORDINAL_POSITION"),
                ("Keys and constraints", "SELECT tc.TABLE_SCHEMA, tc.CONSTRAINT_NAME, tc.CONSTRAINT_TYPE, kcu.COLUMN_NAME, " +
                            "ccu.TABLE_NAME AS REFERENCED_TABLE, ccu.COLUMN_NAME AS REFERENCED_COLUMN " +
                            "FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS tc LEFT JOIN INFORMATION_SCHEMA.KEY_COLUMN_USAGE kcu " +
                            "ON kcu.CONSTRAINT_NAME = tc.CONSTRAINT_NAME AND kcu.TABLE_SCHEMA = tc.TABLE_SCHEMA " +
                            "LEFT JOIN INFORMATION_SCHEMA.REFERENTIAL_CONSTRAINTS rc ON rc.CONSTRAINT_NAME = tc.CONSTRAINT_NAME AND rc.CONSTRAINT_SCHEMA = tc.TABLE_SCHEMA " +
                            "LEFT JOIN INFORMATION_SCHEMA.CONSTRAINT_COLUMN_USAGE ccu ON ccu.CONSTRAINT_NAME = rc.UNIQUE_CONSTRAINT_NAME " +
                            "WHERE tc.TABLE_NAME = @name" + string.Format(CultureInfo.InvariantCulture, filter, "tc.TABLE_SCHEMA") + " ORDER BY 1, 2"),
                ("Indexes", "SELECT s.name AS schema_name, i.name AS index_name, i.type_desc, i.is_unique, c.name AS column_name " +
                            "FROM sys.indexes i JOIN sys.tables t ON t.object_id = i.object_id JOIN sys.schemas s ON s.schema_id = t.schema_id " +
                            "JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id " +
                            "JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id " +
                            "WHERE t.name = @name" + string.Format(CultureInfo.InvariantCulture, filter, "s.name") + " ORDER BY 1, 2, ic.key_ordinal"),
                ("Definition (a view)", "SELECT TABLE_SCHEMA, VIEW_DEFINITION FROM INFORMATION_SCHEMA.VIEWS WHERE TABLE_NAME = @name" +
                            string.Format(CultureInfo.InvariantCulture, filter, "TABLE_SCHEMA")),
            },
            _ => new[]
            {
                ("Columns", "SELECT COLUMN_NAME, COLUMN_TYPE, IS_NULLABLE, COLUMN_KEY, COLUMN_DEFAULT, EXTRA FROM information_schema.COLUMNS " +
                            "WHERE TABLE_NAME = @name AND TABLE_SCHEMA = " + (schema is null ? "DATABASE()" : "@schema") + " ORDER BY ORDINAL_POSITION"),
                ("Keys and constraints", "SELECT CONSTRAINT_NAME, COLUMN_NAME, REFERENCED_TABLE_NAME, REFERENCED_COLUMN_NAME FROM information_schema.KEY_COLUMN_USAGE " +
                            "WHERE TABLE_NAME = @name AND TABLE_SCHEMA = " + (schema is null ? "DATABASE()" : "@schema") + " ORDER BY 1, ORDINAL_POSITION"),
                ("Indexes", "SELECT INDEX_NAME, NON_UNIQUE, COLUMN_NAME, INDEX_TYPE FROM information_schema.STATISTICS " +
                            "WHERE TABLE_NAME = @name AND TABLE_SCHEMA = " + (schema is null ? "DATABASE()" : "@schema") + " ORDER BY INDEX_NAME, SEQ_IN_INDEX"),
                ("Definition (a view)", "SELECT VIEW_DEFINITION FROM information_schema.VIEWS " +
                            "WHERE TABLE_NAME = @name AND TABLE_SCHEMA = " + (schema is null ? "DATABASE()" : "@schema")),
            },
        };
        var sb = new StringBuilder();
        var found = false;
        foreach (var (title, sql) in sections)
        {
            var part = await ReadAsync(connection, engine, sql, (name, schema), ct, emptyNote: "(none)");
            if (title == "Columns") found = !part.Contains("(none)", StringComparison.Ordinal);
            if (title.StartsWith("Definition", StringComparison.Ordinal) && part.Contains("(none)", StringComparison.Ordinal)) continue;
            sb.Append("## ").Append(title).Append('\n').Append(part).Append("\n\n");
        }
        return found ? sb.ToString().TrimEnd() : $"(no table or view named {table} — list them with action tables)";
    }

    /// <summary>The rows of <paramref name="sql"/>, one read already let through by <see cref="SqlGuard"/>.</summary>
    /// <remarks>Its last line counts the rows shown — "(3 rows)", or the line that says more were left: the run's step
    /// reads it there (<c>RunSummary.QueryRows</c>), and never parses the rows.</remarks>
    public static Task<string> QueryAsync(DbConnection connection, DatabaseEngine engine, string sql, CancellationToken ct) =>
        ReadAsync(connection, engine, sql, parameter: null, ct, countRows: true);

    private static async Task<string> ReadAsync(DbConnection connection, DatabaseEngine engine, string sql, (string Name, string? Schema)? parameter,
                                                CancellationToken ct, string? emptyNote = null, bool countRows = false)
    {
        DbTransaction? transaction = null;
        try
        {
            if (engine == DatabaseEngine.SqlServer) transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
            else
            {
                if (engine == DatabaseEngine.MySql)
                {
                    await ReadTextAsTheAnalysisDoesAsync(connection, ct);
                    await ExecAsync(connection, "SET SESSION max_statement_time = " + QuerySeconds, ct, optional: true);
                }
                await ExecAsync(connection, "START TRANSACTION READ ONLY", ct);
                if (engine == DatabaseEngine.Postgres)
                {
                    await ExecAsync(connection, "SET LOCAL standard_conforming_strings = on", ct);
                    await ExecAsync(connection, $"SET LOCAL statement_timeout = '{QuerySeconds}s'", ct);
                }
            }
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.CommandTimeout = QuerySeconds;
            command.Transaction = transaction;
            if (parameter is { } p)
            {
                Add(command, "@name", p.Name);
                if (p.Schema is not null) Add(command, "@schema", p.Schema);
            }
            var sb = new StringBuilder();
            int shown = 0, cut = 0, results = 0, read = 0;
            var more = false;
            string? plan = null;
            // Only an EXPLAIN is read as a plan: a column of saved plans (pg_store_plans, a log table) stays its rows.
            var explain = sql.TrimStart().StartsWith("EXPLAIN", StringComparison.OrdinalIgnoreCase);
            var reader = await command.ExecuteReaderAsync(ct);
            try
            {
                do
                {
                    if (reader.FieldCount == 0) continue;
                    if (results++ > 0) sb.AppendLine();
                    sb.AppendLine(string.Join(",", Enumerable.Range(0, reader.FieldCount).Select(i => Quoted(reader.GetName(i)))));
                    var rowsHere = 0;
                    while (await reader.ReadAsync(ct))
                    {
                        // A plan in one cell (EXPLAIN (FORMAT JSON), EXPLAIN FORMAT=JSON): read whole, as the plan it is —
                        // a cell is otherwise cut at MaxCellChars, and a plan's JSON is no table.
                        if (explain && results == 1 && rowsHere == 0 && reader.FieldCount == 1 && !reader.IsDBNull(0)
                            && Documents.SqlPlanText.FromQueryResult(Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture) ?? "",
                                                                     Tools.ToolOutputBudget.ListChars) is { } read0)
                        {
                            plan = read0;
                            break;
                        }
                        if (++read > MaxRowsRead || sb.Length > Tools.ToolOutputBudget.ListChars)
                        {
                            more = true;
                            break;
                        }
                        sb.AppendLine(string.Join(",", Enumerable.Range(0, reader.FieldCount).Select(i => Cell(reader, i, ref cut))));
                        shown++;
                        rowsHere++;
                    }
                    if (more || plan is not null) break;
                    if (rowsHere == 0) sb.AppendLine(emptyNote ?? "(no rows)");
                } while (await reader.NextResultAsync(ct));
            }
            finally
            {
                // ⚠ Past the cap, the query is stopped where it is: closing the reader would otherwise read every row left,
                // a large table's until the statement timeout, and lose the rows shown to an error. The error the stop itself
                // raises on closing is the stop's, never the query's.
                if (more) Cancel(command);
                try { await reader.DisposeAsync(); }
                catch (DbException ex) when (more) { Diagnostics.Swallow("DatabaseReader.Stopped", ex); }
            }
            if (plan is not null) return plan;
            if (results == 0) return "The query returned no columns.";
            if (cut > 0) sb.AppendLine($"({cut} cell(s) cut at {MaxCellChars} characters, marked …)");
            if (more) sb.AppendLine($"(more rows than the {shown} shown, all this answer holds: aggregate, filter, or page with a limit and an offset)");
            else if (countRows) sb.AppendLine(shown == 1 ? "(1 row)" : $"({shown} rows)");
            return sb.ToString().TrimEnd();
        }
        catch (DbException) when (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException(ct);
        }
        finally
        {
            // Never kept, whatever happened: the transaction is undone before the connection closes.
            try
            {
                if (transaction is not null) await transaction.RollbackAsync(CancellationToken.None);
                else if (connection.State == ConnectionState.Open) await ExecAsync(connection, "ROLLBACK", CancellationToken.None);
            }
            catch (Exception ex) when (ex is DbException or InvalidOperationException) { /* the connection broke: nothing kept */ }
            if (transaction is not null) await transaction.DisposeAsync();
        }
    }

    /// <summary>Sets the MySQL session's modes to the person's, without those that change how text is read, then reads them
    /// back: a mode that would not go (a server that refuses the setting) stops the read.</summary>
    private static async Task ReadTextAsTheAnalysisDoesAsync(DbConnection connection, CancellationToken ct)
    {
        await using var read = connection.CreateCommand();
        read.CommandText = "SELECT @@SESSION.sql_mode";
        var modes = Convert.ToString(await read.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture) ?? string.Empty;
        var kept = string.Join(",", modes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(m => !ForeignModes.Contains(m)));
        await using (var set = connection.CreateCommand())
        {
            set.CommandText = "SET SESSION sql_mode = @modes";
            Add(set, "@modes", kept);
            await set.ExecuteNonQueryAsync(ct);
        }
        var now = Convert.ToString(await read.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture) ?? string.Empty;
        if (now.Split(',', StringSplitOptions.TrimEntries).Any(m => m is "ANSI_QUOTES" or "NO_BACKSLASH_ESCAPES"))
            throw new InvalidOperationException("the session still reads text otherwise than Inferpal (ANSI_QUOTES or NO_BACKSLASH_ESCAPES): nothing was run");
    }

    private static void Cancel(DbCommand command)
    {
        try { command.Cancel(); }
        catch (Exception ex) when (ex is DbException or InvalidOperationException or NotSupportedException) { Diagnostics.Swallow("DatabaseReader.Cancel", ex); }
    }

    private static void Add(DbCommand command, string name, string value)
    {
        var p = command.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        command.Parameters.Add(p);
    }

    private static async Task ExecAsync(DbConnection connection, string sql, CancellationToken ct, bool optional = false)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        try { await command.ExecuteNonQueryAsync(ct); }
        // MySQL has no max_statement_time (MariaDB's): the command's own timeout still holds.
        catch (DbException) when (optional) { }
    }

    private static string Quoted(string text) =>
        text.Contains(',') || text.Contains('"') || text.Contains('\n') || text.Contains('\r') ? "\"" + text.Replace("\"", "\"\"") + "\"" : text;

    private static string Cell(DbDataReader reader, int i, ref int cut)
    {
        if (reader.IsDBNull(i)) return "NULL";
        var value = reader.GetValue(i);
        if (value is byte[] bytes) return $"<{bytes.Length} bytes>";
        if (value is string { } literal && literal == "NULL") return "\"NULL\"";
        var text = value switch
        {
            double d => d.ToString(CultureInfo.InvariantCulture),
            float f => f.ToString(CultureInfo.InvariantCulture),
            decimal m => m.ToString(CultureInfo.InvariantCulture),
            DateTime t => t.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture).TrimEnd('.'),
            DateTimeOffset o => o.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFFzzz", CultureInfo.InvariantCulture),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
        };
        if (text.Length > MaxCellChars) { text = text[..MaxCellChars] + "…"; cut++; }
        return Quoted(text);
    }
}
