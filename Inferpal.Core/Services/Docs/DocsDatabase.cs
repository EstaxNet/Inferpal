using System.IO;
using Microsoft.Data.Sqlite;
using Inferpal.Services.Rag;

namespace Inferpal.Services.Docs;

/// <summary>
/// Persists indexed external documentation in a single, solution-independent SQLite database
/// at <c>%AppData%/Inferpal/docs/docs.db</c>. Unlike the RAG codebase index (one DB per
/// solution), documentation sources are global so they remain available across all projects.
/// </summary>
/// <remarks>
/// Schema (version 1):
/// <list type="bullet">
///   <item><c>doc_sites</c>  — one row per <see cref="DocSite"/> plus crawl stats.</item>
///   <item><c>doc_chunks</c> — one row per <see cref="DocChunk"/>; embedding is a raw float32 BLOB.</item>
/// </list>
/// WAL journal mode + NORMAL synchronous, mirroring <see cref="RagDatabase"/>.
/// </remarks>
internal sealed class DocsDatabase
{
    private static readonly string BaseDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Inferpal", "docs");

    private readonly string _dbPath;
    private const int SchemaVersion = 1;

    // Register the native e_sqlite3 resolver before the first SqliteConnection is created
    // (the ALC plugin VS does not read deps.json). See SqliteBootstrapper / RagDatabase.
    static DocsDatabase() => SqliteBootstrapper.EnsureInitialized();

    public DocsDatabase()
    {
        _dbPath = Path.Combine(BaseDir, "docs.db");
        Directory.CreateDirectory(BaseDir);
        EnsureSchema();
    }

    // ── Sites ────────────────────────────────────────────────────────────────

