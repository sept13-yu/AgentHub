using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentHub.Core.TokenCore;
using Microsoft.Data.Sqlite;

namespace AgentHub.Core.SessionCore.Providers;

/// <summary>Qoder CN 会话：%APPDATA%/com.qodercn.app.stable/main.sqlite 的
/// chat_sessions / chat_session_messages（payload_json 明文）。
/// 列表/详情优先 sqlite；sqlite 正文缺行时再读 ~/.qoder-cn/projects/**/&lt;id&gt;.jsonl。
/// 改名默认写覆盖表（Qoder CN 在跑时不碰库）；删除须先退出，否则明确失败。</summary>
public sealed class QoderCnProvider(TitleOverrideStore titles) : IConversationProvider
{
    private static readonly Regex SysReminder = new(
        @"<system-reminder[\s\S]*?</system-reminder>", RegexOptions.Compiled);

    public string AgentId => "qoder-cn";

    public static bool QoderCnRunning() => QoderCnStore.QoderCnRunning();

    public Task<IReadOnlyList<ConversationSummary>> ListAsync() => Task.Run<IReadOnlyList<ConversationSummary>>(() =>
    {
        var src = QoderCnStore.DbPath;
        if (src is null || !QoderCnStore.TrySnapshot(src, out var db, out var tmp)) return [];
        try
        {
            using var conn = QoderCnStore.OpenRead(db);
            if (!QoderCnStore.TableExists(conn, "chat_sessions")) return [];
            var stats = ReadStats(conn);
            var list = new List<ConversationSummary>();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT * FROM chat_sessions";
            using var r = cmd.ExecuteReader();
            var cols = new ColMap(r);
            var idCol = cols.IdColumn();
            if (idCol is null) return list;
            while (r.Read())
            {
                var id = cols.Str(r, idCol);
                if (id is null) continue;
                if (cols.IsDeleted(r)) continue;
                list.Add(ToSummary(id, cols, r, stats.GetValueOrDefault(id), src, titles.Get(AgentId, id)));
            }
            return list;
        }
        catch (SqliteException)
        {
            return [];
        }
        finally { QoderCnStore.DeleteSnapshot(tmp); }
    });

    public Task<ConversationDetail?> LoadAsync(string id)
    {
        CodexProvider.GuardDbId(id);
        return Task.Run<ConversationDetail?>(() =>
        {
            var src = QoderCnStore.DbPath;
            if (src is null || !QoderCnStore.TrySnapshot(src, out var db, out var tmp)) return null;
            try
            {
                using var conn = QoderCnStore.OpenRead(db);
                if (!QoderCnStore.TableExists(conn, "chat_sessions")) return null;
                var sessionCols = QoderCnStore.TableColumns(conn, "chat_sessions");
                var idCol = sessionCols.Contains("session_id") ? "session_id"
                    : sessionCols.Contains("id") ? "id" : null;
                if (idCol is null) return null;
                using var cmd = conn.CreateCommand();
                cmd.CommandText = $"SELECT * FROM chat_sessions WHERE \"{idCol}\" = $id LIMIT 1";
                cmd.Parameters.AddWithValue("$id", id);
                using var r = cmd.ExecuteReader();
                if (!r.Read()) return null;
                var cols = new ColMap(r);
                var sid = cols.Str(r, cols.IdColumn() ?? "session_id") ?? id;
                var model = DisplayModel(cols.Str(r, "model"));
                var summary = ToSummary(sid, cols, r, null, src, titles.Get(AgentId, sid));
                r.Close();

                var stats = ReadStats(conn).GetValueOrDefault(sid);
                if (stats is not null)
                {
                    summary = summary with
                    {
                        MessageCount = stats.Count,
                        SizeBytes = stats.Size,
                        LastActivityUtc = stats.Last ?? summary.LastActivityUtc,
                    };
                }

                var messages = ReadSqliteMessages(conn, sid);
                if (messages.Count == 0)
                    messages = ReadJsonlMessages(sid);

                var total = messages.Count;
                var last = summary.LastActivityUtc;
                foreach (var m in messages)
                {
                    if (m.TimestampUtc is { } ts && ts > last) last = ts;
                }
                var notes = new List<string>();
                if (model is not null) notes.Add("模型：" + model);
                if (total > 200) notes.Add($"共 {total} 条消息，预览仅显示最近 200 条。");
                return new ConversationDetail
                {
                    Summary = summary with
                    {
                        MessageCount = total > 0 ? total : summary.MessageCount,
                        LastActivityUtc = last,
                    },
                    Messages = CapLast(messages),
                    Note = notes.Count == 0 ? null : string.Join(" ", notes),
                };
            }
            catch (SqliteException)
            {
                return null;
            }
            finally { QoderCnStore.DeleteSnapshot(tmp); }
        });
    }

