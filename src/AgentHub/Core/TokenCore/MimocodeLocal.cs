using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace AgentHub.Core.TokenCore;

/// <summary>MiMo Code 本机库：会话/用量共用。读库前拷三件套，避开宿主占用 WAL。</summary>
internal static class MimocodeLocal
{
    private static string ShareHome => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".local", "share", "mimocode");

    public static string DbPath => Path.Combine(ShareHome, "mimocode.db");
    public static bool DbExists => File.Exists(DbPath);

    public static bool MimocodeRunning()
    {
        try
        {
            foreach (var process in Process.GetProcesses())
            {
                using (process)
                {
                    // 桌面进程名："Xiaomi MiMo"
                    if (process.ProcessName.Contains("Xiaomi MiMo", StringComparison.OrdinalIgnoreCase)
                        || process.ProcessName.Contains("XiaomiMiMo", StringComparison.OrdinalIgnoreCase)
                        || process.ProcessName.Contains("mimocode", StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
        }
        catch (Exception) { }
        return false;
    }

    /// <summary>拷主文件 + WAL/SHM 到临时目录。调用方用完必须 <see cref="DeleteSnapshot"/>。</summary>
    public static bool TrySnapshot(out string db, out string tmp)
    {
        db = "";
        tmp = "";
        if (!DbExists) return false;
        tmp = Path.Combine(Path.GetTempPath(), "agenthub-mimocode-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(tmp);
        try
        {
            db = Path.Combine(tmp, "mimocode.db");
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
            SELECT id, session_id, time_updated, data
            FROM message
            """;
        var list = new List<UsageRecord>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var requestKey = r.IsDBNull(0) ? "" : r.GetString(0);
            if (string.IsNullOrEmpty(requestKey)) continue;
            var sessionId = r.IsDBNull(1) ? requestKey : r.GetString(1);
            var msgUpdated = ReadMs(r, 2);
            var raw = r.IsDBNull(3) ? "" : r.GetString(3);
            if (raw.Length == 0) continue;

            JsonDocument doc;
            try { doc = JsonDocument.Parse(raw); }
            catch (JsonException) { continue; }
            using (doc)
            {
                var root = doc.RootElement;
                if (!root.TryGetProperty("role", out var roleEl)
                    || roleEl.ValueKind != JsonValueKind.String
                    || !string.Equals(roleEl.GetString(), "assistant", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!root.TryGetProperty("tokens", out var tokens) || tokens.ValueKind != JsonValueKind.Object)
                    continue;

                var input = ReadLongProp(tokens, "input");
                var output = ReadLongProp(tokens, "output");
                var reasoning = ReadLongProp(tokens, "reasoning");
                long cacheRead = 0, cacheWrite = 0;
                if (tokens.TryGetProperty("cache", out var cache) && cache.ValueKind == JsonValueKind.Object)
                {
                    cacheRead = ReadLongProp(cache, "read");
                    cacheWrite = ReadLongProp(cache, "write");
                }
                // tokens.input 已是净新增；cache.read/write 单独记账
                if (input == 0 && output == 0 && cacheRead == 0 && cacheWrite == 0 && reasoning == 0)
                    continue;

                DateTime? ts = null;
                if (root.TryGetProperty("time", out var time) && time.ValueKind == JsonValueKind.Object)
                {
                    ts = ReadMsProp(time, "completed") ?? ReadMsProp(time, "created");
                }
                ts ??= msgUpdated;
                if (ts is null) continue;

                var model = "unknown";
                if (root.TryGetProperty("modelID", out var mid) && mid.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(mid.GetString()))
                    model = mid.GetString()!.Trim();

                string? project = null;
                if (root.TryGetProperty("path", out var path) && path.ValueKind == JsonValueKind.Object
                    && path.TryGetProperty("cwd", out var cwd) && cwd.ValueKind == JsonValueKind.String)
                    project = string.IsNullOrWhiteSpace(cwd.GetString()) ? null : cwd.GetString()!.Trim();

                list.Add(new UsageRecord
                {
                    Tool = "mimocode",
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
                });
            }
        }
        return list;
    }

    private static long ReadLongProp(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var v)) return 0;
        return v.ValueKind switch
        {
            JsonValueKind.Number when v.TryGetInt64(out var n) => Math.Max(0, n),
            JsonValueKind.Number => Math.Max(0, (long)v.GetDouble()),
            _ => 0,
        };
    }

    private static DateTime? ReadMsProp(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var v)) return null;
        long ms;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out ms)) { }
        else if (v.ValueKind == JsonValueKind.Number) ms = (long)v.GetDouble();
        else return null;
        return UsageParsers.ParseMs(ms);
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
