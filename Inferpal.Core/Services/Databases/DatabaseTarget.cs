using System.Data.Common;

namespace Inferpal.Services.Databases;

/// <summary>The three engines <c>query_database</c> reads.</summary>
internal enum DatabaseEngine { Postgres, SqlServer, MySql }

/// <summary>What the account a connection uses may do, as far as the engine shows it.</summary>
internal enum AccountRights { ReadOnly, CanWrite, Unknown }

/// <summary>A database to read: its engine, the driver's connection string, and what a card says of it — never the password.</summary>
/// <param name="ConnectionString">For the driver only: holds the password.</param>
/// <param name="Server">Host and port, an instance, or a local socket, as the person reads it.</param>
/// <param name="OnThisMachine">The server is this machine (a loopback name, LocalDB, a local socket): Inferpal may connect
/// before asking; anywhere else it asks first.</param>
internal sealed record DatabaseTarget(DatabaseEngine Engine, string ConnectionString, string Server, string Database, string Account,
                                      bool WindowsAccount, bool OnThisMachine)
{
    /// <summary>The engine's name for a person.</summary>
    public string EngineName => Engine switch
    {
        DatabaseEngine.Postgres  => "PostgreSQL",
        DatabaseEngine.SqlServer => "SQL Server",
        _                        => "MySQL / MariaDB",
    };

    /// <summary>How the card and the steps name the database.</summary>
    public string Label => Database.Length > 0 ? Database : Server;
}

/// <summary>Reads a connection string: its engine — from its form, or the word the agent gave — and what a card shows.</summary>
internal static class DatabaseTargets
{
    private const int ConnectSeconds = 10;

    /// <summary>The engine a word names (<c>postgres</c>, <c>sqlserver</c>, <c>mysql</c> and their usual spellings), or <c>null</c>.</summary>
    public static DatabaseEngine? EngineOf(string? word) => word?.Trim().ToLowerInvariant() switch
    {
        "postgres" or "postgresql" or "pg" or "npgsql" => DatabaseEngine.Postgres,
        "sqlserver" or "sql server" or "mssql" or "sql_server" or "azuresql" => DatabaseEngine.SqlServer,
        "mysql" or "mariadb" => DatabaseEngine.MySql,
        _ => null,
    };

    /// <summary>The database <paramref name="connection"/> opens, or why it cannot be read (said to the model).</summary>
    /// <param name="engine">The engine the agent named, or <c>null</c>: then the string's form must say it.</param>
    public static (DatabaseTarget? Target, string? Refusal) Of(string connection, DatabaseEngine? engine)
    {
        try
        {
            var text = connection.Trim();
            if (text.StartsWith("jdbc:", StringComparison.OrdinalIgnoreCase)) text = text[5..];
            if (Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme.Length > 1 && text.Contains("://", StringComparison.Ordinal))
                return FromUrl(uri, engine);
            var builder = new DbConnectionStringBuilder { ConnectionString = text };
            // ⚠ A file on another machine (\\host\share\…) in any key — a certificate, a password file, a database to attach
            // — is reached by the driver itself, the Windows login going with it, before any card.
            foreach (string key in builder.Keys)
                if (Convert.ToString(builder[key]) is { } value && (value.StartsWith(@"\\", StringComparison.Ordinal) || value.StartsWith("//", StringComparison.Ordinal)))
                    return (null, $"its key {key} names a file on another machine: Inferpal does not connect through it.");
            var found = engine ?? Guess(builder);
            if (found is null)
                return (null, "Inferpal cannot tell this connection's engine from its keys: say it with engine (postgres, sqlserver or mysql).");
            return Build(found.Value, text);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or KeyNotFoundException or InvalidOperationException)
        {
            // ⚠ Never the driver's words: they quote what they could not read, and a value read from an environment variable
            // or a key the agent named may be a secret of its own.
            return (null, "this value does not read as a connection string (a key the driver does not take, or a value it cannot read): " +
                          "check what the place holds, or name the engine with engine.");
        }
    }

