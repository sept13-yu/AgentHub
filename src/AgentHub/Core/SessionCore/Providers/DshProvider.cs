using System.IO;
using System.Text;
using System.Text.Json;
using ZstdSharp;

namespace AgentHub.Core.SessionCore.Providers;

/// <summary>DSH 会话（方案 §4.2 + docs/探测/dsh.md）：
/// ~/.dsh/sessions/--&lt;编码项目&gt;--/session-&lt;uuid&gt;/session.jsonl.zstd。
/// 多帧 zstd——必须按魔数 28 B5 2F FD 切帧逐帧解压（朴素解压静默丢 99.99%）；
/// 尾帧写入中可能截断：失败即停，保留已解压部分并在 Note 里说明。
/// 项目名读 session.cwd（目录名反解有损）；标题优先 session/title 事件，否则覆盖表/首条消息。</summary>
public sealed class DshProvider(TitleOverrideStore titles) : IConversationProvider
{
    private static readonly string Root = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh", "sessions");

    private const string SessionFile = "session.jsonl.zstd";
    private static readonly byte[] ZstdMagic = [0x28, 0xB5, 0x2F, 0xFD];
    private const long MaxFrameOut = 64L * 1024 * 1024;

    public string AgentId => "dsh";

    public Task<IReadOnlyList<ConversationSummary>> ListAsync() => Task.Run<IReadOnlyList<ConversationSummary>>(() =>
    {
        var list = new List<ConversationSummary>();
        if (!Directory.Exists(Root)) return list;
        foreach (var dir in Directory.EnumerateDirectories(Root))
        {
            foreach (var sessionDir in Directory.EnumerateDirectories(dir))
            {
                var file = Path.Combine(sessionDir, SessionFile);
                if (!File.Exists(file)) continue;   // .dsh-mkdir-* 临时目录没有该文件，自然跳过
                var id = NormalizeSessionId(Path.GetFileName(sessionDir));
                var (title, cwd, count, lastTs, truncated) = Scan(file);
                list.Add(new ConversationSummary
                {
                    AgentId = AgentId,
                    Id = id,
                    Title = titles.Get(AgentId, id) ?? title ?? "(无标题)",
                    TitleSource = titles.Get(AgentId, id) is not null ? "override" : title is not null ? "source" : "derived",
                    Project = cwd,   // session.cwd 才是真实路径，目录名反解有损
                    MessageCount = count,
                    SizeBytes = new FileInfo(file).Length,
                    LastActivityUtc = lastTs ?? new FileInfo(file).LastWriteTimeUtc,
                    IsSubagent = false,   // delegationDepth>0 的子代理是独立 session 文件，父子分开计
                    SourceFile = file,
                });
            }
        }
        return list;
    });

