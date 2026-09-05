using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace AgentHub.Core.TokenCore;

/// <summary>ZCode 本机用量与 Coding Plan Key。读库前拷三件套，避开宿主占用 WAL 时主文件为空。</summary>
internal static class ZcodeLocal
{
    private static string Home => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".zcode");

    public static string DbPath => Path.Combine(Home, "cli", "db", "db.sqlite");
    public static string ConfigPath => Path.Combine(Home, "v2", "config.json");
    public static string CachePath => Path.Combine(Home, "v2", "coding-plan-cache.json");

    public static bool DbExists => File.Exists(DbPath);

    /// <summary>拷主文件 + WAL/SHM 到临时目录。调用方用完必须 <see cref="DeleteSnapshot"/>。</summary>
    public static bool TrySnapshot(out string db, out string tmp)
    {
        db = "";
        tmp = "";
        if (!DbExists) return false;
        tmp = Path.Combine(Path.GetTempPath(), "agenthub-zcode-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(tmp);
        try
        {
            db = Path.Combine(tmp, "db.sqlite");
            CopyShared(DbPath, db);
            CopyIfExists(DbPath + "-wal", db + "-wal");
            CopyIfExists(DbPath + "-shm", db + "-shm");
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

    public static void DeleteSnapshot(string? tmp)
    {
        if (string.IsNullOrEmpty(tmp)) return;
        try { Directory.Delete(tmp, recursive: true); }
        catch (IOException) { }
    }

    public static IReadOnlyList<UsageRecord> ReadUsage()
    {
        if (!TrySnapshot(out var db, out var tmp)) return [];
        try { return ReadCopied(db); }
        finally { DeleteSnapshot(tmp); }
    }

    public static string? ReadCodingPlanKey()
    {
        if (!File.Exists(ConfigPath)) return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(ConfigPath));
            return FindCodingPlanKey(doc.RootElement);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    public static bool CodingPlanAvailable()
    {
        if (!File.Exists(CachePath)) return true;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(CachePath));
            return !IsUnavailable(doc.RootElement, "builtin:bigmodel-coding-plan");
        }
        catch (JsonException)
        {
            return true;
        }
        catch (IOException)
        {
            return true;
        }
    }

    private static List<UsageRecord> ReadCopied(string db)
    {
        var cs = new SqliteConnectionStringBuilder
        {
            DataSource = db,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        };
        using var conn = new SqliteConnection(cs.ToString());
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT logical_request_id, session_id, model_id, started_at, completed_at,
                   input_tokens, output_tokens, cache_read_input_tokens, cache_creation_input_tokens
            FROM model_usage
            WHERE status = 'completed'
            """;
        var list = new List<UsageRecord>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var requestKey = r.IsDBNull(0) ? "" : r.GetString(0);
            if (string.IsNullOrEmpty(requestKey)) continue;
            var session = r.IsDBNull(1) ? requestKey : r.GetString(1);
            var model = r.IsDBNull(2) || string.IsNullOrWhiteSpace(r.GetString(2))
                ? "unknown" : r.GetString(2);
            var started = ReadMs(r, 3);
            var completed = ReadMs(r, 4);
            var ts = completed ?? started;
            if (ts is null) continue;

            var input = ReadLong(r, 5);
            var output = ReadLong(r, 6);
            var cacheRead = ReadLong(r, 7);
            var cacheWrite = ReadLong(r, 8);
            var netIn = cacheRead > 0 && cacheRead <= input ? input - cacheRead : input;

            list.Add(new UsageRecord
            {
                Tool = "zcode",
                SessionId = session,
                RequestKey = requestKey,
                TsUtc = ts.Value,
                InputTokens = netIn,
                OutputTokens = output,
                CachedInputTokens = cacheRead,
                CacheWriteTokens = cacheWrite,
                Model = model,
            });
        }
        return list;
    }

    private static string? FindCodingPlanKey(JsonElement root)
    {
        foreach (var name in new[] { "provider", "providers" })
        {
            if (!root.TryGetProperty(name, out var prov)) continue;
            if (prov.ValueKind == JsonValueKind.Object
                && prov.TryGetProperty("builtin:bigmodel-coding-plan", out var one))
            {
                var key = KeyFromProvider(one);
                if (!string.IsNullOrEmpty(key)) return key;
            }
            if (prov.ValueKind == JsonValueKind.Array)
            {
                foreach (var p in prov.EnumerateArray())
                {
                    var id = Str(p, "id") ?? Str(p, "providerId") ?? Str(p, "name");
                    if (id != "builtin:bigmodel-coding-plan") continue;
                    var key = KeyFromProvider(p);
                    if (!string.IsNullOrEmpty(key)) return key;
                }
            }
        }
        return null;
    }

    private static string? KeyFromProvider(JsonElement p)
    {
        if (p.TryGetProperty("options", out var opt) && opt.ValueKind == JsonValueKind.Object)
        {
            var nested = Str(opt, "apiKey") ?? Str(opt, "api_key");
            if (!string.IsNullOrEmpty(nested)) return nested;
        }
        return Str(p, "apiKey") ?? Str(p, "api_key");
    }

    private static bool IsUnavailable(JsonElement el, string id)
    {
        if (el.ValueKind == JsonValueKind.Object)
        {
            if (el.TryGetProperty(id, out var node))
                return StatusUnavailable(node);
            if (el.TryGetProperty("providers", out var providers) && providers.ValueKind == JsonValueKind.Object
                && providers.TryGetProperty(id, out var p))
                return StatusUnavailable(p);
            foreach (var prop in el.EnumerateObject())
            {
                if (StatusUnavailable(prop.Value) && (prop.Name.Contains("coding-plan", StringComparison.OrdinalIgnoreCase)
                    || prop.Name == id))
                    return true;
            }
        }
        return false;
    }

    private static bool StatusUnavailable(JsonElement node)
    {
        if (node.ValueKind == JsonValueKind.String)
            return string.Equals(node.GetString(), "unavailable", StringComparison.OrdinalIgnoreCase);
        if (node.ValueKind != JsonValueKind.Object) return false;
        var status = Str(node, "status") ?? Str(node, "state") ?? Str(node, "availability");
        return string.Equals(status, "unavailable", StringComparison.OrdinalIgnoreCase);
    }

    private static string? Str(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v)
        && v.ValueKind == JsonValueKind.String
            ? EmptyToNull(v.GetString()) : null;

    private static string? EmptyToNull(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static long ReadLong(SqliteDataReader r, int i)
    {
        if (r.IsDBNull(i)) return 0;
        return r.GetFieldType(i) == typeof(long) ? Math.Max(0, r.GetInt64(i)) : Math.Max(0, Convert.ToInt64(r.GetValue(i)));
    }

    private static DateTime? ReadMs(SqliteDataReader r, int i)
    {
        if (r.IsDBNull(i)) return null;
        var ms = r.GetFieldType(i) == typeof(long) ? r.GetInt64(i) : Convert.ToInt64(r.GetValue(i));
        return UsageParsers.ParseMs(ms);
    }

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
