using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Text.Json.Nodes;
using ZstdSharp;

namespace AgentHub.Core.SessionCore.Providers;

/// <summary>DSH 会话：
/// ~/.dsh/sessions/--&lt;编码项目&gt;--/session-&lt;uuid&gt;/session.jsonl.zstd。
/// 多帧 zstd——必须按魔数 28 B5 2F FD 切帧逐帧解压（朴素解压静默丢 99.99%）；
/// 尾帧写入中可能截断：失败即停，保留已解压部分并在 Note 里说明。
/// 项目名读 session.cwd（目录名反解有损）；标题优先 session/title 事件，否则覆盖表/首条消息。</summary>
public sealed class DshProvider(TitleOverrideStore titles) : IConversationProvider
{
    private static readonly string Root = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh", "sessions");

    // TokenTracker: /^session(?:\.v\d+)?\.jsonl(?:\.zstd)?$/i
    private static readonly Regex SessionLogName = new(
        @"^session(?:\.v\d+)?\.jsonl(?:\.zstd)?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
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
                var file = PickSessionLog(sessionDir);
                if (file is null) continue;   // .dsh-mkdir-* 临时目录没有该文件，自然跳过
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

                    if (type is "session" or "session/start")
                    {
                        // 首帧 session 头：字段在顶层（实测形状）
                        sessionId = CodexProvider.GetString(root, "id") ?? CodexProvider.GetString(data, "id") ?? CodexProvider.GetString(data, "sessionId") ?? sessionId;
                        cwd = CodexProvider.GetString(root, "cwd") ?? CodexProvider.GetString(data, "cwd") ?? cwd;
                        continue;
                    }
                    if (type == "session/title")
                    {
                        title = CodexProvider.GetString(data, "title")
                             ?? CodexProvider.GetString(data, "text") ?? title;
                        continue;
                    }
                    if (type is not ("user/message" or "assistant/message" or "message/user" or "message/assistant") || data is null) continue;

                    var role = CodexProvider.GetString(data, "role")
                            ?? (type is "user/message" or "message/user" ? "user" : "assistant");
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
                    TryDeleteProjCache(id);
                    results.Add(new DeleteItemResult { AgentId = AgentId, Id = id, Ok = false, Error = "会话目录不存在（可能已被删除）" });
                    continue;
                }
                var dir = Path.GetDirectoryName(file)!;
                long size = DirSize(dir);
                Directory.Delete(dir, recursive: true);   // 删文件/目录本体（§4.2）
                size += TryDeleteProjCache(id);
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
    // 多帧 zstd：必须按魔数切帧，朴素整包解压会静默丢帧
    // ------------------------------------------------------------------

