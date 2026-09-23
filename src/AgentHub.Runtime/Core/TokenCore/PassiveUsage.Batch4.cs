using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using ZstdSharp;

namespace AgentHub.Core.TokenCore;

/// <summary>
/// Prime Agent、Craft、Kilo CLI、Kilo Code、Roo Code、Zed。
/// Prime 复用 pi 家族读法；Kilo CLI 复用 OpenCode 的 SQLite；Kilo Code 与 Roo Code 共用 ui_messages.json。
/// 会话头和线程库里的累计值按绝对快照入库，重扫覆盖，不把 TokenTracker 的增量游标搬过来。
/// </summary>
internal static partial class PassiveUsage
{
    private const int ZedMaxJsonBytes = 32 * 1024 * 1024;
    private const int CraftHeaderBytes = 1024 * 1024;
    private const int RooHistoryBytes = 1024 * 1024;

    public static List<UsageRecord> ReadCraft(IEnumerable<string> configDirs)
    {
        var bySession = new Dictionary<string, UsageRecord>(StringComparer.Ordinal);
        foreach (var file in CraftSessionFiles(configDirs))
        {
            try
            {
                var row = ReadCraftHeader(file);
                if (row is not null) bySession[row.SessionId] = row;
            }
            catch (Exception ex) when (ex is IOException or JsonException) { }
        }
        return bySession.Values.ToList();
    }

