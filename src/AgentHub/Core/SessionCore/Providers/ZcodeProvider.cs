using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentHub.Core.TokenCore;
using Microsoft.Data.Sqlite;

namespace AgentHub.Core.SessionCore.Providers;

/// <summary>ZCode 会话：~/.zcode/cli/db/db.sqlite 的 session / message / part。
/// 读库拷三件套（与用量同一份）；改名写回 session.title；删除清该会话相关行。
/// 侧栏标题还写在 Electron last-session 里，写库前必须先退出，删完一并清桌面缓存。</summary>
public sealed class ZcodeProvider(TitleOverrideStore titles) : IConversationProvider
{
    public string AgentId => "zcode";

    public static bool ZcodeRunning()
    {
        try
        {
            foreach (var process in Process.GetProcesses())
            {
                using (process)
                    if (process.ProcessName.Contains("zcode", StringComparison.OrdinalIgnoreCase))
                        return true;
            }
        }
        catch (Exception) { }
        return false;
    }

    private static void EnsureWritable(string what)
    {
        if (ZcodeRunning())
            throw new InvalidOperationException($"{what}需要先完全退出 ZCode（包括托盘）后重试——应用还在跑时侧栏标题不会消失。");
    }

    public Task<IReadOnlyList<ConversationSummary>> ListAsync() => Task.Run<IReadOnlyList<ConversationSummary>>(() =>
    {
        if (!ZcodeLocal.TrySnapshot(out var db, out var tmp)) return [];
        try
        {
            using var conn = OpenRead(db);
            var counts = ReadCounts(conn);
            var sizes = ReadSizes(conn);
            var list = new List<ConversationSummary>();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT id, title, directory, parent_id, task_type, time_created, time_updated
                FROM session
                """;
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var id = r.IsDBNull(0) ? "" : r.GetString(0);
                if (string.IsNullOrEmpty(id)) continue;
                var title = Clean(r.IsDBNull(1) ? null : r.GetString(1));
                var directory = Clean(r.IsDBNull(2) ? null : r.GetString(2));
                var parentId = Clean(r.IsDBNull(3) ? null : r.GetString(3));
                var taskType = r.IsDBNull(4) ? "" : r.GetString(4);
                var created = ReadMs(r, 5);
                var updated = ReadMs(r, 6);
                var overrideTitle = titles.Get(AgentId, id);
                list.Add(new ConversationSummary
                {
                    AgentId = AgentId,
                    Id = id,
                    Title = overrideTitle ?? title ?? "(无标题)",
                    TitleSource = overrideTitle is not null ? "override" : title is not null ? "source" : "derived",
                    Project = directory,
                    MessageCount = counts.GetValueOrDefault(id),
                    SizeBytes = sizes.GetValueOrDefault(id),
                    LastActivityUtc = updated ?? created ?? DateTime.UtcNow,
                    IsSubagent = parentId is not null
                        || taskType.Equals("subagent_child", StringComparison.OrdinalIgnoreCase),
                    ParentId = parentId,
                    SourceFile = Directory.Exists(directory) ? directory! : "",
                });
            }
            return list;
        }
        finally { ZcodeLocal.DeleteSnapshot(tmp); }
    });

    public Task<ConversationDetail?> LoadAsync(string id)
    {
        CodexProvider.GuardDbId(id);
        return Task.Run<ConversationDetail?>(() =>
        {
            if (!ZcodeLocal.TrySnapshot(out var db, out var tmp)) return null;
            try
            {
                using var conn = OpenRead(db);
                using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    SELECT id, title, directory, parent_id, task_type, time_created, time_updated
                    FROM session WHERE id = $id
                    """;
                cmd.Parameters.AddWithValue("$id", id);
                using var r = cmd.ExecuteReader();
                if (!r.Read()) return null;
                var title = Clean(r.IsDBNull(1) ? null : r.GetString(1));
                var directory = Clean(r.IsDBNull(2) ? null : r.GetString(2));
                var parentId = Clean(r.IsDBNull(3) ? null : r.GetString(3));
                var taskType = r.IsDBNull(4) ? "" : r.GetString(4);
                var created = ReadMs(r, 5);
                var updated = ReadMs(r, 6);
                r.Close();

                var messages = ReadMessages(conn, id);
                var overrideTitle = titles.Get(AgentId, id);
                var total = messages.Count;
                var capped = CapLast(messages);
                return new ConversationDetail
                {
                    Summary = new ConversationSummary
                    {
                        AgentId = AgentId,
                        Id = id,
                        Title = overrideTitle ?? title ?? "(无标题)",
                        TitleSource = overrideTitle is not null ? "override" : title is not null ? "source" : "derived",
                        Project = directory,
                        MessageCount = total,
                        SizeBytes = ReadSizes(conn).GetValueOrDefault(id),
                        LastActivityUtc = updated ?? created ?? DateTime.UtcNow,
                        IsSubagent = parentId is not null
                            || taskType.Equals("subagent_child", StringComparison.OrdinalIgnoreCase),
                        ParentId = parentId,
                        SourceFile = Directory.Exists(directory) ? directory! : "",
                    },
                    Messages = capped,
                    Note = total > 200 ? $"共 {total} 条消息，预览仅显示最近 200 条。" : null,
                };
            }
            finally { ZcodeLocal.DeleteSnapshot(tmp); }
        });
    }

    public Task RenameAsync(string id, string title)
    {
        CodexProvider.GuardDbId(id);
        titles.Set(AgentId, id, title);
        if (!ZcodeLocal.DbExists)
            throw new FileNotFoundException("未找到 ZCode 会话库");
        try
        {
            using var conn = OpenWrite();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE session
                SET title = $title, title_source = 'user', time_title_updated = $ms
                WHERE id = $id
                """;
            cmd.Parameters.AddWithValue("$title", title);
            cmd.Parameters.AddWithValue("$ms", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            cmd.Parameters.AddWithValue("$id", id);
            if (cmd.ExecuteNonQuery() == 0)
                throw new FileNotFoundException($"会话不存在：{id}");
        }
        catch (SqliteException ex)
        {
            throw new IOException("无法写入 ZCode 会话库，请先退出 ZCode 再改标题。", ex);
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<DeleteItemResult>> DeleteAsync(IEnumerable<string> ids) => Task.Run<IReadOnlyList<DeleteItemResult>>(() =>
    {
        EnsureWritable("删除会话");
        var results = new List<DeleteItemResult>();
        if (!ZcodeLocal.DbExists)
        {
            foreach (var id in ids)
                results.Add(new DeleteItemResult { AgentId = AgentId, Id = id, Ok = false, Error = "未找到 ZCode 会话库" });
            return results;
        }

        try
        {
            using var conn = OpenWrite();
            using var tx = conn.BeginTransaction();
            foreach (var id in ids)
            {
                try
                {
                    CodexProvider.GuardDbId(id);
                    var size = SizeOf(conn, id);
                    if (!SessionExists(conn, id))
                    {
                        var leftover = DeleteLeftovers(id);
                        titles.Remove(AgentId, id);
                        results.Add(new DeleteItemResult
                        {
                            AgentId = AgentId,
                            Id = id,
                            Ok = true,
                            Note = leftover
                                ? "库里已无此行，已清桌面残留标题。"
                                : "库里已无此行。",
                        });
                        continue;
                    }
                    DeleteSessionRows(conn, id);
                    DeleteLeftovers(id);
                    titles.Remove(AgentId, id);
                    results.Add(new DeleteItemResult
                    {
                        AgentId = AgentId,
                        Id = id,
                        Ok = true,
                        FreedBytes = size,
                        Note = "已从 ZCode 库删除，库文件要等 ZCode 自己整理才会变小。",
                    });
                }
                catch (Exception ex)
                {
                    results.Add(new DeleteItemResult { AgentId = AgentId, Id = id, Ok = false, Error = ex.Message });
                }
            }
            tx.Commit();
        }
        catch (SqliteException ex)
        {
            foreach (var id in ids)
            {
                if (results.Any(r => r.Id == id)) continue;
                results.Add(new DeleteItemResult
                {
                    AgentId = AgentId,
                    Id = id,
                    Ok = false,
                    Error = "无法写入 ZCode 会话库，请先退出 ZCode 再删除。",
                });
            }
            if (results.Count == 0)
                results.Add(new DeleteItemResult { AgentId = AgentId, Id = "", Ok = false, Error = ex.Message });
        }
        if (results.Any(r => r.Ok))
            SweepOrphanLeftovers();
        return results;
    });

    // ------------------------------------------------------------------

    private static SqliteConnection OpenRead(string path)
    {
        var cs = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        };
        var conn = new SqliteConnection(cs.ToString());
        conn.Open();
        using var pragma = conn.CreateCommand();
        pragma.CommandText = "PRAGMA query_only=ON";
        pragma.ExecuteNonQuery();
        return conn;
    }

    private static SqliteConnection OpenWrite()
    {
        var cs = new SqliteConnectionStringBuilder
        {
            DataSource = ZcodeLocal.DbPath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        };
        var conn = new SqliteConnection(cs.ToString());
        conn.Open();
        using var busy = conn.CreateCommand();
        busy.CommandText = "PRAGMA busy_timeout=3000";
        busy.ExecuteNonQuery();
        return conn;
    }

    private static Dictionary<string, long> ReadCounts(SqliteConnection conn)
    {
        var map = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT session_id, COUNT(DISTINCT message_id)
            FROM part
            WHERE json_extract(data, '$.type') = 'text'
            GROUP BY session_id
            """;
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var id = r.IsDBNull(0) ? "" : r.GetString(0);
            if (id.Length == 0) continue;
            map[id] = r.GetInt64(1);
        }
        return map;
    }

    private static Dictionary<string, long> ReadSizes(SqliteConnection conn)
    {
        var map = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT session_id, COALESCE(SUM(LENGTH(data)), 0) FROM message GROUP BY session_id
            """;
        AddSizes(cmd, map);
        using var cmd2 = conn.CreateCommand();
        cmd2.CommandText = """
            SELECT session_id, COALESCE(SUM(LENGTH(data)), 0) FROM part GROUP BY session_id
            """;
        AddSizes(cmd2, map);
        return map;
    }

    private static void AddSizes(SqliteCommand cmd, Dictionary<string, long> map)
    {
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var id = r.IsDBNull(0) ? "" : r.GetString(0);
            if (id.Length == 0) continue;
            map[id] = map.GetValueOrDefault(id) + r.GetInt64(1);
        }
    }

    private static List<ConversationMessage> ReadMessages(SqliteConnection conn, string id)
    {
        var parts = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        using (var pcmd = conn.CreateCommand())
        {
            pcmd.CommandText = """
                SELECT message_id, data FROM part
                WHERE session_id = $id
                ORDER BY sequence, time_created
                """;
            pcmd.Parameters.AddWithValue("$id", id);
            using var pr = pcmd.ExecuteReader();
            while (pr.Read())
            {
                var mid = pr.IsDBNull(0) ? "" : pr.GetString(0);
                var raw = pr.IsDBNull(1) ? "" : pr.GetString(1);
                var text = TextFromPart(raw);
                if (mid.Length == 0 || text is null) continue;
                if (!parts.TryGetValue(mid, out var list))
                {
                    list = [];
                    parts[mid] = list;
                }
                list.Add(text);
            }
        }

        var messages = new List<ConversationMessage>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, time_created, data FROM message
            WHERE session_id = $id
            ORDER BY sequence, time_created
            """;
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var mid = r.IsDBNull(0) ? "" : r.GetString(0);
            var ts = ReadMs(r, 1);
            var role = RoleFromMessage(r.IsDBNull(2) ? "" : r.GetString(2));
            if (role is null || !parts.TryGetValue(mid, out var texts) || texts.Count == 0) continue;
            var text = string.Join("\n\n", texts).Trim();
            if (text.Length == 0) continue;
            messages.Add(new ConversationMessage { Role = role, TimestampUtc = ts, Text = text });
        }
        return messages;
    }

    private static string? TextFromPart(string raw)
    {
        if (raw.Length == 0) return null;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var t) || t.GetString() != "text") return null;
            if (!root.TryGetProperty("text", out var txt) || txt.ValueKind != JsonValueKind.String) return null;
            var s = txt.GetString();
            return string.IsNullOrWhiteSpace(s) ? null : s.Trim();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? RoleFromMessage(string raw)
    {
        if (raw.Length == 0) return null;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (!doc.RootElement.TryGetProperty("role", out var role) || role.ValueKind != JsonValueKind.String)
                return null;
            var s = role.GetString();
            return s is "user" or "assistant" ? s : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool SessionExists(SqliteConnection conn, string id)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM session WHERE id = $id LIMIT 1";
        cmd.Parameters.AddWithValue("$id", id);
        return cmd.ExecuteScalar() is not null;
    }

    private static long SizeOf(SqliteConnection conn, string id)
    {
        long n = 0;
        foreach (var table in new[] { "message", "part" })
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT COALESCE(SUM(LENGTH(data)), 0) FROM {table} WHERE session_id = $id";
            cmd.Parameters.AddWithValue("$id", id);
            n += Convert.ToInt64(cmd.ExecuteScalar());
        }
        return n;
    }

    private static void DeleteSessionRows(SqliteConnection conn, string id)
    {
        using (var off = conn.CreateCommand())
        {
            off.CommandText = "PRAGMA foreign_keys=OFF";
            off.ExecuteNonQuery();
        }

        var sessionCols = new[] { "session_id", "parent_session_id", "child_session_id" };
        using (var tables = conn.CreateCommand())
        {
            tables.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table'";
            using var r = tables.ExecuteReader();
            var names = new List<string>();
            while (r.Read()) names.Add(r.GetString(0));
            r.Close();
            foreach (var name in names)
            {
                if (name.Equals("session", StringComparison.OrdinalIgnoreCase)) continue;
                var cols = TableColumns(conn, name);
                foreach (var col in sessionCols)
                {
                    if (!cols.Contains(col, StringComparer.OrdinalIgnoreCase)) continue;
                    using var del = conn.CreateCommand();
                    del.CommandText = $"DELETE FROM \"{name}\" WHERE \"{col}\" = $id";
                    del.Parameters.AddWithValue("$id", id);
                    del.ExecuteNonQuery();
                }
            }
        }

        using var drop = conn.CreateCommand();
        drop.CommandText = "DELETE FROM session WHERE id = $id";
        drop.Parameters.AddWithValue("$id", id);
        drop.ExecuteNonQuery();
    }

    /// <summary>清已不在 session 表里的产物、rollout、exec，以及桌面 last-session / setting.json。</summary>
    public static int SweepOrphanLeftovers()
    {
        var live = LiveSessionIds();
        var n = 0;
        foreach (var id in OrphanArtifactIds())
        {
            if (live.Contains(id)) continue;
            if (DeleteLeftovers(id, desktop: false)) n++;
        }
        if (ClearDesktopPersist(live)) n++;
        if (ZcodeDesktopStore.ClearSessionRefs(live, keepListed: true)) n++;
        return n;
    }

    /// <summary>库行删了之后，产物 / exec / last-session 还会把标题栏拼回来。</summary>
    private static bool DeleteLeftovers(string id, bool desktop = true)
    {
        var changed = false;
        var cli = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".zcode", "cli");
        var artifacts = Path.Combine(cli, "artifacts", id);
        if (Directory.Exists(artifacts))
        {
            try { Directory.Delete(artifacts, recursive: true); changed = true; }
            catch (Exception) { }
        }
        var rollout = Path.Combine(cli, "rollout", "model-io-" + id + ".jsonl");
        if (File.Exists(rollout))
        {
            try { File.Delete(rollout); changed = true; }
            catch (Exception) { }
        }
        if (ZcodeDesktopStore.DeleteExec(id)) changed = true;
        if (desktop && ClearDesktopPersist(id)) changed = true;
        if (desktop && ZcodeDesktopStore.ClearSessionRefs(
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { id }, keepListed: false))
            changed = true;
        return changed;
    }

    private static HashSet<string> LiveSessionIds()
    {
        var live = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!ZcodeLocal.DbExists) return live;
        try
        {
            using var conn = OpenRead(ZcodeLocal.DbPath);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT id FROM session";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var id = r.IsDBNull(0) ? "" : r.GetString(0);
                if (id.Length > 0) live.Add(id);
            }
        }
        catch (Exception) { }
        return live;
    }

    private static IEnumerable<string> OrphanArtifactIds()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var cli = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".zcode", "cli");
        var artifacts = Path.Combine(cli, "artifacts");
        if (Directory.Exists(artifacts))
        {
            foreach (var dir in Directory.EnumerateDirectories(artifacts))
            {
                var id = Path.GetFileName(dir);
                if (id.StartsWith("sess_", StringComparison.OrdinalIgnoreCase) && seen.Add(id))
                    yield return id;
            }
        }
        var rollout = Path.Combine(cli, "rollout");
        if (Directory.Exists(rollout))
        {
            foreach (var file in Directory.EnumerateFiles(rollout, "model-io-sess_*.jsonl"))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                var id = name.StartsWith("model-io-", StringComparison.OrdinalIgnoreCase)
                    ? name["model-io-".Length..] : "";
                if (id.Length > 0 && seen.Add(id))
                    yield return id;
            }
        }
        foreach (var id in ZcodeDesktopStore.OrphanExecIds())
        {
            if (seen.Add(id))
                yield return id;
        }
    }

    private static bool ClearDesktopPersist(string id) =>
        ClearDesktopPersist(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { id }, keepListed: false);

    /// <summary>清 setting.json 里的 initialTaskId / lastActiveTaskByWorkspace。
    /// keepListed=true 时只清不在 live 集合里的引用。</summary>
    private static bool ClearDesktopPersist(HashSet<string> ids, bool keepListed = true)
    {
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".zcode", "v2", "setting.json");
        if (!File.Exists(path)) return false;
        try
        {
            var node = JsonNode.Parse(File.ReadAllText(path));
            if (node is not JsonObject root) return false;
            var changed = false;
            if (root["webRemoteControlLastEnabledContext"] is JsonObject ctx
                && ctx["initialTaskId"]?.GetValue<string>() is { Length: > 0 } taskId
                && ShouldDropPersist(taskId, ids, keepListed))
            {
                ctx.Remove("initialTaskId");
                changed = true;
            }
            if (root["lastActiveTaskByWorkspace"] is JsonObject map)
            {
                var drop = new List<string>();
                foreach (var kv in map)
                {
                    var value = kv.Value?.GetValue<string>();
                    if (!string.IsNullOrEmpty(value) && ShouldDropPersist(value, ids, keepListed))
                        drop.Add(kv.Key);
                }
                foreach (var key in drop)
                {
                    map.Remove(key);
                    changed = true;
                }
            }
            if (!changed) return false;
            File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool ShouldDropPersist(string value, HashSet<string> ids, bool keepListed) =>
        keepListed ? !ids.Contains(value) : ids.Contains(value);

    private static HashSet<string> TableColumns(SqliteConnection conn, string table)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info(\"{table}\")";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            if (!r.IsDBNull(1)) set.Add(r.GetString(1));
        }
        return set;
    }

    private static IReadOnlyList<ConversationMessage> CapLast(List<ConversationMessage> messages)
    {
        if (messages.Count > 200)
            messages = messages.Skip(messages.Count - 200).ToList();
        for (var i = 0; i < messages.Count; i++)
        {
            var m = messages[i];
            if (m.Text.Length > 4000)
                messages[i] = m with { Text = m.Text[..4000] + "\n…（截断）" };
        }
        return messages;
    }

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static DateTime? ReadMs(SqliteDataReader r, int i)
    {
        if (r.IsDBNull(i)) return null;
        var ms = r.GetFieldType(i) == typeof(long) ? r.GetInt64(i) : Convert.ToInt64(r.GetValue(i));
        return UsageParsers.ParseMs(ms);
    }
}
