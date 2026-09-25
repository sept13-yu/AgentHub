using System.IO;
using System.Text;
using System.Text.Json;

namespace AgentHub.Core.TokenCore;

/// <summary>一次计费请求的用量。
/// input / cached / cache_write / output / reasoning 五列互不重叠（含税字段先拆再存）；
/// billed total = 五列之和。model 缺字段归 unknown。</summary>
public sealed record UsageRecord
{
    public required string Tool { get; init; }
    public required string SessionId { get; init; }
    public required string RequestKey { get; init; }
    public required DateTime TsUtc { get; init; }
    public required long InputTokens { get; init; }
    public required long OutputTokens { get; init; }
    public long CachedInputTokens { get; init; }
    public long CacheWriteTokens { get; init; }
    public long ReasoningTokens { get; init; }
    /// <summary>厂商回报成本（USD）。Grok costUsdTicks 等；空则走价表估算。</summary>
    public double? ReportedCostUsd { get; init; }
    public bool IsSubagent { get; init; }
    public string Model { get; init; } = "unknown";
    public string? Project { get; init; }

    public string TsUtcIso => TsUtc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
}

/// <summary>四源用量解析器（规则全部来自 probe 实测，移植时不得改口径）。</summary>
public static class UsageParsers
{
    // ------------------------------------------------------------------
    // Codex：event_msg/payload.type=token_count → info.last_token_usage（增量）
    // 限流快照（total 未前进、last 原样重写）必须跳过；input 含 cached 需减。
    // 模型取最近 turn_context.model（忽略 model_provider）；session_meta.model
    // 仅作兜底；model/rerouted 在本回合内提升有效模型。reasoning 是 output 子集。
    // ------------------------------------------------------------------

    public static IEnumerable<UsageRecord> ParseCodex(string file, string sessionId)
    {
        string? cwd = null, threadSource = null, forkedFrom = null;
        string? selectedModel = null, effectiveModel = null;
        long[]? prevTotalSig = null;

        foreach (var line in ReadLinesShared(file))
        {
            JsonDocument doc;
            try { doc = JsonDocument.Parse(line); }
            catch (JsonException) { continue; }
            using (doc)
            {
                var root = doc.RootElement;
                var type = GetStr(root, "type");
                var payload = GetObj(root, "payload");

                if (type == "session_meta" && payload is not null)
                {
                    cwd = GetStr(payload, "cwd") ?? cwd;
                    threadSource = GetStr(payload, "thread_source") ?? threadSource;
                    forkedFrom = GetStr(payload, "forked_from_id") ?? forkedFrom;
                    // model_provider 是来源标注，不是模型名。
                    var metaModel = GetStr(payload, "model");
                    if (metaModel is not null && selectedModel is null)
                    {
                        selectedModel = metaModel;
                        effectiveModel ??= metaModel;
                    }
                    continue;
                }
                if (type == "turn_context" && payload is not null)
                {
                    cwd = GetStr(payload, "cwd") ?? cwd;
                    var turnModel = GetStr(payload, "model");
                    if (turnModel is not null) selectedModel = turnModel;
                    effectiveModel = selectedModel ?? effectiveModel;
                    continue;
                }

                if (TryCodexReroute(root, payload, out var fromModel, out var toModel))
                {
                    selectedModel = fromModel ?? selectedModel ?? effectiveModel;
                    effectiveModel = toModel;
                    continue;
                }

                var info = ExtractTokenInfo(type, payload);
                if (info is null) continue;

                var last = ReadUsage(info, "last_token_usage");
                var total = ReadUsage(info, "total_token_usage");
                var sig = new[] { total.Input, total.Cached, total.CacheWrite, total.Output, total.Reasoning, total.Total };
                if (prevTotalSig is not null && SigEquals(sig, prevTotalSig))
                    continue;   // 限流快照：total 未前进，last 是重放
                prevTotalSig = sig;

                SplitInclusiveTokens(last.Input, last.Output, last.Cached, last.CacheWrite, last.Reasoning,
                    out var inputTokens, out var outputTokens, cacheWriteInclusive: false);
                if (inputTokens == 0 && outputTokens == 0 && last.Cached == 0 && last.CacheWrite == 0 && last.Reasoning == 0)
                    continue;

                var ts = ParseIso(GetStr(root, "timestamp"));
                if (ts is null) continue;

                yield return new UsageRecord
                {
                    Tool = "codex",
                    SessionId = sessionId,
                    // 同毫秒多条请求靠用量快照区分（禁行号）
                    RequestKey = $"{ts:yyyy-MM-dd'T'HH:mm:ss'Z'}|in={last.Input}|out={last.Output}|cached={last.Cached}|tot={total.Total}",
                    TsUtc = ts.Value,
                    InputTokens = inputTokens,
                    OutputTokens = outputTokens,
                    CachedInputTokens = last.Cached,
                    CacheWriteTokens = last.CacheWrite,
                    ReasoningTokens = last.Reasoning,
                    IsSubagent = IsCodexSubagent(threadSource, forkedFrom),
                    Model = effectiveModel ?? selectedModel ?? "unknown",
                    Project = cwd,
                };
            }
        }
    }