    /// <summary>The engine a key/value string's own keys and port say, when only one does.</summary>
    internal static DatabaseEngine? Guess(DbConnectionStringBuilder b)
    {
        bool Has(params string[] keys) => keys.Any(k => b.ContainsKey(k));
        string Value(params string[] keys) => keys.Where(b.ContainsKey).Select(k => Convert.ToString(b[k]) ?? "").FirstOrDefault() ?? "";
        var server = Value("server", "data source", "host", "address", "addr", "network address").Trim().ToLowerInvariant();
        var port = Value("port");
        var engines = new HashSet<DatabaseEngine>();
        if (Has("data source", "initial catalog", "integrated security", "trusted_connection", "trustservercertificate",
                "multipleactiveresultsets", "multisubnetfailover", "attachdbfilename", "encrypt", "applicationintent")
            || server.StartsWith("(localdb)", StringComparison.Ordinal) || server.StartsWith("tcp:", StringComparison.Ordinal)
            || server.StartsWith("np:", StringComparison.Ordinal) || server.Contains('\\') || server.Contains(',') || port == "1433")
            engines.Add(DatabaseEngine.SqlServer);
        if (Has("search path", "searchpath", "ssl mode", "include error detail", "server compatibility mode", "keepalive")
            || port == "5432")
            engines.Add(DatabaseEngine.Postgres);
        if (Has("uid", "pwd", "allowuservariables", "allow user variables", "convertzerodatetime", "treattinyasboolean",
                "characterset", "charset", "allowpublickeyretrieval", "guidformat") || port == "3306")
            engines.Add(DatabaseEngine.MySql);
        // "Host" and "Username" alone: Npgsql's own keys, which MySqlConnector also takes.
        if (engines.Count == 0 && Has("host") && Has("username") && !Has("server")) engines.Add(DatabaseEngine.Postgres);
        return engines.Count == 1 ? engines.First() : null;
    }

    private static (DatabaseTarget?, string?) FromUrl(Uri uri, DatabaseEngine? engine)
    {
        var scheme = uri.Scheme.ToLowerInvariant();
        var found = engine ?? scheme switch
        {
            "postgres" or "postgresql" => DatabaseEngine.Postgres,
            "mysql" or "mariadb" or "mysqlx" => DatabaseEngine.MySql,
            "sqlserver" or "mssql" => DatabaseEngine.SqlServer,
            _ => (DatabaseEngine?)null,
        };
        if (found is null) return (null, $"A {uri.Scheme}:// address is no database Inferpal reads (postgres://, mysql://, sqlserver://).");
        var user = Uri.UnescapeDataString(uri.UserInfo.Split(':')[0]);
        var password = uri.UserInfo.Contains(':') ? Uri.UnescapeDataString(uri.UserInfo[(uri.UserInfo.IndexOf(':') + 1)..]) : null;
        var database = Uri.UnescapeDataString(uri.AbsolutePath.Trim('/'));
        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
        string text = found.Value switch
        {
            DatabaseEngine.Postgres => new Npgsql.NpgsqlConnectionStringBuilder
            {
                Host = uri.Host, Port = uri.IsDefaultPort || uri.Port < 0 ? 5432 : uri.Port, Database = database, Username = user, Password = password,
                SslMode = Enum.TryParse<Npgsql.SslMode>(query["sslmode"], ignoreCase: true, out var ssl) ? ssl : Npgsql.SslMode.Prefer,
            }.ConnectionString,
            DatabaseEngine.MySql => new MySqlConnector.MySqlConnectionStringBuilder
            {
                Server = uri.Host, Port = (uint)(uri.IsDefaultPort || uri.Port < 0 ? 3306 : uri.Port), Database = database, UserID = user, Password = password,
            }.ConnectionString,
            _ => new Microsoft.Data.SqlClient.SqlConnectionStringBuilder
            {
                DataSource = uri.IsDefaultPort || uri.Port < 0 ? uri.Host : $"{uri.Host},{uri.Port}", InitialCatalog = database,
                UserID = user, Password = password ?? string.Empty,
            }.ConnectionString,
        };
        return Build(found.Value, text);
    }

