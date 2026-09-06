using System.IO;
using System.Net.Http;
using AgentHub.Core.TokenCore;
using Microsoft.Data.Sqlite;

namespace AgentHub.Core.SessionCore.Providers;

/// <summary>WorkBuddy 本机侧栏软删 + 云端删除 + 映射清理。</summary>
internal static class WorkBuddySidebar
{
    private static readonly HttpClient Http = new(new HttpClientHandler { UseProxy = true })
    {
        Timeout = TimeSpan.FromSeconds(15),
    };

    internal static string DbPath => Path.Combine(WorkBuddyAuth.Root, "workbuddy.db");
    private static readonly string[] MappingPaths =
    [
        Path.Combine(WorkBuddyAuth.Root, "edge-sync-mapping-v4.db"),
        Path.Combine(WorkBuddyAuth.Root, "edge-sync-mapping-v3.db"),
        Path.Combine(WorkBuddyAuth.Root, "edge-sync-mapping-v2.db"),
        Path.Combine(WorkBuddyAuth.Root, "edge-sync-mapping.db"),
    ];

    public sealed record SessionMetadata(string? Title, string? Cwd, DateTime? LastActivityUtc);
    public sealed record SoftDeleteOutcome(bool SchemaOk, bool Wrote, int Rows, string? Warning);

    public static IReadOnlyDictionary<string, SessionMetadata> ReadSessionMetadata()
    {
        var result = new Dictionary<string, SessionMetadata>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(DbPath)) return result;
        try
        {
            using var conn = OpenRead(DbPath);
            if (!SessionMetadataSchemaOk(conn)) return result;
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT id,
                       COALESCE(NULLIF(TRIM(custom_title), ''), NULLIF(TRIM(title), '')),
                       cwd,
                       last_activity_at
                FROM sessions
                WHERE deleted_at IS NULL OR deleted_at = 0
                """;
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var id = r.GetString(0);
                var title = r.IsDBNull(1) ? null : r.GetString(1);
                var cwd = r.IsDBNull(2) ? null : r.GetString(2);
                var lastActivity = r.IsDBNull(3) ? null : UnixMs(r.GetInt64(3));
                result[id] = new SessionMetadata(title, cwd, lastActivity);
            }
        }
        catch (Exception)
        {
            // WorkBuddy 正在迁移或锁库时保留 JSONL 兼容路径。
        }
        return result;
    }

    public static bool Rename(string id, string title)
    {
        if (!File.Exists(DbPath)) return false;
        using var conn = OpenWrite(DbPath);
        if (!SessionMetadataSchemaOk(conn)) return false;
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE sessions SET custom_title = $title WHERE id = $id";
        cmd.Parameters.AddWithValue("$title", title);
        cmd.Parameters.AddWithValue("$id", id);
        return cmd.ExecuteNonQuery() > 0;
    }

    public static SoftDeleteOutcome TrySoftDelete(string id)
    {
        if (!File.Exists(DbPath))
            return new SoftDeleteOutcome(false, false, 0, "未找到 workbuddy.db，本机侧栏未改");
        try
        {
            using var conn = OpenWrite(DbPath);
            if (!SessionsSchemaOk(conn))
                return new SoftDeleteOutcome(false, false, 0, "sessions 表结构不符，已跳过写库");

            using var tx = conn.BeginTransaction();
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                UPDATE sessions
                SET deleted_at = $ms, updated_at = $ms
                WHERE id = $id AND (deleted_at IS NULL OR deleted_at = 0)
                """;
            cmd.Parameters.AddWithValue("$ms", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            cmd.Parameters.AddWithValue("$id", id);
            var n = cmd.ExecuteNonQuery();
            tx.Commit();
            return new SoftDeleteOutcome(true, true, n, null);
        }
        catch (Exception ex)
        {
            return new SoftDeleteOutcome(true, false, 0, "本机侧栏软删失败：" + ex.Message);
        }
    }

    /// <summary>本机已软删、映射库还挂着的会话，再打一遍云端删除。</summary>
    public static (int Attempted, int Ok, string? Warning) SweepCloudDeleted(string? settingsSession = null)
    {
        var auth = WorkBuddyAuth.Read(settingsSession);
        var ids = DeletedOrMappedIds();
        var attempted = 0;
        var ok = 0;
        string? warning = null;
        foreach (var id in ids)
        {
            attempted++;
            var warn = TryCloudDelete(id, auth);
            if (warn is null) ok++;
            else warning ??= warn;
        }
        return (attempted, ok, warning);
    }

    public static string? TryCloudDelete(string id, WorkBuddyAuth.Probe auth)
    {
        if (!auth.HasSession || string.IsNullOrEmpty(auth.Bearer))
            return "未找到本机登录态，云端列表请在 WorkBuddy 里再删一次";

        var conversationId = LookupConversationId(id) ?? id;
        try
        {
            var url = "https://www.workbuddy.cn/console/as/conversations/" + Uri.EscapeDataString(conversationId) + "/delete";
            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            req.Headers.TryAddWithoutValidation("accept", "application/json");
            req.Headers.TryAddWithoutValidation("user-agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/149.0.0.0 Safari/537.36 Edg/149.0.0.0");
            req.Headers.TryAddWithoutValidation("x-client-platform", "web");
            req.Headers.TryAddWithoutValidation("origin", "https://www.workbuddy.cn");
            req.Headers.TryAddWithoutValidation("referer", "https://www.workbuddy.cn/");
            req.Headers.TryAddWithoutValidation("cookie", "session=" + auth.Bearer);
            if (auth.Bearer.StartsWith("eyJ", StringComparison.Ordinal) && auth.Bearer.Count(c => c == '.') >= 2)
                req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", auth.Bearer);
            if (!string.IsNullOrEmpty(auth.UserId))
                req.Headers.TryAddWithoutValidation("X-User-Id", auth.UserId);
            req.Headers.TryAddWithoutValidation("X-Domain", "www.workbuddy.cn");
            using var resp = Http.Send(req);
            var code = (int)resp.StatusCode;
            if (code is 200 or 204 or 404)
            {
                TryClearMapping(id, conversationId);
                return null;
            }
            return "云端列表可能还在，请在 WorkBuddy 里再删一次（HTTP " + code + "）";
        }
        catch (Exception ex)
        {
            return "云端列表可能还在，请在 WorkBuddy 里再删一次（" + ex.Message + "）";
        }
    }

