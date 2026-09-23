using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace AgentHub.Core.TokenCore;

/// <summary>用量扫描共用的落盘读法：SQLite 三件套快照、限深列文件、JSON 数字。各家只做路径和字段映射。</summary>
internal static class UsageIo
{
    public static bool TrySnapshot(string? srcDb, string tempPrefix, out string db, out string tmp) =>
        TrySnapshot(srcDb, tempPrefix, string.IsNullOrEmpty(srcDb) ? null : Path.GetFileName(srcDb), out db, out tmp);

    /// <summary>拷主库 + WAL/SHM。dest 名可以和源文件不同（会话库统一叫 main.sqlite）。</summary>
    public static bool TrySnapshot(string? srcDb, string tempPrefix, string? destFileName, out string db, out string tmp)
    {
        db = "";
        tmp = "";
        if (string.IsNullOrEmpty(srcDb) || !File.Exists(srcDb)) return false;
        if (string.IsNullOrEmpty(destFileName)) destFileName = Path.GetFileName(srcDb);
        tmp = Path.Combine(Path.GetTempPath(), tempPrefix + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(tmp);
        try
        {
            db = Path.Combine(tmp, destFileName);
            CopyShared(srcDb, db);
            CopyIfExists(srcDb + "-wal", db + "-wal");
            CopyIfExists(srcDb + "-shm", db + "-shm");
            return true;
        }
        catch
        {
            DeleteSnapshot(tmp);
            tmp = "";
            db = "";
            throw;
        }
    }

    public static void DeleteSnapshot(string? tmp)
    {
        if (string.IsNullOrEmpty(tmp)) return;
        try { Directory.Delete(tmp, recursive: true); }
        catch (IOException) { }
    }

    public static SqliteConnection OpenReadOnly(string db)
    {
        var cs = new SqliteConnectionStringBuilder
        {
            DataSource = db,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        };
        var conn = new SqliteConnection(cs.ToString());
        conn.Open();
        return conn;
    }

    public static bool TableExists(SqliteConnection conn, string name)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $n LIMIT 1";
        cmd.Parameters.AddWithValue("$n", name);
        return cmd.ExecuteScalar() is not null;
    }

    /// <summary>从 root 起按目录深度列文件。root 本身深度为 1；不跟出 maxDepth。</summary>
    public static IEnumerable<string> EnumerateFiles(string root, int maxDepth, Func<string, int, bool> include)
    {
        if (string.IsNullOrEmpty(root) || maxDepth < 1 || !Directory.Exists(root)) yield break;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var stack = new Stack<(string Dir, int Depth)>();
        stack.Push((root, 1));
        while (stack.Count > 0)
        {
            var (dir, depth) = stack.Pop();
            if (!seen.Add(dir)) continue;
            List<string> entries;
            try { entries = Directory.EnumerateFileSystemEntries(dir).ToList(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (var entry in entries)
            {
                if (Directory.Exists(entry))
                {
                    if (depth < maxDepth) stack.Push((entry, depth + 1));
                }
                else if (include(entry, depth))
                    yield return entry;
            }
        }
    }

    public static long JsonLong(JsonElement el, string name)
    {
        if (!TryJsonLong(el, name, out var n)) return 0;
        return n < 0 ? 0 : n;
    }

    public static bool TryJsonLong(JsonElement el, string name, out long value)
    {
        value = 0;
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(name, out var v)) return false;
        switch (v.ValueKind)
        {
            case JsonValueKind.Number when v.TryGetInt64(out var n):
                value = n;
                return true;
            case JsonValueKind.Number when v.TryGetDouble(out var d) && double.IsFinite(d):
                value = (long)Math.Floor(d);
                return true;
            case JsonValueKind.String when long.TryParse(v.GetString(), out var parsed):
                value = parsed;
                return true;
            default:
                return false;
        }
    }

    public static bool HasJsonField(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object
        && el.TryGetProperty(name, out var v)
        && v.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined;

    private static void CopyIfExists(string from, string to)
    {
        if (File.Exists(from)) CopyShared(from, to);
    }

    private static void CopyShared(string from, string to)
    {
        using var src = new FileStream(from, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var dst = new FileStream(to, FileMode.Create, FileAccess.Write, FileShare.None);
        src.CopyTo(dst);
    }
}