    internal static bool IsCodexSubagent(string? threadSource, string? forkedFrom) =>
        (!string.IsNullOrEmpty(threadSource) && !threadSource.Equals("user", StringComparison.OrdinalIgnoreCase))
        || !string.IsNullOrEmpty(forkedFrom);

    /// <summary>把 cache / reasoning 从含税的 input/output 里拆出来。
    /// ZCode/Grok：input 含 cache 读+写，output 含 reasoning。
    /// Codex：input 只含 cache 读（不含 write），output 含 reasoning。</summary>
    internal static void SplitInclusiveTokens(
        long rawIn, long rawOut, long cacheRead, long cacheWrite, long reasoning,
        out long input, out long output, bool cacheWriteInclusive = true)
    {
        input = rawIn;
        if (cacheRead > 0 && cacheRead <= input) input -= cacheRead;
        if (cacheWriteInclusive && cacheWrite > 0 && cacheWrite <= input) input -= cacheWrite;
        output = Math.Max(0, rawOut - Math.Max(0, reasoning));
    }

    private static bool TryCodexReroute(JsonElement root, JsonElement? payload, out string? fromModel, out string toModel)
    {
        fromModel = null;
        toModel = "";
        if (TryReadReroute(root, out fromModel, out toModel)) return true;
        if (payload is { } p && TryReadReroute(p, out fromModel, out toModel)) return true;
        var msg = GetObj(payload, "msg");
        if (msg is { } m && TryReadReroute(m, out fromModel, out toModel)) return true;
        return false;
    }

    private static bool TryReadReroute(JsonElement el, out string? fromModel, out string toModel)
    {
        fromModel = null;
        toModel = "";
        var method = GetStr(el, "method") ?? GetStr(el, "type");
        if (string.IsNullOrEmpty(method)) return false;
        var name = method.Trim().ToLowerInvariant().Replace('_', '/');
        if (name is not ("model/rerouted" or "modelrerouted")) return false;
        var data = GetObj(el, "params") ?? GetObj(el, "payload") ?? el;
        toModel = GetStr(data, "toModel") ?? GetStr(data, "to_model") ?? "";
        if (toModel.Length == 0) return false;
        fromModel = GetStr(data, "fromModel") ?? GetStr(data, "from_model");
        return true;
    }

    private static JsonElement? ExtractTokenInfo(string? type, JsonElement? payload)
    {
        if (type != "event_msg" || payload is null) return null;
        if (GetStr(payload, "type") == "token_count")
        {
            var info = GetObj(payload, "info");
            if (info is not null) return info;
        }
        var msg = GetObj(payload, "msg");
        if (msg is not null && GetStr(msg, "type") == "token_count")
        {
            var info2 = GetObj(msg, "info");
            if (info2 is not null) return info2;
        }
        return null;
    }

    // ------------------------------------------------------------------
    // WorkBuddy：message.usage（providerData.messageId 做 request_key）
    // cached 已含在 input 里（cached ≤ input 才减）；cache_creation 单列。
    // ------------------------------------------------------------------

