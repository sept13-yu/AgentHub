using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LevelDB;

namespace AgentHub.Core.SessionCore.Providers;

/// <summary>ZCode 桌面标题不在 SQLite 里：工作区上次会话写在 Electron Local Storage
/// （<c>zcode-v4-last-session:v1:{workspace}</c>）。库行删了、这个 key 还在，
/// 再开会按 ID 把标题栏拼回来，点开就是 sessionNotFound。</summary>
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

    /// <summary>keepListed=true 时 ids 是还活着的会话，只清指向已删 ID 的桌面引用。</summary>
    public static bool ClearSessionRefs(HashSet<string> ids, bool keepListed)
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
                    if (sessionId.Length > 0 && ShouldDrop(sessionId, ids, keepListed))
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

    private static bool ShouldDrop(string value, HashSet<string> ids, bool keepListed) =>
        keepListed ? !ids.Contains(value) : ids.Contains(value);

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
