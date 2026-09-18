using System.Globalization;
using System.IO;
using System.Text.Json;

namespace AgentHub.Core.TokenCore;

/// <summary>本机 Grok（~/.grok/sessions/**/updates.jsonl）用量。
/// camelCase 的 inputTokens 含 cache，outputTokens 含 reasoning；snake_case 已是净列。
/// 优先用 costUsdTicks / 1e10 作为厂商回报 USD，不丢精度。</summary>
internal static class GrokLocal
{
    private const double UsdTicksPerUsd = 10_000_000_000d;

    public static string Home
    {
        get
        {
            var env = Environment.GetEnvironmentVariable("GROK_HOME");
            if (!string.IsNullOrWhiteSpace(env)) return Path.GetFullPath(env.Trim());
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".grok");
        }
    }

    public static bool SessionsExist => Directory.Exists(Path.Combine(Home, "sessions"));

    public static IReadOnlyList<UsageRecord> ReadUsage()
    {
        var root = Path.Combine(Home, "sessions");
        if (!Directory.Exists(root)) return [];
        var list = new List<UsageRecord>();
        foreach (var updates in Directory.EnumerateFiles(root, "updates.jsonl", SearchOption.AllDirectories))
        {
            try { list.AddRange(ParseUpdates(updates)); }
            catch (IOException) { }
        }
        return list;
    }

    internal static IEnumerable<UsageRecord> ParseUpdates(string file)
    {
        var sessionDir = Path.GetDirectoryName(file) ?? "";
        var sessionId = Path.GetFileName(sessionDir);
        if (string.IsNullOrEmpty(sessionId)) yield break;
        var project = DecodeProject(Path.GetFileName(Path.GetDirectoryName(sessionDir)));
        var fallbackModel = ReadFallbackModel(sessionDir);

        var lineNo = 0;
        foreach (var line in UsageParsers.ReadLinesShared(file))
        {
            lineNo++;
            if (string.IsNullOrWhiteSpace(line)) continue;
            JsonDocument doc;
            try { doc = JsonDocument.Parse(line); }
            catch (JsonException) { continue; }
            using (doc)
            {
                var root = doc.RootElement;
                var update = Obj(Obj(root, "params"), "update") ?? Obj(root, "update");
                if (update is null) continue;
                if (!string.Equals(Str(update, "sessionUpdate"), "turn_completed", StringComparison.Ordinal))
                    continue;
                var usage = Obj(update, "usage");
                if (usage is null) continue;

                var ts = Timestamp(root) ?? DateTime.UtcNow;
                var meta = Obj(Obj(root, "params"), "_meta") ?? Obj(root, "_meta");
                var eventId = Str(meta, "eventId") ?? Str(root, "eventId") ?? Str(root, "id")
                    ?? Str(update, "prompt_id") ?? lineNo.ToString(CultureInfo.InvariantCulture);

                var emitted = false;
                var modelUsage = Obj(usage, "modelUsage") ?? Obj(usage, "model_usage");
                if (modelUsage is { } mu)
                {
                    foreach (var prop in mu.EnumerateObject())
                    {
                        if (prop.Value.ValueKind != JsonValueKind.Object) continue;
                        var rec = NormalizeTurn(prop.Value, sessionId, eventId + "|" + prop.Name, ts,
                            CanonicalModel(prop.Name), project);
                        if (rec is null) continue;
                        emitted = true;
                        yield return rec;
                    }
                }
                if (!emitted)
                {
                    var rec = NormalizeTurn(usage, sessionId, eventId, ts, fallbackModel, project);
                    if (rec is not null) yield return rec;
                }
            }
        }
    }

    internal static UsageRecord? NormalizeTurn(
        JsonElement usage, string sessionId, string requestKey, DateTime ts, string model, string? project)
    {
        var camel = usage.TryGetProperty("inputTokens", out _);
        var rawIn = FirstNum(usage, "inputTokens", "input_tokens");
        var cacheRead = FirstNum(usage, "cachedReadTokens", "cacheReadInputTokens", "cache_read_input_tokens", "cached_input_tokens");
        var cacheWrite = FirstNum(usage, "cacheCreationTokens", "cachedWriteTokens", "cacheWriteInputTokens", "cache_creation_input_tokens");
        var rawOut = FirstNum(usage, "outputTokens", "output_tokens");
        var reasoning = FirstNum(usage, "reasoningTokens", "reasoning_output_tokens");

        long input, output;
        if (camel)
        {
            UsageParsers.SplitInclusiveTokens(rawIn, rawOut, cacheRead, cacheWrite, reasoning,
                out input, out output, cacheWriteInclusive: true);
        }
        else
        {
            // snake_case：input 已是净列，不要再减 cache；output 仍含 reasoning。
            input = rawIn;
            output = Math.Max(0, rawOut - reasoning);
        }
        if (input == 0 && output == 0 && cacheRead == 0 && cacheWrite == 0 && reasoning == 0)
            return null;

        return new UsageRecord
        {
            Tool = "grok",
            SessionId = sessionId,
            RequestKey = requestKey,
            TsUtc = ts,
            InputTokens = input,
            OutputTokens = output,
            CachedInputTokens = cacheRead,
            CacheWriteTokens = cacheWrite,
            ReasoningTokens = reasoning,
            ReportedCostUsd = ReportedUsd(usage),
            Model = CanonicalModel(model),
            Project = project,
        };
    }

    internal static double? ReportedUsd(JsonElement usage)
    {
        if (Bool(usage, "costIsPartial") || Bool(usage, "cost_is_partial")
            || Bool(usage, "usageIsIncomplete") || Bool(usage, "usage_is_incomplete"))
            return null;
        // ticks=0 是精确 $0（免费档），不能当缺字段再去估牌价。
        if (TryFirstDec(usage, out var ticks,
                "costUsdTicks", "totalCostUsdTicks", "cost_usd_ticks", "total_cost_usd_ticks")
            && ticks >= 0)
            return ticks / UsdTicksPerUsd;
        if (TryFirstDec(usage, out var usd, "costUsd", "totalCostUsd", "cost_usd", "total_cost_usd")
            && usd >= 0)
            return usd;
        return null;
    }

    private static string CanonicalModel(string? model)
    {
        var raw = string.IsNullOrWhiteSpace(model) ? "grok-build" : model.Trim();
        var lower = raw.ToLowerInvariant();
        if (lower.Contains("build-free", StringComparison.Ordinal) || lower.EndsWith("-free", StringComparison.Ordinal)
            || lower.Contains("free-tier", StringComparison.Ordinal))
            return "grok-build-free";
        if (lower is "grok-4.5-build" or "grok-4-5-build") return "grok-4.5-build";
        return raw;
    }

    private static string? DecodeProject(string? encoded)
    {
        if (string.IsNullOrWhiteSpace(encoded)) return null;
        try { return Uri.UnescapeDataString(encoded); }
        catch (UriFormatException) { return encoded; }
    }

    private static string ReadFallbackModel(string sessionDir)
    {
        var summary = Path.Combine(sessionDir, "summary.json");
        if (!File.Exists(summary)) return "grok-build";
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(summary));
            var root = doc.RootElement;
            var model = Str(root, "primaryModelId") ?? Str(root, "model")
                ?? Str(Obj(root, "signals"), "primaryModelId");
            return string.IsNullOrWhiteSpace(model) ? "grok-build" : model.Trim();
        }
        catch (Exception)
        {
            return "grok-build";
        }
    }

    private static DateTime? Timestamp(JsonElement root)
    {
        var paramsEl = Obj(root, "params");
        var meta = Obj(paramsEl, "_meta") ?? Obj(root, "_meta");
        var ms = FirstNum(meta, "agentTimestampMs", "timestampMs");
        if (ms == 0) ms = FirstNum(root, "timestamp_ms", "timestamp", "time");
        return UsageParsers.ParseMs(ms);
    }

    private static JsonElement? Obj(JsonElement? el, string name)
    {
        if (el is null || el.Value.ValueKind != JsonValueKind.Object) return null;
        if (!el.Value.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Object) return null;
        return v;
    }

    private static string? Str(JsonElement? el, string name)
    {
        if (el is null || el.Value.ValueKind != JsonValueKind.Object) return null;
        if (!el.Value.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.String) return null;
        var s = v.GetString();
        return string.IsNullOrWhiteSpace(s) ? null : s;
    }

    private static bool Bool(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static long FirstNum(JsonElement? el, params string[] names)
    {
        if (el is null) return 0;
        foreach (var n in names)
        {
            var v = Num(el, n);
            if (v > 0) return v;
        }
        return 0;
    }

    private static long Num(JsonElement? el, string name)
    {
        if (el is null || el.Value.ValueKind != JsonValueKind.Object) return 0;
        if (!el.Value.TryGetProperty(name, out var v)) return 0;
        return v.ValueKind switch
        {
            JsonValueKind.Number when v.TryGetInt64(out var n) => Math.Max(0, n),
            JsonValueKind.Number => (long)Math.Max(0, v.GetDouble()),
            JsonValueKind.String when long.TryParse(v.GetString(), NumberStyles.Any,
                CultureInfo.InvariantCulture, out var p) => Math.Max(0, p),
            _ => 0,
        };
    }

    private static bool TryFirstDec(JsonElement el, out double value, params string[] names)
    {
        value = 0;
        foreach (var n in names)
        {
            if (!el.TryGetProperty(n, out var v)) continue;
            if (v.ValueKind == JsonValueKind.Number)
            {
                value = v.TryGetDouble(out var x) ? x : 0;
                return true;
            }
            if (v.ValueKind == JsonValueKind.String
                && double.TryParse(v.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var p))
            {
                value = p;
                return true;
            }
        }
        return false;
    }
}
