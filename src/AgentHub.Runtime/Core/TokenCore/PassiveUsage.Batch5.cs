using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace AgentHub.Core.TokenCore;

/// <summary>
/// Goose、Droid、AnythingLLM、Claude Science、LM Studio、Unsloth Studio。
/// 会话级累计值按绝对快照入库。LM Studio 用响应 id 合并镜像日志。只投影 token 标量。
/// </summary>
internal static partial class PassiveUsage
{
    private const int LmStudioMaxBytes = 32 * 1024 * 1024;
    private const int DroidCwdBytes = 64 * 1024;

    private static readonly string[] ClaudeScienceTokenColumns =
    [
        "input_tokens", "output_tokens", "cache_read_tokens", "cache_write_tokens",
        "aux_input_tokens", "aux_output_tokens", "aux_cache_read_tokens", "aux_cache_write_tokens",
    ];

    private static readonly HashSet<string> UnslothMeteredProviders = new(StringComparer.Ordinal)
    {
        "anthropic", "deepseek", "gemini", "huggingface", "kimi", "mistral", "openai", "openrouter", "qwen",
    };

    public static List<UsageRecord> ReadGoose(IEnumerable<string> dbPaths)
    {
        var list = new List<UsageRecord>();
        foreach (var path in dbPaths)
            WithSqlite(path, "agenthub-goose-", conn => ReadGooseDb(conn, list));
        return list;
    }

    public static List<UsageRecord> ReadDroid(IEnumerable<string> sessionDirs)
    {
        var list = new List<UsageRecord>();
        foreach (var file in DroidCanonicalFiles(sessionDirs))
        {
            try
            {
                var row = ReadDroidFile(file);
                if (row is not null) list.Add(row);
            }
            catch (Exception ex) when (ex is IOException or JsonException) { }
        }
        return list;
    }

    public static List<UsageRecord> ReadAnythingLlm(IEnumerable<string> dbPaths)
    {
        var list = new List<UsageRecord>();
        foreach (var path in dbPaths)
            WithSqlite(path, "agenthub-anythingllm-", conn => ReadAnythingLlmDb(conn, list));
        return list;
    }

    public static List<UsageRecord> ReadClaudeScience(IEnumerable<string> dbPaths)
    {
        var list = new List<UsageRecord>();
        foreach (var path in dbPaths)
            WithSqlite(path, "agenthub-claude-science-", conn => ReadClaudeScienceDb(conn, list));
        return list;
    }

    public static List<UsageRecord> ReadLmStudio(IEnumerable<string> homes)
    {
        var byKey = new Dictionary<string, UsageRecord>(StringComparer.Ordinal);
        foreach (var file in LmStudioLogs(homes))
        {
            try { ScanLmStudio(file, byKey); }
            catch (Exception ex) when (ex is IOException or JsonException) { }
        }
        return byKey.Values.ToList();
    }

    public static List<UsageRecord> ReadUnsloth(IEnumerable<string> dbPaths)
    {
        var list = new List<UsageRecord>();
        foreach (var path in dbPaths)
            WithSqlite(path, "agenthub-unsloth-", conn => ReadUnslothDb(conn, list));
        return list;
    }

