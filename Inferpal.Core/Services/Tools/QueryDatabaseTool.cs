using System.Data.Common;
using System.Text;
using System.Text.Json;
using Inferpal.Services.Databases;

namespace Inferpal.Services.Tools;

/// <summary>
/// <c>query_database</c>: reads a development database the project declares — its tables, a table's structure, the rows of
/// any one read (SELECT, SHOW, DESCRIBE, EXPLAIN, SQL Server's reading procedures) — read-only, after the person agrees
/// (pari 115).
/// </summary>
/// <remarks>
/// ⚠ Inferpal stores no connection: the agent names where the project declares it (<see cref="DatabaseSources"/>).
/// ⚠ Three locks hold the read: the analysis (<see cref="SqlGuard"/>, a refusal before any card), the transaction that keeps
/// nothing (<see cref="DatabaseReader"/>), and an account that can only read, which the card says when the engine shows it.
/// ⚠ A server elsewhere is never reached before the person's answer: a connection sends credentials — a repository's
/// <c>.env</c> could name a server of its own with <c>Integrated Security</c>, and the person's Windows login would go
/// there unasked. So: on this machine, connect, read the account's rights, then ask ("Always" for that database); anywhere
/// else, ask first, every time unless one of the person's own rules allows that whole subject. ⚠ No connection is held
/// while the card waits (a card may wait for hours, and a server closes an idle connection): read the rights, close, ask,
/// open again.
/// </remarks>
internal sealed class QueryDatabaseTool(IApprovalService approval, Func<string?> root) : ITool
{
    public string Name => "query_database";

    /// <inheritdoc/>
    /// <remarks>It reads, through a network connection (a local server is a socket too); nothing it runs writes.</remarks>
    public ToolEffect EffectOf(JsonElement args) => ToolEffect.Reads | ToolEffect.Network;

    public string Description =>
        "Reads a development database the project declares (PostgreSQL, SQL Server, MySQL or MariaDB), read-only: its tables, " +
        "a table's structure, or the rows of one read. connection names where the project declares it, never the connection " +
        "string itself. No write, no DDL, no second statement, nothing outside the database. The person approves the first " +
        "read of each database.";

    public object Parameters => new
    {
        type = "object",
        properties = new
        {
            connection = new { type = "string", description = "Where the project declares the connection: <file>#<key> (appsettings.Development.json#ConnectionStrings:Default, .env#DATABASE_URL), env:<NAME>, or secrets:<project file>#<key> (user secrets)." },
            action     = new { type = "string", description = "tables (the default without sql), describe (a table: columns, keys, indexes), or query (with sql)." },
            table      = new { type = "string", description = "describe: the table or view, as schema.table or table." },
            sql        = new { type = "string", description = "query: one read — SELECT, WITH … SELECT, EXPLAIN without ANALYZE, SHOW, DESCRIBE, or EXEC sp_help 'name' (SQL Server). EXPLAIN (FORMAT JSON) on PostgreSQL, EXPLAIN FORMAT=JSON on MySQL or MariaDB: the plan read as its costliest operators." },
            engine     = new { type = "string", description = "postgres, sqlserver or mysql — only when the connection string's form does not say it." },
        },
        required = new[] { "connection" },
    };

    public async Task<string> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var reference = args.Str("connection") ?? throw new ArgumentException("connection is required.");
        var sql = args.Str("sql");
        var table = args.Str("table");
        var action = args.Keyword("action") ?? (sql is not null ? "query" : table is not null ? "describe" : "tables");
        if (action == "columns") action = "describe";
        if (action is not ("tables" or "describe" or "query"))
            return $"Error: unknown action '{action}' (expected tables, describe or query).";
        DatabaseEngine? engine = null;
        if (args.Keyword("engine") is { } word && (engine = DatabaseTargets.EngineOf(word)) is null)
            return $"Error: unknown engine '{word}' (expected postgres, sqlserver or mysql).";
        if (action == "query" && string.IsNullOrWhiteSpace(sql)) return "Error: action query needs sql (one read).";
        if (action == "describe" && string.IsNullOrWhiteSpace(table)) return "Error: action describe needs table.";

        var (source, notFound) = await DatabaseSources.ResolveAsync(reference, root(), ct);
        if (source is null) return "Error: " + notFound;
        var (target, unreadable) = DatabaseTargets.Of(source.ConnectionString, engine);
        if (target is null) return $"Error: {source.Shown}: {unreadable}";
        // Refused before any card: there is nothing to approve.
        if (action == "query" && SqlGuard.Refusal(sql!, target.Engine) is { } refused)
            return $"Error: Inferpal only reads a database, and this query is refused: {refused}. Send one read " +
                   "(SELECT, WITH … SELECT, EXPLAIN without ANALYZE, SHOW, DESCRIBE, or a reading procedure such as sp_help).";

