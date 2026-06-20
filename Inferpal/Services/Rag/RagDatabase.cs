using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace Inferpal.Services.Rag;

/// <summary>
/// Persists the RAG index for a single solution in a SQLite database at
/// <c>%AppData%/Inferpal/rag/{hash}.db</c>.
/// </summary>
/// <remarks>
/// Schema (version 1):
/// <list type="bullet">
///   <item><c>meta</c>    — schema version key/value store.</item>
///   <item><c>chunks</c>  — one row per <see cref="RagChunk"/>; embedding is a raw BLOB of float32.</item>
/// </list>
/// WAL journal mode + NORMAL synchronous keep writes fast while remaining crash-safe.
/// <para>
/// The older JSON+binary format (<c>{hash}.chunks.json</c> / <c>{hash}.embeddings.bin</c>)
/// is ignored; re-indexing from source is triggered automatically when the DB is empty.
/// </para>
/// </remarks>
internal sealed class RagDatabase
{
    // ── Storage paths ─────────────────────────────────────────────────────────

    private static readonly string BaseDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Inferpal", "rag");

    private readonly string _dbPath;
    private readonly string _rootHash;

    private const int SchemaVersion = 1;

    // ── Construction ──────────────────────────────────────────────────────────

    // Register the native e_sqlite3 resolver before the first SqliteConnection is
    // created (its static ctor runs the first P/Invoke). See SqliteBootstrapper.
    static RagDatabase() => SqliteBootstrapper.EnsureInitialized();

    public RagDatabase(string solutionRoot)
    {
        _rootHash = ComputePathHash(solutionRoot);
        _dbPath   = Path.Combine(BaseDir, $"{_rootHash}.db");
        Directory.CreateDirectory(BaseDir);
        EnsureSchema();
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Loads chunks for a single source file using the <c>idx_chunks_file</c> index.
    /// O(log N) in the DB — far cheaper than <see cref="LoadAsync"/> for targeted lookups.
    /// </summary>
    public async Task<List<RagChunk>> LoadFileAsync(string filePath, CancellationToken ct)
    {
        var result = new List<RagChunk>();
        await using var conn = OpenConnection();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT file_path, rel_path, start_line, end_line,
                   content, content_hash, type_name, embedding
            FROM   chunks
            WHERE  root_hash = $rh AND file_path = $fp
            ORDER  BY start_line";
        cmd.Parameters.AddWithValue("$rh", _rootHash);
        cmd.Parameters.AddWithValue("$fp", filePath);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result.Add(new RagChunk
            {
                FilePath    = reader.GetString(0),
                RelPath     = reader.GetString(1),
                StartLine   = reader.GetInt32(2),
                EndLine     = reader.GetInt32(3),
                Content     = reader.GetString(4),
                ContentHash = reader.GetString(5),
                TypeName    = await reader.IsDBNullAsync(6, ct) ? null : reader.GetString(6),
                Embedding   = await reader.IsDBNullAsync(7, ct) ? null : BlobToFloats((byte[])reader[7]),
            });
        }

        return result;
    }

    /// <summary>
    /// Loads all chunks for this solution root from the SQLite DB.
    /// Returns an empty list when the DB is new or empty.
    /// </summary>
    public async Task<List<RagChunk>> LoadAsync(CancellationToken ct)
    {
        var result = new List<RagChunk>();
        await using var conn = OpenConnection();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT file_path, rel_path, start_line, end_line,
                   content, content_hash, type_name, embedding
            FROM   chunks
            WHERE  root_hash = $rh
            ORDER  BY file_path, start_line";
        cmd.Parameters.AddWithValue("$rh", _rootHash);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var chunk = new RagChunk
            {
                FilePath    = reader.GetString(0),
                RelPath     = reader.GetString(1),
                StartLine   = reader.GetInt32(2),
                EndLine     = reader.GetInt32(3),
                Content     = reader.GetString(4),
                ContentHash = reader.GetString(5),
                TypeName    = await reader.IsDBNullAsync(6, ct) ? null : reader.GetString(6),
                Embedding   = await reader.IsDBNullAsync(7, ct) ? null : BlobToFloats((byte[])reader[7]),
            };
            result.Add(chunk);
        }

