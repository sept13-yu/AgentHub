using System.Globalization;
using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace AgentHub.Core.TokenCore;

/// <summary>
/// Qoder / Qoder CN 本机用量：被动读 <c>SharedClientCache/cache/db/local.db</c>
/// 的 assistant <c>token_info</c>。不读 prompt/response 正文，不用 <c>state.vscdb</c>。
/// 口径对齐 TokenTracker <c>normalizeQoderTokens</c>：prompt 已含 cache，拆成净输入 + cached。
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

    public static IReadOnlyList<UsageRecord> ReadInternational() => Read(QoderQuota.International);
    public static IReadOnlyList<UsageRecord> ReadChina() => Read(QoderQuota.China);

    public static IReadOnlyList<UsageRecord> Read(QoderQuota.Site site)
    {
        if (!TrySnapshot(site, out var db, out var tmp)) return [];
        try { return ReadCopied(db, ToolId(site)); }
        finally { DeleteSnapshot(tmp); }
    }

    internal static string ToolId(QoderQuota.Site site) =>
        site.Id == "china" ? "qoder-cn" : "qoder";

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
                cache = rawCache;
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
