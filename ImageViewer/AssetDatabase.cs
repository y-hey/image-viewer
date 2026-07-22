using Microsoft.Data.Sqlite;
using System.IO;

namespace ImageViewer;

public sealed class AssetDatabase : IDisposable
{
    private readonly SqliteConnection _conn;

    public AssetDatabase(string rootPath)
    {
        var dbDir = Path.Combine(rootPath, "_db");
        Directory.CreateDirectory(dbDir);
        _conn = new SqliteConnection($"Data Source={Path.Combine(dbDir, "assets.db")}");
        _conn.Open();
        Init();
    }

    private void Init()
    {
        Exec("PRAGMA journal_mode=WAL");
        Exec("PRAGMA foreign_keys=ON");
        Exec("""
            CREATE TABLE IF NOT EXISTS assets (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                path TEXT NOT NULL UNIQUE COLLATE NOCASE,
                filename TEXT NOT NULL,
                extension TEXT NOT NULL,
                file_size INTEGER NOT NULL,
                source TEXT,
                added_at TEXT NOT NULL DEFAULT (datetime('now'))
            );
            CREATE TABLE IF NOT EXISTS tags (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                name TEXT NOT NULL UNIQUE COLLATE NOCASE
            );
            CREATE TABLE IF NOT EXISTS asset_tags (
                asset_id INTEGER NOT NULL REFERENCES assets(id) ON DELETE CASCADE,
                tag_id INTEGER NOT NULL REFERENCES tags(id) ON DELETE CASCADE,
                PRIMARY KEY (asset_id, tag_id)
            );
            CREATE INDEX IF NOT EXISTS idx_assets_path ON assets(path);
            CREATE INDEX IF NOT EXISTS idx_tags_name ON tags(name);
            CREATE TABLE IF NOT EXISTS asset_metadata (
                asset_id INTEGER PRIMARY KEY REFERENCES assets(id) ON DELETE CASCADE,
                asset_type TEXT,
                usage TEXT,
                notes TEXT,
                metadata_json TEXT
            );
        """);
        Migrate();
    }

