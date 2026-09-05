using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentHub.Core.TokenCore;

namespace AgentHub.Core.SessionCore.Providers;

/// <summary>WorkBuddy 会话（方案 §4.2）：
/// ~/.workbuddy/projects/&lt;项目&gt;/&lt;uuid&gt;.jsonl（+ 同名目录 subagents/ tool-results/）。
/// 新版标题优先读 workbuddy.db sessions.custom_title/title；旧版回退最后一条 ai-title。
/// 预览须剥 user 消息里的 system-reminder 段；~/.workbuddy/sessions/*.json 是心跳不当会话。</summary>
public sealed class WorkBuddyProvider(TitleOverrideStore titles, Action<string>? log = null) : IConversationProvider
{
    private static readonly string ProjectsRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".workbuddy", "projects");

    private static readonly Regex SysReminder = new(
        @"<system-reminder[\s\S]*?</system-reminder>", RegexOptions.Compiled);

    public string AgentId => "workbuddy";

    public Task<IReadOnlyList<ConversationSummary>> ListAsync() => Task.Run<IReadOnlyList<ConversationSummary>>(() =>
    {
        var list = new List<ConversationSummary>();
        if (!Directory.Exists(ProjectsRoot)) return list;
        var sidebar = WorkBuddySidebar.ReadSessionMetadata();
        foreach (var jsonl in Directory.EnumerateFiles(ProjectsRoot, "*.jsonl", SearchOption.AllDirectories))
        {
            var id = Path.GetFileNameWithoutExtension(jsonl);
            if (!IsUuid(id)) continue;
            try
            {
                var (title, cwd, count, lastTs) = Scan(jsonl);
                sidebar.TryGetValue(id, out var metadata);
                title = Clean(metadata?.Title) ?? Clean(title);
                cwd = Clean(metadata?.Cwd) ?? Clean(cwd);
                long size = new FileInfo(jsonl).Length;
                var dir = Path.Combine(Path.GetDirectoryName(jsonl)!, id);
                if (Directory.Exists(dir))
                    try { size += DirSize(dir); } catch (IOException) { }
                var parentId = ParentFromPath(jsonl);
                list.Add(new ConversationSummary
                {
                    AgentId = AgentId,
                    Id = id,
                    Title = title ?? "(无标题)",
                    TitleSource = title is null ? "derived" : "source",
                    Project = cwd,
                    MessageCount = count,
                    SizeBytes = size,
                    LastActivityUtc = metadata?.LastActivityUtc ?? lastTs ?? new FileInfo(jsonl).LastWriteTimeUtc,
                    IsSubagent = parentId is not null,
                    ParentId = parentId,
                    SourceFile = jsonl,
                });
            }
            catch (Exception ex)
            {
                log?.Invoke($"[sessions] WorkBuddy 读失败 {jsonl} {ex.GetType().Name}: {ex.Message}");
            }
        }
        return list;
    });

    public Task<ConversationDetail?> LoadAsync(string id)
    {
        CodexProvider.GuardId(id);
        return Task.Run<ConversationDetail?>(() =>
        {
            var jsonl = FindFile(id);
            if (jsonl is null) return null;

            var messages = new List<ConversationMessage>();
            string? title = null, cwd = null;
            DateTime? lastTs = null;
            long size = new FileInfo(jsonl).Length;

            foreach (var line in UsageParsers.ReadLinesShared(jsonl))
            {
                if (line.Length == 0) continue;
                JsonDocument doc;
                try { doc = JsonDocument.Parse(line); }
                catch (JsonException) { continue; }
                using (doc)
                {
                    var root = doc.RootElement;
                    if (!root.TryGetProperty("type", out var typeEl)) continue;
                    var type = typeEl.GetString();

                    if (type == "ai-title")
                    {
                        if (root.TryGetProperty("aiTitle", out var at) && at.ValueKind == JsonValueKind.String)
                            title = at.GetString();   // 后写覆盖先写（取最后一条）
                        continue;
                    }

                    if (type != "message") continue;   // 跳过 file-history-snapshot / function_call 等
                    var role = CodexProvider.GetString(root, "role");
                    if (role is not ("user" or "assistant")) continue;
                    var text = ExtractText(root);
                    if (role == "user") text = StripSystemReminder(text);
                    if (text.Length == 0) continue;

                    var ts = root.TryGetProperty("timestamp", out var tsEl) && tsEl.ValueKind == JsonValueKind.Number
                        && tsEl.TryGetInt64(out var ms)
                        ? DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime : (DateTime?)null;
                    lastTs = ts ?? lastTs;
                    cwd = CodexProvider.GetString(root, "cwd") ?? cwd;
                    messages.Add(new ConversationMessage { Role = role, TimestampUtc = ts, Text = text });
                }
            }

            WorkBuddySidebar.ReadSessionMetadata().TryGetValue(id, out var metadata);
            title = Clean(metadata?.Title) ?? Clean(title);
            cwd = Clean(metadata?.Cwd) ?? Clean(cwd);

            return new ConversationDetail
            {
                Summary = new ConversationSummary
                {
                    AgentId = AgentId,
                    Id = id,
                    Title = title ?? "(无标题)",
                    TitleSource = title is null ? "derived" : "source",
                    Project = cwd,
                    MessageCount = messages.Count,
                    SizeBytes = size,
                    LastActivityUtc = metadata?.LastActivityUtc ?? lastTs ?? new FileInfo(jsonl).LastWriteTimeUtc,
                    IsSubagent = ParentFromPath(jsonl) is not null,
                    ParentId = ParentFromPath(jsonl),
                    SourceFile = jsonl,
                },
                Messages = Cap(messages),
                Note = messages.Count >= 200 ? "消息较多，预览仅显示前 200 条。" : null,
            };
        });
    }

    public Task RenameAsync(string id, string title)
    {
        CodexProvider.GuardId(id);
        var jsonl = FindFile(id) ?? throw new FileNotFoundException($"会话文件不存在：{id}");
        if (WorkBuddySidebar.Rename(id, title)) return Task.CompletedTask;
        // 旧版没有 sessions 元数据行时，仍按原格式追加 ai-title。
        var cwd = Scan(jsonl).Cwd ?? "";
        var line = JsonSerializer.Serialize(new
        {
            timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            type = "ai-title",
            aiTitle = title,
            sessionId = id,
            cwd,
        });
        File.AppendAllText(jsonl, line + "\n", Encoding.UTF8);
        return Task.CompletedTask;
    }

    public static bool WorkBuddyRunning()
    {
        try
        {
            foreach (var process in Process.GetProcesses())
            {
                using (process)
                    if (process.ProcessName.Contains("workbuddy", StringComparison.OrdinalIgnoreCase))
                        return true;
            }
        }
        catch (Exception) { }
        return false;
    }

    public Task<IReadOnlyList<DeleteItemResult>> DeleteAsync(IEnumerable<string> ids) => Task.Run<IReadOnlyList<DeleteItemResult>>(() =>
    {
        if (WorkBuddyRunning())
            throw new InvalidOperationException("删除需要先完全退出 WorkBuddy 后重试——应用还在跑时侧栏标题不会消失。");
        var results = new List<DeleteItemResult>();
        var auth = WorkBuddyAuth.Read();
        foreach (var id in ids)
        {
            CodexProvider.GuardId(id);
            try
            {
                var (okFiles, size, fileError) = DeleteFiles(id);
                var soft = WorkBuddySidebar.TrySoftDelete(id);
                var warnings = new List<string>();
                if (!string.IsNullOrEmpty(soft.Warning)) warnings.Add(soft.Warning);

                string? cloudWarn = null;
                if (okFiles)
                    cloudWarn = WorkBuddySidebar.TryCloudDelete(id, auth);
                if (!string.IsNullOrEmpty(cloudWarn)) warnings.Add(cloudWarn);

                if (!okFiles)
                {
                    results.Add(new DeleteItemResult
                    {
                        AgentId = AgentId, Id = id, Ok = false,
                        Error = fileError ?? "删除文件失败",
                        FreedBytes = size,
                        Warning = warnings.Count > 0 ? string.Join("；", warnings) : null,
                    });
                    continue;
                }

                var note = soft.SchemaOk
                    ? (soft.Rows > 0 ? "已从本机侧栏软删" : "本机侧栏无对应行")
                    : null;
                titles.Remove(AgentId, id);
                results.Add(new DeleteItemResult
                {
                    AgentId = AgentId, Id = id, Ok = true, FreedBytes = size,
                    Note = note,
                    Warning = warnings.Count > 0 ? string.Join("；", warnings) : null,
                });
            }
            catch (Exception ex)
            {
                results.Add(new DeleteItemResult { AgentId = AgentId, Id = id, Ok = false, Error = ex.Message });
            }
        }
        return results;
    });

    /// <summary>删 jsonl 与同名 sidecar。文件不在也当成功。</summary>
    private static (bool Ok, long Size, string? Error) DeleteFiles(string id)
    {
        try
        {
            long size = 0;
            var jsonl = FindFile(id);
            if (jsonl is not null)
            {
                size += new FileInfo(jsonl).Length;
                File.Delete(jsonl);
            }
            var dir = jsonl is not null
                ? Path.Combine(Path.GetDirectoryName(jsonl)!, id)
                : FindSidecar(id);
            if (dir is not null && Directory.Exists(dir))
            {
                size += DirSize(dir);
                Directory.Delete(dir, recursive: true);
            }
            return (true, size, null);
        }
        catch (Exception ex)
        {
            return (false, 0, ex.Message);
        }
    }

    private static string? FindSidecar(string id)
    {
        if (!Directory.Exists(ProjectsRoot)) return null;
        foreach (var dir in Directory.EnumerateDirectories(ProjectsRoot, id, SearchOption.AllDirectories))
            if (string.Equals(Path.GetFileName(dir), id, StringComparison.OrdinalIgnoreCase))
                return dir;
        return null;
    }

    // ------------------------------------------------------------------

    internal static string? ParentFromPath(string jsonl)
    {
        var marker = $"{Path.DirectorySeparatorChar}subagents{Path.DirectorySeparatorChar}";
        if (!jsonl.Contains(marker, StringComparison.OrdinalIgnoreCase)) return null;
        var parentDir = Path.GetDirectoryName(Path.GetDirectoryName(jsonl));
        var name = parentDir is null ? null : Path.GetFileName(parentDir);
        return string.IsNullOrEmpty(name) || !IsUuid(name) ? null : name;
    }

    private static string? FindFile(string id)
    {
        if (!Directory.Exists(ProjectsRoot)) return null;
        foreach (var f in Directory.EnumerateFiles(ProjectsRoot, "*.jsonl", SearchOption.AllDirectories))
            if (string.Equals(Path.GetFileNameWithoutExtension(f), id, StringComparison.OrdinalIgnoreCase))
                return f;
        return null;
    }

    /// <summary>JSONL 扫描：旧版标题回退 / cwd / 消息数 / 末次时间。</summary>
    private static (string? Title, string? Cwd, long Count, DateTime? LastTs) Scan(string jsonl)
    {
        string? title = null, cwd = null;
        long count = 0;
        DateTime? lastTs = null;
        foreach (var line in UsageParsers.ReadLinesShared(jsonl))
        {
            if (line.Length == 0) continue;
            JsonDocument doc;
            try { doc = JsonDocument.Parse(line); }
            catch (JsonException) { continue; }
            using (doc)
            {
                var root = doc.RootElement;
                if (!root.TryGetProperty("type", out var typeEl)) continue;
                switch (typeEl.GetString())
                {
                    case "ai-title":
                        if (root.TryGetProperty("aiTitle", out var at) && at.ValueKind == JsonValueKind.String)
                            title = at.GetString();
                        break;
                    case "message":
                        count++;
                        lastTs = root.TryGetProperty("timestamp", out var tsEl) && tsEl.TryGetInt64(out var ms)
                            ? DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime : lastTs;
                        cwd = CodexProvider.GetString(root, "cwd") ?? cwd;
                        break;
                }
            }
        }
        return (title, cwd, count, lastTs);
    }

    private static string ExtractText(JsonElement root)
    {
        if (!root.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) return "";
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

    /// <summary>剥 user 消息里的 system-reminder 段（方案 §4.2：预览必须剥）。</summary>
    internal static string StripSystemReminder(string text) => SysReminder.Replace(text, "").Trim();

    private static bool IsUuid(string s) =>
        Guid.TryParse(s, out _);

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static long DirSize(string dir)
    {
        long n = 0;
        foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            try { n += new FileInfo(f).Length; } catch (IOException) { }
        }
        return n;
    }

    private static IReadOnlyList<ConversationMessage> Cap(List<ConversationMessage> messages)
    {
        var capped = messages.Count > 200 ? messages[..200] : messages;
        var result = new List<ConversationMessage>(capped.Count);
        foreach (var m in capped)
            result.Add(m with { Text = m.Text.Length > 4000 ? m.Text[..4000] + "\n…（截断）" : m.Text });
        return result;
    }
}
