using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentHub.Core.TokenCore;
using Microsoft.Data.Sqlite;

namespace AgentHub.Core.SessionCore.Providers;

/// <summary>MiMo Code 会话：~/.local/share/mimocode/mimocode.db → session / message / part。
/// 读库拷三件套；改名/删除在 Xiaomi MiMo 运行时拒绝写入。</summary>
public sealed class MimocodeProvider(TitleOverrideStore titles) : IConversationProvider
{
    private static readonly Regex SysReminder = new(
        @"<system-reminder[\s\S]*?</system-reminder>", RegexOptions.Compiled);

    public string AgentId => "mimocode";

    private static void EnsureWritable(string what)
    {
        if (MimocodeLocal.MimocodeRunning())
            throw new IOException($"{what}需要先完全退出 Xiaomi MiMo 后重试。");
    }

    public Task<IReadOnlyList<ConversationSummary>> ListAsync() => Task.Run<IReadOnlyList<ConversationSummary>>(() =>
    {
        if (!MimocodeLocal.TrySnapshot(out var db, out var tmp)) return [];
        try
        {
            using var conn = OpenRead(db);
            var counts = ReadCounts(conn);
            var sizes = ReadSizes(conn);
            var list = new List<ConversationSummary>();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT id, title, directory, parent_id, time_created, time_updated
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
                var created = ReadMs(r, 4);
                var updated = ReadMs(r, 5);
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
                    IsSubagent = parentId is not null,
                    ParentId = parentId,
                    SourceFile = MimocodeLocal.DbPath,
                });
            }
            return list;
        }
        finally { MimocodeLocal.DeleteSnapshot(tmp); }
    });

    public Task<ConversationDetail?> LoadAsync(string id)
    {
        CodexProvider.GuardDbId(id);
        return Task.Run<ConversationDetail?>(() =>
        {
            if (!MimocodeLocal.TrySnapshot(out var db, out var tmp)) return null;
            try
            {
                using var conn = OpenRead(db);
                using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    SELECT id, title, directory, parent_id, time_created, time_updated
                    FROM session WHERE id = $id
                    """;
                cmd.Parameters.AddWithValue("$id", id);
                using var r = cmd.ExecuteReader();
                if (!r.Read()) return null;
                var title = Clean(r.IsDBNull(1) ? null : r.GetString(1));
                var directory = Clean(r.IsDBNull(2) ? null : r.GetString(2));
                var parentId = Clean(r.IsDBNull(3) ? null : r.GetString(3));
                var created = ReadMs(r, 4);
                var updated = ReadMs(r, 5);
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
                        IsSubagent = parentId is not null,
                        ParentId = parentId,
                        SourceFile = MimocodeLocal.DbPath,
                    },
                    Messages = capped,
                    Note = total > 200 ? $"共 {total} 条消息，预览仅显示最后 200 条。" : null,
                };
            }
            finally { MimocodeLocal.DeleteSnapshot(tmp); }
        });
    }

    public Task RenameAsync(string id, string title)
    {
        CodexProvider.GuardDbId(id);
        titles.Set(AgentId, id, title);
        if (!MimocodeLocal.DbExists)
            throw new FileNotFoundException("未找到 MiMo 会话库");
        EnsureWritable("改标题");
        try
        {
            using var conn = OpenWrite();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE session
                SET title = $title, time_updated = $ms
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
            throw new IOException("无法写入 MiMo 会话库，请先退出 Xiaomi MiMo 再改标题。", ex);
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<DeleteItemResult>> DeleteAsync(IEnumerable<string> ids) => Task.Run<IReadOnlyList<DeleteItemResult>>(() =>
    {
        EnsureWritable("删除会话");
        var results = new List<DeleteItemResult>();
        if (!MimocodeLocal.DbExists)
        {
            foreach (var id in ids)
                results.Add(new DeleteItemResult { AgentId = AgentId, Id = id, Ok = false, Error = "未找到 MiMo 会话库" });
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
                        titles.Remove(AgentId, id);
                        results.Add(new DeleteItemResult
                        {
                            AgentId = AgentId,
                            Id = id,
                            Ok = true,
                            Note = "库里已无此行。",
                        });
                        continue;
                    }
                    DeleteSessionRows(conn, id);
                    titles.Remove(AgentId, id);
                    results.Add(new DeleteItemResult
                    {
                        AgentId = AgentId,
                        Id = id,
                        Ok = true,
                        FreedBytes = size,
                        Note = "已从 MiMo 库删除。",
                    });
                }
                catch (Exception ex)
                {
                    results.Add(new DeleteItemResult { AgentId = AgentId, Id = id, Ok = false, Error = ex.Message });
                }
            }
            tx.Commit();
        }
        catch (SqliteException)
        {
            foreach (var id in ids)
            {
                if (results.Any(r => r.Id == id)) continue;
                results.Add(new DeleteItemResult
                {
                    AgentId = AgentId,
                    Id = id,
                    Ok = false,
                    Error = "无法写入 MiMo 会话库，请先退出 Xiaomi MiMo 再删除。",
                });
            }
        }
        return results;
    });

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
            DataSource = MimocodeLocal.DbPath,
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
            SELECT session_id, COUNT(*)
            FROM message
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
                ORDER BY time_created
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
            ORDER BY time_created
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
            if (role == "user")
                text = StripSystemReminder(text);
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
            // 跳过 synthetic / system-reminder 段
            if (root.TryGetProperty("synthetic", out var syn)
                && syn.ValueKind == JsonValueKind.True)
                return null;
            if (!root.TryGetProperty("text", out var txt) || txt.ValueKind != JsonValueKind.String) return null;
            var s = txt.GetString();
            if (string.IsNullOrWhiteSpace(s)) return null;
            if (s.Contains("<system-reminder", StringComparison.OrdinalIgnoreCase))
                return null;
            return s.Trim();
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

    private static string StripSystemReminder(string text) => SysReminder.Replace(text, "").Trim();

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

        foreach (var table in new[] { "part", "message", "todo", "task", "session_share", "actor_registry", "task_event", "permission", "permission_grant" })
        {
            if (!TableExists(conn, table)) continue;
            var cols = TableColumns(conn, table);
            foreach (var col in new[] { "session_id", "id" })
            {
                if (col == "id" && !table.Equals("session", StringComparison.OrdinalIgnoreCase))
                {
                    // 仅 session 用 id；相关表优先 session_id
                    continue;
                }
                if (!cols.Contains(col, StringComparer.OrdinalIgnoreCase)) continue;
                using var del = conn.CreateCommand();
                del.CommandText = $"DELETE FROM \"{table}\" WHERE \"{col}\" = $id";
                del.Parameters.AddWithValue("$id", id);
                del.ExecuteNonQuery();
                break;
            }
        }

        using var drop = conn.CreateCommand();
        drop.CommandText = "DELETE FROM session WHERE id = $id";
        drop.Parameters.AddWithValue("$id", id);
        drop.ExecuteNonQuery();
    }

    private static bool TableExists(SqliteConnection conn, string table)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name=$n LIMIT 1";
        cmd.Parameters.AddWithValue("$n", table);
        return cmd.ExecuteScalar() is not null;
    }

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
