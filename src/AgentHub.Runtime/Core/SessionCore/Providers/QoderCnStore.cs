using System.Diagnostics;
using System.IO;
using AgentHub.Core.TokenCore;
using Microsoft.Data.Sqlite;

namespace AgentHub.Core.SessionCore.Providers;

/// <summary>Qoder CN 桌面会话库：%APPDATA%/com.qodercn.app.stable/main.sqlite。
/// 读库拷三件套；路径探测对齐 <see cref="QoderQuota.ChinaDataRoots"/>，不含国际版 Qoder / QoderCN。</summary>
internal static class QoderCnStore
{
    internal static IEnumerable<string> DbCandidates()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in RawCandidates())
        {
            string full;
            try { full = Path.GetFullPath(raw.Trim()); }
            catch (Exception) { continue; }
            if (seen.Add(full)) yield return full;
        }
    }

    private static IEnumerable<string> RawCandidates()
    {
        var env = Environment.GetEnvironmentVariable("QODERCN_SESSION_DB")
            ?? Environment.GetEnvironmentVariable("QODER_CN_SESSION_DB");
        if (!string.IsNullOrWhiteSpace(env))
            yield return env.Trim();
        foreach (var root in QoderQuota.ChinaDataRoots())
            yield return Path.Combine(root, "main.sqlite");
        yield return Path.Combine(QoderQuota.AppSupportRoot(), "com.qodercn.app.stable", "main.sqlite");
        yield return Path.Combine(QoderQuota.AppSupportRoot(), "com.qodercn.app", "main.sqlite");
    }

    /// <summary>第一个实际存在的 main.sqlite。列表/详情用这份；没有则 null。</summary>
    internal static string? DbPath
    {
        get
        {
            foreach (var path in DbCandidates())
                if (File.Exists(path)) return path;
            return null;
        }
    }

    internal static bool DbExists => DbPath is not null;

    internal static string ProjectsRoot => QoderLocal.ChinaProjectsDir;

    /// <summary>拷主文件 + WAL/SHM 到临时目录。调用方用完必须 <see cref="DeleteSnapshot"/>。</summary>
    internal static bool TrySnapshot(out string db, out string tmp) =>
        TrySnapshot(DbPath, out db, out tmp);

    internal static bool TrySnapshot(string? src, out string db, out string tmp) =>
        UsageIo.TrySnapshot(src, "agenthub-qoder-cn-sess-", "main.sqlite", out db, out tmp);

    internal static void DeleteSnapshot(string? tmp) => UsageIo.DeleteSnapshot(tmp);

    internal static string? FindJsonl(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return null;
        var root = ProjectsRoot;
        if (!Directory.Exists(root)) return null;
        var name = sessionId.Trim() + ".jsonl";
        try
        {
            foreach (var file in Directory.EnumerateFiles(root, name, SearchOption.AllDirectories))
                return file;
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return null;
    }

    internal static bool QoderCnRunning()
    {
        try
        {
            foreach (var process in Process.GetProcesses())
            {
                using (process)
                {
                    var n = process.ProcessName;
                    if (n.Contains("qodercn", StringComparison.OrdinalIgnoreCase)) return true;
                    if (n.Contains("qoder-cn", StringComparison.OrdinalIgnoreCase)) return true;
                    if (n.Contains("Qoder CN", StringComparison.OrdinalIgnoreCase)) return true;
                }
            }
        }
        catch (Exception) { }
        return false;
    }

    internal static SqliteConnection OpenRead(string path)
    {
        var cs = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        };
        var conn = new SqliteConnection(cs.ToString());
        conn.Open();
        using var pragma = conn.CreateCommand();
        pragma.CommandText = "PRAGMA query_only=ON";
        pragma.ExecuteNonQuery();
        return conn;
    }

    internal static SqliteConnection OpenWrite(string path)
    {
        var cs = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        };
        var conn = new SqliteConnection(cs.ToString());
        conn.Open();
        using var busy = conn.CreateCommand();
        busy.CommandText = "PRAGMA busy_timeout=3000";
        busy.ExecuteNonQuery();
        return conn;
    }

    internal static bool TableExists(SqliteConnection conn, string table)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name=$n LIMIT 1";
        cmd.Parameters.AddWithValue("$n", table);
        return cmd.ExecuteScalar() is not null;
    }

    internal static HashSet<string> TableColumns(SqliteConnection conn, string table)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info(\"{table}\")";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            if (!r.IsDBNull(1)) set.Add(r.GetString(1));
        }
        return set;
    }

}
