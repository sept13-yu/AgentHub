using System.Globalization;
using System.Text;
using System.Text.Json;

namespace AgentHub.Core.TokenCore;

/// <summary>
/// Cline CLI v3 / 桌面端，以及 Command Code。
/// 全量扫描后按请求键覆盖，不搬 TokenTracker 的增量账本。
/// 只投影 token、模型、时间和项目，不保留提示词或正文。
/// </summary>
internal static partial class PassiveUsage
{
    public static List<UsageRecord> ReadCline(IEnumerable<string> sessionDirs)
    {
        var files = new List<string>();
        foreach (var root in sessionDirs)
        {
            files.AddRange(UsageIo.EnumerateFiles(root, 2, static (path, depth) =>
                depth == 2 && path.EndsWith(".messages.json", StringComparison.OrdinalIgnoreCase)));
        }
        files.Sort(StringComparer.Ordinal);
        var list = new List<UsageRecord>();
        foreach (var file in files)
        {
            try { ReadClineFile(file, list); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        }
        return list;
    }

    public static List<UsageRecord> ReadCommandCode(IEnumerable<string> homes)
    {
        var files = new List<string>();
        foreach (var home in homes)
        {
            if (string.IsNullOrWhiteSpace(home)) continue;
            files.AddRange(UsageIo.EnumerateFiles(Path.Combine(home, "projects"), 2, static (path, depth) =>
                depth == 2 && IsCommandCodeLog(path)));
        }
        files.Sort(StringComparer.Ordinal);
        // id|timestamp 跨文件只留一条，后出现的副本覆盖前面的，避免 fork/clone 重复计入。
        var byKey = new Dictionary<string, UsageRecord>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            try { ReadCommandCodeFile(file, byKey); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        }
        return byKey.Values.ToList();
    }

    private static void ReadClineFile(string file, List<UsageRecord> list)
    {
        var sessionDir = Path.GetDirectoryName(file);
        var sessionId = string.IsNullOrEmpty(sessionDir) ? "" : Path.GetFileName(sessionDir);
        if (string.IsNullOrEmpty(sessionId)) sessionId = "cline";
        var meta = ReadClineMeta(sessionDir, sessionId);
        var stem = ClineTranscriptStem(file);
        var sub = !string.Equals(stem, sessionId, StringComparison.OrdinalIgnoreCase);

        using var doc = JsonDocument.Parse(File.ReadAllText(file));
        if (!TryClineMessages(doc.RootElement, out var messages)) return;
        var local = new Dictionary<string, UsageRecord>(StringComparer.Ordinal);
        var index = 0;
        foreach (var msg in messages.EnumerateArray())
        {
            var i = index++;
            if (msg.ValueKind != JsonValueKind.Object) continue;
            if (!string.Equals(UsageParsers.GetStr(msg, "role"), "assistant", StringComparison.Ordinal)) continue;
            var metrics = UsageParsers.GetObj(msg, "metrics");
            if (metrics is null) continue;
            if (!UsageIo.TryJsonLong(msg, "ts", out var tsMs) || tsMs <= 0) continue;
            if (meta.ImportedAtMs is long imported && tsMs <= imported) continue;

            var cacheRead = UsageIo.JsonLong(metrics.Value, "cacheReadTokens");
            var cacheWrite = UsageIo.JsonLong(metrics.Value, "cacheWriteTokens");
            var rawIn = UsageIo.JsonLong(metrics.Value, "inputTokens");
            var rawOut = UsageIo.JsonLong(metrics.Value, "outputTokens");
            // outputTokens 已含 reasoning。拆出后两列相加仍等于原来的 output。
            var reasoning = Math.Min(UsageIo.JsonLong(metrics.Value, "reasoningTokenCount"), rawOut);
            UsageParsers.SplitInclusiveTokens(rawIn, rawOut, cacheRead, cacheWrite, reasoning,
                out var input, out var output);
            // 两个 cache 桶都在 inputTokens 里。超出含税总量时未缓存部分记 0，不再把含税值原样留下。
            input = Math.Max(0, rawIn - cacheRead - cacheWrite);
            var model = ClineModelName(msg, meta.Model);
            var cost = ClineCost(metrics.Value, model);
            if (input == 0 && output == 0 && cacheRead == 0 && cacheWrite == 0 && reasoning == 0 && cost is not > 0)
                continue;

            var ts = UsageParsers.ParseMs(tsMs);
            if (ts is null) continue;
            var id = UsageParsers.GetStr(msg, "id");
            var key = string.IsNullOrEmpty(id)
                ? string.Create(CultureInfo.InvariantCulture, $"{stem}:ts:{tsMs}:{i}")
                : stem + ":" + id.Trim();
            local[key] = Row("cline", sessionId, key, ts.Value, input, output, cacheRead, cacheWrite, reasoning, model, null)
                with { ReportedCostUsd = cost, IsSubagent = sub };
        }
        list.AddRange(local.Values);
    }

    private static bool TryClineMessages(JsonElement root, out JsonElement messages)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            messages = root;
            return true;
        }
        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("messages", out messages)
            && messages.ValueKind == JsonValueKind.Array)
            return true;
        messages = default;
        return false;
    }

    private readonly record struct ClineMeta(string? Model, long? ImportedAtMs);

    private static ClineMeta ReadClineMeta(string? sessionDir, string sessionId)
    {
        if (string.IsNullOrEmpty(sessionDir)) return default;
        var path = Path.Combine(sessionDir, sessionId + ".json");
        if (!File.Exists(path)) return default;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            var model = UsageParsers.GetStr(root, "model");
            long? imported = null;
            var from = UsageParsers.GetObj(UsageParsers.GetObj(root, "metadata"), "importedFrom");
            var raw = UsageParsers.GetStr(from, "importedAt");
            if (UsageParsers.ParseIso(raw) is { } ts)
                imported = new DateTimeOffset(DateTime.SpecifyKind(ts, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
            return new ClineMeta(string.IsNullOrWhiteSpace(model) ? null : model.Trim(), imported);
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return default;
        }
    }

    private static string ClineTranscriptStem(string file)
    {
        var name = Path.GetFileName(file);
        const string suffix = ".messages.json";
        if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            return name[..^suffix.Length];
        return Path.GetFileNameWithoutExtension(name);
    }

    private static string ClineModelName(JsonElement msg, string? fallback)
    {
        var info = UsageParsers.GetObj(msg, "modelInfo");
        var id = UsageParsers.GetStr(info, "id");
        if (!string.IsNullOrWhiteSpace(id)) return id.Trim();
        if (!string.IsNullOrWhiteSpace(fallback)) return fallback.Trim();
        var provider = UsageParsers.GetStr(info, "provider");
        if (!string.IsNullOrWhiteSpace(provider))
        {
            var slug = ProviderSlug(provider);
            if (slug.Length > 0) return "provider:" + slug;
        }
        return "unknown";
    }

    private static string ProviderSlug(string provider)
    {
        var raw = provider.Trim().ToLowerInvariant();
        var sb = new StringBuilder(raw.Length);
        foreach (var ch in raw)
        {
            if (ch is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '.' or '_' or '-')
                sb.Append(ch);
        }
        return sb.ToString();
    }

    /// <summary>正数上报成本优先。cline-free/*、cline-pass/* 和 :free 没有正数成本时记 $0，避免价表按裸模型名计费。</summary>
    private static double? ClineCost(JsonElement metrics, string model)
    {
        double? reported = null;
        if (metrics.ValueKind == JsonValueKind.Object
            && metrics.TryGetProperty("cost", out var cost)
            && cost.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
        {
            double n = 0;
            var ok = cost.ValueKind == JsonValueKind.Number && cost.TryGetDouble(out n) && double.IsFinite(n);
            if (!ok && cost.ValueKind == JsonValueKind.String)
                ok = double.TryParse(cost.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out n)
                     && double.IsFinite(n);
            if (ok && n >= 0) reported = n;
        }
        if (reported is > 0) return reported;
        if (ClineFree(model)) return 0;
        return reported;
    }

    private static bool ClineFree(string model) =>
        model.Contains("cline-free/", StringComparison.OrdinalIgnoreCase)
        || model.Contains("cline-pass/", StringComparison.OrdinalIgnoreCase)
        || model.EndsWith(":free", StringComparison.OrdinalIgnoreCase);

    private static bool IsCommandCodeLog(string path)
    {
        var name = Path.GetFileName(path);
        if (!name.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)) return false;
        if (name.Contains(".prompts.", StringComparison.OrdinalIgnoreCase)) return false;
        return !name.EndsWith(".checkpoints.jsonl", StringComparison.OrdinalIgnoreCase);
    }

    private static void ReadCommandCodeFile(string file, Dictionary<string, UsageRecord> byKey)
    {
        string? sessionId = null;
        string? cwd = null;
        var local = new Dictionary<string, UsageRecord>(StringComparer.Ordinal);
        foreach (var line in UsageParsers.ReadLinesShared(file))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            JsonDocument doc;
            try { doc = JsonDocument.Parse(line); }
            catch (JsonException) { continue; }
            using (doc)
            {
                var root = doc.RootElement;
                var type = UsageParsers.GetStr(root, "type");
                if (type == "session")
                {
                    sessionId ??= UsageParsers.GetStr(root, "id");
                    cwd ??= UsageParsers.GetStr(root, "cwd");
                    continue;
                }
                if (type != "message") continue;
                var id = UsageParsers.GetStr(root, "id");
                var timestamp = UsageParsers.GetStr(root, "timestamp");
                if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(timestamp)) continue;
                var usage = UsageParsers.GetObj(root, "usage");
                if (usage is null) continue;
                if (!TryCommandCodeUsage(usage.Value, out var input, out var output, out var cacheRead, out var cacheWrite))
                    continue;
                var ts = UsageParsers.ParseIso(timestamp);
                if (ts is null) continue;
                var key = id.Trim() + "|" + timestamp.Trim();
                local[key] = Row("command-code", "command-code", key, ts.Value,
                    input, output, cacheRead, cacheWrite, 0, CommandCodeModel(UsageParsers.GetStr(root, "model")), null);
            }
        }

        var sid = string.IsNullOrWhiteSpace(sessionId)
            ? Path.GetFileNameWithoutExtension(file)
            : sessionId.Trim();
        if (string.IsNullOrEmpty(sid)) sid = "command-code";
        var project = string.IsNullOrWhiteSpace(cwd) ? null : cwd.Trim();
        foreach (var row in local.Values)
            byKey[row.RequestKey] = row with { SessionId = sid, Project = project };
    }

    /// <summary>inputTokens 已含 cache 读和写。750 / 读 600 / 写 50 / 出 20 拆成 100 / 600 / 50 / 20。</summary>
    private static bool TryCommandCodeUsage(
        JsonElement usage, out long input, out long output, out long cacheRead, out long cacheWrite)
    {
        cacheRead = UsageIo.JsonLong(usage, "cacheReadTokens");
        cacheWrite = UsageIo.JsonLong(usage, "cacheWriteTokens");
        var rawIn = UsageIo.JsonLong(usage, "inputTokens");
        var rawOut = UsageIo.JsonLong(usage, "outputTokens");
        UsageParsers.SplitInclusiveTokens(rawIn, rawOut, cacheRead, cacheWrite, 0, out input, out output);
        input = Math.Max(0, rawIn - cacheRead - cacheWrite);
        if (input + output + cacheRead + cacheWrite > 0) return true;
        if (!CommandCodeExplicitZero(usage)) return false;
        input = output = cacheRead = cacheWrite = 0;
        return true;
    }

    private static bool CommandCodeExplicitZero(JsonElement usage) =>
        IsJsonZero(usage, "inputTokens")
        && IsJsonZero(usage, "outputTokens")
        && IsJsonAbsentOrZero(usage, "cacheReadTokens")
        && IsJsonAbsentOrZero(usage, "cacheWriteTokens");

    private static bool IsJsonZero(JsonElement el, string name)
    {
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(name, out var value)) return false;
        return value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var n) && double.IsFinite(n) && n == 0;
    }

    private static bool IsJsonAbsentOrZero(JsonElement el, string name)
    {
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(name, out var value)) return true;
        if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return true;
        return value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var n) && double.IsFinite(n) && n == 0;
    }

    private static string CommandCodeModel(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "unknown";
        var name = raw.Trim();
        var slash = name.LastIndexOf('/');
        if (slash >= 0) name = name[(slash + 1)..].Trim();
        return name.Length == 0 ? "unknown" : name;
    }
}