    public Task<ConversationDetail?> LoadAsync(string id)
    {
        CodexProvider.GuardId(id);
        return Task.Run<ConversationDetail?>(() =>
        {
            var file = FindFile(id);
            if (file is null) return null;

            var (raw, framesOk, framesSeen, truncated) = DecompressAll(File.ReadAllBytes(file));
            var messages = new List<ConversationMessage>();
            string? title = null, cwd = null, sessionId = null;
            DateTime? lastTs = null;

            foreach (var line in raw.Split('\n'))
            {
                var l = line.TrimEnd('\r');
                if (l.Length == 0) continue;
                JsonDocument doc;
                try { doc = JsonDocument.Parse(l); }
                catch (JsonException) { continue; }
                using (doc)
                {
                    var root = doc.RootElement;
                    if (!root.TryGetProperty("type", out var typeEl)) continue;
                    var type = typeEl.GetString();
                    var data = root.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Object ? d : (JsonElement?)null;

                    if (type == "session" && data is null)
                    {
                        // 首帧 session 头：字段在顶层（实测形状）
                        sessionId = CodexProvider.GetString(root, "id") ?? sessionId;
                        cwd = CodexProvider.GetString(root, "cwd") ?? cwd;
                        continue;
                    }
                    if (type == "session/title")
                    {
                        title = CodexProvider.GetString(data, "title")
                             ?? CodexProvider.GetString(data, "text") ?? title;
                        continue;
                    }
                    if (type is not ("user/message" or "assistant/message") || data is null) continue;

                    var role = CodexProvider.GetString(data, "role")
                            ?? (type == "user/message" ? "user" : "assistant");
                    if (role is not ("user" or "assistant")) continue;
                    var text = ExtractText(data);
                    if (text.Length == 0) continue;

                    var ts = root.TryGetProperty("time", out var tEl) && tEl.TryGetInt64(out var ms)
                        ? DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime : (DateTime?)null;
                    lastTs = ts ?? lastTs;
                    cwd = CodexProvider.GetString(root, "cwd") ?? CodexProvider.GetString(data, "cwd") ?? cwd;
                    messages.Add(new ConversationMessage { Role = role, TimestampUtc = ts, Text = text });
                }
            }

            string? note = null;
            if (truncated)
                note = $"尾帧写入中截断，已解压 {framesOk}/{framesSeen} 帧。预览为部分内容。";
            else if (messages.Count >= 200)
                note = "消息较多，预览仅显示前 200 条。";

            return new ConversationDetail
            {
                Summary = new ConversationSummary
                {
                    AgentId = AgentId,
                    Id = sessionId ?? id,
                    Title = titles.Get(AgentId, id) ?? title ?? "(无标题)",
                    TitleSource = titles.Get(AgentId, id) is not null ? "override" : title is not null ? "source" : "derived",
                    Project = cwd,
                    MessageCount = messages.Count,
                    SizeBytes = new FileInfo(file).Length,
                    LastActivityUtc = lastTs ?? new FileInfo(file).LastWriteTimeUtc,
                    IsSubagent = false,
                    SourceFile = file,
                },
                Messages = messages.Count > 200 ? messages[..200] : messages,
                Note = note,
            };
        });
    }