    public Task RenameAsync(string id, string title)
    {
        CodexProvider.GuardDbId(id);
        titles.Set(AgentId, id, title);
        if (QoderCnRunning()) return Task.CompletedTask;
        var db = QoderCnStore.DbPath;
        if (db is null) return Task.CompletedTask;
        try
        {
            using var conn = QoderCnStore.OpenWrite(db);
            if (!QoderCnStore.TableExists(conn, "chat_sessions"))
                throw new FileNotFoundException("未找到 Qoder CN 会话表");
            var cols = QoderCnStore.TableColumns(conn, "chat_sessions");
            if (!cols.Contains("title")) return Task.CompletedTask;
            var idCol = cols.Contains("session_id") ? "session_id" : cols.Contains("id") ? "id" : null;
            if (idCol is null) return Task.CompletedTask;
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"UPDATE chat_sessions SET title = $title WHERE \"{idCol}\" = $id";
            cmd.Parameters.AddWithValue("$title", title);
            cmd.Parameters.AddWithValue("$id", id);
            if (cmd.ExecuteNonQuery() == 0)
                throw new FileNotFoundException($"会话不存在：{id}");
        }
        catch (SqliteException ex)
        {
            throw new IOException("无法写入 Qoder CN 会话库，请先退出 Qoder CN再改标题。", ex);
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<DeleteItemResult>> DeleteAsync(IEnumerable<string> ids) => Task.Run<IReadOnlyList<DeleteItemResult>>(() =>
    {
        var results = new List<DeleteItemResult>();
        if (QoderCnRunning())
        {
            foreach (var id in ids)
            {
                results.Add(new DeleteItemResult
                {
                    AgentId = AgentId,
                    Id = id,
                    Ok = false,
                    Error = "删除需要先完全退出 Qoder CN（包括托盘）后重试——应用还在跑时写入会损坏数据库。",
                });
            }
            return results;
        }

        var db = QoderCnStore.DbPath;
        if (db is null)
        {
            foreach (var id in ids)
                results.Add(new DeleteItemResult { AgentId = AgentId, Id = id, Ok = false, Error = "未找到 Qoder CN 会话库" });
            return results;
        }

        try
        {
            using var conn = QoderCnStore.OpenWrite(db);
            using var tx = conn.BeginTransaction();
            foreach (var id in ids)
            {
                try
                {
                    CodexProvider.GuardDbId(id);
                    var size = SizeOf(conn, id);
                    if (!SessionExists(conn, id))
                    {
                        TryDeleteJsonl(id);
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
                    TryDeleteJsonl(id);
                    titles.Remove(AgentId, id);
                    results.Add(new DeleteItemResult
                    {
                        AgentId = AgentId,
                        Id = id,
                        Ok = true,
                        FreedBytes = size,
                        Note = "已从 Qoder CN 会话库删除。",
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
                    Error = "无法写入 Qoder CN 会话库，请先退出 Qoder CN再删除。",
                });
            }
        }
        return results;
    });

    // ------------------------------------------------------------------
    // 列表 / 详情
    // ------------------------------------------------------------------

    private ConversationSummary ToSummary(
        string id, ColMap cols, SqliteDataReader r, MsgStat? stat, string src, string? overrideTitle)
    {
        var title = Clean(cols.Str(r, "title"));
        var cwd = Clean(cols.Str(r, "cwd")) ?? Clean(cols.Str(r, "project_path")) ?? Clean(cols.Str(r, "workspace_path"));
        var (isSub, parentId) = SideChat(id, cols, r);
        var last = stat?.Last
            ?? cols.Ts(r, "updated_at")
            ?? cols.Ts(r, "last_activity_at")
            ?? cols.Ts(r, "created_at")
            ?? File.GetLastWriteTimeUtc(src);
        return new ConversationSummary
        {
            AgentId = AgentId,
            Id = id,
            Title = overrideTitle ?? title ?? "(无标题)",
            TitleSource = overrideTitle is not null ? "override" : title is not null ? "source" : "derived",
            Project = cwd,
            MessageCount = stat?.Count ?? 0,
            SizeBytes = stat?.Size ?? 0,
            LastActivityUtc = last,
            IsSubagent = isSub,
            ParentId = parentId,
            SourceFile = src,
        };
    }

    private static (bool IsSub, string? Parent) SideChat(string sessionId, ColMap cols, SqliteDataReader r)
    {
        var parent = Clean(cols.Str(r, "owner_session_id"))
            ?? Clean(cols.Str(r, "parent_session_id"))
            ?? Clean(cols.Str(r, "parent_id"));
        if (parent is not null && parent.Equals(sessionId, StringComparison.OrdinalIgnoreCase))
            parent = null;

        var extraSub = false;
        var extra = cols.Str(r, "extra_json") ?? cols.Str(r, "extra");
        if (!string.IsNullOrWhiteSpace(extra))
        {
            try
            {
                using var doc = JsonDocument.Parse(extra);
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Object)
                {
                    parent ??= Clean(Str(root, "ownerSessionId") ?? Str(root, "owner_session_id")
                        ?? Str(root, "parentSessionId") ?? Str(root, "parent_session_id")
                        ?? Str(root, "parentId"));
                    extraSub = Truthy(root, "isSideChat") || Truthy(root, "is_side_chat")
                        || Truthy(root, "sideChat") || Truthy(root, "side_chat");
                }
            }
            catch (JsonException) { }
        }

        var kind = cols.Str(r, "execution_kind") ?? cols.Str(r, "conversation_mode") ?? cols.Str(r, "product_mode");
        var kindSub = kind is not null && (
            kind.Contains("side", StringComparison.OrdinalIgnoreCase)
            || kind.Contains("subagent", StringComparison.OrdinalIgnoreCase)
            || kind.Contains("sub_agent", StringComparison.OrdinalIgnoreCase)
            || kind.Contains("sub-agent", StringComparison.OrdinalIgnoreCase));
        var flagSub = cols.Truthy(r, "is_side_chat") || cols.Truthy(r, "is_subagent") || cols.Truthy(r, "side_chat");
        if (parent is not null && parent.Equals(sessionId, StringComparison.OrdinalIgnoreCase))
            parent = null;
        return (parent is not null || extraSub || kindSub || flagSub, parent);
    }

    private static Dictionary<string, MsgStat> ReadStats(SqliteConnection conn)
    {
        var map = new Dictionary<string, MsgStat>(StringComparer.OrdinalIgnoreCase);
        if (!QoderCnStore.TableExists(conn, "chat_session_messages")) return map;
        var cols = QoderCnStore.TableColumns(conn, "chat_session_messages");
        if (!cols.Contains("session_id")) return map;
        var sizeExpr = cols.Contains("payload_json")
            ? "COALESCE(SUM(LENGTH(payload_json)), 0)"
            : "0";
        var lastParts = new List<string>();
        if (cols.Contains("updated_at")) lastParts.Add("updated_at");
        if (cols.Contains("created_at")) lastParts.Add("created_at");
        var lastExpr = lastParts.Count == 0 ? "NULL" : lastParts.Count == 1
            ? $"MAX({lastParts[0]})"
            : $"MAX({string.Join(", ", lastParts)})";
        var countExpr = cols.Contains("payload_json")
            ? "SUM(CASE WHEN json_extract(payload_json, '$.role') IN ('user','assistant') THEN 1 ELSE 0 END)"
            : "COUNT(*)";
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT session_id, {countExpr}, {sizeExpr}, {lastExpr}
            FROM chat_session_messages
            GROUP BY session_id
            """;
        try
        {
            ReadStatRows(cmd, map);
        }
        catch (SqliteException)
        {
            cmd.CommandText = $"""
                SELECT session_id, COUNT(*), {sizeExpr}, {lastExpr}
                FROM chat_session_messages
                GROUP BY session_id
                """;
            ReadStatRows(cmd, map);
        }
        return map;
    }

    private static void ReadStatRows(SqliteCommand cmd, Dictionary<string, MsgStat> map)
    {
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var id = r.IsDBNull(0) ? "" : Convert.ToString(r.GetValue(0), CultureInfo.InvariantCulture) ?? "";
            if (id.Length == 0) continue;
            var count = r.IsDBNull(1) ? 0 : Convert.ToInt64(r.GetValue(1), CultureInfo.InvariantCulture);
            var size = r.IsDBNull(2) ? 0 : Convert.ToInt64(r.GetValue(2), CultureInfo.InvariantCulture);
            var last = r.IsDBNull(3) ? null : ParseTsValue(r.GetValue(3));
            map[id] = new MsgStat(count, size, last);
        }
    }

    private static List<ConversationMessage> ReadSqliteMessages(SqliteConnection conn, string id)
    {
        var list = new List<(long Seq, DateTime? Ts, ConversationMessage Msg)>();
        if (!QoderCnStore.TableExists(conn, "chat_session_messages")) return [];
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM chat_session_messages WHERE session_id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        var cols = new ColMap(r);
        while (r.Read())
        {
            var raw = cols.Str(r, "payload_json") ?? cols.Str(r, "payload") ?? cols.Str(r, "content");
            var rowTs = cols.Ts(r, "updated_at") ?? cols.Ts(r, "created_at");
            var parsed = ParsePayload(raw, rowTs);
            if (parsed is null) continue;
            var seq = cols.Int64(r, "sequence") ?? cols.Int64(r, "seq") ?? 0;
            list.Add((seq, parsed.TimestampUtc, parsed));
        }
        return list
            .OrderBy(x => x.Seq)
            .ThenBy(x => x.Ts ?? DateTime.MaxValue)
            .Select(x => x.Msg)
            .ToList();
    }

    private static List<ConversationMessage> ReadJsonlMessages(string sessionId)
    {
        var file = QoderCnStore.FindJsonl(sessionId);
        if (file is null) return [];
        var list = new List<ConversationMessage>();
        foreach (var line in UsageParsers.ReadLinesShared(file))
        {
            var msg = ParseJsonlLine(line);
            if (msg is not null) list.Add(msg);
        }
        return list;
    }

    internal static ConversationMessage? ParsePayload(string? raw, DateTime? rowTs)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        JsonDocument doc;
        try { doc = JsonDocument.Parse(raw); }
        catch (JsonException) { return null; }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.String)
            {
                var inner = root.GetString();
                return string.IsNullOrWhiteSpace(inner) ? null : ParsePayload(inner, rowTs);
            }
            if (root.ValueKind != JsonValueKind.Object) return null;
            return MessageFromObject(root, rowTs);
        }
    }

    internal static ConversationMessage? ParseJsonlLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        JsonDocument doc;
        try { doc = JsonDocument.Parse(line); }
        catch (JsonException) { return null; }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            var type = Str(root, "type");
            if (type is "file-history-snapshot" or "progress" or "system" or "hook" or "tool_result")
                return null;
            var msg = Obj(root, "message") ?? root;
            var roleHint = type is "user" or "assistant" ? type : null;
            return MessageFromObject(msg, TimestampOf(root) ?? TimestampOf(msg), roleHint);
        }
    }

    private static ConversationMessage? MessageFromObject(JsonElement root, DateTime? fallbackTs, string? roleOverride = null)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        var role = Str(root, "role") ?? roleOverride ?? Str(root, "type");
        if (role is "human") role = "user";
        if (role is "ai" or "bot" or "model") role = "assistant";
        if (role is not ("user" or "assistant")) return null;

        var chunks = new List<string>();
        var text = Str(root, "text");
        if (!string.IsNullOrWhiteSpace(text))
            chunks.Add(text.Trim());
        else
        {
            AppendContent(root, "parts", chunks);
            AppendContent(root, "content", chunks);
        }
        if (role == "assistant")
        {
            AppendToolNames(root, "parts", chunks);
            AppendToolNames(root, "content", chunks);
        }

        var body = string.Join("\n\n", DistinctKeep(chunks)).Trim();
        if (role == "user") body = SysReminder.Replace(body, "").Trim();
        if (body.Length == 0) return null;
        return new ConversationMessage
        {
            Role = role,
            TimestampUtc = TimestampOf(root) ?? fallbackTs,
            Text = body,
        };
    }

    private static void AppendContent(JsonElement root, string name, List<string> chunks)
    {
        if (!root.TryGetProperty(name, out var el)) return;
        if (el.ValueKind == JsonValueKind.String)
        {
            var s = el.GetString();
            if (!string.IsNullOrWhiteSpace(s)) chunks.Add(s.Trim());
            return;
        }
        if (el.ValueKind != JsonValueKind.Array) return;
        foreach (var part in el.EnumerateArray())
        {
            if (part.ValueKind == JsonValueKind.String)
            {
                var s = part.GetString();
                if (!string.IsNullOrWhiteSpace(s)) chunks.Add(s.Trim());
                continue;
            }
            if (part.ValueKind != JsonValueKind.Object) continue;
            var type = Str(part, "type");
            if (type is "thinking" or "reasoning" or "hook" or "system" or "system-reminder")
                continue;
            if (type is "tool" or "tool_use" or "tool_call" or "function_call" or "toolCall")
                continue;
            if (type is not (null or "text" or "output_text" or "input_text"))
                continue;
            var t = Str(part, "text") ?? Str(part, "content");
            if (!string.IsNullOrWhiteSpace(t)) chunks.Add(t.Trim());
        }
    }

    private static void AppendToolNames(JsonElement root, string name, List<string> chunks)
    {
        if (!root.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.Array) return;
        foreach (var part in el.EnumerateArray())
        {
            if (part.ValueKind != JsonValueKind.Object) continue;
            var type = Str(part, "type");
            if (type is not ("tool" or "tool_use" or "tool_call" or "function_call" or "toolCall")) continue;
            var tool = ToolName(part);
            if (tool is null) continue;
            var line = "· 工具 " + tool;
            if (!chunks.Contains(line, StringComparer.Ordinal)) chunks.Add(line);
        }
    }

    private static string? ToolName(JsonElement part)
    {
        foreach (var key in new[] { "name", "toolName", "tool_name" })
        {
            var s = Str(part, key);
            if (!string.IsNullOrWhiteSpace(s) && s is not "tool") return s.Trim();
        }
        foreach (var nested in new[] { "function", "toolCall", "tool_call", "tool" })
        {
            var obj = Obj(part, nested);
            if (obj is null) continue;
            var s = Str(obj.Value, "name") ?? Str(obj.Value, "toolName") ?? Str(obj.Value, "tool_name");
            if (!string.IsNullOrWhiteSpace(s)) return s.Trim();
        }
        return null;
    }

    private static IEnumerable<string> DistinctKeep(List<string> chunks)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in chunks)
        {
            if (seen.Add(c)) yield return c;
        }
    }

    // ------------------------------------------------------------------
    // 删除
    // ------------------------------------------------------------------

    private static bool SessionExists(SqliteConnection conn, string id)
    {
        if (!QoderCnStore.TableExists(conn, "chat_sessions")) return false;
        var cols = QoderCnStore.TableColumns(conn, "chat_sessions");
        var idCol = cols.Contains("session_id") ? "session_id" : cols.Contains("id") ? "id" : null;
        if (idCol is null) return false;
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT 1 FROM chat_sessions WHERE \"{idCol}\" = $id LIMIT 1";
        cmd.Parameters.AddWithValue("$id", id);
        return cmd.ExecuteScalar() is not null;
    }

    private static long SizeOf(SqliteConnection conn, string id)
    {
        if (!QoderCnStore.TableExists(conn, "chat_session_messages")) return 0;
        var cols = QoderCnStore.TableColumns(conn, "chat_session_messages");
        if (!cols.Contains("session_id") || !cols.Contains("payload_json")) return 0;
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(SUM(LENGTH(payload_json)), 0) FROM chat_session_messages WHERE session_id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        return Convert.ToInt64(cmd.ExecuteScalar() ?? 0, CultureInfo.InvariantCulture);
    }

    private static void DeleteSessionRows(SqliteConnection conn, string id)
    {
        using (var off = conn.CreateCommand())
        {
            off.CommandText = "PRAGMA foreign_keys=OFF";
            off.ExecuteNonQuery();
        }

        using (var tables = conn.CreateCommand())
        {
            tables.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table'";
            using var r = tables.ExecuteReader();
            var names = new List<string>();
            while (r.Read()) names.Add(r.GetString(0));
            r.Close();
            foreach (var name in names)
            {
                if (name.Equals("chat_sessions", StringComparison.OrdinalIgnoreCase)) continue;
                var cols = QoderCnStore.TableColumns(conn, name);
                foreach (var col in new[] { "session_id", "owner_session_id" })
                {
                    if (!cols.Contains(col)) continue;
                    using var del = conn.CreateCommand();
                    del.CommandText = $"DELETE FROM \"{name}\" WHERE \"{col}\" = $id";
                    del.Parameters.AddWithValue("$id", id);
                    del.ExecuteNonQuery();
                }
            }
        }

        var sessionCols = QoderCnStore.TableColumns(conn, "chat_sessions");
        var idCol = sessionCols.Contains("session_id") ? "session_id" : "id";
        using var drop = conn.CreateCommand();
        drop.CommandText = $"DELETE FROM chat_sessions WHERE \"{idCol}\" = $id";
        drop.Parameters.AddWithValue("$id", id);
        drop.ExecuteNonQuery();
    }

    private static void TryDeleteJsonl(string id)
    {
        try
        {
            var jsonl = QoderCnStore.FindJsonl(id);
            if (jsonl is null) return;
            if (File.Exists(jsonl)) File.Delete(jsonl);
            var dir = Path.Combine(Path.GetDirectoryName(jsonl)!, id);
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch (Exception) { }
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

    private static string? DisplayModel(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var label = QoderLocal.ResolveChinaModelDisplay(raw);
        return label is "unknown" ? null : label;
    }

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? Str(JsonElement el, string name) => UsageParsers.GetStr(el, name);

    private static JsonElement? Obj(JsonElement el, string name) => UsageParsers.GetObj(el, name);

    private static bool Truthy(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var v)) return false;
        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.Number when v.TryGetInt64(out var n) => n != 0,
            JsonValueKind.String when bool.TryParse(v.GetString(), out var b) => b,
            JsonValueKind.String => v.GetString() is "1" or "yes",
            _ => false,
        };
    }

    private static DateTime? TimestampOf(JsonElement el)
    {
        foreach (var name in new[] { "timestamp", "ts", "time", "createdAt", "created_at", "updatedAt", "updated_at" })
        {
            if (!el.TryGetProperty(name, out var v)) continue;
            var parsed = ParseTsElement(v);
            if (parsed is not null) return parsed;
        }
        return null;
    }

    private static DateTime? ParseTsElement(JsonElement v)
    {
        if (v.ValueKind == JsonValueKind.String)
            return ParseTsValue(v.GetString());
        if (v.ValueKind == JsonValueKind.Number)
        {
            if (v.TryGetInt64(out var n)) return UsageParsers.ParseMs(n);
            return UsageParsers.ParseMs((long)v.GetDouble());
        }
        return null;
    }

    internal static DateTime? ParseTsValue(object? v)
    {
        if (v is null or DBNull) return null;
        try
        {
            return v switch
            {
                DateTime dt => DateTime.SpecifyKind(dt, dt.Kind == DateTimeKind.Unspecified ? DateTimeKind.Utc : dt.Kind).ToUniversalTime(),
                DateTimeOffset dto => dto.UtcDateTime,
                long l => UsageParsers.ParseMs(l),
                int n => UsageParsers.ParseMs(n),
                double d => UsageParsers.ParseMs((long)d),
                decimal m => UsageParsers.ParseMs((long)m),
                string s => ParseTsString(s),
                _ => ParseTsString(Convert.ToString(v, CultureInfo.InvariantCulture)),
            };
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static DateTime? ParseTsString(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var iso = UsageParsers.ParseIso(s);
        if (iso is not null) return iso;
        if (long.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var n))
            return UsageParsers.ParseMs(n);
        return null;
    }

    private sealed class ColMap
    {
        private readonly Dictionary<string, int> _map = new(StringComparer.OrdinalIgnoreCase);

        public ColMap(SqliteDataReader r)
        {
            for (var i = 0; i < r.FieldCount; i++)
                _map[r.GetName(i)] = i;
        }

        public bool Has(string name) => _map.ContainsKey(name);

        public string? IdColumn() =>
            Has("session_id") ? "session_id" : Has("id") ? "id" : null;

        public string? Str(SqliteDataReader r, string name)
        {
            if (!_map.TryGetValue(name, out var i) || r.IsDBNull(i)) return null;
            var raw = r.GetValue(i);
            var s = raw as string ?? Convert.ToString(raw, CultureInfo.InvariantCulture);
            return string.IsNullOrWhiteSpace(s) ? null : s.Trim();
        }

        public DateTime? Ts(SqliteDataReader r, string name)
        {
            if (!_map.TryGetValue(name, out var i) || r.IsDBNull(i)) return null;
            return ParseTsValue(r.GetValue(i));
        }

        public long? Int64(SqliteDataReader r, string name)
        {
            if (!_map.TryGetValue(name, out var i) || r.IsDBNull(i)) return null;
            try { return Convert.ToInt64(r.GetValue(i), CultureInfo.InvariantCulture); }
            catch (Exception) { return null; }
        }

        public bool Truthy(SqliteDataReader r, string name)
        {
            if (!_map.TryGetValue(name, out var i) || r.IsDBNull(i)) return false;
            var v = r.GetValue(i);
            return v switch
            {
                bool b => b,
                long l => l != 0,
                int n => n != 0,
                string s => s is "1" or "true" or "True" or "yes",
                _ => false,
            };
        }

        public bool IsDeleted(SqliteDataReader r)
        {
            if (_map.TryGetValue("deleted_at", out var i) && !r.IsDBNull(i))
                return true;
            return Truthy(r, "deleted") || Truthy(r, "is_deleted");
        }
    }

    private sealed record MsgStat(long Count, long Size, DateTime? Last);
}