    public static IEnumerable<UsageRecord> ParseWorkBuddy(string file, string sessionId, bool isSubagent)
    {
        string? projectFromDir = null;
        foreach (var line in ReadLinesShared(file))
        {
            JsonDocument doc;
            try { doc = JsonDocument.Parse(line); }
            catch (JsonException) { continue; }
            using (doc)
            {
                var root = doc.RootElement;
                var msg = GetObj(root, "message");
                var usage = msg is not null ? GetObj(msg, "usage") : null;
                if (usage is null) continue;

                long rawInput = GetInt(usage, "input_tokens");
                long output = GetInt(usage, "output_tokens");
                if (rawInput == 0 && output == 0) continue;

                long cached = CachedTokens(usage, root);
                long cacheWrite = GetInt(usage, "cache_creation_input_tokens");
                long inputTokens = cached <= rawInput ? rawInput - cached : rawInput;
                if (inputTokens == 0 && output == 0) continue;

                var requestKey = RequestKeyFromProvider(root) ?? GetStr(root, "id");
                if (string.IsNullOrEmpty(requestKey)) continue;

                var ts = ParseMs(GetNum(root, "timestamp"));
                if (ts is null) continue;

                string? project = GetStr(root, "cwd");
                project ??= projectFromDir ??= ProjectFromPath(file);
                // WorkBuddy 把模型写在 providerData.model，根级 / message.model 在现网 jsonl 里几乎都空
                var pd = GetObj(root, "providerData");
                var model = GetStr(pd, "model")
                    ?? GetStr(root, "model")
                    ?? (msg is not null ? GetStr(msg, "model") : null);

                yield return new UsageRecord
                {
                    Tool = "workbuddy",
                    SessionId = sessionId,
                    RequestKey = requestKey!,
                    TsUtc = ts.Value,
                    InputTokens = inputTokens,
                    OutputTokens = output,
                    CachedInputTokens = cached,
                    CacheWriteTokens = cacheWrite,
                    IsSubagent = isSubagent,
                    Model = model ?? "unknown",
                    Project = project,
                };
            }
        }
    }

    private static string? RequestKeyFromProvider(JsonElement root)
    {
        var pd = GetObj(root, "providerData");
        return pd is not null ? GetStr(pd, "messageId") : null;
    }

    /// <summary>缓存命中真值：usage.cache_read_input_tokens，否则 rawUsage.prompt_cache_hit_tokens
    /// / prompt_tokens_details.cached_tokens（rawUsage.cache_read_input_tokens 恒为 0，别用）。</summary>
    private static long CachedTokens(JsonElement? usage, JsonElement? root)
    {
        long cr = GetInt(usage, "cache_read_input_tokens");
        if (cr > 0) return cr;
        var pd = GetObj(root, "providerData");
        var raw = pd is not null ? GetObj(pd, "rawUsage") : null;
        if (raw is null) return 0;
        long hit = GetInt(raw, "prompt_cache_hit_tokens");
        if (hit > 0) return hit;
        var details = GetObj(raw, "prompt_tokens_details");
        return details is not null ? GetInt(details, "cached_tokens") : 0;
    }

    /// <summary>info.last/total_token_usage 快照（Codex 专用）。</summary>
    private readonly record struct Usage5(long Input, long Cached, long CacheWrite, long Output, long Reasoning, long Total);

    private static Usage5 ReadUsage(JsonElement? info, string name)
    {
        var u = GetObj(info, name);
        var cacheWrite = GetInt(u, "cache_creation_input_tokens");
        if (cacheWrite == 0) cacheWrite = GetInt(u, "cache_write_input_tokens");
        return new Usage5(
            GetInt(u, "input_tokens"), GetInt(u, "cached_input_tokens"), cacheWrite,
            GetInt(u, "output_tokens"), GetInt(u, "reasoning_output_tokens"),
            GetInt(u, "total_tokens"));
    }