    private static HashSet<string> DeletedOrMappedIds()
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var live = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (File.Exists(DbPath))
        {
            try
            {
                using var conn = OpenRead(DbPath);
                if (SessionsSchemaOk(conn))
                {
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = """
                        SELECT id FROM sessions
                        WHERE deleted_at IS NULL OR deleted_at = 0
                        """;
                    using var r = cmd.ExecuteReader();
                    while (r.Read()) live.Add(r.GetString(0));
                }
            }
            catch (Exception) { }
        }
        foreach (var path in MappingPaths)
        {
            if (!File.Exists(path)) continue;
            try
            {
                using var conn = OpenRead(path);
                if (!MappingSchemaOk(conn)) continue;
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT session_id, conversation_id FROM edge_sync_mapping";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    var sid = r.IsDBNull(0) ? "" : r.GetString(0);
                    var cid = r.IsDBNull(1) ? "" : r.GetString(1);
                    if (sid.Length > 0 && !live.Contains(sid)) ids.Add(sid);
                    if (cid.Length > 0 && !cid.StartsWith("convmsg:", StringComparison.OrdinalIgnoreCase)
                        && !live.Contains(cid))
                        ids.Add(cid);
                }
            }
            catch (Exception) { }
        }
        return ids;
    }

    private static string? LookupConversationId(string sessionId)
    {
        foreach (var path in MappingPaths)
        {
            if (!File.Exists(path)) continue;
            try
            {
                using var conn = OpenRead(path);
                if (!MappingSchemaOk(conn)) continue;
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT conversation_id FROM edge_sync_mapping WHERE session_id = $id LIMIT 1";
                cmd.Parameters.AddWithValue("$id", sessionId);
                if (cmd.ExecuteScalar() is string found && found.Length > 0)
                    return found;
            }
            catch (Exception)
            {
                // 旧版映射打不开就试下一份
            }
        }
        return null;
    }

    private static void TryClearMapping(string sessionId, string conversationId)
    {
        foreach (var path in MappingPaths)
        {
            if (!File.Exists(path)) continue;
            try
            {
                using var conn = OpenWrite(path);
                if (!MappingSchemaOk(conn)) continue;
                using var tx = conn.BeginTransaction();
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = """
                    DELETE FROM edge_sync_mapping
                    WHERE session_id = $sid OR conversation_id = $cid OR conversation_id = $sid
                    """;
                cmd.Parameters.AddWithValue("$sid", sessionId);
                cmd.Parameters.AddWithValue("$cid", conversationId);
                cmd.ExecuteNonQuery();
                tx.Commit();
            }
            catch (Exception)
            {
                // 映射清不掉不影响本地软删结果
            }
        }
    }

    private static bool SessionsSchemaOk(SqliteConnection conn)
    {
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='sessions'";
            if (cmd.ExecuteScalar() is null) return false;
            var cols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using var info = conn.CreateCommand();
            info.CommandText = "PRAGMA table_info(sessions)";
            using var r = info.ExecuteReader();
            while (r.Read())
                cols.Add(r.GetString(1));
            return cols.Contains("id") && cols.Contains("deleted_at");
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool SessionMetadataSchemaOk(SqliteConnection conn)
    {
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='sessions'";
            if (cmd.ExecuteScalar() is null) return false;
            var cols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using var info = conn.CreateCommand();
            info.CommandText = "PRAGMA table_info(sessions)";
            using var r = info.ExecuteReader();
            while (r.Read())
                cols.Add(r.GetString(1));
            return cols.Contains("id") && cols.Contains("title") && cols.Contains("custom_title")
                && cols.Contains("cwd") && cols.Contains("last_activity_at") && cols.Contains("deleted_at");
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool MappingSchemaOk(SqliteConnection conn)
    {
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='edge_sync_mapping'";
            if (cmd.ExecuteScalar() is null) return false;
            var cols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using var info = conn.CreateCommand();
            info.CommandText = "PRAGMA table_info(edge_sync_mapping)";
            using var r = info.ExecuteReader();
            while (r.Read())
                cols.Add(r.GetString(1));
            return cols.Contains("session_id") && cols.Contains("conversation_id");
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static SqliteConnection OpenWrite(string path)
    {
        var cs = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWrite, Pooling = false };
        var conn = new SqliteConnection(cs.ToString());
        conn.Open();
        using var busy = conn.CreateCommand();
        busy.CommandText = "PRAGMA busy_timeout=5000";
        busy.ExecuteNonQuery();
        return conn;
    }

    private static SqliteConnection OpenRead(string path)
    {
        var cs = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false };
        var conn = new SqliteConnection(cs.ToString());
        conn.Open();
        using var q = conn.CreateCommand();
        q.CommandText = "PRAGMA query_only=ON; PRAGMA busy_timeout=3000";
        q.ExecuteNonQuery();
        return conn;
    }

    private static DateTime? UnixMs(long value)
    {
        try { return DateTimeOffset.FromUnixTimeMilliseconds(value).UtcDateTime; }
        catch (ArgumentOutOfRangeException) { return null; }
    }
}