    public Task RenameAsync(string id, string title)
    {
        CodexProvider.GuardId(id);
        titles.Set(AgentId, id, title);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<DeleteItemResult>> DeleteAsync(IEnumerable<string> ids) => Task.Run<IReadOnlyList<DeleteItemResult>>(() =>
    {
        var results = new List<DeleteItemResult>();
        foreach (var id in ids)
        {
            CodexProvider.GuardId(id);
            try
            {
                var file = FindFile(id);
                if (file is null)
                {
                    results.Add(new DeleteItemResult { AgentId = AgentId, Id = id, Ok = false, Error = "会话目录不存在（可能已被删除）" });
                    continue;
                }
                var dir = Path.GetDirectoryName(file)!;
                long size = DirSize(dir);
                Directory.Delete(dir, recursive: true);   // 删文件/目录（方案 §4.2）
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
    // 多帧 zstd（docs/探测/dsh.md 硬性规则）
    // ------------------------------------------------------------------

    /// <summary>按魔数切帧逐帧解压。某帧失败即停，已解压部分保留（返回 truncated=true）。</summary>
    internal static (string Text, int FramesOk, int FramesSeen, bool Truncated) DecompressAll(byte[] data)
    {
        var offsets = FrameOffsets(data);
        if (offsets.Count == 0) return ("", 0, 0, false);

        var payload = new MemoryStream();
        int ok = 0;
        for (int i = 0; i < offsets.Count; i++)
        {
            int end = i + 1 < offsets.Count ? offsets[i + 1] : data.Length;
            var frame = new byte[end - offsets[i]];
            Array.Copy(data, offsets[i], frame, 0, frame.Length);
            try
            {
                using var input = new MemoryStream(frame);
                using var zstream = new DecompressionStream(input);
                zstream.CopyTo(payload, 81920);
                if (payload.Length > MaxFrameOut * offsets.Count) throw new InvalidDataException("解压体积超限");
                ok++;
            }
            catch (Exception)
            {
                // 尾帧写入中截断：停在这里，保留 payload 已有内容（可能与截断前写入的部分重复，接受）
                return (Encoding.UTF8.GetString(payload.ToArray()), ok, offsets.Count, true);
            }
        }
        return (Encoding.UTF8.GetString(payload.ToArray()), ok, offsets.Count, false);
    }

    private static List<int> FrameOffsets(byte[] data)
    {
        var offsets = new List<int>();
        int start = 0;
        while (true)
        {
            int idx = IndexOf(data, ZstdMagic, start);
            if (idx < 0) break;
            offsets.Add(idx);
            start = idx + 4;
        }
        return offsets;
    }

    private static int IndexOf(byte[] hay, byte[] needle, int start)
    {
        for (int i = start; i <= hay.Length - needle.Length; i++)
        {
            bool ok = true;
            for (int j = 0; j < needle.Length; j++)
                if (hay[i + j] != needle[j]) { ok = false; break; }
            if (ok) return i;
        }
        return -1;
    }

    internal static string NormalizeSessionId(string dirName)
    {
        // `session-<uuid>` 与裸 uuid 都归一小写 uuid
        var m = System.Text.RegularExpressions.Regex.Match(dirName,
            @"(?:session-)?([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})$");
        if (m.Success) return m.Groups[1].Value.ToLowerInvariant();
        return dirName.StartsWith("session-") ? dirName["session-".Length..] : dirName;
    }

    private static string? FindFile(string id)
    {
        if (!Directory.Exists(Root)) return null;
        foreach (var dir in Directory.EnumerateDirectories(Root))
            foreach (var sessionDir in Directory.EnumerateDirectories(dir))
                if (NormalizeSessionId(Path.GetFileName(sessionDir)) == id.ToLowerInvariant())
                    return Path.Combine(sessionDir, SessionFile);
        return null;
    }

    private static (string? Title, string? Cwd, long Count, DateTime? LastTs, bool Truncated) Scan(string file)
    {
        var (raw, _, _, truncated) = DecompressAll(File.ReadAllBytes(file));
        string? title = null, cwd = null;
        long count = 0;
        DateTime? lastTs = null;
        foreach (var line in raw.Split('\n'))
        {
            var l = line.TrimEnd('\r');
            if (l.Length == 0) continue;
            JsonDocument doc;
            try { doc = JsonDocument.Parse(l); }
            catch (JsonException) { continue; }
            using (doc)
            {
                var root = doc.RootElement;
                if (!root.TryGetProperty("type", out var typeEl)) continue;
                var type = typeEl.GetString();
                if (type == "session")
                {
                    cwd = CodexProvider.GetString(root, "cwd") ?? cwd;
                    continue;
                }
                if (type == "session/title")
                {
                    var data = root.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Object ? d : (JsonElement?)null;
                    title = CodexProvider.GetString(data, "title") ?? CodexProvider.GetString(data, "text") ?? title;
                    continue;
                }
                if (type is "user/message" or "assistant/message")
                {
                    count++;
                    if (root.TryGetProperty("time", out var tEl) && tEl.TryGetInt64(out var ms))
                        lastTs = DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;
                }
            }
        }
        return (title, cwd, count, lastTs, truncated);
    }

    private static string ExtractText(JsonElement? dataNullable)
    {
        // user/message.data.content[].text；assistant/message 的文本结构防御式兼容
        if (dataNullable is null) return "";
        var data = dataNullable.Value;
        if (data.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
        {
            var sb = new StringBuilder();
            foreach (var part in content.EnumerateArray())
            {
                if (part.ValueKind != JsonValueKind.Object) continue;
                if (!part.TryGetProperty("text", out var txt) || txt.ValueKind != JsonValueKind.String) continue;
                var s = txt.GetString();
                if (!string.IsNullOrEmpty(s)) sb.AppendLine(s);
            }
            var joined = sb.ToString().TrimEnd('\r', '\n');
            if (joined.Length > 0) return joined;
        }
        return CodexProvider.GetString(data, "text") ?? "";
    }

    private static long DirSize(string dir)
    {
        long n = 0;
        foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            try { n += new FileInfo(f).Length; } catch (IOException) { }
        }
        return n;
    }
}