    private static bool SigEquals(long[] a, long[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }

    // ------------------------------------------------------------------
    // DSH: legacy assistant/chunk.usage + v3 assistant/message|message/assistant.data.usage;
    // same request key (seq preferred, else turn:step) keeps last. inputTokens excludes cache as-is;
    // reasoningTokens -> ReasoningTokens, NOT folded into OutputTokens (TokenTracker disjoint columns).
    // ------------------------------------------------------------------

    public static IEnumerable<UsageRecord> ParseDsh(string plainText, string sessionId, string? project)
    {
        var lastByKey = new Dictionary<string, (long In, long Out, long Cached, long CacheWrite, long Reasoning, string? Model, long TsMs)>(StringComparer.Ordinal);
        bool isSubagent = false;
        string? currentModel = null;
        var cwd = project;

        foreach (var rawLine in plainText.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.Length == 0) continue;
            JsonDocument doc;
            try { doc = JsonDocument.Parse(line); }
            catch (JsonException) { continue; }
            using (doc)
            {
                var root = doc.RootElement;
                var type = GetStr(root, "type");
                if (type is "session" or "session/start")
                {
                    isSubagent = GetNum(root, "delegationDepth") > 0
                        || GetNum(GetObj(root, "data"), "delegationDepth") > 0;
                    cwd ??= GetStr(root, "cwd") ?? GetStr(GetObj(root, "data"), "cwd");
                    continue;
                }
                if (type == "request/header")
                {
                    var header = GetObj(GetObj(root, "data"), "header");
                    var config = GetObj(header, "config");
                    // 保留原始 model，展示归一在查询端做
                    currentModel = GetStr(config, "model") ?? currentModel;
                    continue;
                }

                long seq = GetNum(root, "seq");
                var dataEl = GetObj(root, "data");
                long ts = GetNum(root, "time") != 0 ? GetNum(root, "time")
                    : GetNum(root, "timestamp") != 0 ? GetNum(root, "timestamp")
                    : GetNum(dataEl, "time") != 0 ? GetNum(dataEl, "time")
                    : GetNum(dataEl, "timestamp");
                if (ts == 0) ts = GetNum(root, "time0");

                if (type == "assistant/chunk")
                {
                    if (dataEl is null) continue;
                    var chunk = GetObj(dataEl, "chunk");
                    if (chunk is null || GetStr(chunk, "type") != "usage") continue;
                    var usage = GetObj(chunk, "usage");
                    if (usage is null) continue;

                    long turn = GetNum(dataEl, "turn"), step = GetNum(dataEl, "step");
                    var key = seq > 0 ? seq.ToString() : $"{turn}:{step}";
                    var model = GetStr(dataEl, "model") ?? GetStr(chunk, "model") ?? currentModel;
                    lastByKey[key] = (
                        GetInt(usage, "inputTokens") + GetInt(usage, "input_tokens"),
                        GetInt(usage, "outputTokens") + GetInt(usage, "output_tokens"),
                        GetInt(usage, "cacheReadTokens") + GetInt(usage, "cache_read_tokens"),
                        GetInt(usage, "cacheWriteTokens") + GetInt(usage, "cacheCreationTokens")
                            + GetInt(usage, "cache_write_tokens") + GetInt(usage, "cache_creation_tokens"),
                        GetInt(usage, "reasoningTokens") + GetInt(usage, "reasoning_tokens"),
                        model, ts);
                    continue;
                }

                // v3: assistant/message or message/assistant; usage under data.usage
                if (type is not ("assistant/message" or "message/assistant")) continue;
                {
                    var usage = GetObj(dataEl, "usage") ?? GetObj(root, "usage");
                    if (usage is null) continue;

                    var msg = GetObj(dataEl, "message");
                    var source = GetObj(msg, "source");
                    var model = GetStr(source, "model") ?? GetStr(dataEl, "model") ?? currentModel;

                    long turn = GetNum(dataEl, "turn"), step = GetNum(dataEl, "step");
                    var key = seq > 0 ? seq.ToString()
                        : (turn != 0 || step != 0) ? $"{turn}:{step}"
                        : $"msg:{lastByKey.Count}";

                    lastByKey[key] = (
                        GetInt(usage, "inputTokens") + GetInt(usage, "input_tokens"),
                        GetInt(usage, "outputTokens") + GetInt(usage, "output_tokens"),
                        GetInt(usage, "cacheReadTokens") + GetInt(usage, "cache_read_tokens"),
                        GetInt(usage, "cacheWriteTokens") + GetInt(usage, "cacheCreationTokens")
                            + GetInt(usage, "cache_write_tokens") + GetInt(usage, "cache_creation_tokens"),
                        GetInt(usage, "reasoningTokens") + GetInt(usage, "reasoning_tokens"),
                        model, ts);
                }
            }
        }

        foreach (var (key, s) in lastByKey)
        {
            if (s.In == 0 && s.Out == 0 && s.Cached == 0 && s.CacheWrite == 0 && s.Reasoning == 0) continue;
            if (s.TsMs == 0) continue;
            var tsUtc = DateTimeOffset.FromUnixTimeMilliseconds(s.TsMs).UtcDateTime;
            yield return new UsageRecord
            {
                Tool = "dsh",
                SessionId = sessionId,
                RequestKey = key,
                TsUtc = tsUtc,
                InputTokens = s.In,
                OutputTokens = s.Out,
                CachedInputTokens = s.Cached,
                CacheWriteTokens = s.CacheWrite,
                ReasoningTokens = s.Reasoning,
                IsSubagent = isSubagent,
                Model = s.Model ?? "unknown",
                Project = cwd,
            };
        }
    }

