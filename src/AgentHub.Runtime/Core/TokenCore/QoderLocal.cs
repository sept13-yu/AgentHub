using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace AgentHub.Core.TokenCore;

/// <summary>
/// Qoder 用量分两条互不混用的本机路径：
/// <list type="bullet">
/// <item>国际版 <c>qoder</c>：%APPDATA%/Qoder/SharedClientCache/cache/db/local.db 的 assistant token_info
/// （对齐 TokenTracker normalizeQoderTokens：prompt 已含 cache）。不读 state.vscdb，不解密库内容。</item>
/// <item>国内版 <c>qoder-cn</c>：桌面端是 Electron/Wails（product=qodercn），不是 VS Code 叉。
/// 用量源是 ~/.qoder-cn/projects/**/*.jsonl（可用 QODER_CN_HOME / QODERCN_CONFIG_DIR 覆盖）。
/// 同机若残留 %APPDATA%/Qoder 国际版目录，不得并入 qoder-cn。
/// 国内桌面会话 UI 库是 %APPDATA%/com.qodercn.app.stable/main.sqlite，本类不拿它当 token 来源。</item>
/// </list>
/// </summary>
internal static class QoderLocal
{
    private const string UsageSql = """
        SELECT
          cm.rowid AS row_id,
          cm.id,
          cm.session_id,
          cm.request_id,
          cm.token_info,
          cm.model_info,
          cm.gmt_create,
          cr.extra AS record_extra,
          cs.preferred_model_info,
          cs.project_uri,
          cs.project_name
        FROM chat_message AS cm
        LEFT JOIN chat_record AS cr ON cr.request_id = cm.request_id
        LEFT JOIN chat_session AS cs ON cs.session_id = cm.session_id
        WHERE cm.role = 'assistant'
          AND cm.token_info IS NOT NULL
          AND trim(cm.token_info) NOT IN ('', '{}')
        ORDER BY cm.gmt_create, cm.rowid
        """;

    private const string UsageSqlMessageOnly = """
        SELECT
          cm.rowid AS row_id,
          cm.id,
          cm.session_id,
          cm.request_id,
          cm.token_info,
          cm.model_info,
          cm.gmt_create
        FROM chat_message AS cm
        WHERE cm.role = 'assistant'
          AND cm.token_info IS NOT NULL
          AND trim(cm.token_info) NOT IN ('', '{}')
        ORDER BY cm.gmt_create, cm.rowid
        """;

    public static string DbPath(QoderQuota.Site site) => QoderQuota.LocalDbPath(site);
    public static bool DbExists(QoderQuota.Site site) => File.Exists(DbPath(site));

    /// <summary>国内 CLI/桌面会话根：默认 ~/.qoder-cn，可用 QODER_CN_HOME 覆盖。</summary>
    public static string ChinaHome => QoderQuota.ChinaConfigHome;

    public static string ChinaProjectsDir => Path.Combine(ChinaHome, "projects");

    /// <summary>与 <see cref="GrokLocal.SessionsExist"/> 同形：有 projects 目录才入库，不要求国际版 local.db。</summary>
    public static bool ChinaUsageExists => Directory.Exists(ChinaProjectsDir);

    public static IReadOnlyList<UsageRecord> ReadInternational() => ReadSqlite(QoderQuota.International);

    public static IReadOnlyList<UsageRecord> ReadChina() => ReadChinaJsonl();

    private static IReadOnlyList<UsageRecord> ReadSqlite(QoderQuota.Site site)
    {
        if (site.Id == "china") return [];
        if (!TrySnapshot(site, out var db, out var tmp)) return [];
        try { return ReadCopied(db, ToolId(site)); }
        finally { DeleteSnapshot(tmp); }
    }

    internal static string ToolId(QoderQuota.Site site) =>
        site.Id == "china" ? "qoder-cn" : "qoder";

    // ------------------------------------------------------------------
    // 国内版 jsonl（~/.qoder-cn/projects/**/*.jsonl）
    // ------------------------------------------------------------------

