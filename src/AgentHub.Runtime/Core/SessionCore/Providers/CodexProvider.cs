using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using AgentHub.Core.TokenCore;

namespace AgentHub.Core.SessionCore.Providers;

/// <summary>Codex CLI 会话（方案 §4.2）：
/// ~/.codex/sessions/**/*.jsonl（年/月/日结构）+ archived_sessions。
/// 标题无稳定字段 → 覆盖表；改标题写覆盖表；删除 = 删 jsonl 文件。
/// 消息：response_item.payload.type=message（role user/assistant；developer 是注入指令，预览跳过）。</summary>
public sealed class CodexProvider(TitleOverrideStore titles, Action<string>? log = null) : IConversationProvider
{
    private static readonly string[] Roots =
    [
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "sessions"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "archived_sessions"),
    ];

    public string AgentId => "codex";

    public Task<IReadOnlyList<ConversationSummary>> ListAsync() => Task.Run<IReadOnlyList<ConversationSummary>>(() =>
    {
        var list = new List<ConversationSummary>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in Roots)
        {
            if (!Directory.Exists(root)) continue;
            foreach (var file in Directory.EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories))
            {
                var id = SessionIdFromName(Path.GetFileName(file));
                if (id is null || !seen.Add(id)) continue;
                try
                {
                    var (title, titleSource, cwd, isSub, firstUser, lastTs, parentId) = ScanHead(file);
                    list.Add(new ConversationSummary
                    {
                        AgentId = AgentId,
                        Id = id,
                        Title = titles.Get(AgentId, id) ?? title ?? firstUser ?? "(无标题)",
                        TitleSource = titles.Get(AgentId, id) is not null ? "override"
                            : title is not null ? "source" : titleSource,
                        Project = cwd,
                        MessageCount = 0,   // 列表不逐文件全读（5MB 级文件 ×134 太重）；详情页给精确数
                        SizeBytes = new FileInfo(file).Length,
                        LastActivityUtc = lastTs ?? new FileInfo(file).LastWriteTimeUtc,
                        IsSubagent = isSub,
                        ParentId = parentId,
                        SourceFile = file,
                    });
                }
                catch (Exception ex)
                {
                    log?.Invoke($"[sessions] Codex 读失败 {file} {ex.GetType().Name}: {ex.Message}");
                }
            }
        }
        return list;
    });

    public Task<ConversationDetail?> LoadAsync(string id)
    {
        GuardId(id);
        return Task.Run<ConversationDetail?>(() =>
        {
            var file = FindFile(id);
            if (file is null) return null;

            var messages = new List<ConversationMessage>();
            string? cwd = null, threadSource = null, sessionMetaId = null;
            DateTime? lastTs = null;
            long size = new FileInfo(file).Length;

            foreach (var line in UsageParsers.ReadLinesShared(file))
            {
                if (line.Length == 0) continue;
                JsonDocument doc;
                try { doc = JsonDocument.Parse(line); }
                catch (JsonException) { continue; }
                using (doc)
                {
                    var root = doc.RootElement;
                    var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
                    var payload = root.TryGetProperty("payload", out var p) && p.ValueKind == JsonValueKind.Object ? p : (JsonElement?)null;

                    if (type == "session_meta" && payload is not null)
                    {
                        sessionMetaId = GetString(payload, "id") ?? GetString(payload, "session_id") ?? sessionMetaId;
                        cwd = GetString(payload, "cwd") ?? cwd;
                        threadSource = GetString(payload, "thread_source") ?? threadSource;
                        continue;
                    }
                    if (type == "turn_context" && payload is not null)
                    {
                        cwd = GetString(payload, "cwd") ?? cwd;
                        continue;
                    }

                    if (type == "response_item" && payload is not null
                        && GetString(payload, "type") == "message")
                    {
                        var role = GetString(payload, "role");
                        if (role is not ("user" or "assistant")) continue;   // developer = 注入的权限/环境指令
                        var text = ExtractContentText(payload);
                        if (text.Length == 0) continue;
                        var ts = ParseTs(root.TryGetProperty("timestamp", out var tsEl) ? tsEl.GetString() : null);
                        lastTs = ts ?? lastTs;
                        messages.Add(new ConversationMessage { Role = role, TimestampUtc = ts, Text = text });
                    }
                }
            }

            var (headTitle, _, _, isSub2, firstUser2, _, parentId) = ScanHead(file);
            var title = titles.Get(AgentId, id) ?? headTitle ?? firstUser2 ?? "(无标题)";
            return new ConversationDetail
            {
                Summary = new ConversationSummary
                {
                    AgentId = AgentId,
                    Id = id,
                    Title = title,
                    TitleSource = titles.Get(AgentId, id) is not null ? "override" : headTitle is not null ? "source" : "derived",
                    Project = cwd,
                    MessageCount = messages.Count,
                    SizeBytes = size,
                    LastActivityUtc = lastTs ?? new FileInfo(file).LastWriteTimeUtc,
                    IsSubagent = IsSubThread(threadSource, parentId),
                    ParentId = parentId,
                    SourceFile = file,
                },
                Messages = Cap(messages),
            };
        });
    }

    public Task RenameAsync(string id, string title)
    {
        GuardId(id);
        titles.Set(AgentId, id, title);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<DeleteItemResult>> DeleteAsync(IEnumerable<string> ids) => Task.Run<IReadOnlyList<DeleteItemResult>>(() =>
    {
        var results = new List<DeleteItemResult>();
        foreach (var id in ids)
        {
            GuardId(id);
            try
            {
                var file = FindFile(id);
                if (file is null)
                {
                    // jsonl 已没了，仍清理侧栏索引 / UI 状态，避免 orphan 标题。
                    var leftover = CodexDesktopCleanup.RemoveLeftovers(id);
                    titles.Remove(AgentId, id);
                    results.Add(new DeleteItemResult
                    {
                        AgentId = AgentId,
                        Id = id,
                        Ok = true,
                        Note = leftover
                            ? "源文件已不在，已清理侧栏索引与 UI 残留。"
                            : "源文件已不在。",
                    });
                    continue;
                }
                long size = new FileInfo(file).Length;
                File.Delete(file);
                CodexDesktopCleanup.RemoveLeftovers(id);
                titles.Remove(AgentId, id);
                results.Add(new DeleteItemResult { AgentId = AgentId, Id = id, Ok = true, FreedBytes = size });
            }
            catch (Exception ex)
            {
                results.Add(new DeleteItemResult { AgentId = AgentId, Id = id, Ok = false, Error = ex.Message });
            }
        }
        return results;
    });

    // ------------------------------------------------------------------

    internal static string? SessionIdFromName(string name)
    {
        // rollout-2026-06-11T13-44-41-<uuid>.jsonl → uuid
        var m = System.Text.RegularExpressions.Regex.Match(name,
            @"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}");
        return m.Success ? m.Value.ToLowerInvariant() : null;
    }

    private string? FindFile(string id)
    {
        foreach (var root in Roots)
        {
            if (!Directory.Exists(root)) continue;
            foreach (var file in Directory.EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories))
                if (SessionIdFromName(Path.GetFileName(file)) == id.ToLowerInvariant())
                    return file;
        }
        return null;
    }

    /// <summary>轻量头扫描：只读前 ~200 行拿 meta/标题素材，不扫全文件。
    /// 标题优先 ~/.codex/session_index.jsonl 的 thread_name（Codex 侧栏那条）。</summary>
    private static (string? Title, string Source, string? Cwd, bool IsSub, string? FirstUser, DateTime? LastTs, string? ParentId) ScanHead(string file)
    {
        string? cwd = null, threadSource = null, sessionId = null, firstUser = null, parentId = null;
        int lines = 0;
        foreach (var line in UsageParsers.ReadLinesShared(file))
        {
            if (++lines > 200) break;
            JsonDocument doc;
            try { doc = JsonDocument.Parse(line); }
            catch (JsonException) { continue; }
            using (doc)
            {
                var root = doc.RootElement;
                var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
                if (type is not ("session_meta" or "turn_context" or "response_item")) continue;
                if (!root.TryGetProperty("payload", out var p) || p.ValueKind != JsonValueKind.Object) continue;
                if (type != "response_item")
                {
                    cwd = GetString(p, "cwd") ?? cwd;
                    threadSource = GetString(p, "thread_source") ?? threadSource;
                    sessionId = GetString(p, "session_id") ?? GetString(p, "id") ?? sessionId;
                    parentId = GetString(p, "parent_thread_id") ?? GetString(p, "forked_from_id") ?? parentId;
                }
                else if (firstUser is null && GetString(p, "type") == "message" && GetString(p, "role") == "user")
                {
                    var text = ExtractContentText(p);
                    if (text.Length > 0 && !IsInjectedPrompt(text))
                        firstUser = DeriveTitle(text);
                }
            }
        }
        var named = CodexThreadNames.Get(sessionId);
        return (named, named is not null ? "source" : "derived", cwd, IsSubThread(threadSource, parentId), firstUser, null, parentId);
    }

    internal static bool IsSubThread(string? threadSource, string? forkedFrom = null) =>
        (!string.IsNullOrEmpty(threadSource) && !threadSource.Equals("user", StringComparison.OrdinalIgnoreCase))
        || !string.IsNullOrEmpty(forkedFrom);

    internal static bool IsInjectedPrompt(string text)
    {
        var t = text.TrimStart();
        if (t.StartsWith("<environment_context", StringComparison.OrdinalIgnoreCase)) return true;
        if (t.StartsWith("<INSTRUCTIONS", StringComparison.OrdinalIgnoreCase)) return true;
        if (t.StartsWith("<app-context", StringComparison.OrdinalIgnoreCase)) return true;
        if (t.Contains("<environment_context>", StringComparison.OrdinalIgnoreCase)) return true;
        if (t.StartsWith("# AGENTS.md", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    internal static string? GetString(JsonElement? el, string name) =>
        el is not null && el.Value.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    internal static DateTime? ParseTs(string? iso)
    {
        if (string.IsNullOrEmpty(iso)) return null;
        if (DateTime.TryParse(iso, null, System.Globalization.DateTimeStyles.RoundtripKind, out var dt))
            return dt.ToUniversalTime();
        return null;
    }

    /// <summary>content[].text 拼接（input_text / output_text / text）。</summary>
    internal static string ExtractContentText(JsonElement? payload)
    {
        if (payload is null || !payload.Value.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            return "";
        var sb = new StringBuilder();
        foreach (var part in content.EnumerateArray())
        {
            if (part.ValueKind != JsonValueKind.Object) continue;
            if (!part.TryGetProperty("text", out var txt) || txt.ValueKind != JsonValueKind.String) continue;
            var s = txt.GetString();
            if (!string.IsNullOrEmpty(s)) sb.AppendLine(s);
        }
        return sb.ToString().TrimEnd('\r', '\n');
    }

    internal static string DeriveTitle(string text)
    {
        var firstLine = text.Split('\n')[0].Trim();
        return firstLine.Length <= 60 ? firstLine : firstLine[..60] + "…";
    }

    internal static IReadOnlyList<ConversationMessage> Cap(List<ConversationMessage> messages, int max = 200, int maxChars = 4000)
    {
        if (messages.Count > max) messages = messages[..max];
        foreach (var m in messages.ToList())
        {
            if (m.Text.Length > maxChars)
                messages[messages.IndexOf(m)] = m with { Text = m.Text[..maxChars] + "\n…（截断）" };
        }
        return messages;
    }

    /// <summary>id 白名单：ASCII 字母/数字/连字符/下划线。挡住路径穿越与注入（方案 §4.3）。
    /// ZCode 原生 id 是 sess_&lt;uuid&gt;。</summary>
    internal static void GuardId(string id)
    {
        if (id.Length is < 1 or > 100 || !id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            throw new ArgumentException("非法会话 id");
    }

    /// <summary>库型会话（cursor/zcode）：id 只进参数化 SQL、不拼路径，只挡空/超长/NUL。
    /// 源头会出带换行等怪字符的 id（实测 Cursor task-call 子会话），白名单会把删除全拦死。</summary>
    internal static void GuardDbId(string id)
    {
        if (id.Length is < 1 or > 200 || id.Contains('\0'))
            throw new ArgumentException("非法会话 id");
    }
}

/// <summary>Codex Desktop 侧栏标题：~/.codex/session_index.jsonl 的 thread_name。</summary>
internal static class CodexThreadNames
{
    private static readonly string IndexPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "session_index.jsonl");
    private static Dictionary<string, string>? _map;
    private static DateTime _mtimeUtc;

    public static string? Get(string? sessionId)
    {
        if (string.IsNullOrEmpty(sessionId)) return null;
        Ensure();
        return _map is not null
            && _map.TryGetValue(sessionId, out var name)
            && !string.IsNullOrWhiteSpace(name)
            ? name.Trim()
            : null;
    }

    private static void Ensure()
    {
        try
        {
            if (!File.Exists(IndexPath))
            {
                _map ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                return;
            }
            var mt = File.GetLastWriteTimeUtc(IndexPath);
            if (_map is not null && mt == _mtimeUtc) return;
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in UsageParsers.ReadLinesShared(IndexPath))
            {
                if (line.Length == 0) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var id = CodexProvider.GetString(doc.RootElement, "id");
                    var name = CodexProvider.GetString(doc.RootElement, "thread_name");
                    if (id is not null && name is not null) map[id] = name;
                }
                catch (JsonException) { }
            }
            _map = map;
            _mtimeUtc = mt;
        }
        catch
        {
            _map ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }
}

/// <summary>Codex Desktop 删除后残留：session_index.jsonl + state_5.sqlite 侧栏 threads +
/// sqlite/codex-dev.db（local_thread_catalog）+ thread_history_1.sqlite + .codex-global-state.json。
/// 对照 ZcodeProvider.DeleteLeftovers；写 sqlite / global-state 前最好退出 Codex，避免被内存态覆盖或文件锁。</summary>
internal static class CodexDesktopCleanup
{
    private static readonly System.Text.RegularExpressions.Regex UuidInText = new(
        "[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    private static string Home => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");

    private static string IndexPath => Path.Combine(Home, "session_index.jsonl");
    private static string GlobalStatePath => Path.Combine(Home, ".codex-global-state.json");

    /// <summary>ChatGPT.exe（OpenAI Codex 包）或 codex.exe 在跑。</summary>
    public static bool CodexRunning()
    {
        try
        {
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    var name = p.ProcessName;
                    if (name.Equals("codex", StringComparison.OrdinalIgnoreCase))
                        return true;
                    if (!name.Equals("ChatGPT", StringComparison.OrdinalIgnoreCase))
                        continue;
                    string? path = null;
                    try { path = p.MainModule?.FileName; } catch { }
                    if (path is not null && path.Contains("OpenAI.Codex", StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                finally { p.Dispose(); }
            }
        }
        catch { }
        return false;
    }

    /// <summary>删索引行 + state_5/history sqlite 线程行 + 尽力 scrub global-state。任一侧有改动返回 true。</summary>
    public static bool RemoveLeftovers(string id)
    {
        var changed = RemoveFromSessionIndex(id);
        if (RemoveFromSqlite(id))
            changed = true;
        if (ScrubGlobalState(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { id }))
            changed = true;
        return changed;
    }


    /// <summary>扫 session_index 中无对应 jsonl 的孤儿，清理索引与 global-state。返回清掉的索引条数。</summary>
    public static int SweepOrphanLeftovers()
    {
        var live = LiveSessionIds();
        var orphans = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var kept = new List<string>();
        var removed = 0;
        if (File.Exists(IndexPath))
        {
            foreach (var line in UsageParsers.ReadLinesShared(IndexPath))
            {
                if (line.Length == 0) continue;
                string? id = null;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    id = CodexProvider.GetString(doc.RootElement, "id");
                }
                catch (JsonException)
                {
                    kept.Add(line);
                    continue;
                }
                if (id is null || live.Contains(id))
                {
                    kept.Add(line);
                    continue;
                }
                orphans.Add(id);
                removed++;
            }
            if (removed > 0)
                WriteIndex(kept);
        }
        // Codex 桌面侧栏现以 state_5.sqlite threads / codex-dev.db catalog 为准；jsonl 已删但仍残留会幽灵显示
        foreach (var orphanId in ListSqliteOrphanThreadIds())
        {
            orphans.Add(orphanId);
            if (RemoveFromSqlite(orphanId))
                removed++;
        }
        foreach (var orphanId in ListDevCatalogOrphanThreadIds())
        {
            if (!orphans.Add(orphanId)) continue;
            if (RemoveFromSqlite(orphanId))
                removed++;
        }
        if (orphans.Count > 0)
            ScrubGlobalState(orphans);
        return removed;
    }

    public static bool RemoveFromSessionIndex(string id)
    {
        if (!File.Exists(IndexPath)) return false;
        var kept = new List<string>();
        var removed = false;
        foreach (var line in UsageParsers.ReadLinesShared(IndexPath))
        {
            if (line.Length == 0) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var lineId = CodexProvider.GetString(doc.RootElement, "id");
                if (lineId is not null && lineId.Equals(id, StringComparison.OrdinalIgnoreCase))
                {
                    removed = true;
                    continue;
                }
            }
            catch (JsonException) { }
            kept.Add(line);
        }
        if (!removed) return false;
        WriteIndex(kept);
        return true;
    }

    private static void WriteIndex(List<string> lines)
    {
        var dir = Path.GetDirectoryName(IndexPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = IndexPath + ".tmp-" + Guid.NewGuid().ToString("N");
        var payload = lines.Count == 0 ? "" : string.Join("\n", lines) + "\n";
        File.WriteAllText(tmp, payload, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Copy(tmp, IndexPath, overwrite: true);
        try { File.Delete(tmp); } catch { }
    }

    private static HashSet<string> LiveSessionIds()
    {
        var live = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in new[]
                 {
                     Path.Combine(Home, "sessions"),
                     Path.Combine(Home, "archived_sessions"),
                 })
        {
            if (!Directory.Exists(root)) continue;
            foreach (var file in Directory.EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories))
            {
                var id = CodexProvider.SessionIdFromName(Path.GetFileName(file));
                if (id is not null) live.Add(id);
            }
        }
        return live;
    }

    private static string StateDbPath => Path.Combine(Home, "state_5.sqlite");
    private static string NestedStateDbPath => Path.Combine(Home, "sqlite", "state_5.sqlite");
    private static string HistoryDbPath => Path.Combine(Home, "thread_history_1.sqlite");
    /// <summary>Codex Desktop 侧栏目录：~/.codex/sqlite/codex-dev.db（local_thread_catalog）。</summary>
    private static string DevCatalogDbPath => Path.Combine(Home, "sqlite", "codex-dev.db");

    /// <summary>从 state_5.sqlite / thread_history_1.sqlite / codex-dev.db 按 thread id（及 rollout_path 含 id）删除。
    /// 缺库、锁住、表不存在时吞掉异常返回 false，不阻断删 jsonl / 索引。</summary>
    public static bool RemoveFromSqlite(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;
        var changed = false;
        try
        {
            if (RemoveFromStateDb(id)) changed = true;
        }
        catch
        {
            /* locked / missing / schema drift */
        }
        try
        {
            if (RemoveFromNestedStateDb(id)) changed = true;
        }
        catch
        {
            /* locked / missing / schema drift */
        }
        try
        {
            if (RemoveFromHistoryDb(id)) changed = true;
        }
        catch
        {
            /* locked / missing / schema drift */
        }
        try
        {
            if (RemoveFromDevCatalogDb(id)) changed = true;
        }
        catch
        {
            /* locked / missing / schema drift */
        }
        return changed;
    }

    private static bool RemoveFromStateDb(string id)
    {
        if (!File.Exists(StateDbPath)) return false;
        using var conn = OpenWrite(StateDbPath);
        using var tx = conn.BeginTransaction();
        var removed = 0;
        removed += ExecDelete(conn, tx,
            "DELETE FROM thread_artifacts WHERE thread_id = $id", id);
        removed += ExecDelete(conn, tx,
            "DELETE FROM thread_dynamic_tools WHERE thread_id = $id", id);
        removed += ExecDelete(conn, tx,
            """
            DELETE FROM thread_spawn_edges
            WHERE parent_thread_id = $id OR child_thread_id = $id
            """, id);
        // 主表：精确 id，或 rollout_path 含该 uuid（路径里带线程 id）
        removed += ExecDelete(conn, tx,
            """
            DELETE FROM threads
            WHERE id = $id
               OR instr(lower(coalesce(rollout_path, '')), lower($id)) > 0
            """, id);
        tx.Commit();
        return removed > 0;
    }

    private static bool RemoveFromHistoryDb(string id)
    {
        if (!File.Exists(HistoryDbPath)) return false;
        using var conn = OpenWrite(HistoryDbPath);
        using var tx = conn.BeginTransaction();
        var removed = 0;
        foreach (var sql in new[]
                 {
                     "DELETE FROM thread_turns WHERE thread_id = $id",
                     "DELETE FROM thread_items WHERE thread_id = $id",
                     "DELETE FROM thread_realtime_items WHERE thread_id = $id",
                     "DELETE FROM thread_history_projection_state WHERE thread_id = $id",
                 })
        {
            removed += ExecDelete(conn, tx, sql, id);
        }
        tx.Commit();
        return removed > 0;
    }

    /// <summary>旧版嵌套库 ~/.codex/sqlite/state_5.sqlite（无 projects 表），按同 schema 尽力清理。</summary>
    private static bool RemoveFromNestedStateDb(string id)
    {
        if (!File.Exists(NestedStateDbPath)) return false;
        // 与根目录 state_5 指向同一文件时跳过，避免重复删
        try
        {
            if (File.Exists(StateDbPath)
                && string.Equals(
                    Path.GetFullPath(NestedStateDbPath),
                    Path.GetFullPath(StateDbPath),
                    StringComparison.OrdinalIgnoreCase))
                return false;
        }
        catch { }

        using var conn = OpenWrite(NestedStateDbPath);
        using var tx = conn.BeginTransaction();
        var removed = 0;
        removed += ExecDelete(conn, tx,
            "DELETE FROM thread_dynamic_tools WHERE thread_id = $id", id);
        removed += ExecDelete(conn, tx,
            """
            DELETE FROM thread_spawn_edges
            WHERE parent_thread_id = $id OR child_thread_id = $id
            """, id);
        removed += ExecDelete(conn, tx,
            """
            DELETE FROM threads
            WHERE id = $id
               OR instr(lower(coalesce(rollout_path, '')), lower($id)) > 0
            """, id);
        tx.Commit();
        return removed > 0;
    }

    /// <summary>Desktop 侧栏 catalog：local_thread_catalog + 相关 thread_id 表；revision +1。</summary>
    private static bool RemoveFromDevCatalogDb(string id)
    {
        if (!File.Exists(DevCatalogDbPath)) return false;
        using var conn = OpenWrite(DevCatalogDbPath);
        using var tx = conn.BeginTransaction();
        var removed = 0;
        foreach (var sql in new[]
                 {
                     "DELETE FROM local_thread_catalog_scan_entries WHERE thread_id = $id",
                     "DELETE FROM thread_timeline_ledger WHERE thread_id = $id",
                     "DELETE FROM inbox_items WHERE thread_id = $id",
                     "DELETE FROM automation_runs WHERE thread_id = $id",
                     "DELETE FROM automations WHERE target_thread_id = $id",
                     "DELETE FROM local_thread_catalog WHERE thread_id = $id",
                 })
        {
            removed += ExecDelete(conn, tx, sql, id);
        }

        if (removed > 0)
        {
            try
            {
                using var bump = conn.CreateCommand();
                bump.Transaction = tx;
                bump.CommandText =
                    "UPDATE local_thread_catalog_metadata SET catalog_revision = catalog_revision + 1 WHERE id = 1";
                bump.ExecuteNonQuery();
            }
            catch (SqliteException)
            {
                /* metadata 表缺失则忽略 */
            }
        }

        tx.Commit();
        return removed > 0;
    }

    /// <summary>state_5.threads 中 rollout_path 文件已不存在（或为空）的线程 id。</summary>
    private static List<string> ListSqliteOrphanThreadIds()
    {
        var orphans = new List<string>();
        if (!File.Exists(StateDbPath)) return orphans;
        try
        {
            using var conn = OpenWrite(StateDbPath);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT id, rollout_path FROM threads";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var tid = r.IsDBNull(0) ? null : r.GetString(0);
                if (string.IsNullOrEmpty(tid)) continue;
                var rollout = r.IsDBNull(1) ? null : r.GetString(1);
                if (RolloutFileExists(rollout)) continue;
                orphans.Add(tid);
            }
        }
        catch
        {
            /* ignore */
        }
        return orphans;
    }

    /// <summary>codex-dev.db local_thread_catalog 中无对应 sessions/*.jsonl 的幽灵 thread id。</summary>
    private static List<string> ListDevCatalogOrphanThreadIds()
    {
        var orphans = new List<string>();
        if (!File.Exists(DevCatalogDbPath)) return orphans;
        try
        {
            var live = LiveSessionIds();
            using var conn = OpenWrite(DevCatalogDbPath);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT thread_id FROM local_thread_catalog";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var tid = r.IsDBNull(0) ? null : r.GetString(0);
                if (string.IsNullOrEmpty(tid)) continue;
                if (live.Contains(tid)) continue;
                orphans.Add(tid);
            }
        }
        catch
        {
            /* ignore */
        }
        return orphans;
    }

    private static bool RolloutFileExists(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var p = path;
        if (p.StartsWith(@"\\?\", StringComparison.Ordinal)) p = p[4..];
        else if (p.StartsWith("//?/", StringComparison.Ordinal)) p = p[4..];
        try { return File.Exists(p); }
        catch { return false; }
    }

    private static SqliteConnection OpenWrite(string path)
    {
        var cs = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        };
        var conn = new SqliteConnection(cs.ToString());
        conn.Open();
        using (var pragma = conn.CreateCommand())
        {
            pragma.CommandText = "PRAGMA foreign_keys=ON";
            pragma.ExecuteNonQuery();
        }
        return conn;
    }

    private static int ExecDelete(SqliteConnection conn, SqliteTransaction tx, string sql, string id)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$id", id);
        try
        {
            return cmd.ExecuteNonQuery();
        }
        catch (SqliteException)
        {
            // 表不存在等：忽略单条，继续其余
            return 0;
        }
    }

    /// <summary>按会话 UUID 清理 .codex-global-state.json：删含该 id 的键、值为该 id 的属性、数组中的 id 项。
    /// 失败吞掉（文件锁 / Codex 在写）；同步写 .bak 降低下次启动回滚旧态的概率。</summary>
    public static bool ScrubGlobalState(HashSet<string> ids)
    {
        if (ids.Count == 0 || !File.Exists(GlobalStatePath)) return false;
        try
        {
            var json = File.ReadAllText(GlobalStatePath, Encoding.UTF8);
            var node = JsonNode.Parse(json);
            if (node is null) return false;
            var removed = 0;
            ScrubNode(node, ids, ref removed);
            if (removed == 0) return false;
            var next = node.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
            var tmp = GlobalStatePath + ".tmp-" + Guid.NewGuid().ToString("N");
            File.WriteAllText(tmp, next, new UTF8Encoding(false));
            File.Copy(tmp, GlobalStatePath, overwrite: true);
            try
            {
                var bak = GlobalStatePath + ".bak";
                File.Copy(tmp, bak, overwrite: true);
            }
            catch { }
            try { File.Delete(tmp); } catch { }
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool RefsId(string? s, HashSet<string> ids)
    {
        if (string.IsNullOrEmpty(s)) return false;
        if (ids.Contains(s)) return true;
        foreach (System.Text.RegularExpressions.Match m in UuidInText.Matches(s))
            if (ids.Contains(m.Value)) return true;
        return false;
    }

    private static void ScrubNode(JsonNode node, HashSet<string> ids, ref int removed)
    {
        if (node is JsonObject obj)
        {
            foreach (var key in obj.Select(kv => kv.Key).ToList())
            {
                if (RefsId(key, ids))
                {
                    obj.Remove(key);
                    removed++;
                    continue;
                }
                var child = obj[key];
                if (child is JsonValue jv
                    && jv.TryGetValue<string>(out var sv)
                    && ids.Contains(sv))
                {
                    obj.Remove(key);
                    removed++;
                    continue;
                }
                if (child is not null) ScrubNode(child, ids, ref removed);
            }
        }
        else if (node is JsonArray arr)
        {
            for (var i = arr.Count - 1; i >= 0; i--)
            {
                var item = arr[i];
                if (item is JsonValue jv
                    && jv.TryGetValue<string>(out var s)
                    && RefsId(s, ids))
                {
                    arr.RemoveAt(i);
                    removed++;
                    continue;
                }
                if (item is not null) ScrubNode(item, ids, ref removed);
            }
        }
    }
}