    private static (DatabaseTarget?, string?) Build(DatabaseEngine engine, string text)
    {
        switch (engine)
        {
            case DatabaseEngine.Postgres:
            {
                var b = new Npgsql.NpgsqlConnectionStringBuilder(text) { Timeout = ConnectSeconds, CommandTimeout = DatabaseReader.QuerySeconds, Pooling = false, ApplicationName = "Inferpal" };
                var host = b.Host ?? "localhost";
                var socket = host.StartsWith('/');
                var server = socket ? $"the local socket {host}" : $"{host}:{(b.Port == 0 ? 5432 : b.Port)}";
                return (new DatabaseTarget(engine, b.ConnectionString, server, b.Database ?? b.Username ?? string.Empty,
                                           b.Username ?? Environment.UserName, WindowsAccount: false, socket || IsLocal(host)), null);
            }
            case DatabaseEngine.SqlServer:
            {
                var b = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(text)
                    { ConnectTimeout = ConnectSeconds, Pooling = false, ApplicationName = "Inferpal", ApplicationIntent = Microsoft.Data.SqlClient.ApplicationIntent.ReadOnly };
                var source = b.DataSource ?? string.Empty;
                var host = source;
                foreach (var prefix in new[] { "tcp:", "np:", "lpc:" })
                    if (host.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) host = host[prefix.Length..];
                // ⚠ A failover partner is a second server the driver reaches on its own when the first does not answer: on
                // this machine only when both are.
                var partner = b.FailoverPartner ?? string.Empty;
                var local = (host.StartsWith("(localdb)", StringComparison.OrdinalIgnoreCase) || source.StartsWith("lpc:", StringComparison.OrdinalIgnoreCase)
                             || IsLocal(host.Split('\\', ',')[0]))
                            && (partner.Length == 0 || IsLocal(partner.Split('\\', ',')[0]));
                var windows = b.IntegratedSecurity || b.Authentication == Microsoft.Data.SqlClient.SqlAuthenticationMethod.ActiveDirectoryIntegrated;
                return (new DatabaseTarget(engine, b.ConnectionString, source.Length > 0 ? source : "(local)", b.InitialCatalog ?? string.Empty,
                                           windows ? Environment.UserName : b.UserID ?? string.Empty, windows, local), null);
            }
            default:
            {
                // ⚠ No LOAD DATA LOCAL: the server could ask for any file of this machine.
                var b = new MySqlConnector.MySqlConnectionStringBuilder(text)
                    { ConnectionTimeout = ConnectSeconds, DefaultCommandTimeout = (uint)DatabaseReader.QuerySeconds, Pooling = false, ApplicationName = "Inferpal",
                      AllowLoadLocalInfile = false };
                var host = b.Server ?? "localhost";
                // ⚠ A named pipe goes to the server it names (\\host\pipe\…, over SMB with the Windows login): local only when
                // that server is; a Unix socket is a path on this machine.
                var socket = host.StartsWith('/') || b.ConnectionProtocol == MySqlConnector.MySqlConnectionProtocol.UnixSocket;
                return (new DatabaseTarget(engine, b.ConnectionString, socket ? $"the local socket {host}" : $"{host}:{(b.Port == 0 ? 3306 : b.Port)}",
                                           b.Database ?? string.Empty, b.UserID ?? string.Empty, WindowsAccount: false, socket || IsLocal(host)), null);
            }
        }
    }

    /// <summary>A host this machine answers for, by its name as written: never resolved (a name that resolves here may not
    /// tomorrow, and a lookup reaches the network).</summary>
    internal static bool IsLocal(string host)
    {
        var h = host.Trim().Trim('[', ']').ToLowerInvariant();
        // ⚠ Not *.localhost: Windows sends such a name to DNS.
        if (h is "localhost" or "." or "(local)" or "::1" or "0:0:0:0:0:0:0:1") return true;
        if (h.StartsWith("127.", StringComparison.Ordinal) && System.Net.IPAddress.TryParse(h, out _)) return true;
        return h.Length > 0 && string.Equals(h, Environment.MachineName, StringComparison.OrdinalIgnoreCase);
    }
}
