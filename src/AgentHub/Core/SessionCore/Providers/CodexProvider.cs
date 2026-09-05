using System.IO;
using System.Text;
using System.Text.Json;
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
                    IsSubagent = IsSubThread(threadSource),
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
                    results.Add(new DeleteItemResult { AgentId = AgentId, Id = id, Ok = false, Error = "文件不存在（可能已被删除）" });
                    continue;
                }
                long size = new FileInfo(file).Length;
                File.Delete(file);
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
                    parentId = GetString(p, "parent_thread_id") ?? parentId;
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
        return (named, named is not null ? "source" : "derived", cwd, IsSubThread(threadSource), firstUser, null, parentId);
    }

    internal static bool IsSubThread(string? threadSource) =>
        !string.IsNullOrEmpty(threadSource)
        && !threadSource.Equals("user", StringComparison.OrdinalIgnoreCase);

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