    public static List<UsageRecord> ReadKiloCli(IEnumerable<string> dbPaths)
    {
        var byKey = new Dictionary<string, UsageRecord>(StringComparer.Ordinal);
        var fingerprints = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var path in dbPaths)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) continue;
            if (!UsageIo.TrySnapshot(path, "agenthub-kilo-", out var db, out var tmp)) continue;
            try { ReadOpenCodeDb(db, byKey, fingerprints, "kilo-cli"); }
            catch (SqliteException) { }
            finally { UsageIo.DeleteSnapshot(tmp); }
        }
        return byKey.Values.ToList();
    }

    public static List<UsageRecord> ReadKiloCode(IEnumerable<string> editorRoots) =>
        ReadClineTasks(editorRoots, "kilo-code", "kilocode.kilo-code", ClineModel.Kilo);

    public static List<UsageRecord> ReadRooCode(IEnumerable<string> editorRoots) =>
        ReadClineTasks(editorRoots, "roo-code", "rooveterinaryinc.roo-cline", ClineModel.Roo);

    public static List<UsageRecord> ReadZed(IEnumerable<string> dbPaths)
    {
        var list = new List<UsageRecord>();
        foreach (var path in dbPaths)
            WithSqlite(path, "agenthub-zed-", conn => ReadZedDb(conn, list));
        return list;
    }

    private static List<string> CraftSessionFiles(IEnumerable<string> configDirs)
    {
        var files = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var config in configDirs)
        {
            if (string.IsNullOrWhiteSpace(config) || !Directory.Exists(config)) continue;
            foreach (var workspace in CraftWorkspaces(config))
            {
                var sessions = Path.Combine(workspace, "sessions");
                if (!Directory.Exists(sessions)) continue;
                IEnumerable<string> dirs;
                try { dirs = Directory.EnumerateDirectories(sessions).ToList(); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
                foreach (var dir in dirs)
                {
                    var file = Path.Combine(dir, "session.jsonl");
                    if (File.Exists(file) && seen.Add(file)) files.Add(file);
                }
            }
        }
        files.Sort(StringComparer.Ordinal);
        return files;
    }

    private static IEnumerable<string> CraftWorkspaces(string configDir)
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var defaults = Path.Combine(configDir, "workspaces");
        if (Directory.Exists(defaults))
        {
            try
            {
                foreach (var dir in Directory.EnumerateDirectories(defaults))
                    roots.Add(dir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        var config = Path.Combine(configDir, "config.json");
        if (File.Exists(config))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(config));
                if (doc.RootElement.TryGetProperty("workspaces", out var list) && list.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in list.EnumerateArray())
                    {
                        var root = UsageParsers.GetStr(item, "rootPath");
                        if (!string.IsNullOrWhiteSpace(root) && Directory.Exists(root))
                            roots.Add(root);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException) { }
        }
        return roots;
    }

    private static UsageRecord? ReadCraftHeader(string file)
    {
        var line = ReadCappedFirstJsonLine(file, CraftHeaderBytes);
        if (string.IsNullOrEmpty(line)) return null;
        using var doc = JsonDocument.Parse(line);
        var header = doc.RootElement;
        var usage = UsageParsers.GetObj(header, "tokenUsage");
        if (usage is null) return null;
        var input = UsageIo.JsonLong(usage.Value, "inputTokens");
        var output = UsageIo.JsonLong(usage.Value, "outputTokens");
        var cacheRead = UsageIo.JsonLong(usage.Value, "cacheReadTokens");
        var cacheWrite = UsageIo.JsonLong(usage.Value, "cacheCreationTokens");
        if (input == 0 && output == 0 && cacheRead == 0 && cacheWrite == 0) return null;
        var sessionId = FirstText(UsageParsers.GetStr(header, "id"), UsageParsers.GetStr(header, "sdkSessionId"))
            ?? Path.GetFileName(Path.GetDirectoryName(file));
        if (string.IsNullOrEmpty(sessionId)) return null;
        var ts = CraftTime(header, file);
        if (ts is null) return null;
        var sessions = Path.GetDirectoryName(Path.GetDirectoryName(file));
        var project = sessions is null ? null : Path.GetDirectoryName(sessions);
        return Row("craft-agents", sessionId, "snapshot", ts.Value, input, output, cacheRead, cacheWrite, 0,
            UsageParsers.GetStr(header, "model") ?? "craft-unknown",
            string.IsNullOrEmpty(project) ? null : project);
    }

    private static DateTime? CraftTime(JsonElement header, string file)
    {
        foreach (var name in new[] { "lastMessageAt", "lastUsedAt", "createdAt" })
        {
            if (header.ValueKind != JsonValueKind.Object || !header.TryGetProperty(name, out var value)) continue;
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var ms) && ms > 0)
            {
                try { return DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime; }
                catch (ArgumentOutOfRangeException) { continue; }
            }
            if (value.ValueKind == JsonValueKind.String && UsageParsers.ParseIso(value.GetString()) is { } iso)
                return iso;
        }
        try { return File.GetLastWriteTimeUtc(file); }
        catch (IOException) { return null; }
    }

    private static string? ReadCappedFirstJsonLine(string file, int maxBytes)
    {
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var limit = (int)Math.Min(maxBytes, stream.Length);
        if (limit <= 0) return null;
        var buf = new byte[limit];
        var n = stream.Read(buf, 0, limit);
        var text = Encoding.UTF8.GetString(buf, 0, n);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim().TrimEnd('\r');
            if (line.Length > 0) return line;
        }
        return null;
    }

    private enum ClineModel { Kilo, Roo }

    private static List<UsageRecord> ReadClineTasks(
        IEnumerable<string> editorRoots, string tool, string extensionId, ClineModel modelKind)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var list = new List<UsageRecord>();
        foreach (var file in ClineTaskFiles(editorRoots, extensionId))
        {
            var taskId = Path.GetFileName(Path.GetDirectoryName(file));
            if (string.IsNullOrEmpty(taskId)) continue;
            var taskModel = modelKind == ClineModel.Roo ? RooTaskModel(file) : null;
            JsonDocument doc;
            try { doc = JsonDocument.Parse(File.ReadAllText(file)); }
            catch (Exception ex) when (ex is IOException or JsonException) { continue; }
            using (doc)
            {
                if (doc.RootElement.ValueKind != JsonValueKind.Array) continue;
                foreach (var msg in doc.RootElement.EnumerateArray())
                {
                    var say = UsageParsers.GetStr(msg, "say");
                    if (say is not ("api_req_started" or "api_req_deleted")) continue;
                    var text = UsageParsers.GetStr(msg, "text");
                    if (string.IsNullOrEmpty(text) || !text.StartsWith('{')) continue;
                    if (!UsageIo.TryJsonLong(msg, "ts", out var tsMs) || tsMs <= 0) continue;
                    var key = taskId + ":" + tsMs.ToString(CultureInfo.InvariantCulture);
                    if (seen.Contains(key)) continue;
                    JsonDocument payloadDoc;
                    try { payloadDoc = JsonDocument.Parse(text); }
                    catch (JsonException) { continue; }
                    using (payloadDoc)
                    {
                        var payload = payloadDoc.RootElement;
                        var input = UsageIo.JsonLong(payload, "tokensIn");
                        var output = UsageIo.JsonLong(payload, "tokensOut");
                        var cacheRead = UsageIo.JsonLong(payload, "cacheReads");
                        var cacheWrite = UsageIo.JsonLong(payload, "cacheWrites");
                        // 请求开始时先写一条全零，完成后再原位回填同一个 ts。全零不能占住去重键。
                        if (input == 0 && output == 0 && cacheRead == 0 && cacheWrite == 0) continue;
                        DateTime ts;
                        try { ts = DateTimeOffset.FromUnixTimeMilliseconds(tsMs).UtcDateTime; }
                        catch (ArgumentOutOfRangeException) { continue; }
                        var model = modelKind == ClineModel.Kilo
                            ? KiloProviderModel(UsageParsers.GetStr(payload, "inferenceProvider"))
                            : RooModel(taskModel, UsageParsers.GetStr(payload, "apiProtocol"));
                        seen.Add(key);
                        list.Add(Row(tool, taskId, tsMs.ToString(CultureInfo.InvariantCulture), ts,
                            input, output, cacheRead, cacheWrite, 0, model, null));
                    }
                }
            }
        }
        return list;
    }

    private static List<string> ClineTaskFiles(IEnumerable<string> editorRoots, string extensionId)
    {
        var files = new List<string>();
        foreach (var root in editorRoots)
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            var tasks = Path.Combine(root, "User", "globalStorage", extensionId, "tasks");
            files.AddRange(UsageIo.EnumerateFiles(tasks, 2, static (path, _) =>
                Path.GetFileName(path) == "ui_messages.json"));
        }
        files.Sort(StringComparer.Ordinal);
        return files;
    }

    private static string KiloProviderModel(string? provider)
    {
        if (string.IsNullOrWhiteSpace(provider)) return "provider:unknown";
        var slug = Regex.Replace(provider.Trim().ToLowerInvariant(), @"\s+", "-");
        slug = Regex.Replace(slug, "[^a-z0-9._-]", "");
        if (slug.Length == 0 || !slug.Any(char.IsAsciiLetterOrDigit)) return "provider:unknown";
        return "provider:" + slug;
    }

    private static string? RooTaskModel(string uiMessagesPath)
    {
        var history = Path.Combine(Path.GetDirectoryName(uiMessagesPath) ?? "", "api_conversation_history.json");
        if (!File.Exists(history)) return null;
        string raw;
        try { raw = File.ReadAllText(history); }
        catch (IOException) { return null; }
        if (raw.Length > RooHistoryBytes)
        {
            var naive = raw[^RooHistoryBytes..];
            var block = naive.IndexOf("<environment_details>", StringComparison.Ordinal);
            raw = block >= 0 ? naive[block..] : naive;
        }
        string? last = null;
        foreach (Match match in Regex.Matches(raw, @"<model>\s*([^<\s][^<]*?)\s*</model>"))
        {
            var value = match.Groups[1].Value.Trim();
            if (value.Length > 0) last = value;
        }
        return last;
    }

    private static string RooModel(string? explicitModel, string? apiProtocol)
    {
        if (!string.IsNullOrWhiteSpace(explicitModel)) return explicitModel.Trim();
        if (!string.IsNullOrWhiteSpace(apiProtocol))
        {
            var slug = Regex.Replace(apiProtocol.Trim().ToLowerInvariant(), "[^a-z0-9._-]", "");
            if (slug.Length > 0) return "protocol:" + slug;
        }
        return "unknown";
    }

    private static void ReadZedDb(SqliteConnection conn, List<UsageRecord> list)
    {
        if (!UsageIo.TableExists(conn, "threads")) return;
        var columns = SqliteColumns(conn, "threads");
        if (!columns.Contains("id") || !columns.Contains("data") || !columns.Contains("data_type")) return;
        var updated = columns.Contains("updated_at") ? "updated_at" : "NULL";
        var created = columns.Contains("created_at") ? "created_at" : "NULL";
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT id, {updated} AS updated_at, {created} AS created_at, data_type, data FROM threads";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var id = ReadText(reader, "id");
            if (string.IsNullOrEmpty(id)) continue;
            var blob = ReadBlob(reader, "data");
            if (blob is null || blob.Length == 0) continue;
            var json = DecodeZedBlob(ReadText(reader, "data_type"), blob);
            if (string.IsNullOrEmpty(json)) continue;
            JsonDocument doc;
            try { doc = JsonDocument.Parse(json); }
            catch (JsonException) { continue; }
            using (doc)
            {
                var thread = doc.RootElement;
                if (thread.TryGetProperty("imported", out var imported)
                    && imported.ValueKind == JsonValueKind.True)
                    continue;
                var modelObj = UsageParsers.GetObj(thread, "model");
                var model = modelObj is null ? null : UsageParsers.GetStr(modelObj, "model");
                if (string.IsNullOrWhiteSpace(model)) continue;
                var ts = UsageParsers.ParseIso(ReadText(reader, "updated_at"))
                    ?? UsageParsers.ParseIso(ReadText(reader, "created_at"))
                    ?? UsageParsers.ParseIso(UsageParsers.GetStr(thread, "updated_at"));
                if (ts is null) continue;
                if (!AppendZedUsage(list, id, model, ts.Value, thread)) continue;
            }
        }
    }

    private static bool AppendZedUsage(List<UsageRecord> list, string sessionId, string model, DateTime ts, JsonElement thread)
    {
        if (thread.TryGetProperty("request_token_usage", out var requests) && ZedRequestSum(requests) > 0)
        {
            var added = false;
            if (requests.ValueKind == JsonValueKind.Object)
            {
                foreach (var item in requests.EnumerateObject())
                    added |= AppendZedOne(list, sessionId, item.Name, model, ts, item.Value);
            }
            else if (requests.ValueKind == JsonValueKind.Array)
            {
                var i = 0;
                foreach (var item in requests.EnumerateArray())
                    added |= AppendZedOne(list, sessionId, "idx:" + i++.ToString(CultureInfo.InvariantCulture), model, ts, item);
            }
            return added;
        }
        if (!thread.TryGetProperty("cumulative_token_usage", out var cumulative)) return false;
        return AppendZedOne(list, sessionId, "snapshot", model, ts, cumulative);
    }

    private static bool AppendZedOne(List<UsageRecord> list, string sessionId, string key, string model, DateTime ts, JsonElement usage)
    {
        if (usage.ValueKind != JsonValueKind.Object) return false;
        var input = ZedCount(usage, "input_tokens");
        var output = ZedCount(usage, "output_tokens");
        var cacheRead = ZedCount(usage, "cache_read_input_tokens");
        var cacheWrite = ZedCount(usage, "cache_creation_input_tokens");
        if (input == 0 && output == 0 && cacheRead == 0 && cacheWrite == 0) return false;
        if (string.IsNullOrEmpty(key)) return false;
        list.Add(Row("zed-agent", sessionId, key, ts, input, output, cacheRead, cacheWrite, 0, model, null));
        return true;
    }

    private static long ZedRequestSum(JsonElement requests)
    {
        long sum = 0;
        if (requests.ValueKind == JsonValueKind.Object)
        {
            foreach (var item in requests.EnumerateObject())
                sum += ZedUsageSum(item.Value);
        }
        else if (requests.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in requests.EnumerateArray())
                sum += ZedUsageSum(item);
        }
        return sum;
    }

    private static long ZedUsageSum(JsonElement usage) =>
        usage.ValueKind == JsonValueKind.Object
            ? ZedCount(usage, "input_tokens") + ZedCount(usage, "output_tokens")
              + ZedCount(usage, "cache_read_input_tokens") + ZedCount(usage, "cache_creation_input_tokens")
            : 0;

    /// <summary>数字取非负整数；字符串按 parseInt，只接受正数。</summary>
    private static long ZedCount(JsonElement usage, string name)
    {
        if (!usage.TryGetProperty(name, out var value)) return 0;
        if (value.ValueKind == JsonValueKind.Number)
            return UsageIo.JsonLong(usage, name);
        if (value.ValueKind != JsonValueKind.String) return 0;
        var text = value.GetString();
        if (string.IsNullOrEmpty(text)) return 0;
        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) || n <= 0)
            return 0;
        return n;
    }

    private static byte[]? ReadBlob(SqliteDataReader reader, string column)
    {
        var i = reader.GetOrdinal(column);
        if (reader.IsDBNull(i)) return null;
        try { return reader.GetFieldValue<byte[]>(i); }
        catch (InvalidCastException) { return null; }
    }

    private static string? DecodeZedBlob(string? dataType, byte[] blob)
    {
        var type = (dataType ?? "").Trim().ToLowerInvariant();
        try
        {
            if (type == "json")
                return blob.Length > ZedMaxJsonBytes ? null : Encoding.UTF8.GetString(blob);
            if (type != "zstd") return null;
            using var input = new MemoryStream(blob);
            using var zstd = new DecompressionStream(input);
            using var output = new MemoryStream();
            var buf = new byte[81920];
            int n;
            while ((n = zstd.Read(buf, 0, buf.Length)) > 0)
            {
                if (output.Length + n > ZedMaxJsonBytes) return null;
                output.Write(buf, 0, n);
            }
            return Encoding.UTF8.GetString(output.ToArray());
        }
        catch (Exception)
        {
            return null;
        }
    }
}