    /// <summary>provider/model -> model (TokenTracker normalizeDshModelName).</summary>
    private static string? StripModelPath(string? model)
    {
        if (string.IsNullOrWhiteSpace(model)) return null;
        var t = model.Trim();
        var slash = t.LastIndexOf('/');
        return slash >= 0 ? t[(slash + 1)..] : t;
    }
    // Cursor CSV：GET cursor.com/api/dashboard/export-usage-events-csv?strategy=tokens
    // 按表头名取列（列序会变）。四列并列：Input (w/o Cache Write)=净新增，
    // Input (w/ Cache Write)=cache write（不是合计，禁止相减），Cache Read，Output。
    // Total Tokens = 四列之和，与官网 Usage 页一致。
    // ------------------------------------------------------------------

    public static IEnumerable<UsageRecord> ParseCursorCsv(string csv)
    {
        var lines = csv.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var start = 0;
        while (start < lines.Length && string.IsNullOrWhiteSpace(lines[start])) start++;
        if (start >= lines.Length - 1)
            throw new InvalidDataException("CSV 为空");

        var header = SplitCsvLine(lines[start]);
        var col = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < header.Count; i++)
            col[header[i].Trim().Trim('"')] = i;

        int Req(string name)
        {
            if (!col.TryGetValue(name, out var idx))
                throw new InvalidDataException("CSV 缺列：" + name);
            return idx;
        }

        var dateIdx = Req("Date");
        var modelIdx = Req("Model");
        var inputWithIdx = Req("Input (w/ Cache Write)");
        var inputWithoutIdx = Req("Input (w/o Cache Write)");
        var cacheReadIdx = Req("Cache Read");
        var outputIdx = Req("Output Tokens");
        var totalIdx = Req("Total Tokens");
        var minFields = Math.Max(Math.Max(dateIdx, modelIdx),
            Math.Max(Math.Max(inputWithIdx, inputWithoutIdx), Math.Max(cacheReadIdx, Math.Max(outputIdx, totalIdx)))) + 1;

        for (var i = start + 1; i < lines.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(lines[i])) continue;
            var fields = SplitCsvLine(lines[i]);
            if (fields.Count < minFields) continue;

            var inputWithout = ParseCsvLong(fields[inputWithoutIdx]);
            var cacheWrite = ParseCsvLong(fields[inputWithIdx]);
            var cacheRead = ParseCsvLong(fields[cacheReadIdx]);
            var output = ParseCsvLong(fields[outputIdx]);
            var total = ParseCsvLong(fields[totalIdx]);
            if (inputWithout <= 0 && output <= 0 && cacheRead <= 0 && cacheWrite <= 0 && total <= 0) continue;

            var dateRaw = fields[dateIdx].Trim().Trim('"');
            if (!DateTimeOffset.TryParse(dateRaw, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var dto)
                && !DateTimeOffset.TryParse(dateRaw, out dto))
                continue;