    private void Migrate()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "PRAGMA table_info(assets)";
        var cols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var r = cmd.ExecuteReader())
            while (r.Read()) cols.Add(r.GetString(1));

        if (!cols.Contains("hash"))
            Exec("ALTER TABLE assets ADD COLUMN hash TEXT");
    }

    public void SyncFiles(IReadOnlyList<ImageEntry> files)
    {
        using var tx = _conn.BeginTransaction();

        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var cmd = _conn.CreateCommand())
        {
            cmd.CommandText = "SELECT path FROM assets";
            using var r = cmd.ExecuteReader();
            while (r.Read()) existing.Add(r.GetString(0));
        }

        using (var cmd = _conn.CreateCommand())
        {
            cmd.CommandText = """
                INSERT OR IGNORE INTO assets (path, filename, extension, file_size)
                VALUES (@p, @n, @e, @s)
            """;
            var pP = cmd.Parameters.Add("@p", SqliteType.Text);
            var pN = cmd.Parameters.Add("@n", SqliteType.Text);
            var pE = cmd.Parameters.Add("@e", SqliteType.Text);
            var pS = cmd.Parameters.Add("@s", SqliteType.Integer);

            foreach (var f in files)
            {
                if (existing.Contains(f.RelativePath)) continue;
                pP.Value = f.RelativePath;
                pN.Value = Path.GetFileName(f.RelativePath);
                pE.Value = Path.GetExtension(f.RelativePath);
                pS.Value = f.FileSize;
                cmd.ExecuteNonQuery();
            }
        }

        var current = files.Select(f => f.RelativePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var stale = existing.Where(p => !current.Contains(p)).ToList();
        if (stale.Count > 0)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "DELETE FROM assets WHERE path = @p";
            var pP = cmd.Parameters.Add("@p", SqliteType.Text);
            foreach (var p in stale) { pP.Value = p; cmd.ExecuteNonQuery(); }
        }

        tx.Commit();
    }

    public long? GetAssetId(string relativePath)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT id FROM assets WHERE path = @p";
        cmd.Parameters.AddWithValue("@p", relativePath);
        return cmd.ExecuteScalar() is long id ? id : null;
    }

    public void AddTag(long assetId, string tagName)
    {
        Exec("INSERT OR IGNORE INTO tags (name) VALUES (@n)", ("@n", tagName));
        Exec("""
            INSERT OR IGNORE INTO asset_tags (asset_id, tag_id)
            VALUES (@a, (SELECT id FROM tags WHERE name = @n))
        """, ("@a", assetId), ("@n", tagName));
    }

    public void RemoveTag(long assetId, string tagName)
    {
        Exec("""
            DELETE FROM asset_tags WHERE asset_id = @a
            AND tag_id = (SELECT id FROM tags WHERE name = @n)
        """, ("@a", assetId), ("@n", tagName));
        Exec("""
            DELETE FROM tags WHERE name = @n
            AND id NOT IN (SELECT tag_id FROM asset_tags)
        """, ("@n", tagName));
    }

    public List<string> GetAssetTags(long assetId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT t.name FROM tags t
            JOIN asset_tags at ON at.tag_id = t.id
            WHERE at.asset_id = @a ORDER BY t.name
        """;
        cmd.Parameters.AddWithValue("@a", assetId);
        return ReadStrings(cmd);
    }

    public List<string> GetAllTags()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT DISTINCT t.name FROM tags t
            JOIN asset_tags at ON at.tag_id = t.id
            ORDER BY t.name
        """;
        return ReadStrings(cmd);
    }

    public HashSet<string> SearchByTags(IReadOnlyList<string> tags)
    {
        if (tags.Count == 0) return [];
        using var cmd = _conn.CreateCommand();
        var plist = new List<string>();
        for (int i = 0; i < tags.Count; i++)
        {
            plist.Add($"@t{i}");
            cmd.Parameters.AddWithValue($"@t{i}", tags[i]);
        }
        cmd.CommandText = $"""
            SELECT a.path FROM assets a
            JOIN asset_tags at ON at.asset_id = a.id
            JOIN tags t ON t.id = at.tag_id
            WHERE t.name IN ({string.Join(",", plist)})
            GROUP BY a.path HAVING COUNT(DISTINCT t.name) = {tags.Count}
        """;
        return [.. ReadStrings(cmd)];
    }

    public void ComputeAndStoreHash(long assetId, string fullPath)
    {
        try
        {
            using var fs = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var size = fs.Length;
            var buf = new byte[(int)Math.Min(65536, size)];
            var read = fs.Read(buf, 0, buf.Length);

            var hash = System.Security.Cryptography.SHA256.HashData(buf.AsSpan(0, read));
            var hex = $"{size:X16}{Convert.ToHexString(hash)}";

            Exec("UPDATE assets SET hash = @h WHERE id = @id", ("@h", hex), ("@id", assetId));
        }
        catch { }
    }

    public List<AssetRecord> GetAssetsWithoutHash()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT id, path, filename, file_size FROM assets WHERE hash IS NULL";
        var list = new List<AssetRecord>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new AssetRecord(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetInt64(3), null));
        return list;
    }

    public List<List<AssetRecord>> FindDuplicates()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, path, filename, file_size, hash FROM assets
            WHERE hash IN (SELECT hash FROM assets WHERE hash IS NOT NULL GROUP BY hash HAVING COUNT(*) > 1)
            ORDER BY hash, path
        """;
        var groups = new Dictionary<string, List<AssetRecord>>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var rec = new AssetRecord(
                r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetInt64(3),
                r.IsDBNull(4) ? null : r.GetString(4));
            if (rec.Hash == null) continue;
            if (!groups.TryGetValue(rec.Hash, out var list))
            {
                list = [];
                groups[rec.Hash] = list;
            }
            list.Add(rec);
        }
        return groups.Values.Where(g => g.Count > 1).ToList();
    }

    // --- Metadata ---

    public AssetMeta? GetMetadata(long assetId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT asset_type, usage, notes, metadata_json FROM asset_metadata WHERE asset_id = @id";
        cmd.Parameters.AddWithValue("@id", assetId);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return new AssetMeta(
            r.IsDBNull(0) ? "" : r.GetString(0),
            r.IsDBNull(1) ? "" : r.GetString(1),
            r.IsDBNull(2) ? "" : r.GetString(2),
            r.IsDBNull(3) ? "" : r.GetString(3));
    }

    public void SetMetadata(long assetId, AssetMeta meta)
    {
        Exec("""
            INSERT INTO asset_metadata (asset_id, asset_type, usage, notes, metadata_json)
            VALUES (@id, @t, @u, @n, @j)
            ON CONFLICT(asset_id) DO UPDATE SET
                asset_type = @t, usage = @u, notes = @n, metadata_json = @j
        """, ("@id", assetId), ("@t", meta.AssetType), ("@u", meta.Usage),
             ("@n", meta.Notes), ("@j", meta.MetadataJson));
    }

    public List<(string path, List<string> tags, AssetMeta? meta)> ExportAll()
    {
        var result = new List<(string, List<string>, AssetMeta?)>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT id, path FROM assets ORDER BY path";
        var assets = new List<(long id, string path)>();
        using (var r = cmd.ExecuteReader())
            while (r.Read()) assets.Add((r.GetInt64(0), r.GetString(1)));
        foreach (var (id, path) in assets)
            result.Add((path, GetAssetTags(id), GetMetadata(id)));
        return result;
    }

    public void Dispose() => _conn.Dispose();

    private void Exec(string sql, params (string name, object val)[] args)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v);
        cmd.ExecuteNonQuery();
    }

    private static List<string> ReadStrings(SqliteCommand cmd)
    {
        var list = new List<string>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(r.GetString(0));
        return list;
    }
}

public sealed record AssetRecord(long Id, string Path, string FileName, long FileSize, string? Hash);

public sealed record AssetMeta(string AssetType, string Usage, string Notes, string MetadataJson);