        var subject = Subject(target, source);
        DbConnection? connection = null;
        try
        {
            var rights = (AccountRights?)null;
            if (target.OnThisMachine)
            {
                connection = await DatabaseReader.Open(target, ct);
                rights = await DatabaseReader.RightsAsync(connection, target.Engine, ct);
                await connection.DisposeAsync();
                connection = null;
            }
            var details = Details(target, source, rights, action, table, sql);
            var approved = target.OnThisMachine
                ? await approval.RequestScopedApprovalAsync(Name, details, subject, grantKey: $"{Name} {GrantOf(target)}", ct)
                // Elsewhere: a card every time, unless the person's own rule names this whole subject.
                : approval.Allows(Name, subject)
                    ? await approval.RequestScopedApprovalAsync(Name, details, subject, grantKey: $"{Name} elsewhere {GrantOf(target)}", ct)
                    : await approval.RequestApprovalAsync(Name, details, ct, subject: subject, forcePrompt: true);
            if (!approved) return Execution.ToolFailure.CancelledByUser;

            connection = await DatabaseReader.Open(target, ct);
            var result = action switch
            {
                "tables"   => await DatabaseReader.TablesAsync(connection, target.Engine, ct),
                "describe" => await DatabaseReader.DescribeAsync(connection, target.Engine, table!, ct),
                _          => await DatabaseReader.QueryAsync(connection, target.Engine, sql!, ct),
            };
            var head = $"{target.EngineName} database {target.Label} on {target.Server} (from {source.Shown}):";
            return head + "\n" + result;
        }
        catch (Exception ex) when (ex is NotSupportedException && ex.Message.Contains("Globalization Invariant", StringComparison.OrdinalIgnoreCase))
        {
            return "Error: SQL Server cannot be reached from this machine without its ICU library (libicu): install it, then " +
                   "restart the editor. PostgreSQL and MySQL need nothing more.";
        }
        catch (Exception ex) when (ex is DbException or TimeoutException or InvalidOperationException or System.Net.Sockets.SocketException
                                   or System.IO.IOException or System.Security.Authentication.AuthenticationException)
        {
            if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
            // The driver's own words: they name the server, the account or the object, never the password.
            return connection is null
                ? $"Error: Inferpal could not connect to {target.EngineName} on {target.Server} (from {source.Shown}): {Diagnostics.RootMessage(ex)}"
                : $"Error: {target.EngineName} database {target.Label} (from {source.Shown}) answered: {Diagnostics.RootMessage(ex)}";
        }
        finally
        {
            if (connection is not null) await connection.DisposeAsync();
        }
    }

    /// <summary>What the permission rules read: the engine, the server and database, and where the project declares it —
    /// a rule like <c>deny query_database prod</c> refuses a server by its name.</summary>
    internal static string Subject(DatabaseTarget target, DatabaseSource source) =>
        $"{EngineWord(target.Engine)} {target.Server}/{target.Database} {source.Reference}";

    /// <summary>What an "Always" opens: this database, through this account — another account reaching it asks again.</summary>
    private static string GrantOf(DatabaseTarget target) => $"{EngineWord(target.Engine)} {target.Server}/{target.Database} as {target.Account}";

    internal static string EngineWord(DatabaseEngine engine) => engine switch
    {
        DatabaseEngine.Postgres  => "postgres",
        DatabaseEngine.SqlServer => "sqlserver",
        _                        => "mysql",
    };

    /// <summary>
    /// The card's details, one <c>key: value</c> per line then the query: read back by the card
    /// (<see cref="Presentation.ApprovalCard"/>), and shown as written where no card is drawn.
    /// </summary>
    /// <param name="rights"><c>null</c>: not read yet (a server elsewhere is reached only after the answer).</param>
    /// <remarks>⚠ Each value is held to one line: a table name the model writes, or a database name a repository's settings
    /// hold, with a line break would add a fact of its own (<c>where: this machine</c>) and the card would hide its warning.</remarks>
    internal static string Details(DatabaseTarget target, DatabaseSource source, AccountRights? rights, string action, string? table, string? sql)
    {
        static string Line(string value) => value.Replace('\r', ' ').Replace('\n', ' ');
        var sb = new StringBuilder()
            .Append("database: ").Append(Line(target.Label)).Append('\n')
            .Append("found in: ").Append(Line(source.Shown)).Append('\n')
            .Append("engine: ").Append(target.EngineName).Append('\n')
            .Append("server: ").Append(Line(target.Server)).Append('\n')
            .Append("account: ").Append(target.WindowsAccount ? "windows " : string.Empty).Append(Line(target.Account)).Append('\n')
            .Append("rights: ").Append(rights switch
            {
                AccountRights.ReadOnly => "read-only",
                AccountRights.CanWrite => "can write",
                AccountRights.Unknown  => "unknown",
                _                      => "not read yet",
            }).Append('\n')
            .Append("where: ").Append(target.OnThisMachine ? "this machine" : "elsewhere").Append('\n')
            .Append("asks: ").Append(action == "describe" ? "describe " + Line(table ?? string.Empty) : action);
        if (action == "query") sb.Append("\n\n").Append(sql!.Trim());
        return sb.ToString();
    }
}
