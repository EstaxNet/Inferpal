using System.Globalization;
using System.Text;
using Inferpal.Localization;
using Inferpal.Services.Docs;
using Microsoft.Data.Sqlite;

namespace Inferpal.Services.Rag;

/// <summary>One repository's index, as the view says it. <paramref name="Id"/>: the base's file name.</summary>
/// <param name="AskText">What Forget asks in the row.</param>
/// <param name="Note">Why it was just not forgotten; empty otherwise.</param>
internal sealed record KeptIndexRow(string Id, string Name, string Folder, string Detail, bool OpenHere,
                                    string AskText, string ForgetLabel, string Note);

/// <summary>What Inferpal keeps on this machine, for the settings page.</summary>
/// <param name="OlderWriter">An older version still writing the old place; empty otherwise.</param>
/// <param name="Message">What the last gesture said; empty otherwise.</param>
internal sealed record KeptIndexesModel(string Summary, IReadOnlyList<KeptIndexRow> Rows, string OlderWriter, string Note,
                                        string Message);

/// <summary>The indexes a pruning removed: their names, and what came back.</summary>
internal sealed record PrunedIndexes(IReadOnlyList<string> Names, long Bytes);

/// <summary>
/// What Inferpal keeps on this machine: the index of each repository opened, its size, and the way to forget it. One
/// reader for the settings pages of both editors and for <c>/index</c>, so that ACP gets the same list and the same refusals.
/// </summary>
/// <remarks>
/// ⚠ A base does not record its root, but every chunk carries its path and its path under the root: the folder is read
/// from there. A base without a chunk has nothing to show. ⚠ A folder that is gone proves nothing: a drive unplugged, a
/// clone moved. It is said, and it does not shorten an index's life.
/// </remarks>
internal static class KeptIndexes
{
    /// <summary>Where the settings show what is kept.</summary>
    internal const string Page = "search";

    /// <summary>The section of <see cref="Page"/> that lists it.</summary>
    internal const string Section = "SettingsSectionKept";

    /// <summary>How long an index stays without being opened.</summary>
    internal static readonly TimeSpan OldAge = TimeSpan.FromDays(90);

    private sealed record KeptBase(string File, string? OldFile, string? Root, long Bytes, DateTime Opened)
    {
        public string Id => Path.GetFileName(File);
        public string Name => Root is { Length: > 0 } root && Path.GetFileName(root) is { Length: > 0 } name ? name : Root ?? Id;
    }

    /// <summary>A size as the pages say it: 9.6 MB, 523 MB, 1.2 GB.</summary>
    internal static string Size(long bytes)
    {
        // Never "0 MB" for a base: it always takes something.
        var mb = bytes == 0 ? 0 : Math.Max(bytes / 1_000_000.0, 0.1);
        return mb >= 1000 ? $"{(mb / 1000).ToString("0.#", CultureInfo.CurrentCulture)} GB"
                          : $"{mb.ToString(mb >= 100 ? "0" : "0.#", CultureInfo.CurrentCulture)} MB";
    }

    private static long Bytes(string file) => new[] { file, file + "-wal", file + "-shm" }.Sum(Length);

    /// <summary>A log comes and goes as other windows close their connections.</summary>
    private static long Length(string file)
    {
        try { return new FileInfo(file).Length; }
        catch (FileNotFoundException) { return 0; }
    }

    private static bool IsBase(string file) => file.EndsWith(".db", StringComparison.OrdinalIgnoreCase);

    /// <summary>The folder a base indexes; <c>null</c> when it cannot be read. <paramref name="empty"/>: it holds no chunk.</summary>
    private static string? ReadRoot(string file, out bool empty)
    {
        empty = false;
        try
        {
            using var conn = IndexHome.Open(file);
            using var cmd  = conn.CreateCommand();
            cmd.CommandText = $"SELECT file_path, rel_path FROM {IndexHome.CodeChunks} LIMIT 1";
            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
            {
                empty = true;
                return null;
            }
            var path = reader.GetString(0);
            var rel  = reader.GetString(1).Replace('/', Path.DirectorySeparatorChar);
            return path.EndsWith(rel, StringComparison.OrdinalIgnoreCase)
                ? path[..^rel.Length].TrimEnd('\\', '/')
                : Path.GetDirectoryName(path);
        }
        catch (SqliteException ex)
        {
            empty = ex.SqliteErrorCode == 1;   // no such table: a base never filled
            if (!empty) Diagnostics.Swallow("KeptIndexes.ReadRoot", ex);
            return null;
        }
    }

    private static string? OldCopy(string? oldDir, string id) =>
        oldDir is not null && File.Exists(Path.Combine(oldDir, id)) ? Path.Combine(oldDir, id) : null;