            var ts = dto.UtcDateTime;
            var localDay = dto.ToLocalTime().ToString("yyyy-MM-dd");
            var model = fields[modelIdx].Trim().Trim('"');
            if (string.IsNullOrEmpty(model)) model = "unknown";

            yield return new UsageRecord
            {
                Tool = "cursor",
                SessionId = "cursor-day:" + localDay,
                // 主键含 model：同日多模型不得互相覆盖
                RequestKey = dateRaw + "|" + model,
                TsUtc = ts,
                InputTokens = inputWithout,
                OutputTokens = output,
                CachedInputTokens = cacheRead,
                CacheWriteTokens = cacheWrite,
                Model = model,
            };
        }
    }

    internal static List<string> SplitCsvLine(string line)
    {
        var fields = new List<string>();
        var sb = new StringBuilder();
        var inQuotes = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                    else inQuotes = false;
                }
                else sb.Append(c);
            }
            else if (c == '"') inQuotes = true;
            else if (c == ',') { fields.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(c);
        }
        fields.Add(sb.ToString());
        return fields;
    }

    internal static long ParseCsvLong(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return 0;
        var s = raw.Trim().Trim('"').Replace(",", "");
        return long.TryParse(s, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var n)
            ? Math.Max(0, n) : 0;
    }

    /// <summary>共享读会话 jsonl：写入方仍打开文件时也能扫到正在追加的行。</summary>
    internal static IEnumerable<string> ReadLinesShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        string? line;
        while ((line = reader.ReadLine()) is not null)
            yield return line;
    }

    // ------------------------------------------------------------------
    // 共用 JSON 助手
    // ------------------------------------------------------------------

    internal static JsonElement? GetObj(JsonElement? el, string name)
    {
        if (el is null) return null;
        if (el.Value.ValueKind != JsonValueKind.Object) return null;
        if (!el.Value.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Object) return null;
        return v;
    }

    internal static string? GetStr(JsonElement? el, string name)
    {
        if (el is null) return null;
        if (el.Value.ValueKind != JsonValueKind.Object) return null;
        if (!el.Value.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.String) return null;
        var s = v.GetString();
        return string.IsNullOrEmpty(s) ? null : s;
    }

    internal static long GetInt(JsonElement? el, string name)
    {
        if (el is null) return 0;
        if (el.Value.ValueKind != JsonValueKind.Object) return 0;
        if (!el.Value.TryGetProperty(name, out var v)) return 0;
        return v.ValueKind switch
        {
            JsonValueKind.Number when v.TryGetInt64(out var n) => Math.Max(0, n),
            _ => 0,
        };
    }

    internal static long GetNum(JsonElement? el, string name)
    {
        if (el is null) return 0;
        if (el.Value.ValueKind != JsonValueKind.Object) return 0;
        if (!el.Value.TryGetProperty(name, out var v)) return 0;
        return v.ValueKind switch
        {
            JsonValueKind.Number when v.TryGetInt64(out var n) => n,
            JsonValueKind.Number => (long)v.GetDouble(),
            _ => 0,
        };
    }

    internal static DateTime? ParseIso(string? iso)
    {
        if (string.IsNullOrEmpty(iso)) return null;
        if (DateTime.TryParse(iso, null, System.Globalization.DateTimeStyles.RoundtripKind, out var dt))
            return dt.ToUniversalTime();
        return null;
    }

    internal static DateTime? ParseMs(long ms)
    {
        if (ms <= 0) return null;
        if (ms < 10_000_000_000) ms *= 1000;   // 秒 → 毫秒
        try { return DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime; }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    private static string? ProjectFromPath(string file)
    {
        // projects/<slug>/... → slug 反解（有损，行内 cwd 优先）
        var dir = Path.GetDirectoryName(file)!;
        var parent = Path.GetFileName(Path.GetDirectoryName(dir));
        if (parent is not null && parent.Equals("projects", StringComparison.OrdinalIgnoreCase))
        {
            var slug = Path.GetFileName(dir);
            var parts = slug.Split('-');
            if (parts.Length > 1 && parts[0].Length == 1 && char.IsLetter(parts[0][0]))
                return parts[0] + @":\" + string.Join(@"\", parts[1..]);
        }
        return null;
    }
}
