using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LevelDB;
using Microsoft.Data.Sqlite;

namespace AgentHub.Core.SessionCore.Providers;

/// <summary>ZCode 侧栏标题在 <c>~/.zcode/v2/tasks-index.sqlite</c>，
/// 工作区上次会话还写在 Electron Local Storage。
/// 只删 cli 会话库时，这两处都会把标题拼回来。</summary>
internal static class ZcodeDesktopStore
{
    private static readonly byte[] FileOriginPrefix =
        [..Encoding.ASCII.GetBytes("_file://"), 0, 1];

    public static string LocalStorageDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ZCode", "session", "Local Storage", "leveldb");

    public static string ExecRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".zcode", "cli", "exec");

    public static string TasksIndexPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".zcode", "v2", "tasks-index.sqlite");

    public static IEnumerable<string> OrphanExecIds()
    {
        if (!Directory.Exists(ExecRoot)) yield break;
        foreach (var dir in Directory.EnumerateDirectories(ExecRoot))
        {
            var id = Path.GetFileName(dir);
            if (id.StartsWith("sess_", StringComparison.OrdinalIgnoreCase))
                yield return id;
        }
    }

    public static bool DeleteExec(string id)
    {
        var dir = Path.Combine(ExecRoot, id);
        if (!Directory.Exists(dir)) return false;
        try
        {
            Directory.Delete(dir, recursive: true);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>keepListed=true 时 ids 是还活着的会话，只清指向已删 ID 的桌面引用。
    /// liveWorkspaces 有值时，没有活会话的工作区 last-session 一并删掉，避免空页签把标题栏拼回来。</summary>
    public static bool ClearSessionRefs(HashSet<string> ids, bool keepListed, IReadOnlySet<string>? liveWorkspaces = null)
    {
        if (!Directory.Exists(LocalStorageDir)) return false;
        try
        {
            using var db = new DB(new Options { CreateIfMissing = false }, LocalStorageDir);
            var drop = new List<byte[]>();
            var drafts = new List<(byte[] Key, byte[] Value)>();
            foreach (var kv in db)
            {
                if (!TryLocalStorageName(kv.Key, out var name)) continue;
                if (name.StartsWith("zcode-v4-last-session:", StringComparison.Ordinal))
                {
                    var sessionId = DecodeValue(kv.Value).Trim();
                    var workspace = WorkspaceFromLastSessionKey(name);
                    if (sessionId.Length > 0 && ShouldDrop(sessionId, ids, keepListed))
                        drop.Add(kv.Key.ToArray());
                    else if (keepListed && liveWorkspaces is not null
                        && !ContainsWorkspace(liveWorkspaces, workspace))
                        drop.Add(kv.Key.ToArray());
                    continue;
                }
                if (name.StartsWith("zcode-v4-composer-drafts:", StringComparison.Ordinal))
                    drafts.Add((kv.Key.ToArray(), kv.Value.ToArray()));
            }

            var changed = false;
            foreach (var key in drop)
            {
                db.Delete(key, new WriteOptions { Sync = true });
                changed = true;
            }
            foreach (var (key, value) in drafts)
            {
                if (!TryStripDraftScopes(value, ids, keepListed, out var next)) continue;
                if (next is null) db.Delete(key, new WriteOptions { Sync = true });
                else db.Put(key, next, new WriteOptions { Sync = true });
                changed = true;
            }
            return changed;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>侧栏「项目」列表读 tasks-index，不读 cli 会话库。
    /// keepListed=true 时把不在 live 里的可见任务标成已删除。</summary>
    public static bool ClearTaskIndex(HashSet<string> ids, bool keepListed)
    {
        if (!File.Exists(TasksIndexPath)) return false;
        try
        {
            var cs = new SqliteConnectionStringBuilder
            {
                DataSource = TasksIndexPath,
                Mode = SqliteOpenMode.ReadWrite,
                Pooling = false,
            };
            using var conn = new SqliteConnection(cs.ToString());
            conn.Open();
            using (var busy = conn.CreateCommand())
            {
                busy.CommandText = "PRAGMA busy_timeout=3000";
                busy.ExecuteNonQuery();
            }

            var drop = new List<string>();
            using (var list = conn.CreateCommand())
            {
                list.CommandText = "SELECT task_id FROM tasks WHERE deleted = 0 AND archived = 0";
                using var r = list.ExecuteReader();
                while (r.Read())
                {
                    var id = r.IsDBNull(0) ? "" : r.GetString(0);
                    if (id.Length > 0 && ShouldDrop(id, ids, keepListed))
                        drop.Add(id);
                }
            }
            if (drop.Count == 0) return false;

            var ms = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            using var tx = conn.BeginTransaction();
            foreach (var id in drop)
            {
                using (var upd = conn.CreateCommand())
                {
                    upd.Transaction = tx;
                    upd.CommandText = """
                        UPDATE tasks
                        SET deleted = 1, archived = 1, updated_at = $ms
                        WHERE task_id = $id
                        """;
                    upd.Parameters.AddWithValue("$ms", ms);
                    upd.Parameters.AddWithValue("$id", id);
                    upd.ExecuteNonQuery();
                }
                using var ord = conn.CreateCommand();
                ord.Transaction = tx;
                ord.CommandText = "DELETE FROM task_group_view_node_orders WHERE node_key LIKE $like";
                ord.Parameters.AddWithValue("$like", "%" + id + "%");
                ord.ExecuteNonQuery();
            }
            tx.Commit();
            using var ck = conn.CreateCommand();
            ck.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
            ck.ExecuteNonQuery();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool ShouldDrop(string value, HashSet<string> ids, bool keepListed) =>
        keepListed ? !ids.Contains(value) : ids.Contains(value);

    internal static string NormalizeWorkspace(string path)
    {
        var text = path.Trim().TrimEnd('\\', '/');
        if (text.Length == 0) return "";
        try { return Path.GetFullPath(text); }
        catch (Exception) { return text; }
    }

    internal static bool ContainsWorkspace(IReadOnlySet<string> live, string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var norm = NormalizeWorkspace(path);
        if (live.Contains(norm)) return true;
        foreach (var item in live)
        {
            if (item.Equals(norm, StringComparison.OrdinalIgnoreCase)
                || item.Equals(path.Trim(), StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static string WorkspaceFromLastSessionKey(string name)
    {
        const string mark = ":v1:";
        var i = name.IndexOf(mark, StringComparison.Ordinal);
        return i < 0 ? "" : name[(i + mark.Length)..];
    }

    private static bool TryLocalStorageName(byte[] key, out string name)
    {
        name = "";
        if (key.Length <= FileOriginPrefix.Length) return false;
        if (!key.AsSpan().StartsWith(FileOriginPrefix)) return false;
        name = Encoding.UTF8.GetString(key, FileOriginPrefix.Length, key.Length - FileOriginPrefix.Length);
        return name.Length > 0;
    }

    private static string DecodeValue(byte[] value)
    {
        if (value.Length == 0) return "";
        if (value[0] == 1)
            return Encoding.UTF8.GetString(value, 1, value.Length - 1);
        if (value[0] == 0)
            return Encoding.Unicode.GetString(value, 1, value.Length - 1);
        return Encoding.UTF8.GetString(value);
    }

    private static byte[] EncodeUtf8Value(string text)
    {
        var body = Encoding.UTF8.GetBytes(text);
        var raw = new byte[body.Length + 1];
        raw[0] = 1;
        Buffer.BlockCopy(body, 0, raw, 1, body.Length);
        return raw;
    }

    private static bool TryStripDraftScopes(
        byte[] value, HashSet<string> ids, bool keepListed, out byte[]? next)
    {
        next = null;
        var json = DecodeValue(value);
        if (json.Length == 0) return false;
        try
        {
            var node = JsonNode.Parse(json);
            if (node is not JsonObject root) return false;
            if (root["scopes"] is not JsonObject scopes) return false;
            var drop = new List<string>();
            foreach (var kv in scopes)
            {
                if (ShouldDrop(kv.Key, ids, keepListed))
                    drop.Add(kv.Key);
            }
            if (drop.Count == 0) return false;
            foreach (var key in drop) scopes.Remove(key);
            if (scopes.Count == 0)
                return true;
            next = EncodeUtf8Value(root.ToJsonString(new JsonSerializerOptions { WriteIndented = false }));
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