    private static void ReadGooseDb(SqliteConnection conn, List<UsageRecord> list)
    {
        if (!UsageIo.TableExists(conn, "sessions")) return;
        var columns = SqliteColumns(conn, "sessions");
        if (!columns.Contains("id") || !columns.Contains("model_config_json")) return;
        string Opt(string col) => columns.Contains(col) ? col : "NULL AS " + col;
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT id, model_config_json, {Opt("created_at")},
                   {Opt("total_tokens")}, {Opt("input_tokens")}, {Opt("output_tokens")},
                   {Opt("accumulated_total_tokens")}, {Opt("accumulated_input_tokens")},
                   {Opt("accumulated_output_tokens")}
            FROM sessions
            WHERE model_config_json IS NOT NULL AND TRIM(model_config_json) != ''
            """;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var id = ReadText(reader, "id");
            var model = GooseModel(ReadText(reader, "model_config_json"));
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(model)) continue;
            var total = GooseCount(reader, columns, "accumulated_total_tokens", "total_tokens");
            var input = GooseCount(reader, columns, "accumulated_input_tokens", "input_tokens");
            var output = GooseCount(reader, columns, "accumulated_output_tokens", "output_tokens");
            if (total == 0 && input == 0 && output == 0) continue;
            var ts = FlexibleTime(ReadText(reader, "created_at"), naiveAsUtc: true) ?? DateTime.UtcNow;
            var reasoning = Math.Max(0, total - input - output);
            list.Add(Row("goose", id, "snapshot", ts, input, output, 0, 0, reasoning, model, null));
        }
    }

    private static long GooseCount(SqliteDataReader reader, HashSet<string> columns, string accumulated, string single)
    {
        if (columns.Contains(accumulated) && PresentCount(reader, accumulated, out var acc)) return acc;
        if (columns.Contains(single) && PresentCount(reader, single, out var one)) return one;
        return 0;
    }

    /// <summary>NULL 不当成 0。TryCount 对空单元格也返回 true。</summary>
    private static bool PresentCount(SqliteDataReader reader, string column, out long value)
    {
        value = 0;
        if (reader.IsDBNull(reader.GetOrdinal(column))) return false;
        return TryCount(reader, column, false, out value);
    }

    private static string? GooseModel(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var name = UsageParsers.GetStr(doc.RootElement, "model_name");
            return string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        }
        catch (JsonException) { return null; }
    }

    private static List<string> DroidCanonicalFiles(IEnumerable<string> sessionDirs)
    {
        var groups = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var root in sessionDirs)
        {
            foreach (var file in UsageIo.EnumerateFiles(root, 8, static (path, _) =>
                         path.EndsWith(".settings.json", StringComparison.OrdinalIgnoreCase)))
            {
                var id = DroidSessionId(file);
                if (string.IsNullOrEmpty(id)) continue;
                if (!groups.TryGetValue(id, out var list)) groups[id] = list = [];
                list.Add(file);
            }
        }
        var chosen = new List<string>();
        foreach (var group in groups.Values)
        {
            if (group.Count == 1)
            {
                chosen.Add(group[0]);
                continue;
            }
            string? best = null;
            long bestMetric = -1;
            var bestMtime = DateTime.MinValue;
            foreach (var file in group)
            {
                if (!TryDroidUsage(file, out var usage)) continue;
                DateTime mtime;
                try { mtime = File.GetLastWriteTimeUtc(file); }
                catch (IOException) { continue; }
                var metric = usage.Sum;
                var better = metric > bestMetric
                    || (metric == bestMetric && mtime > bestMtime)
                    || (metric == bestMetric && mtime == bestMtime && (best is null || string.CompareOrdinal(file, best) < 0));
                if (!better) continue;
                best = file;
                bestMetric = metric;
                bestMtime = mtime;
            }
            chosen.Add(best ?? group[0]);
        }
        chosen.Sort(StringComparer.Ordinal);
        return chosen;
    }

    private static UsageRecord? ReadDroidFile(string file)
    {
        if (!TryDroidUsage(file, out var usage) || usage.Sum == 0) return null;
        var sessionId = DroidSessionId(file);
        if (string.IsNullOrEmpty(sessionId)) return null;
        DateTime ts;
        try { ts = File.GetLastWriteTimeUtc(file); }
        catch (IOException) { return null; }
        using var doc = JsonDocument.Parse(File.ReadAllText(file));
        return Row("droid", sessionId, "snapshot", ts, usage.Input, usage.Output, usage.CacheRead, usage.CacheWrite,
            usage.Thinking, DroidModel(doc.RootElement, file), DroidCwd(file));
    }

    private readonly record struct DroidUsage(long Input, long Output, long CacheRead, long CacheWrite, long Thinking)
    {
        public long Sum => Input + Output + CacheRead + CacheWrite + Thinking;
    }

    private static bool TryDroidUsage(string file, out DroidUsage usage)
    {
        usage = default;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            var tokenUsage = UsageParsers.GetObj(doc.RootElement, "tokenUsage");
            if (tokenUsage is null) return false;
            var input = UsageIo.JsonLong(tokenUsage.Value, "inputTokens");
            var output = UsageIo.JsonLong(tokenUsage.Value, "outputTokens");
            var cacheWrite = UsageIo.JsonLong(tokenUsage.Value, "cacheCreationTokens");
            var cacheRead = UsageIo.JsonLong(tokenUsage.Value, "cacheReadTokens");
            var thinking = UsageIo.JsonLong(tokenUsage.Value, "thinkingTokens");
            var total = UsageIo.JsonLong(tokenUsage.Value, "totalTokens");
            var known = input + output + cacheWrite + cacheRead + thinking;
            var missing = total > known ? total - known : 0;
            if (missing > 0)
            {
                if (output == 0) output += missing;
                else thinking += missing;
            }
            usage = new DroidUsage(input, output, cacheRead, cacheWrite, thinking);
            return true;
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return false;
        }
    }

    private static string DroidSessionId(string file)
    {
        var name = Path.GetFileName(file);
        const string suffix = ".settings.json";
        return name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) ? name[..^suffix.Length] : "";
    }

    private static string DroidModel(JsonElement settings, string file)
    {
        var model = NormalizeDroidModel(UsageParsers.GetStr(settings, "model"));
        if (model.Length > 0) return model;
        model = DroidSidecarModel(file) ?? "";
        if (model.Length > 0) return model;
        var provider = NormalizeDroidProvider(UsageParsers.GetStr(settings, "providerLock"));
        if (provider == "unknown") provider = InferDroidProvider(UsageParsers.GetStr(settings, "model"));
        return provider switch
        {
            "anthropic" => "claude-unknown",
            "openai" => "gpt-unknown",
            "google" => "gemini-unknown",
            "xai" => "grok-unknown",
            _ => "unknown",
        };
    }

    private static string NormalizeDroidModel(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var s = raw.StartsWith("custom:", StringComparison.Ordinal) ? raw["custom:".Length..] : raw;
        s = Regex.Replace(s, @"\[[^\]]*\]", "");
        s = s.ToLowerInvariant();
        s = Regex.Replace(s, @"[\s.]+", "-");
        s = Regex.Replace(s, "-+", "-").Trim('-');
        return s;
    }

    private static string NormalizeDroidProvider(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "unknown";
        var v = raw.Trim().ToLowerInvariant().Replace("-", "_", StringComparison.Ordinal);
        if (v is "claude" or "anthropic") return "anthropic";
        if (v == "openai") return "openai";
        if (v is "google" or "google_ai" or "gemini" or "vertex" or "vertex_ai") return "google";
        if (v is "xai" or "x_ai" or "grok") return "xai";
        return v;
    }

    private static string InferDroidProvider(string? model)
    {
        if (string.IsNullOrWhiteSpace(model)) return "unknown";
        var m = model.ToLowerInvariant();
        if (m.Contains("claude", StringComparison.Ordinal) || m.Contains("opus", StringComparison.Ordinal)
            || m.Contains("sonnet", StringComparison.Ordinal) || m.Contains("haiku", StringComparison.Ordinal))
            return "anthropic";
        if (m.StartsWith("gpt-", StringComparison.Ordinal) || m.Contains("-gpt-", StringComparison.Ordinal)
            || m.Contains("chatgpt", StringComparison.Ordinal) || Regex.IsMatch(m, @"^o\d"))
            return "openai";
        if (m.Contains("gemini", StringComparison.Ordinal)) return "google";
        if (m.Contains("grok", StringComparison.Ordinal)) return "xai";
        return "unknown";
    }

    private static string? DroidSidecarModel(string settingsPath)
    {
        var sidecar = DroidSidecar(settingsPath);
        if (sidecar is null || !File.Exists(sidecar)) return null;
        try
        {
            using var reader = new StreamReader(new FileStream(sidecar, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
            for (var i = 0; i < 500; i++)
            {
                var line = reader.ReadLine();
                if (line is null) break;
                var idx = line.IndexOf("Model:", StringComparison.Ordinal);
                if (idx < 0) continue;
                var tail = line[(idx + "Model:".Length)..];
                var cut = tail.Length;
                foreach (var ch in new[] { '"', '\\', '[' })
                {
                    var p = tail.IndexOf(ch);
                    if (p >= 0 && p < cut) cut = p;
                }
                var normalized = NormalizeDroidModel(tail[..cut]);
                if (normalized.Length > 0) return normalized;
            }
        }
        catch (IOException) { }
        return null;
    }

    private static string? DroidCwd(string settingsPath)
    {
        var sidecar = DroidSidecar(settingsPath);
        if (sidecar is null || !File.Exists(sidecar)) return null;
        try
        {
            using var stream = new FileStream(sidecar, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var limit = (int)Math.Min(DroidCwdBytes, stream.Length);
            if (limit <= 0) return null;
            var buf = new byte[limit];
            var n = stream.Read(buf, 0, limit);
            var text = Encoding.UTF8.GetString(buf, 0, n);
            foreach (var raw in text.Split('\n'))
            {
                if (!raw.Contains("\"cwd\"", StringComparison.Ordinal)) continue;
                JsonDocument doc;
                try { doc = JsonDocument.Parse(raw.Trim()); }
                catch (JsonException) { continue; }
                using (doc)
                {
                    var type = UsageParsers.GetStr(doc.RootElement, "type");
                    if (type is not ("session_start" or "session")) continue;
                    var cwd = UsageParsers.GetStr(doc.RootElement, "cwd");
                    if (!string.IsNullOrWhiteSpace(cwd)) return cwd.Trim();
                }
            }
        }
        catch (IOException) { }
        return null;
    }

    private static string? DroidSidecar(string settingsPath)
    {
        const string suffix = ".settings.json";
        if (!settingsPath.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return null;
        return settingsPath[..^suffix.Length] + ".jsonl";
    }

    private static void ReadAnythingLlmDb(SqliteConnection conn, List<UsageRecord> list)
    {
        if (!UsageIo.TableExists(conn, "workspace_chats")) return;
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, createdAt,
                   CASE WHEN json_valid(response) THEN json_extract(response, '$.metrics.prompt_tokens') ELSE NULL END AS prompt_tokens,
                   CASE WHEN json_valid(response) THEN json_extract(response, '$.metrics.completion_tokens') ELSE NULL END AS completion_tokens,
                   CASE WHEN json_valid(response) THEN json_extract(response, '$.metrics.total_tokens') ELSE NULL END AS total_tokens,
                   CASE WHEN json_valid(response) THEN json_extract(response, '$.metrics.model') ELSE NULL END AS model
            FROM workspace_chats
            ORDER BY id
            """;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var id = ReadText(reader, "id");
            if (string.IsNullOrEmpty(id)) continue;
            if (!TryCount(reader, "prompt_tokens", false, out var input)) input = 0;
            if (!TryCount(reader, "completion_tokens", false, out var output)) output = 0;
            if (input == 0 && output == 0) continue;
            if (!TryCount(reader, "total_tokens", false, out var total)) total = 0;
            var ts = FlexibleTime(ReadText(reader, "createdAt"), naiveAsUtc: true);
            if (ts is null) continue;
            list.Add(Row("anythingllm", id, id, ts.Value, input, output, 0, 0, Math.Max(0, total - input - output),
                ReadText(reader, "model") ?? "anythingllm-unknown", null));
        }
    }

    private static void ReadClaudeScienceDb(SqliteConnection conn, List<UsageRecord> list)
    {
        if (!UsageIo.TableExists(conn, "frames")) return;
        var columns = SqliteColumns(conn, "frames");
        if (!columns.Contains("id")) return;
        var present = ClaudeScienceTokenColumns.Where(columns.Contains).ToList();
        if (present.Count == 0) return;
        string Opt(string col) => columns.Contains(col) ? col : "NULL AS " + col;
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT id, {Opt("parent_frame_id")}, {Opt("model")},
                   {string.Join(", ", ClaudeScienceTokenColumns.Select(Opt))},
                   {Opt("created_at")}, {Opt("updated_at")}, {Opt("completed_at")}
            FROM frames
            WHERE {string.Join(" OR ", present.Select(col => $"COALESCE({col}, 0) <> 0"))}
            """;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var id = ReadText(reader, "id");
            if (string.IsNullOrEmpty(id)) continue;
            long Count(string col) => TryCount(reader, col, false, out var n) ? n : 0;
            var input = Math.Max(0, Count("input_tokens") - Count("cache_read_tokens") - Count("cache_write_tokens"))
                + Math.Max(0, Count("aux_input_tokens") - Count("aux_cache_read_tokens") - Count("aux_cache_write_tokens"));
            var output = Count("output_tokens") + Count("aux_output_tokens");
            var cacheRead = Count("cache_read_tokens") + Count("aux_cache_read_tokens");
            var cacheWrite = Count("cache_write_tokens") + Count("aux_cache_write_tokens");
            if (input + output + cacheRead + cacheWrite == 0) continue;
            var ts = FlexibleTime(ReadText(reader, "completed_at"), naiveAsUtc: true)
                ?? FlexibleTime(ReadText(reader, "updated_at"), naiveAsUtc: true)
                ?? FlexibleTime(ReadText(reader, "created_at"), naiveAsUtc: true);
            if (ts is null) continue;
            var row = Row("claude-science", id, id, ts.Value, input, output, cacheRead, cacheWrite, 0,
                ReadText(reader, "model") ?? "claude-science", null);
            if (!string.IsNullOrEmpty(ReadText(reader, "parent_frame_id"))) row = row with { IsSubagent = true };
            list.Add(row);
        }
    }

    private static List<string> LmStudioLogs(IEnumerable<string> homes)
    {
        var files = new List<string>();
        foreach (var home in homes)
        {
            if (string.IsNullOrWhiteSpace(home)) continue;
            files.AddRange(UsageIo.EnumerateFiles(Path.Combine(home, "server-logs"), 8, static (path, _) =>
                path.EndsWith(".log", StringComparison.OrdinalIgnoreCase)));
        }
        files.Sort(StringComparer.Ordinal);
        return files;
    }

    private static void ScanLmStudio(string file, Dictionary<string, UsageRecord> byKey)
    {
        var info = new FileInfo(file);
        if (!info.Exists || info.Length == 0 || info.Length > LmStudioMaxBytes) return;
        string text;
        using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var reader = new StreamReader(stream, Encoding.UTF8))
            text = reader.ReadToEnd();
        const string marker = "\"usage\"";
        var search = 0;
        var metaFrom = 0;
        var fallbackTs = info.LastWriteTimeUtc;
        while (search < text.Length)
        {
            var at = text.IndexOf(marker, search, StringComparison.Ordinal);
            if (at < 0) break;
            search = at + marker.Length;
            if (at > 0 && text[at - 1] == '\\') continue;
            var i = SkipSpace(text, at + marker.Length);
            if (i >= text.Length || text[i] != ':') continue;
            i = SkipSpace(text, i + 1);
            if (i >= text.Length || text[i] != '{') continue;
            var end = BalancedEnd(text, i);
            if (end < 0) continue;
            search = end;
            var meta = text[Math.Max(metaFrom, 0)..at];
            metaFrom = end;
            JsonDocument doc;
            try { doc = JsonDocument.Parse(text[i..end]); }
            catch (JsonException) { continue; }
            using (doc)
            {
                if (!TryLocalStudioTokens(doc.RootElement, out var input, out var output, out var cacheRead, out var cacheWrite, out var reasoning))
                    continue;
                var responseId = LmStudioResponseId(LastJsonString(meta, "id"));
                var model = LastJsonString(meta, "model");
                if (string.IsNullOrWhiteSpace(model)) model = "unknown";
                else model = model.Trim();
                var ts = LmStudioTimestamp(meta) ?? fallbackTs;
                var key = responseId ?? LmStudioFallbackKey(file, at, model, input, output, cacheRead, cacheWrite, reasoning);
                if (byKey.ContainsKey(key)) continue;
                var sessionId = responseId ?? Path.GetFileName(file);
                byKey[key] = Row("lm-studio", sessionId, key, ts, input, output, cacheRead, cacheWrite, reasoning, model, null);
            }
        }
    }

    private static string? LmStudioResponseId(string? id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        return id.StartsWith("chatcmpl-", StringComparison.Ordinal)
            || id.StartsWith("cmpl-", StringComparison.Ordinal)
            || id.StartsWith("resp_", StringComparison.Ordinal)
            ? id : null;
    }

    private static string LmStudioFallbackKey(string file, int marker, string model, long input, long output, long cacheRead, long cacheWrite, long reasoning)
    {
        var raw = string.Join('\0', file, marker.ToString(CultureInfo.InvariantCulture), model,
            input.ToString(CultureInfo.InvariantCulture), output.ToString(CultureInfo.InvariantCulture),
            cacheRead.ToString(CultureInfo.InvariantCulture), cacheWrite.ToString(CultureInfo.InvariantCulture),
            reasoning.ToString(CultureInfo.InvariantCulture));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant();
    }

    /// <summary>
    /// prompt 含 cache。入库五列互斥：input 去掉 cache，reasoning 从 completion 拆出。
    /// </summary>
    private static bool TryLocalStudioTokens(JsonElement usage, out long input, out long output, out long cacheRead, out long cacheWrite, out long reasoning)
    {
        input = output = cacheRead = cacheWrite = reasoning = 0;
        var prompt = FirstCount(usage, "prompt_tokens", "promptTokens", "input_tokens", "inputTokens");
        var completion = FirstCount(usage, "completion_tokens", "completionTokens", "output_tokens", "outputTokens");
        var reported = FirstCount(usage, "total_tokens", "totalTokens");
        var total = Math.Max(reported, prompt + completion);
        if (total <= 0) return false;
        var promptDetails = FirstObj(usage, "prompt_tokens_details", "input_tokens_details", "inputTokensDetails");
        var outputDetails = FirstObj(usage, "completion_tokens_details", "output_tokens_details", "completionTokensDetails", "outputTokensDetails");
        cacheRead = Math.Min(prompt, Math.Max(
            promptDetails is null ? 0 : FirstCount(promptDetails.Value, "cached_tokens", "cache_read_tokens"),
            UsageIo.JsonLong(usage, "cached_tokens")));
        cacheWrite = Math.Min(Math.Max(0, prompt - cacheRead), Math.Max(
            promptDetails is null ? 0 : FirstCount(promptDetails.Value, "cache_creation_input_tokens", "cache_write_tokens"),
            UsageIo.JsonLong(usage, "cache_creation_input_tokens")));
        reasoning = Math.Min(completion, Math.Max(
            outputDetails is null ? 0 : UsageIo.JsonLong(outputDetails.Value, "reasoning_tokens"),
            UsageIo.JsonLong(usage, "reasoning_tokens")));
        input = Math.Max(0, total - completion - cacheRead - cacheWrite);
        output = Math.Max(0, completion - reasoning);
        return true;
    }

    private static long FirstCount(JsonElement el, params string[] names)
    {
        foreach (var name in names)
            if (UsageIo.HasJsonField(el, name)) return UsageIo.JsonLong(el, name);
        return 0;
    }

    private static JsonElement? FirstObj(JsonElement el, params string[] names)
    {
        foreach (var name in names)
        {
            var obj = UsageParsers.GetObj(el, name);
            if (obj is not null) return obj;
        }
        return null;
    }

    private static DateTime? LmStudioTimestamp(string text)
    {
        DateTime? found = null;
        foreach (Match match in Regex.Matches(text, @"\[(\d{4})-(\d{2})-(\d{2}) (\d{2}):(\d{2}):(\d{2})\]\["))
        {
            if (!int.TryParse(match.Groups[1].Value, out var year)
                || !int.TryParse(match.Groups[2].Value, out var month)
                || !int.TryParse(match.Groups[3].Value, out var day)
                || !int.TryParse(match.Groups[4].Value, out var hour)
                || !int.TryParse(match.Groups[5].Value, out var minute)
                || !int.TryParse(match.Groups[6].Value, out var second))
                continue;
            try
            {
                found = new DateTime(year, month, day, hour, minute, second, DateTimeKind.Local).ToUniversalTime();
            }
            catch (ArgumentOutOfRangeException) { }
        }
        return found;
    }

    private static string? LastJsonString(string text, string field)
    {
        var marker = "\"" + field + "\"";
        var cursor = 0;
        string? found = null;
        while (cursor < text.Length)
        {
            var index = text.IndexOf(marker, cursor, StringComparison.Ordinal);
            if (index < 0) break;
            cursor = index + marker.Length;
            if (index > 0 && text[index - 1] == '\\') continue;
            var valueStart = SkipSpace(text, cursor);
            if (valueStart >= text.Length || text[valueStart] != ':') continue;
            valueStart = SkipSpace(text, valueStart + 1);
            if (!TryReadJsonString(text, valueStart, out var value, out var next)) continue;
            found = value;
            cursor = next;
        }
        return found;
    }

    private static bool TryReadJsonString(string text, int start, out string value, out int next)
    {
        value = "";
        next = start;
        if (start >= text.Length || text[start] != '"') return false;
        var sb = new StringBuilder();
        var escaped = false;
        for (var i = start + 1; i < text.Length; i++)
        {
            var ch = text[i];
            if (escaped)
            {
                sb.Append(ch);
                escaped = false;
                continue;
            }
            if (ch == '\\') { escaped = true; continue; }
            if (ch == '"')
            {
                value = sb.ToString();
                next = i + 1;
                return true;
            }
            sb.Append(ch);
        }
        return false;
    }

    private static int SkipSpace(string text, int index)
    {
        while (index < text.Length && char.IsWhiteSpace(text[index])) index++;
        return index;
    }

    private static int BalancedEnd(string text, int start)
    {
        if (start >= text.Length || text[start] != '{') return -1;
        var depth = 0;
        var inString = false;
        var escaped = false;
        for (var i = start; i < text.Length; i++)
        {
            var ch = text[i];
            if (inString)
            {
                if (escaped) escaped = false;
                else if (ch == '\\') escaped = true;
                else if (ch == '"') inString = false;
                continue;
            }
            if (ch == '"') inString = true;
            else if (ch == '{') depth++;
            else if (ch == '}')
            {
                depth--;
                if (depth == 0) return i + 1;
            }
        }
        return -1;
    }

    private static void ReadUnslothDb(SqliteConnection conn, List<UsageRecord> list)
    {
        var tables = SqliteTableSet(conn, "chat_messages", "chat_threads", "api_usage_events");
        if (tables.Contains("chat_messages"))
        {
            var join = tables.Contains("chat_threads") ? "LEFT JOIN chat_threads t ON t.id = m.thread_id" : "";
            var fallback = tables.Contains("chat_threads") ? "t.model_id" : "NULL";
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"""
                SELECT m.id AS id, m.created_at AS created_at,
                       {UnslothField("$.responseDetails.responseModelId", "response_model")},
                       {UnslothField("$.contextUsage.modelId", "requested_model")},
                       {UnslothField("$.responseDetails.providerType", "provider_type")},
                       {fallback} AS fallback_model,
                       {UnslothField("$.contextUsage.promptTokens", "prompt_tokens")},
                       {UnslothField("$.contextUsage.completionTokens", "completion_tokens")},
                       {UnslothField("$.contextUsage.totalTokens", "total_tokens")},
                       {UnslothField("$.contextUsage.cachedTokens", "cached_tokens")},
                       {UnslothField("$.contextUsage.cacheWriteTokens", "cache_write_tokens")},
                       {UnslothField("$.contextUsage.reasoningTokens", "reasoning_tokens")}
                FROM chat_messages m {join}
                WHERE m.role = 'assistant'
                """;
            ReadUnslothRows(cmd, "chat", list);
        }
        if (!tables.Contains("api_usage_events")) return;
        var apiColumns = SqliteColumns(conn, "api_usage_events");
        foreach (var name in new[] { "id", "model", "prompt_tokens", "completion_tokens", "total_tokens", "created_at" })
            if (!apiColumns.Contains(name)) return;
        using var api = conn.CreateCommand();
        api.CommandText = """
            SELECT e.id AS id, e.created_at AS created_at,
                   e.model AS response_model, e.model AS requested_model,
                   'local' AS provider_type, NULL AS fallback_model,
                   e.prompt_tokens AS prompt_tokens, e.completion_tokens AS completion_tokens,
                   e.total_tokens AS total_tokens,
                   0 AS cached_tokens, 0 AS cache_write_tokens, 0 AS reasoning_tokens
            FROM api_usage_events e
            """;
        ReadUnslothRows(api, "api", list);
    }

    private static string UnslothField(string path, string alias) =>
        $"CASE WHEN json_valid(m.metadata_json) THEN json_extract(m.metadata_json, '{path}') ELSE NULL END AS {alias}";

    private static void ReadUnslothRows(SqliteCommand cmd, string kind, List<UsageRecord> list)
    {
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var id = ReadText(reader, "id");
            if (string.IsNullOrEmpty(id)) continue;
            using var usage = JsonDocument.Parse(JsonSerializer.Serialize(new
            {
                prompt_tokens = CountOrZero(reader, "prompt_tokens"),
                completion_tokens = CountOrZero(reader, "completion_tokens"),
                total_tokens = CountOrZero(reader, "total_tokens"),
                cached_tokens = CountOrZero(reader, "cached_tokens"),
                cache_creation_input_tokens = CountOrZero(reader, "cache_write_tokens"),
                reasoning_tokens = CountOrZero(reader, "reasoning_tokens"),
            }));
            if (!TryLocalStudioTokens(usage.RootElement, out var input, out var output, out var cacheRead, out var cacheWrite, out var reasoning))
                continue;
            var ts = FlexibleTime(ReadText(reader, "created_at"), naiveAsUtc: true);
            if (ts is null) continue;
            var model = UnslothModel(kind, ReadText(reader, "response_model"), ReadText(reader, "requested_model"),
                ReadText(reader, "fallback_model"), ReadText(reader, "provider_type"));
            list.Add(Row("unsloth-studio", kind + ":" + id, kind + ":" + id, ts.Value,
                input, output, cacheRead, cacheWrite, reasoning, model, null));
        }
    }

    private static string UnslothModel(string kind, string? response, string? requested, string? fallback, string? provider)
    {
        var raw = FirstText(response, requested, fallback) ?? "unknown";
        var providerType = (provider ?? "").Trim().ToLowerInvariant();
        if (kind == "api" || providerType == "local") return "local/" + raw;
        if (!UnslothMeteredProviders.Contains(providerType))
            return "unpriced/" + (providerType.Length == 0 ? "unknown" : providerType) + "/" + raw;
        return raw.StartsWith(providerType + "/", StringComparison.OrdinalIgnoreCase) ? raw : providerType + "/" + raw;
    }

    private static HashSet<string> SqliteTableSet(SqliteConnection conn, params string[] names)
    {
        var wanted = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table'";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var name = reader.IsDBNull(0) ? null : reader.GetString(0);
            if (name is not null && wanted.Contains(name)) found.Add(name);
        }
        return found;
    }

    private static DateTime? FlexibleTime(string? raw, bool naiveAsUtc)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var text = raw.Trim();
        if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var epoch) && epoch > 0)
        {
            if (epoch < 100_000_000_000L) epoch *= 1000;
            try { return DateTimeOffset.FromUnixTimeMilliseconds(epoch).UtcDateTime; }
            catch (ArgumentOutOfRangeException) { return null; }
        }
        if (naiveAsUtc)
        {
            var match = Regex.Match(text, @"^(\d{4})-(\d{2})-(\d{2})[T ](\d{2}):(\d{2}):(\d{2})(?:\.(\d{1,3}))?$");
            if (match.Success)
            {
                var millis = match.Groups[7].Success ? int.Parse(match.Groups[7].Value.PadRight(3, '0'), CultureInfo.InvariantCulture) : 0;
                try
                {
                    return new DateTime(
                        int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture),
                        int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture),
                        int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture),
                        int.Parse(match.Groups[4].Value, CultureInfo.InvariantCulture),
                        int.Parse(match.Groups[5].Value, CultureInfo.InvariantCulture),
                        int.Parse(match.Groups[6].Value, CultureInfo.InvariantCulture),
                        millis, DateTimeKind.Utc);
                }
                catch (ArgumentOutOfRangeException) { return null; }
            }
        }
        return UsageParsers.ParseIso(text);
    }
}