    /// <summary>The bases with something to show, the largest first. A base not yet copied counts where it is read.</summary>
    private static List<KeptBase> Scan(string dir, string? oldDir)
    {
        var seen = new Dictionary<string, KeptBase>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in oldDir is null ? [dir] : new[] { dir, oldDir })
        {
            if (!Directory.Exists(folder)) continue;
            foreach (var file in Directory.EnumerateFiles(folder).Where(IsBase))
            {
                var id = Path.GetFileName(file);
                if (seen.ContainsKey(id)) continue;
                // Before the first connection: closing it folds a leftover log into the base, and dates the base now.
                var written = IndexHome.WrittenAt(file);
                var opened  = IndexHome.OpenedAt(file) is { } at && at > written ? at : written;
                var bytes   = Bytes(file);
                var root    = ReadRoot(file, out var empty);
                if (empty) continue;
                seen[id] = new(file, folder == dir ? OldCopy(oldDir, id) : null, root, bytes, opened);
            }
        }
        return [.. seen.Values.OrderByDescending(b => b.Bytes)];
    }

    private static string When(KeptBase b, DateTime now)
    {
        var days = (int)(now - b.Opened).TotalDays;
        var opened = days <= 0 ? Strings.KeptOpenedToday : Strings.KeptOpenedDaysAgo(days);
        return b.Root is null ? $"{opened} · {Strings.KeptUnknownFolder}"
             : Directory.Exists(b.Root) ? opened
             : $"{opened} · {Strings.KeptFolderNotFound}";
    }

    private static string Detail(KeptBase b, string? openId, DateTime now) =>
        $"{Size(b.Bytes)} · {(string.Equals(b.Id, openId, StringComparison.OrdinalIgnoreCase) ? Strings.KeptOpenNow : When(b, now))}";

    /// <summary>The view: <paramref name="dir"/> holds the indexes, <paramref name="oldDir"/> what earlier versions left.</summary>
    /// <param name="openId">The base of the folder open here: it has no Forget.</param>
    /// <param name="heldId">The base a Forget just could not remove.</param>
    internal static KeptIndexesModel Read(string dir, string? oldDir, string? openId, DateTime now, long docsBytes,
                                          string message = "", string? heldId = null)
    {
        var bases = Scan(dir, oldDir);
        var rows = bases.Select(b =>
        {
            var size = Size(b.Bytes);
            return new KeptIndexRow(b.Id, b.Name, b.Root ?? string.Empty, Detail(b, openId, now),
                                    string.Equals(b.Id, openId, StringComparison.OrdinalIgnoreCase),
                                    Strings.KeptForgetAsk(size), Strings.KeptForgetLabel(b.Name), b.Id == heldId ? Strings.KeptHeld : string.Empty);
        }).ToList();

        var written = oldDir is not null && Directory.Exists(oldDir)
            ? Directory.EnumerateFiles(oldDir).Where(IsBase).Where(IndexHome.WrittenSinceCopy).ToList()
            : [];
        var older = written.Count == 0 ? string.Empty : Strings.KeptOlderWriter(written.Count, Size(written.Sum(Bytes)));
        var note  = docsBytes > 0 ? $"{Strings.KeptNote} {Strings.KeptNoteDocs(Size(docsBytes))}" : Strings.KeptNote;
        return new(Strings.KeptSummary(bases.Count, Size(bases.Sum(b => b.Bytes))), rows, older, note, message);
    }

    /// <summary>Removes a base from both places. The old one before the local one: left behind, the next opening would
    /// read it, and the index forgotten would come back.</summary>
    /// <remarks>⚠ Never by clearing the connection pool first: what a pooled connection holds is all that stands between
    /// another conversation of this process and a base removed under it.</remarks>
    private static bool TryRemove(KeptBase b)
    {
        try
        {
            IndexHome.DropOpened(b.File);
            if (b.OldFile is { } old) IndexHome.Remove(old);
            IndexHome.Remove(b.File);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>Forgets the base <paramref name="id"/>; what it says, and the base it could not remove.</summary>
    /// <remarks>⚠ <paramref name="id"/> comes from a page: only a base's own name is looked for, and in the scan, so nothing
    /// else on this machine can be named.</remarks>
    internal static (string Message, string? HeldId) Forget(string id, string dir, string? oldDir, string? openId)
    {
        var found = Scan(dir, oldDir).FirstOrDefault(b => string.Equals(b.Id, id, StringComparison.OrdinalIgnoreCase));
        return found is null ? (Strings.KeptUnknown(id), null) : Forget(found, openId);
    }

    private static (string Message, string? HeldId) Forget(KeptBase b, string? openId)
    {
        if (string.Equals(b.Id, openId, StringComparison.OrdinalIgnoreCase)) return (Strings.KeptNotHere(b.Name), null);
        return TryRemove(b) ? (Strings.KeptForgotten(b.Name, Size(b.Bytes)), null) : (Strings.KeptHeld, b.Id);
    }

    /// <summary>Removes the indexes not opened for <see cref="OldAge"/>; <c>null</c> when none went.</summary>
    /// <remarks>⚠ A base with no mark starts its 90 days here, in a mark of their own: after an update no base has one, its
    /// date says when it was written, not when it served, and every quiet repository would go at once. ⚠ The old copy
    /// counts: an older version may still be writing it, and it would build everything again. Dates only, for each base: a
    /// connection is for those that go.</remarks>
    internal static PrunedIndexes? Prune(string dir, string? oldDir, string? openId, DateTime now)
    {
        if (!Directory.Exists(dir)) return null;
        var names = new List<string>();
        var total = 0L;
        foreach (var file in Directory.EnumerateFiles(dir).Where(IsBase).ToList())
        {
            var id = Path.GetFileName(file);
            if (string.Equals(id, openId, StringComparison.OrdinalIgnoreCase)) continue;
            if (IndexHome.OpenedAt(file) is null && IndexHome.SeenAt(file) is null)
            {
                IndexHome.NoteSeen(file, now);
                continue;
            }
            var old  = OldCopy(oldDir, id);
            var last = new[] { IndexHome.WrittenAt(file), IndexHome.OpenedAt(file) ?? default, IndexHome.SeenAt(file) ?? default,
                               old is null ? default : IndexHome.WrittenAt(old) }.Max();
            if (now - last < OldAge) continue;

            var bytes = Bytes(file);
            var root  = ReadRoot(file, out var empty);
            var kept  = new KeptBase(file, old, root, bytes, last);
            if (!TryRemove(kept) || empty) continue;
            names.Add(kept.Name);
            total += bytes;
        }
        return names.Count == 0 ? null : new(names, total);
    }

    private static string? OpenId(string? root) =>
        string.IsNullOrEmpty(root) ? null : Path.GetFileName(RagDatabase.PathFor(root));

    private static long DocsBytes() => Bytes(DocsDatabase.PathNow());

    /// <summary>The view of this machine, with <paramref name="root"/> open here.</summary>
    internal static KeptIndexesModel ReadHere(string? root, string message = "", string? heldId = null) =>
        Read(RagDatabase.BaseDir(), RagDatabase.OldDir, OpenId(root), DateTime.UtcNow, DocsBytes(), message, heldId);

    /// <summary>Forgets a base, and the view as it stands after.</summary>
    internal static KeptIndexesModel ForgetHere(string id, string? root)
    {
        var (message, held) = Forget(id, RagDatabase.BaseDir(), RagDatabase.OldDir, OpenId(root));
        return ReadHere(root, held is null ? message : string.Empty, held);
    }

    /// <summary>The pruning of this machine, with <paramref name="root"/> open here.</summary>
    internal static PrunedIndexes? PruneHere(string? root)
    {
        try { return Prune(RagDatabase.BaseDir(), RagDatabase.OldDir, OpenId(root), DateTime.UtcNow); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Diagnostics.Swallow("KeptIndexes.Prune", ex);
            return null;
        }
    }

    /// <summary><c>/index list</c>: what is kept, after the pruning it says.</summary>
    internal static string ListHere(string? root)
    {
        var pruned = PruneHere(root);
        return List(ReadHere(root), pruned);
    }

    /// <summary>The list as a command says it.</summary>
    internal static string List(KeptIndexesModel model, PrunedIndexes? pruned)
    {
        var sb = new StringBuilder();
        if (pruned is not null)
            sb.AppendLine(Strings.KeptPruned(pruned.Names.Count, Size(pruned.Bytes), string.Join(", ", pruned.Names.Select(n => $"`{n}`"))))
              .AppendLine();
        if (model.Rows.Count == 0) return sb.Append(Strings.KeptEmpty).ToString();
        sb.AppendLine(Strings.KeptListTitle(model.Summary));
        // Two folders of one name: `/index forget` then needs the whole folder.
        var sameName = model.Rows.GroupBy(r => r.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1)
                            .Select(g => g.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var row in model.Rows) sb.AppendLine($"- `{(sameName.Contains(row.Name) ? row.Folder : row.Name)}` — {row.Detail}");
        if (model.OlderWriter.Length > 0) sb.AppendLine().AppendLine(model.OlderWriter);
        return sb.AppendLine().Append(Strings.KeptListHint).ToString();
    }

    /// <summary><c>/index forget &lt;name&gt;</c>: by the name the list shows, or by the whole folder.</summary>
    internal static string ForgetByNameHere(string name, string? root) =>
        ForgetByName(name, RagDatabase.BaseDir(), RagDatabase.OldDir, OpenId(root));

    internal static string ForgetByName(string name, string dir, string? oldDir, string? openId)
    {
        var named = Scan(dir, oldDir)
            .Where(b => string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase) || PathComparer.SameDirectory(b.Root, name))
            .ToList();
        return named.Count switch
        {
            0 => Strings.KeptUnknown(name),
            1 => Forget(named[0], openId).Message,
            _ => Strings.KeptAmbiguous(name),
        };
    }
}