    /// <summary>Loads all configured documentation sources with their crawl stats.</summary>
    public async Task<List<(DocSite Site, int PageCount, int ChunkCount)>> LoadSitesAsync(CancellationToken ct)
    {
        var result = new List<(DocSite, int, int)>();
        await using var conn = OpenConnection();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, title, start_url, page_count, chunk_count FROM doc_sites ORDER BY title";
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var site = new DocSite(reader.GetString(0), reader.GetString(1), reader.GetString(2));
            result.Add((site, reader.GetInt32(3), reader.GetInt32(4)));
        }
        return result;
    }

    // ── Chunks ───────────────────────────────────────────────────────────────

    /// <summary>Loads every chunk across all documentation sources into memory.</summary>
    public async Task<List<DocChunk>> LoadAllChunksAsync(CancellationToken ct)
    {
        var result = new List<DocChunk>();
        await using var conn = OpenConnection();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT doc_id, url, page_title, heading, content, content_hash, embedding
            FROM   doc_chunks";
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result.Add(new DocChunk
            {
                DocId       = reader.GetString(0),
                Url         = reader.GetString(1),
                PageTitle   = reader.GetString(2),
                Heading     = await reader.IsDBNullAsync(3, ct) ? null : reader.GetString(3),
                Content     = reader.GetString(4),
                ContentHash = reader.GetString(5),
                Embedding   = await reader.IsDBNullAsync(6, ct) ? null : BlobToFloats((byte[])reader[6]),
            });
        }
        return result;
    }

    /// <summary>
    /// Atomically replaces a documentation source and all of its chunks (used after a re-crawl).
    /// </summary>
    public async Task SaveSiteAsync(
        DocSite site, int pageCount, IReadOnlyList<DocChunk> chunks, CancellationToken ct)
    {
        await using var conn = OpenConnection();
        await using var tx   = await conn.BeginTransactionAsync(ct) as SqliteTransaction
                               ?? throw new InvalidOperationException("Could not begin transaction.");

        await using (var del = conn.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = "DELETE FROM doc_chunks WHERE doc_id = $id";
            del.Parameters.AddWithValue("$id", site.Id);
            await del.ExecuteNonQueryAsync(ct);
        }

        await using (var up = conn.CreateCommand())
        {
            up.Transaction = tx;
            up.CommandText = @"
                INSERT INTO doc_sites (id, title, start_url, page_count, chunk_count)
                VALUES ($id, $t, $u, $pc, $cc)
                ON CONFLICT(id) DO UPDATE SET
                    title = $t, start_url = $u, page_count = $pc, chunk_count = $cc";
            up.Parameters.AddWithValue("$id", site.Id);
            up.Parameters.AddWithValue("$t",  site.Title);
            up.Parameters.AddWithValue("$u",  site.StartUrl);
            up.Parameters.AddWithValue("$pc", pageCount);
            up.Parameters.AddWithValue("$cc", chunks.Count);
            await up.ExecuteNonQueryAsync(ct);
        }

        await InsertChunksAsync(conn, tx, site.Id, chunks, ct);
        await tx.CommitAsync(ct);
    }

    /// <summary>Removes a documentation source and all of its chunks.</summary>
    public async Task DeleteSiteAsync(string docId, CancellationToken ct)
    {
        await using var conn = OpenConnection();
        await using var tx   = await conn.BeginTransactionAsync(ct) as SqliteTransaction
                               ?? throw new InvalidOperationException("Could not begin transaction.");

        foreach (var table in new[] { "doc_chunks", "doc_sites" })
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = $"DELETE FROM {table} WHERE {(table == "doc_sites" ? "id" : "doc_id")} = $id";
            cmd.Parameters.AddWithValue("$id", docId);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }

    // ── Schema ───────────────────────────────────────────────────────────────

    private void EnsureSchema()
    {
        using var conn = OpenConnection();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS meta (
                key   TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS doc_sites (
                id          TEXT PRIMARY KEY,
                title       TEXT NOT NULL,
                start_url   TEXT NOT NULL,
                page_count  INTEGER NOT NULL DEFAULT 0,
                chunk_count INTEGER NOT NULL DEFAULT 0
            );

            CREATE TABLE IF NOT EXISTS doc_chunks (
                id           INTEGER PRIMARY KEY AUTOINCREMENT,
                doc_id       TEXT NOT NULL,
                url          TEXT NOT NULL,
                page_title   TEXT NOT NULL,
                heading      TEXT,
                content      TEXT NOT NULL,
                content_hash TEXT NOT NULL,
                embedding    BLOB
            );

            CREATE INDEX IF NOT EXISTS idx_doc_chunks_doc ON doc_chunks (doc_id);

            INSERT OR IGNORE INTO meta (key, value) VALUES ('schema_version', '" + SchemaVersion + @"');";
        cmd.ExecuteNonQuery();
    }

    // ── Insert helper ──────────────────────────────────────────────────────────

    private static async Task InsertChunksAsync(
        SqliteConnection conn, SqliteTransaction tx,
        string docId, IReadOnlyList<DocChunk> chunks, CancellationToken ct)
    {
        if (chunks.Count == 0) return;

        await using var insert = conn.CreateCommand();
        insert.Transaction = tx;
        insert.CommandText = @"
            INSERT INTO doc_chunks
                (doc_id, url, page_title, heading, content, content_hash, embedding)
            VALUES
                ($id, $url, $pt, $h, $ct, $ch, $emb)";

        var pId  = insert.Parameters.Add("$id",  SqliteType.Text);
        var pUrl = insert.Parameters.Add("$url", SqliteType.Text);
        var pPt  = insert.Parameters.Add("$pt",  SqliteType.Text);
        var pH   = insert.Parameters.Add("$h",   SqliteType.Text);
        var pCt  = insert.Parameters.Add("$ct",  SqliteType.Text);
        var pCh  = insert.Parameters.Add("$ch",  SqliteType.Text);
        var pEmb = insert.Parameters.Add("$emb", SqliteType.Blob);
        insert.Prepare();

        foreach (var c in chunks)
        {
            ct.ThrowIfCancellationRequested();
            pId.Value  = docId;
            pUrl.Value = c.Url;
            pPt.Value  = c.PageTitle;
            pH.Value   = (object?)c.Heading ?? DBNull.Value;
            pCt.Value  = c.Content;
            pCh.Value  = c.ContentHash;
            pEmb.Value = c.Embedding is { Length: > 0 }
                ? (object)FloatsToBlob(c.Embedding)
                : DBNull.Value;
            await insert.ExecuteNonQueryAsync(ct);
        }
    }

    // ── Connection factory ──────────────────────────────────────────────────────

    private SqliteConnection OpenConnection()
    {
        var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        using var pragma = conn.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;";
        pragma.ExecuteNonQuery();
        return conn;
    }

    // ── Embedding serialisation ───────────────────────────────────────────────

    private static byte[] FloatsToBlob(float[] values)
    {
        var bytes = new byte[values.Length * sizeof(float)];
        Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private static float[] BlobToFloats(byte[] blob)
    {
        if (blob.Length % sizeof(float) != 0) return [];
        var floats = new float[blob.Length / sizeof(float)];
        Buffer.BlockCopy(blob, 0, floats, 0, blob.Length);
        return floats;
    }
}
