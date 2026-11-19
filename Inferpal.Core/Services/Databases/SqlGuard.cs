using System.Text;
using System.Text.RegularExpressions;

namespace Inferpal.Services.Databases;

/// <summary>
/// The first of <c>query_database</c>'s read-only locks: the query read in its engine's dialect — comments, strings and
/// quoted names set aside — then held to one statement that reads the database: <c>SELECT</c>, <c>WITH</c>,
/// <c>EXPLAIN</c>, <c>SHOW</c> (MySQL, PostgreSQL), <c>DESCRIBE</c> (MySQL), and SQL Server's system procedures that only
/// read (<c>sp_help</c>, <c>sp_helptext</c>…), called alone with literal arguments. Nothing that acts outside a transaction
/// passes. The second lock is the transaction it runs in (<see cref="DatabaseReader"/>), the third an account that can only
/// read.
/// </summary>
/// <remarks>
/// ⚠ The analysis is what stops what a transaction cannot: <c>COPY … TO PROGRAM</c> and <c>pg_read_file</c> read, so a
/// read-only transaction lets them run; <c>dblink</c> writes through another connection; <c>xp_cmdshell</c> runs a program.
/// ⚠ Each dialect reads its own way: MySQL takes <c>--</c> as a comment only before a space (<c>1--1</c> is 1 - -1) and
/// runs what <c>/*! … */</c> holds; PostgreSQL keeps a backslash in a standard string (<c>'a\'</c> ends there) but not in
/// <c>E''</c>; T-SQL needs no <c>;</c> between two statements, so a write anywhere in the text is a second statement — and a
/// <c>COMMIT</c> there would keep the transaction the reader means to undo, the statements after it running on their own.
/// ⚠ What reads OUTSIDE the database stays refused: the server's files (<c>pg_read_file</c>, <c>LOAD_FILE</c>,
/// <c>OPENROWSET(BULK)</c>) and other servers (<c>dblink</c>, <c>OPENQUERY</c>).
/// </remarks>
internal static class SqlGuard
{
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(1);

    // Whole words, outside comments, strings and quoted names.
    private static readonly HashSet<string> Denied = new(StringComparer.OrdinalIgnoreCase)
    {
        "INSERT", "UPDATE", "DELETE", "MERGE", "UPSERT", "CREATE", "ALTER", "DROP", "TRUNCATE", "RENAME", "COMMENT",
        "GRANT", "REVOKE", "EXEC", "EXECUTE", "CALL", "DO", "COPY", "INTO", "LOCK", "UNLOCK", "VACUUM", "ANALYZE", "REINDEX",
        "CLUSTER", "REFRESH", "LOAD", "HANDLER", "NOTIFY", "LISTEN", "UNLISTEN", "PREPARE", "DEALLOCATE", "IMPORT", "INSTALL",
        "UNINSTALL", "WAITFOR", "SHUTDOWN", "KILL", "BACKUP", "RESTORE", "DBCC", "BULK", "RECONFIGURE", "OPENROWSET",
        "OPENQUERY", "OPENDATASOURCE", "CHECKPOINT", "DISCARD", "RESET", "SECURITY", "LOGIN", "USER", "ROLE",
        // T-SQL's transaction and permission words: with no ';' needed, each would end the read and act on its own.
        "COMMIT", "ROLLBACK", "SAVE", "BEGIN", "DENY", "DISABLE", "ENABLE", "WRITETEXT", "UPDATETEXT",
    };

    // SQL Server's system procedures that only read the database's description.
    private static readonly HashSet<string> ReadingProcedures = new(StringComparer.OrdinalIgnoreCase)
    {
        "sp_help", "sp_helptext", "sp_helpindex", "sp_helpconstraint", "sp_columns", "sp_tables", "sp_pkeys", "sp_fkeys",
        "sp_stored_procedures", "sp_statistics", "sp_sproc_columns", "sp_helpdb", "sp_depends", "sp_server_info",
    };

    // Functions that act outside the transaction, or read the server's own files, whatever the account.
    private static readonly Regex DeniedFunction = new(
        @"(?<![A-Za-z_$])(pg_read_file|pg_read_binary_file|pg_ls_\w+|pg_stat_file|pg_file_\w+|pg_terminate_backend|pg_cancel_backend|" +
        @"pg_reload_conf|pg_rotate_logfile|pg_switch_wal|pg_create_\w+|pg_drop_\w+|pg_promote|pg_logical_\w+|pg_advisory_\w+|" +
        @"pg_try_advisory_\w+|pg_stat_reset\w*|pg_stat_statements_reset|pg_notify|pg_start_backup|pg_stop_backup|pg_backup_\w+|" +
        @"pg_wal_\w+|pg_replication_\w+|pg_log_\w+|lo_\w+|setval|nextval|set_config|dblink\w*|query_to_xml\w*|cursor_to_xml\w*|" +
        @"load_file|get_lock|release_lock|release_all_locks|sys_exec|sys_eval|xp_\w+|sp_\w+)\s*\(",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, MatchTimeout);