    /// <summary>按魔数切帧逐帧解压。某帧失败即停，已解压部分保留（返回 truncated=true）。</summary>
    internal static (string Text, int FramesOk, int FramesSeen, bool Truncated) DecompressAll(byte[] data)
    {
        var offsets = FrameOffsets(data);
        // TokenTracker / DSH v3: compression:none writes session.v3.jsonl (no zstd frames)
        if (offsets.Count == 0) return (Encoding.UTF8.GetString(data), 0, 0, false);

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

    /// <summary>
    /// Per session dir, if multiple logs exist pick one: newest mtime, then higher .vN, then prefer .zstd (TokenTracker).
    /// </summary>
    internal static string? PickSessionLog(string sessionDir)
    {
        if (!Directory.Exists(sessionDir)) return null;
        var candidates = new List<(string Path, DateTime MtimeUtc, int Version, bool IsZstd)>();
        foreach (var path in Directory.EnumerateFiles(sessionDir))
        {
            var name = Path.GetFileName(path);
            if (!SessionLogName.IsMatch(name)) continue;
            var mtime = File.GetLastWriteTimeUtc(path);
            var ver = 0;
            var vm = Regex.Match(name, @"\.v(\d+)\.jsonl", RegexOptions.IgnoreCase);
            if (vm.Success) int.TryParse(vm.Groups[1].Value, out ver);
            var isZstd = name.EndsWith(".zstd", StringComparison.OrdinalIgnoreCase);
            candidates.Add((path, mtime, ver, isZstd));
        }
        if (candidates.Count == 0) return null;
        if (candidates.Count == 1) return candidates[0].Path;
        candidates.Sort((a, b) =>
        {
            var c = b.MtimeUtc.CompareTo(a.MtimeUtc);
            if (c != 0) return c;
            c = b.Version.CompareTo(a.Version);
            if (c != 0) return c;
            return (b.IsZstd ? 1 : 0).CompareTo(a.IsZstd ? 1 : 0);
        });
        return candidates[0].Path;
    }

    private static string? FindFile(string id)
    {
        if (!Directory.Exists(Root)) return null;
        foreach (var dir in Directory.EnumerateDirectories(Root))
            foreach (var sessionDir in Directory.EnumerateDirectories(dir))
                if (NormalizeSessionId(Path.GetFileName(sessionDir)) == id.ToLowerInvariant())
                    return PickSessionLog(sessionDir);
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
                if (type is "session" or "session/start")
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
                if (type is "user/message" or "assistant/message" or "message/user" or "message/assistant")
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


    private static readonly string ProjCacheSessions = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".dsh", "storages", "session_projcache", "sessions");

    /// <summary>删 ~/.dsh/storages/session_projcache 中与会话对应的缓存（缺文件/锁住忽略）。</summary>
    private static long TryDeleteProjCache(string id)
    {
        long n = 0;
        try
        {
            if (!Directory.Exists(ProjCacheSessions)) return 0;
            var uuid = NormalizeSessionId(id);
            foreach (var name in new[] { id + ".json", "session-" + uuid + ".json", uuid + ".json" })
            {
                var path = Path.Combine(ProjCacheSessions, name);
                try
                {
                    if (!File.Exists(path)) continue;
                    n += new FileInfo(path).Length;
                    File.Delete(path);
                }
                catch (Exception) { }
            }
            var index = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".dsh", "storages", "session_projcache", "session_projcache.json");
            TryScrubProjCacheIndex(index, uuid);
        }
        catch (Exception) { }
        return n;
    }

    private static void TryScrubProjCacheIndex(string indexPath, string uuid)
    {
        try
        {
            if (!File.Exists(indexPath)) return;
            var text = File.ReadAllText(indexPath);
            if (text.IndexOf(uuid, StringComparison.OrdinalIgnoreCase) < 0) return;
            var node = System.Text.Json.Nodes.JsonNode.Parse(text);
            if (node is System.Text.Json.Nodes.JsonObject jo)
            {
                var drop = new List<string>();
                foreach (var kv in jo)
                {
                    if (kv.Key.Contains(uuid, StringComparison.OrdinalIgnoreCase)
                        || (kv.Value?.ToJsonString().Contains(uuid, StringComparison.OrdinalIgnoreCase) ?? false))
                        drop.Add(kv.Key);
                }
                if (drop.Count == 0) return;
                foreach (var k in drop) jo.Remove(k);
                WriteJsonAtomic(indexPath, jo.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            }
            else if (node is System.Text.Json.Nodes.JsonArray arr)
            {
                var removed = false;
                for (var i = arr.Count - 1; i >= 0; i--)
                {
                    var s = arr[i]?.ToJsonString() ?? "";
                    if (!s.Contains(uuid, StringComparison.OrdinalIgnoreCase)) continue;
                    arr.RemoveAt(i);
                    removed = true;
                }
                if (!removed) return;
                WriteJsonAtomic(indexPath, arr.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            }
        }
        catch (Exception) { }
    }

    private static void WriteJsonAtomic(string path, string payload)
    {
        var tmp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(tmp, payload);
        File.Copy(tmp, path, overwrite: true);
        try { File.Delete(tmp); } catch { }
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