    private static List<UsageRecord> ReadChinaJsonl()
    {
        var root = ChinaProjectsDir;
        if (!Directory.Exists(root)) return [];
        var list = new List<UsageRecord>();
        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories); }
        catch (IOException) { return list; }
        catch (UnauthorizedAccessException) { return list; }
        foreach (var file in files)
        {
            try { list.AddRange(ParseChinaJsonl(file)); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return list;
    }

    /// <summary>解析一条会话 jsonl。国内版 assistant.usage 常把 token 记成 0、只给 credits；
    /// 仪表盘 byAgent 按五列 token 之和，故 credits&gt;0 且 token 全 0 时用正文估一个 OutputTokens。
    /// credits 不是 USD，ReportedCostUsd 保持空，qfmodel 走价表/noPrice。</summary>
    internal static IEnumerable<UsageRecord> ParseChinaJsonl(string file)
    {
        var fallbackSession = Path.GetFileNameWithoutExtension(file);
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
                var rec = ParseChinaAssistantLine(doc.RootElement, fallbackSession, line, lineNo);
                if (rec is not null) yield return rec;
            }
        }
    }

    internal static UsageRecord? ParseChinaAssistantLine(
        JsonElement root, string fallbackSession, string line, int lineNo)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        var type = UsageParsers.GetStr(root, "type");
        var msg = UsageParsers.GetObj(root, "message");
        var role = msg is not null ? UsageParsers.GetStr(msg, "role") : UsageParsers.GetStr(root, "role");
        var isAssistant = string.Equals(type, "assistant", StringComparison.OrdinalIgnoreCase)
            || string.Equals(role, "assistant", StringComparison.OrdinalIgnoreCase);
        if (!isAssistant) return null;

        var usage = msg is not null ? UsageParsers.GetObj(msg, "usage") : null;
        usage ??= UsageParsers.GetObj(root, "usage");
        if (usage is null) return null;

        var rawIn = TokenCount(usage, "input_tokens");
        var rawOut = TokenCount(usage, "output_tokens");
        var cacheRead = TokenCount(usage, "cache_read_input_tokens");
        var cacheWrite = TokenCount(usage, "cache_creation_input_tokens");
        var reasoning = TokenCount(usage, "reasoning_tokens") + TokenCount(usage, "reasoning_output_tokens");
        var hasCredits = TryPositiveDec(usage.Value, "credits", out _)
            || TryPositiveDec(usage.Value, "original_credits", out _);

        UsageParsers.SplitInclusiveTokens(rawIn, rawOut, cacheRead, cacheWrite, reasoning,
            out var input, out var output, cacheWriteInclusive: false);

        if (input == 0 && output == 0 && cacheRead == 0 && cacheWrite == 0 && reasoning == 0)
        {
            if (!hasCredits) return null;
            // 国内 jsonl 经常 token 全 0、credits 非 0：用 assistant 文本估输出 token，避免仪表盘环图仍为 0。
            output = EstimateOutputTokens(msg);
        }

        var sessionId = UsageParsers.GetStr(root, "sessionId")
            ?? UsageParsers.GetStr(root, "session_id")
            ?? (msg is not null ? UsageParsers.GetStr(msg, "sessionId") : null)
            ?? fallbackSession;
        if (string.IsNullOrWhiteSpace(sessionId)) sessionId = fallbackSession;
        sessionId = sessionId.Trim();

        var requestKey = FirstNonEmpty(
            StrFromUsage(usage, "request_id"),
            StrFromUsage(usage, "requestId"),
            msg is not null ? UsageParsers.GetStr(msg, "id") : null,
            UsageParsers.GetStr(root, "uuid"),
            UsageParsers.GetStr(root, "id"));
        if (string.IsNullOrEmpty(requestKey))
            requestKey = StableLineKey(sessionId, line, lineNo);

        var ts = ReadLineTimestamp(root) ?? (msg is not null ? ReadLineTimestamp(msg.Value) : null);
        if (ts is null) return null;

        var model = ResolveChinaModelDisplay(
            (msg is not null ? UsageParsers.GetStr(msg, "model") : null)
            ?? UsageParsers.GetStr(root, "model")
            ?? "unknown");
        var project = UsageParsers.GetStr(root, "cwd")
            ?? (msg is not null ? UsageParsers.GetStr(msg, "cwd") : null);

        return new UsageRecord
        {
            Tool = "qoder-cn",
            SessionId = sessionId,
            RequestKey = requestKey,
            TsUtc = ts.Value,
            InputTokens = input,
            OutputTokens = output,
            CachedInputTokens = cacheRead,
            CacheWriteTokens = cacheWrite,
            ReasoningTokens = reasoning,
            Model = model,
            Project = project,
        };
    }

    /// <summary>UTF-8 字节/4，至少 1，保证 byAgent.tokens &gt; 0。</summary>

    /// <summary>
    /// Qoder CN stores internal route ids (qfmodel); UI labels them Qwen3.8-Flash in dynamic-text.
    /// Token dashboard uses this for every tool. Xiaomi MiMo API ids / closed-beta preview ids
    /// map to the official Desktop picker labels (MiMo V2.6 Flash / Pro / Pro Ultraspeed).
    /// </summary>
    internal static string ResolveChinaModelDisplay(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "unknown";
        // qoder/...、qoder-custom-uuid/workbuddy/deepseek-v4.1-flash → 只留最后一段
        var key = ModelNameNormalizer.Leaf(raw);
        if (key.Length == 0) return "unknown";
        if (ChinaModelDisplay.TryGetValue(key, out var label)) return label;
        // 邀测 id 等：先走价表别名再套官方展示名（mimo-x-flash-preview → mimo-v2.6-flash → MiMo V2.6 Flash）
        if (PriceAliases.TryMap(key, out var canonical)
            && ChinaModelDisplay.TryGetValue(canonical, out label))
            return label;
        return key;
    }

    private static readonly Dictionary<string, string> ChinaModelDisplay =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["qfmodel"] = "Qwen3.8-Flash",
            ["qmodel_38max"] = "Qwen3.8-Max",
            ["qmodel"] = "Qwen3.7-Plus",
            ["qmodel_latest"] = "Qwen3.7-Max",
            ["dfmodel"] = "DeepSeek-V4-Flash",
            ["dmodel"] = "DeepSeek-V4-Pro",
            ["gfmodel"] = "GLM-5.3-Flash",
            ["gmodel"] = "GLM-5.3",
            ["kmodel"] = "Kimi-K2.7-Code",
            ["kmodel_latest"] = "Kimi-K3",
            ["mmodel"] = "MiniMax-M3",
            ["cmodel"] = "Cantus",
            ["mimo-v2.6-flash"] = "MiMo V2.6 Flash",
            ["mimo-v2.6-pro"] = "MiMo V2.6 Pro",
            ["mimo-v2.6-pro-ultraspeed"] = "MiMo V2.6 Pro Ultraspeed",
        };

    internal static long EstimateOutputTokens(JsonElement? message)
    {
        var text = ExtractAssistantText(message);
        if (text.Length == 0) return 1;
        var n = Encoding.UTF8.GetByteCount(text) / 4;
        return n > 0 ? n : 1;
    }

    private static string ExtractAssistantText(JsonElement? message)
    {
        if (message is null || message.Value.ValueKind != JsonValueKind.Object) return "";
        if (!message.Value.TryGetProperty("content", out var content)) return "";
        if (content.ValueKind == JsonValueKind.String) return content.GetString() ?? "";
        if (content.ValueKind == JsonValueKind.Object)
            return UsageParsers.GetStr(content, "text") ?? UsageParsers.GetStr(content, "content") ?? "";
        if (content.ValueKind != JsonValueKind.Array) return "";
        var sb = new StringBuilder();
        foreach (var part in content.EnumerateArray())
        {
            if (part.ValueKind == JsonValueKind.String)
            {
                sb.Append(part.GetString());
                continue;
            }
            if (part.ValueKind != JsonValueKind.Object) continue;
            var kind = UsageParsers.GetStr(part, "type");
            if (kind is not (null or "text" or "output_text")) continue;
            var t = UsageParsers.GetStr(part, "text") ?? UsageParsers.GetStr(part, "content");
            if (!string.IsNullOrEmpty(t)) sb.Append(t);
        }
        return sb.ToString();
    }

    private static DateTime? ReadLineTimestamp(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        if (root.TryGetProperty("timestamp", out var v))
        {
            var parsed = TimestampValue(v);
            if (parsed is not null) return parsed;
        }
        foreach (var name in new[] { "ts", "time", "createdAt", "created_at" })
        {
            if (!root.TryGetProperty(name, out var alt)) continue;
            var parsed = TimestampValue(alt);
            if (parsed is not null) return parsed;
        }
        return null;
    }

    private static DateTime? TimestampValue(JsonElement v)
    {
        if (v.ValueKind == JsonValueKind.String)
        {
            var s = v.GetString();
            var iso = UsageParsers.ParseIso(s);
            if (iso is not null) return iso;
            if (long.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var n))
                return UsageParsers.ParseMs(n);
            return null;
        }
        if (v.ValueKind != JsonValueKind.Number) return null;
        if (v.TryGetInt64(out var i)) return UsageParsers.ParseMs(i);
        return UsageParsers.ParseMs((long)v.GetDouble());
    }

    private static string StableLineKey(string sessionId, string line, int lineNo)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(line)));
        if (hash.Length > 16) hash = hash[..16];
        return sessionId + "|L" + lineNo.ToString(CultureInfo.InvariantCulture) + "|" + hash;
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var v in values)
        {
            if (!string.IsNullOrWhiteSpace(v)) return v.Trim();
        }
        return null;
    }

    private static string? StrFromUsage(JsonElement? usage, string name)
    {
        if (usage is null || usage.Value.ValueKind != JsonValueKind.Object) return null;
        if (!usage.Value.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.String)
        {
            var s = v.GetString();
            return string.IsNullOrWhiteSpace(s) ? null : s.Trim();
        }
        if (v.ValueKind == JsonValueKind.Number) return v.ToString();
        return null;
    }

    private static long TokenCount(JsonElement? el, string name)
    {
        if (el is null || el.Value.ValueKind != JsonValueKind.Object) return 0;
        if (!el.Value.TryGetProperty(name, out var v)) return 0;
        return v.ValueKind switch
        {
            JsonValueKind.Number when v.TryGetInt64(out var n) => Math.Max(0, n),
            JsonValueKind.Number => (long)Math.Max(0, v.GetDouble()),
            JsonValueKind.String when decimal.TryParse(v.GetString(), NumberStyles.Any,
                CultureInfo.InvariantCulture, out var d) => (long)Math.Max(0, d),
            _ => 0,
        };
    }

    private static bool TryPositiveDec(JsonElement el, string name, out decimal value)
    {
        value = 0;
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(name, out var v)) return false;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out value)) return value > 0;
        if (v.ValueKind == JsonValueKind.Number)
        {
            value = (decimal)v.GetDouble();
            return value > 0;
        }
        if (v.ValueKind == JsonValueKind.String && decimal.TryParse(v.GetString(),
                NumberStyles.Any, CultureInfo.InvariantCulture, out value))
            return value > 0;
        value = 0;
        return false;
    }

    // ------------------------------------------------------------------
    // 国际版 SQLite（%APPDATA%/Qoder/.../local.db）
    // ------------------------------------------------------------------

    /// <summary>prompt 已含 cached；净输入 = prompt − cache，billed = prompt + output。</summary>
    internal static bool TryNormalizeTokens(string? tokenInfo, out long input, out long cached, out long output)
    {
        input = cached = output = 0;
        if (string.IsNullOrWhiteSpace(tokenInfo)) return false;
        try
        {
            using var doc = JsonDocument.Parse(tokenInfo);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            if (!TryNum(root, "prompt_tokens", out var prompt) || prompt < 0) return false;
            if (!TryNum(root, "completion_tokens", out var completion) || completion < 0) return false;
            var cache = 0L;
            if (TryNum(root, "cached_tokens", out var rawCache) && rawCache >= 0)
                cache = (long)rawCache;
            var promptI = (long)prompt;
            var cacheI = (long)cache;
            var outI = (long)completion;
            input = Math.Max(0, promptI - cacheI);
            cached = Math.Min(promptI, cacheI);
            output = outI;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    internal static string ModelFromRow(string? modelInfo, string? recordExtra, string? preferredModelInfo)
    {
        return FirstModel(
                   Obj(modelInfo), "model_key", "modelKey")
               ?? NestedModel(Obj(recordExtra), "modelConfig", "model_config", "key")
               ?? FirstModel(
                   Obj(preferredModelInfo),
                   "model_key", "modelKey", "preferred_model", "preferredModel")
               ?? "qoder-agent";
    }

    internal static string MessageKey(string? id, string? sessionId, long rowId)
    {
        var sid = string.IsNullOrWhiteSpace(sessionId) ? "" : sessionId.Trim();
        var mid = string.IsNullOrWhiteSpace(id) ? "" : id.Trim();
        if (sid.Length > 0 && mid.Length > 0) return sid + "|" + mid;
        if (mid.Length > 0) return mid;
        return "row:" + rowId.ToString(CultureInfo.InvariantCulture);
    }

    private static bool TrySnapshot(QoderQuota.Site site, out string db, out string tmp)
    {
        db = "";
        tmp = "";
        var src = DbPath(site);
        if (!File.Exists(src)) return false;
        tmp = Path.Combine(Path.GetTempPath(), "agenthub-qoder-" + site.CacheNamespace + "-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(tmp);
        try
        {
            db = Path.Combine(tmp, "local.db");
            CopyShared(src, db);
            CopyIfExists(src + "-wal", db + "-wal");
            CopyIfExists(src + "-shm", db + "-shm");
            return true;
        }
        catch
        {
            DeleteSnapshot(tmp);
            tmp = "";
            db = "";
            throw;
        }
    }

    private static void DeleteSnapshot(string? tmp)
    {
        if (string.IsNullOrEmpty(tmp)) return;
        try { Directory.Delete(tmp, recursive: true); }
        catch (IOException) { }
    }

    private static List<UsageRecord> ReadCopied(string db, string tool)
    {
        var cs = new SqliteConnectionStringBuilder
        {
            DataSource = db,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        };
        using var conn = new SqliteConnection(cs.ToString());
        conn.Open();
        try { return ReadUsageRows(conn, tool, withJoins: true); }
        catch (SqliteException)
        {
            return ReadUsageRows(conn, tool, withJoins: false);
        }
    }

    private static List<UsageRecord> ReadUsageRows(SqliteConnection conn, string tool, bool withJoins)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = withJoins ? UsageSql : UsageSqlMessageOnly;
        var list = new List<UsageRecord>();
        var seenRows = new HashSet<long>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var rowId = ReadLong(r, 0);
            if (rowId != 0 && !seenRows.Add(rowId)) continue;

            var tokenInfo = ReadStr(r, 4);
            if (!TryNormalizeTokens(tokenInfo, out var input, out var cached, out var output))
                continue;
            if (input == 0 && cached == 0 && output == 0) continue;

            var ts = ReadMs(r, 6);
            if (ts is null) continue;

            var id = ReadStr(r, 1);
            var session = ReadStr(r, 2);
            var requestKey = MessageKey(id, session, rowId);
            var sessionId = string.IsNullOrWhiteSpace(session) ? requestKey : session.Trim();
            var modelInfo = ReadStr(r, 5);
            var extra = withJoins && r.FieldCount > 7 ? ReadStr(r, 7) : null;
            var preferred = withJoins && r.FieldCount > 8 ? ReadStr(r, 8) : null;
            var project = withJoins && r.FieldCount > 9 ? ProjectPath(ReadStr(r, 9)) : null;
            if (project is null && withJoins && r.FieldCount > 10)
                project = EmptyToNull(ReadStr(r, 10));

            list.Add(new UsageRecord
            {
                Tool = tool,
                SessionId = sessionId,
                RequestKey = requestKey,
                TsUtc = ts.Value,
                InputTokens = input,
                OutputTokens = output,
                CachedInputTokens = cached,
                CacheWriteTokens = 0,
                ReasoningTokens = 0,
                Model = ModelFromRow(modelInfo, extra, preferred),
                Project = project,
            });
        }
        return list;
    }

    private static string? ProjectPath(string? uri)
    {
        if (string.IsNullOrWhiteSpace(uri)) return null;
        var raw = uri.Trim();
        if (!raw.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
            return raw;
        try
        {
            var path = new Uri(raw).LocalPath;
            return string.IsNullOrWhiteSpace(path) ? null : Uri.UnescapeDataString(path);
        }
        catch (UriFormatException)
        {
            return raw;
        }
    }

    private static JsonElement? Obj(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? FirstModel(JsonElement? el, params string[] names)
    {
        if (el is not { } root || root.ValueKind != JsonValueKind.Object) return null;
        foreach (var name in names)
        {
            if (!root.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.String) continue;
            var s = v.GetString();
            if (!string.IsNullOrWhiteSpace(s)) return s.Trim();
        }
        return null;
    }

    private static string? NestedModel(JsonElement? el, string camel, string snake, string key)
    {
        if (el is not { } root || root.ValueKind != JsonValueKind.Object) return null;
        JsonElement nested;
        if (root.TryGetProperty(camel, out nested) && nested.ValueKind == JsonValueKind.Object)
            return FirstModel(nested, key);
        if (root.TryGetProperty(snake, out nested) && nested.ValueKind == JsonValueKind.Object)
            return FirstModel(nested, key);
        return null;
    }

    private static bool TryNum(JsonElement el, string name, out decimal value)
    {
        value = 0;
        if (!el.TryGetProperty(name, out var v)) return false;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out value)) return true;
        if (v.ValueKind == JsonValueKind.Number)
        {
            value = (decimal)v.GetDouble();
            return true;
        }
        return v.ValueKind == JsonValueKind.String && decimal.TryParse(v.GetString(),
            NumberStyles.Any, CultureInfo.InvariantCulture, out value);
    }

    private static string? ReadStr(SqliteDataReader r, int i)
    {
        if (i >= r.FieldCount || r.IsDBNull(i)) return null;
        var raw = r.GetValue(i);
        return raw is string s ? s : raw?.ToString();
    }

    private static long ReadLong(SqliteDataReader r, int i)
    {
        if (r.IsDBNull(i)) return 0;
        return r.GetFieldType(i) == typeof(long) ? r.GetInt64(i) : Convert.ToInt64(r.GetValue(i));
    }

    private static DateTime? ReadMs(SqliteDataReader r, int i)
    {
        if (r.IsDBNull(i)) return null;
        var v = r.GetValue(i);
        try
        {
            return v switch
            {
                long l => UsageParsers.ParseMs(l),
                int n => UsageParsers.ParseMs(n),
                double d => UsageParsers.ParseMs((long)d),
                decimal m => UsageParsers.ParseMs((long)m),
                string s when long.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var p)
                    => UsageParsers.ParseMs(p),
                _ => UsageParsers.ParseMs(Convert.ToInt64(v, CultureInfo.InvariantCulture)),
            };
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? EmptyToNull(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static void CopyIfExists(string from, string to)
    {
        if (File.Exists(from)) CopyShared(from, to);
    }

    private static void CopyShared(string from, string to)
    {
        using var src = new FileStream(from, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var dst = new FileStream(to, FileMode.Create, FileAccess.Write, FileShare.None);
        src.CopyTo(dst);
    }
}