        return result;
    }

    /// <summary>
    /// Replaces ALL stored chunks for this root (used at the end of a full re-index pass).
    /// Runs inside a single transaction for atomicity.
    /// </summary>
    public async Task SaveAsync(IReadOnlyList<RagChunk> chunks, CancellationToken ct)
    {
        await using var conn = OpenConnection();
        await using var tx   = await conn.BeginTransactionAsync(ct) as SqliteTransaction
                               ?? throw new InvalidOperationException("Could not begin transaction.");

        // Delete all existing chunks for this root
        await using var del = conn.CreateCommand();
        del.Transaction  = tx;
        del.CommandText  = "DELETE FROM chunks WHERE root_hash = $rh";
        del.Parameters.AddWithValue("$rh", _rootHash);
        await del.ExecuteNonQueryAsync(ct);

        // Bulk-insert new chunks
        await InsertChunksAsync(conn, tx, chunks, ct);
        await tx.CommitAsync(ct);
    }

    /// <summary>
    /// Replaces chunks for a single source file (surgical incremental update).
    /// Far cheaper than <see cref="SaveAsync"/> when only one file changed.
    /// </summary>
    public async Task SaveFileAsync(string filePath, IReadOnlyList<RagChunk> chunks, CancellationToken ct)
    {
        await using var conn = OpenConnection();
        await using var tx   = await conn.BeginTransactionAsync(ct) as SqliteTransaction
                               ?? throw new InvalidOperationException("Could not begin transaction.");

        await using var del = conn.CreateCommand();
        del.Transaction  = tx;
        del.CommandText  = "DELETE FROM chunks WHERE root_hash = $rh AND file_path = $fp";
        del.Parameters.AddWithValue("$rh", _rootHash);
        del.Parameters.AddWithValue("$fp", filePath);
        await del.ExecuteNonQueryAsync(ct);

        await InsertChunksAsync(conn, tx, chunks, ct);
        await tx.CommitAsync(ct);
    }

    /// <summary>Removes all chunks for a deleted source file.</summary>
    public async Task DeleteFileAsync(string filePath, CancellationToken ct)
    {
        await using var conn = OpenConnection();
        await using var cmd  = conn.CreateCommand();
        cmd.CommandText      = "DELETE FROM chunks WHERE root_hash = $rh AND file_path = $fp";
        cmd.Parameters.AddWithValue("$rh", _rootHash);
        cmd.Parameters.AddWithValue("$fp", filePath);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ── Schema ────────────────────────────────────────────────────────────────

    private void EnsureSchema()
    {
        using var conn = OpenConnection();

        conn.Execute(@"
            CREATE TABLE IF NOT EXISTS meta (
                key   TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS chunks (
                id           INTEGER PRIMARY KEY AUTOINCREMENT,
                root_hash    TEXT    NOT NULL,
                file_path    TEXT    NOT NULL,
                rel_path     TEXT    NOT NULL,
                start_line   INTEGER NOT NULL,
                end_line     INTEGER NOT NULL,
                content      TEXT    NOT NULL,
                content_hash TEXT    NOT NULL,
                type_name    TEXT,
                embedding    BLOB
            );

            CREATE INDEX IF NOT EXISTS idx_chunks_root
                ON chunks (root_hash);

            CREATE INDEX IF NOT EXISTS idx_chunks_file
                ON chunks (root_hash, file_path);");

        // Check / stamp schema version
        using var ver = conn.CreateCommand();
        ver.CommandText = "SELECT value FROM meta WHERE key = 'schema_version'";
        var existing = ver.ExecuteScalar() as string;

        if (existing is null)
        {
            using var stamp = conn.CreateCommand();
            stamp.CommandText =
                "INSERT OR REPLACE INTO meta (key, value) VALUES ('schema_version', $v)";
            stamp.Parameters.AddWithValue("$v", SchemaVersion.ToString());
            stamp.ExecuteNonQuery();
        }
        // Future migrations: compare int.Parse(existing) < SchemaVersion → ALTER TABLE …
    }

    // ── Insert helper ──────────────────────────────────────────────────────────

    private async Task InsertChunksAsync(
        SqliteConnection    conn,
        SqliteTransaction   tx,
        IReadOnlyList<RagChunk> chunks,
        CancellationToken   ct)
    {
        if (chunks.Count == 0) return;

        await using var insert = conn.CreateCommand();
        insert.Transaction  = tx;
        insert.CommandText  = @"
            INSERT INTO chunks
                (root_hash, file_path, rel_path, start_line, end_line,
                 content, content_hash, type_name, embedding)
            VALUES
                ($rh, $fp, $rp, $sl, $el, $ct, $ch, $tn, $emb)";

        var pRh  = insert.Parameters.Add("$rh",  SqliteType.Text);
        var pFp  = insert.Parameters.Add("$fp",  SqliteType.Text);
        var pRp  = insert.Parameters.Add("$rp",  SqliteType.Text);
        var pSl  = insert.Parameters.Add("$sl",  SqliteType.Integer);
        var pEl  = insert.Parameters.Add("$el",  SqliteType.Integer);
        var pCt  = insert.Parameters.Add("$ct",  SqliteType.Text);
        var pCh  = insert.Parameters.Add("$ch",  SqliteType.Text);
        var pTn  = insert.Parameters.Add("$tn",  SqliteType.Text);
        var pEmb = insert.Parameters.Add("$emb", SqliteType.Blob);

        insert.Prepare();

        foreach (var c in chunks)
        {
            ct.ThrowIfCancellationRequested();
            pRh.Value  = _rootHash;
            pFp.Value  = c.FilePath;
            pRp.Value  = c.RelPath;
            pSl.Value  = c.StartLine;
            pEl.Value  = c.EndLine;
            pCt.Value  = c.Content;
            pCh.Value  = c.ContentHash;
            pTn.Value  = (object?)c.TypeName ?? DBNull.Value;
            pEmb.Value = c.Embedding is { Length: > 0 }
                ? (object)FloatsToBlob(c.Embedding)
                : DBNull.Value;
            await insert.ExecuteNonQueryAsync(ct);
        }
    }

    // ── Connection factory ────────────────────────────────────────────────────

    private SqliteConnection OpenConnection()
    {
        var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        // WAL for concurrent read/write; NORMAL sync = no fsync on every commit
        conn.Execute("PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;");
        return conn;
    }

    // ── Embedding serialisation ───────────────────────────────────────────────

    /// <summary>Serialises a float[] to a raw BLOB (no header, little-endian).</summary>
    private static byte[] FloatsToBlob(float[] values)
    {
        var bytes = new byte[values.Length * sizeof(float)];
        Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    /// <summary>Deserialises a raw BLOB back to float[].</summary>
    private static float[] BlobToFloats(byte[] blob)
    {
        if (blob.Length % sizeof(float) != 0) return [];
        var floats = new float[blob.Length / sizeof(float)];
        Buffer.BlockCopy(blob, 0, floats, 0, blob.Length);
        return floats;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>Returns 12-char lowercase hex derived from MD5 of the normalised path.</summary>
    private static string ComputePathHash(string path)
    {
        var bytes = MD5.HashData(System.Text.Encoding.UTF8.GetBytes(
            path.ToLowerInvariant().TrimEnd('\\', '/')));
        return Convert.ToHexString(bytes)[..12].ToLowerInvariant();
    }
}

// ── SqliteConnection extension ─────────────────────────────────────────────────

file static class SqliteConnectionExtensions
{
    /// <summary>Executes a multi-statement SQL string (no parameters).</summary>
    public static void Execute(this SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