    private static readonly Regex Word = new(@"[A-Za-z_][A-Za-z0-9_$]*", RegexOptions.CultureInvariant, MatchTimeout);

    // A keyword glued to a number (1e0INTO, 0xINSERT) is a keyword to the server.
    private static readonly Regex Glued = new(@"(?<=[0-9])([A-Za-z_]+)", RegexOptions.CultureInvariant, MatchTimeout);

    // PostgreSQL's dollar-quote tag: an identifier, non-ASCII letters included ($é$), never a dollar.
    private static readonly Regex DollarTag = new(@"\G\$([\p{L}_\u0080-\uFFFF][\p{L}\p{N}_\u0080-\uFFFF]*)?\$", RegexOptions.CultureInvariant, MatchTimeout);

    // A procedure call as the whole statement: EXEC[UTE] [sys.]sp_name, then its arguments. ⚠ Never another schema's
    // (dbo.sp_help, [app].sp_help): that is a procedure of the database's own, whatever its name.
    private static readonly Regex ProcedureCall = new(
        @"^\s*(?:EXEC(?:UTE)?\s+)?(?:sys\s*\.\s*)?(?<name>sp_\w+)\b(?<args>.*)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline, MatchTimeout);

    // Literal arguments only, comma-separated, each maybe named (@objname = 'orders'): a string, a number, NULL, DEFAULT or a
    // name. A second statement after them would not be an argument.
    private const string Argument = @"(?:@\w+\s*=\s*)?(?:N?\s*'S'|-?\d+(?:\.\d+)?|NULL|DEFAULT|name|[A-Za-z_][\w.]*)";
    private static readonly Regex LiteralArguments = new($@"^\s*(?:{Argument}(?:\s*,\s*{Argument})*)?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, MatchTimeout);

    /// <summary>Why <paramref name="sql"/> may not run, or <c>null</c> when it is one read.</summary>
    /// <remarks>Fails closed: text it cannot read (an unterminated string, a pattern past its time) is refused.</remarks>
    public static string? Refusal(string sql, DatabaseEngine engine)
    {
        try
        {
            var (code, why) = CodeOnly(sql, engine);
            if (why is not null) return why;
            var statements = code.Split(';').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
            if (statements.Count == 0) return "the query is empty";
            if (statements.Count > 1) return "it holds more than one statement";
            var statement = statements[0];
            var words = Words(statement);
            var first = words.FirstOrDefault()?.ToUpperInvariant();
            if (engine == DatabaseEngine.SqlServer && (first is "EXEC" or "EXECUTE" || first?.StartsWith("SP_", StringComparison.Ordinal) == true))
                return ProcedureRefusal(statement);
            // SHOW only reads (SHOW CREATE TABLE, SHOW GRANTS: their words are not acts) — but its WHERE takes any
            // expression, a call that reads the server's files included.
            if (first == "SHOW" && engine != DatabaseEngine.SqlServer)
            {
                if (words.Any(w => w.Equals("WHERE", StringComparison.OrdinalIgnoreCase))) return "SHOW … WHERE takes any expression: read the catalog with SELECT instead";
                return DeniedFunction.Match(statement) is { Success: true } called ? $"it calls {called.Groups[1].Value}, which acts beyond a read" : null;
            }
            if (first is not ("SELECT" or "WITH" or "EXPLAIN") && !(engine == DatabaseEngine.MySql && first is "DESCRIBE" or "DESC"))
                return $"it is not a read: it starts with {first ?? "no keyword"}";
            if (words.FirstOrDefault(Denied.Contains) is { } denied) return $"it contains {denied.ToUpperInvariant()}";
            if (DeniedFunction.Match(statement) is { Success: true } f) return $"it calls {f.Groups[1].Value}, which acts beyond a read";
            return null;
        }
        catch (RegexMatchTimeoutException)
        {
            return "it could not be read in time";
        }
    }

    /// <summary>A SQL Server procedure call: one of the procedures that only read, alone, with literal arguments.</summary>
    private static string? ProcedureRefusal(string statement)
    {
        if (ProcedureCall.Match(statement) is not { Success: true } call) return "it runs a procedure: only the system procedures that read are allowed (sp_help, sp_helptext…)";
        var name = call.Groups["name"].Value;
        if (!ReadingProcedures.Contains(name)) return $"it runs {name}, which is not one of the system procedures that only read";
        var args = call.Groups["args"].Value;
        if (!LiteralArguments.IsMatch(args)) return $"{name} is given more than literal values";
        return Words(args).FirstOrDefault(Denied.Contains) is { } denied
            ? $"it contains {denied.ToUpperInvariant()}"
            : null;
    }

    /// <summary>The words of <paramref name="code"/>, and those glued to a number (<c>1e0INTO</c> → <c>INTO</c>,
    /// <c>0xINSERT</c> → <c>INSERT</c>).</summary>
    private static List<string> Words(string code)
    {
        var words = Word.Matches(code).Select(m => m.Value).ToList();
        foreach (Match m in Glued.Matches(code))
        {
            words.Add(m.Groups[1].Value);
            if (m.Groups[1].Value.Length > 1) words.Add(m.Groups[1].Value[1..]);
        }
        return words;
    }

    /// <summary>The query with every comment, string and quoted name replaced by a placeholder, or why it cannot be read.</summary>
    internal static (string Code, string? Why) CodeOnly(string sql, DatabaseEngine d)
    {
        var code = new StringBuilder(sql.Length);
        var i = 0;
        while (i < sql.Length)
        {
            var c = sql[i];
            var next = i + 1 < sql.Length ? sql[i + 1] : '\0';
            if (c == '-' && next == '-' && (d != DatabaseEngine.MySql || i + 2 >= sql.Length || sql[i + 2] <= ' ')
                || c == '#' && d == DatabaseEngine.MySql)
            {
                // ⚠ A line comment ends at \r as at \n (PostgreSQL's lexer): read longer, what follows a lone \r would be
                // taken for the comment while the server runs it.
                while (i < sql.Length && sql[i] != '\n' && sql[i] != '\r') i++;
                code.Append(' ');
                continue;
            }
            if (c == '/' && next == '*')
            {
                if (d == DatabaseEngine.MySql && i + 2 < sql.Length && (sql[i + 2] == '!' || sql[i + 2] == 'M' && i + 3 < sql.Length && sql[i + 3] == '!'))
                    return ("", "it holds a MySQL executable comment (/*! */), which runs as code");
                // PostgreSQL and SQL Server nest comments; MySQL does not.
                var depth = 0;
                while (i < sql.Length)
                {
                    if (i + 1 < sql.Length && sql[i] == '/' && sql[i + 1] == '*')
                    {
                        depth = d == DatabaseEngine.MySql ? 1 : depth + 1;
                        i += 2;
                        continue;
                    }
                    if (i + 1 < sql.Length && sql[i] == '*' && sql[i + 1] == '/')
                    {
                        depth--;
                        i += 2;
                        if (depth == 0) break;
                        continue;
                    }
                    i++;
                }
                if (depth != 0) return ("", "it holds an unterminated comment");
                code.Append(' ');
                continue;
            }
            if (c == '$' && d == DatabaseEngine.Postgres && (i == 0 || !char.IsLetterOrDigit(sql[i - 1]) && sql[i - 1] != '_')
                && DollarTag.Match(sql, i) is { Success: true } tag)
            {
                var end = sql.IndexOf(tag.Value, i + tag.Length, StringComparison.Ordinal);
                if (end < 0) return ("", "it holds an unterminated dollar-quoted string");
                i = end + tag.Length;
                code.Append(" 'S' ");
                continue;
            }
            if (c == '\'' || c == '"' && d == DatabaseEngine.MySql)
            {
                // Backslash escapes: MySQL's strings, and PostgreSQL's E'' strings only.
                var escapes = d == DatabaseEngine.MySql
                              || d == DatabaseEngine.Postgres && i > 0 && (sql[i - 1] == 'E' || sql[i - 1] == 'e')
                                 && (i < 2 || !char.IsLetterOrDigit(sql[i - 2]) && sql[i - 2] != '_');
                if (!Skip(sql, ref i, c, escapes)) return ("", "it holds an unterminated string");
                code.Append(" 'S' ");
                continue;
            }
            var close = c switch
            {
                '"' when d != DatabaseEngine.MySql => '"',
                '[' when d == DatabaseEngine.SqlServer => ']',
                '`' when d == DatabaseEngine.MySql => '`',
                _ => '\0',
            };
            if (close != '\0')
            {
                var start = i;
                if (!Skip(sql, ref i, close, escapes: false)) return ("", "it holds an unterminated quoted name");
                // ⚠ A quoted name called as a function ("pg_read_file"(…)) is that function: written out, for the checks to
                // read; any other quoted name is a placeholder, so a column named "user" is no keyword.
                var after = i;
                while (after < sql.Length && char.IsWhiteSpace(sql[after])) after++;
                code.Append(after < sql.Length && sql[after] == '('
                    ? " " + sql[(start + 1)..(i - 1)].Replace(new string(close, 2), close.ToString()) + " "
                    : " name ");
                continue;
            }
            code.Append(c);
            i++;
        }
        return (code.ToString(), null);
    }

    /// <summary>Moves <paramref name="i"/> past the quoted text that opens there; a doubled closing character is part of it.</summary>
    private static bool Skip(string sql, ref int i, char close, bool escapes)
    {
        i++;
        while (i < sql.Length)
        {
            if (escapes && sql[i] == '\\') { i += 2; continue; }
            if (sql[i] == close)
            {
                if (i + 1 < sql.Length && sql[i + 1] == close) { i += 2; continue; }
                i++;
                return true;
            }
            i++;
        }
        return false;
    }
}
