using System.IO;
using System.Net.Http;
using Microsoft.Data.Sqlite;
using AgentHub.Core.ProxyCore;
using AgentHub.Core.SessionCore.Providers;

namespace AgentHub.Core.TokenCore;

/// <summary>TokenCore：扫描各家入库 tokens.db，换档只查本地聚合。</summary>
public sealed class TokenService
{
    private readonly AgentHubConfig _config;
    private readonly Action<string>? _log;
    private readonly object _scanGate = new();
    private string? _cursorCsvError;

    private const string CursorCsvUrl = "https://cursor.com/api/dashboard/export-usage-events-csv?strategy=tokens";
    private static readonly HttpClient CursorHttp = new(new HttpClientHandler
    {
        UseProxy = true,
        AllowAutoRedirect = false,
    }) { Timeout = TimeSpan.FromSeconds(30) };

    public TokenService(AgentHubConfig config, Action<string>? log = null)
    {
        _config = config;
        _log = log;
    }

    // ------------------------------------------------------------------
    // 扫描
    // ------------------------------------------------------------------

    /// <summary>本地源全量入库（codex/workbuddy/dsh/zcode，单事务）。不碰网络，亚秒级。
    /// 主键冲突时更新用量列；仅当旧 model 为 unknown
    /// 且新解析出真名时回填（WorkBuddy 曾误读根级 model，存量全是 unknown）。</summary>
    public ScanAllResult ScanAllLocal()
    {
        lock (_scanGate)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var sources = new Dictionary<string, SourceScanStat>(StringComparer.Ordinal);

            using (var conn = Open())
            {
                InitSchema(conn);
                using var tx = conn.BeginTransaction();

                SourceScanStat Ingest(string tool, string file, Func<IEnumerable<UsageRecord>> parse)
                {
                    try
                    {
                        var n = 0;
                        foreach (var rec in parse())
                            n += InsertRecord(conn, tx, rec);
                        return new SourceScanStat(1, n, 0);
                    }
                    catch (Exception ex)
                    {
                        _log?.Invoke($"[tokencore] {tool} 读失败 {file} {ex.GetType().Name}: {ex.Message}");
                        return new SourceScanStat(1, 0, 1);
                    }
                }

                static SourceScanStat Sum(IEnumerable<SourceScanStat> parts)
                {
                    int files = 0, inserted = 0, skipped = 0;
                    foreach (var p in parts)
                    {
                        files += p.Files;
                        inserted += p.Inserted;
                        skipped += p.Skipped;
                    }
                    return new SourceScanStat(files, inserted, skipped);
                }

                sources["codex"] = Sum(EnumerateCodexSessions()
                    .Select(x => Ingest("codex", x.File, () => UsageParsers.ParseCodex(x.File, x.Id))));
                sources["workbuddy"] = Sum(EnumerateWorkBuddySessions()
                    .Select(x => Ingest("workbuddy", x.File, () => UsageParsers.ParseWorkBuddy(x.File, x.Id, x.IsSub))));
                sources["dsh"] = Sum(EnumerateDshSessions().Select(x => Ingest("dsh", x.File, () =>
                {
                    var (plain, _, _, _) = DshProvider.DecompressAll(File.ReadAllBytes(x.File));
                    return plain.Length == 0
                        ? Enumerable.Empty<UsageRecord>()
                        : UsageParsers.ParseDsh(plain, x.Id, x.Project);
                })));

                sources["zcode"] = ZcodeLocal.DbExists
                    ? Ingest("zcode", ZcodeLocal.DbPath, () => ZcodeLocal.ReadUsage())
                    : new SourceScanStat(0, 0, 0);

                tx.Commit();
            }

            sw.Stop();
            var inserted = sources.Values.Sum(s => s.Inserted);
            var files = sources.Values.Sum(s => s.Files);
            return new ScanAllResult(inserted, files, sw.Elapsed.TotalSeconds, sources);
        }
    }

    /// <summary>Cursor CSV 拉取入库。网络请求在锁外（拉不通也不挡本地扫描），入库事务在锁内。
    /// 拉取失败记日志并返回 Skipped=1 的统计。</summary>
    public SourceScanStat ScanCursorCsv()
    {
        var (recs, err) = FetchCursorCsv();
        lock (_scanGate)
        {
            _cursorCsvError = err;
            if (recs is null)
            {
                _log?.Invoke($"[tokencore] cursor 读失败 csv {(_cursorCsvError ?? "未知错误")}");
                return new SourceScanStat(1, 0, 1);
            }
            var n = 0;
            using (var conn = Open())
            {
                InitSchema(conn);
                using var tx = conn.BeginTransaction();
                foreach (var rec in recs)
                    n += InsertRecord(conn, tx, rec);
                tx.Commit();
            }
            return new SourceScanStat(1, n, 0);
        }
    }

    /// <summary>Trae 官网按会话用量入库。网络在锁外；开关关或本机/设置都没有登录态则跳过。</summary>
    public SourceScanStat ScanTraeUsage()
    {
        if (!_config.Dashboard.ShowQuotaTrae)
            return new SourceScanStat(0, 0, 0);
        if (!TraeAuth.HasCredentials(_config))
            return new SourceScanStat(0, 0, 0);

        var (recs, err) = TraeUsage.Fetch(_config);
        lock (_scanGate)
        {
            if (recs is null)
            {
                _log?.Invoke($"[tokencore] trae 读失败 usage {err ?? "未知错误"}");
                return new SourceScanStat(1, 0, 1);
            }
            var n = 0;
            using (var conn = Open())
            {
                InitSchema(conn);
                using var tx = conn.BeginTransaction();
                foreach (var rec in recs)
                    n += InsertRecord(conn, tx, rec);
                tx.Commit();
            }
            return new SourceScanStat(1, n, 0);
        }
    }

    /// <summary>全量 = 本地 + Cursor CSV + Trae 用量。仅作兜底（正常路径走 ScanScheduler 分阶段，
    /// 本地秒回、网络尾巴后台跑）。</summary>
    public ScanAllResult ScanAll()
    {
        var result = ScanAllLocal();
        var cursor = ScanCursorCsv();
        var trae = ScanTraeUsage();
        var sources = new Dictionary<string, SourceScanStat>(result.Sources, StringComparer.Ordinal)
        {
            ["cursor"] = cursor,
            ["trae"] = trae,
        };
        return result with
        {
            Sources = sources,
            Inserted = result.Inserted + cursor.Inserted + trae.Inserted,
            Files = result.Files + cursor.Files + trae.Files,
        };
    }

    private static int InsertRecord(SqliteConnection conn, SqliteTransaction tx, UsageRecord r)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO usage_records
            (tool, session_id, request_key, ts_utc, local_date, input_tokens, output_tokens,
             cached_input_tokens, cache_write_tokens, reasoning_tokens, is_subagent, model, project)
            VALUES ($tool, $sid, $rk, $ts, $ld, $in, $out, $cached, $cw, $reason, $sub, $model, $project)
            ON CONFLICT(tool, session_id, request_key) DO UPDATE SET
              input_tokens = excluded.input_tokens,
              output_tokens = excluded.output_tokens,
              cached_input_tokens = excluded.cached_input_tokens,
              cache_write_tokens = excluded.cache_write_tokens,
              ts_utc = excluded.ts_utc,
              local_date = excluded.local_date,
              model = CASE
                WHEN usage_records.model = 'unknown' AND excluded.model <> 'unknown'
                THEN excluded.model ELSE usage_records.model END
            """;
        cmd.Parameters.AddWithValue("$tool", r.Tool);
        cmd.Parameters.AddWithValue("$sid", r.SessionId);
        cmd.Parameters.AddWithValue("$rk", r.RequestKey);
        cmd.Parameters.AddWithValue("$ts", r.TsUtcIso);
        cmd.Parameters.AddWithValue("$ld", r.TsUtc.ToLocalTime().ToString("yyyy-MM-dd"));   // 本机时区自然日
        cmd.Parameters.AddWithValue("$in", r.InputTokens);
        cmd.Parameters.AddWithValue("$out", r.OutputTokens);
        cmd.Parameters.AddWithValue("$cached", r.CachedInputTokens);
        cmd.Parameters.AddWithValue("$cw", r.CacheWriteTokens);
        cmd.Parameters.AddWithValue("$reason", r.ReasoningTokens);
        cmd.Parameters.AddWithValue("$sub", r.IsSubagent ? 1 : 0);
        cmd.Parameters.AddWithValue("$model", r.Model);
        cmd.Parameters.AddWithValue("$project", (object?)r.Project ?? DBNull.Value);
        return cmd.ExecuteNonQuery();
    }

    // ------------------------------------------------------------------
    // /usage 查询
    // ------------------------------------------------------------------

    public Dictionary<string, object?> Usage(string range)
    {
        range = UsageRange.Normalize(range);
        var today = DateTime.Today;
        var (from, to) = UsageRange.Current(range, today);
        var (prevFrom, prevTo) = UsageRange.Previous(range, today);

        using var conn = Open();
        InitSchema(conn);

        var filter = UsageToolFilter();
        var rows = ReadModelRows(conn, from, to, filter);
        var byAgent = BuildByAgent(rows);
        var total = byAgent.Sum(a => (long)a["tokens"]!);
        var prev = SumBilled(conn, prevFrom, prevTo, filter);
        var (cost, partial, currency) = UsageCost.Estimate(
            rows.Select(r => (r.Model, r.Input, r.Output)),
            PriceSyncService.Resolve(_config.Dashboard.PriceOverrides),
            _config.Dashboard.CostEstimate,
            _config.Dashboard.CostCurrency,
            _config.Dashboard.FxFallbackRate);

        return new Dictionary<string, object?>
        {
            ["range"] = new Dictionary<string, object?>
            {
                ["key"] = range,
                ["from"] = from.ToString("yyyy-MM-dd"),
                ["to"] = to.ToString("yyyy-MM-dd"),
            },
            ["total"] = new Dictionary<string, object?>
            {
                ["tokens"] = total,
                ["prevTokens"] = prev > 0 ? prev : null,
                ["cost"] = cost,
                ["costPartial"] = partial,
                ["currency"] = currency,
            },
            ["byAgent"] = byAgent,
            ["days"] = ReadDailyDays(conn, HeatmapStart(today), today, filter),
        };
    }

    private const int HeatmapWeeks = 26;

    private static DateTime HeatmapStart(DateTime today)
    {
        var monday = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
        return monday.AddDays(-(HeatmapWeeks - 1) * 7);
    }

    private static List<Dictionary<string, object?>> ReadDailyDays(
        SqliteConnection conn, DateTime from, DateTime to, string filter)
    {
        var days = new List<Dictionary<string, object?>>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT local_date,
                   COALESCE(SUM(input_tokens + output_tokens + cached_input_tokens + cache_write_tokens), 0)
            FROM usage_records
            WHERE local_date BETWEEN $from AND $to {filter}
            GROUP BY local_date
            HAVING SUM(input_tokens + output_tokens + cached_input_tokens + cache_write_tokens) > 0
            ORDER BY local_date
            """;
        cmd.Parameters.AddWithValue("$from", from.ToString("yyyy-MM-dd"));
        cmd.Parameters.AddWithValue("$to", to.ToString("yyyy-MM-dd"));
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            days.Add(new Dictionary<string, object?>
            {
                ["date"] = r.GetString(0),
                ["tokens"] = r.GetInt64(1),
            });
        }
        return days;
    }

    private string UsageToolFilter()
    {
        var dash = _config.Dashboard;
        var hide = new List<string>();
        if (!dash.ShowAgentDsh) hide.Add("dsh");
        if (!dash.ShowQuotaTrae) hide.Add("trae");
        if (!dash.ShowQuotaWorkBuddy) hide.Add("workbuddy");
        if (!dash.ShowQuotaZcode) hide.Add("zcode");
        if (!dash.ShowQuotaCursor) hide.Add("cursor");
        if (!dash.ShowQuotaCodex) hide.Add("codex");
        if (hide.Count == 0) return "";
        return "AND tool NOT IN (" + string.Join(", ", hide.Select(t => "'" + t + "'")) + ")";
    }

    private sealed record ModelRow(string Tool, string Model, long Input, long Output, long Cached, long CacheWrite)
    {
        public long Tokens => Input + Output + Cached + CacheWrite;
    }

    private static List<ModelRow> ReadModelRows(SqliteConnection conn, DateTime from, DateTime to, string filter)
    {
        var rows = new List<ModelRow>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT tool, model, SUM(input_tokens), SUM(output_tokens),
                   SUM(cached_input_tokens), SUM(cache_write_tokens)
            FROM usage_records
            WHERE local_date BETWEEN $from AND $to {filter}
            GROUP BY tool, model
            """;
        cmd.Parameters.AddWithValue("$from", from.ToString("yyyy-MM-dd"));
        cmd.Parameters.AddWithValue("$to", to.ToString("yyyy-MM-dd"));
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            rows.Add(new ModelRow(
                r.GetString(0),
                r.IsDBNull(1) || string.IsNullOrWhiteSpace(r.GetString(1)) ? "unknown" : r.GetString(1),
                r.GetInt64(2), r.GetInt64(3), r.GetInt64(4), r.GetInt64(5)));
        }
        return rows;
    }

    private static long SumBilled(SqliteConnection conn, DateTime from, DateTime to, string filter)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT COALESCE(SUM(input_tokens + output_tokens + cached_input_tokens + cache_write_tokens), 0)
            FROM usage_records
            WHERE local_date BETWEEN $from AND $to {filter}
            """;
        cmd.Parameters.AddWithValue("$from", from.ToString("yyyy-MM-dd"));
        cmd.Parameters.AddWithValue("$to", to.ToString("yyyy-MM-dd"));
        return (long)cmd.ExecuteScalar()!;
    }

    private static List<Dictionary<string, object?>> BuildByAgent(List<ModelRow> rows)
    {
        var byTool = new Dictionary<string, List<ModelRow>>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (!byTool.TryGetValue(row.Tool, out var list))
            {
                list = [];
                byTool[row.Tool] = list;
            }
            list.Add(row);
        }

        var agents = new List<Dictionary<string, object?>>();
        foreach (var (tool, list) in byTool)
        {
            var tokens = list.Sum(x => x.Tokens);
            if (tokens <= 0) continue;
            var models = list
                .Select(x => new Dictionary<string, object?> { ["name"] = x.Model, ["tokens"] = x.Tokens })
                .OrderByDescending(m => (long)m["tokens"]!)
                .ToList();
            agents.Add(new Dictionary<string, object?>
            {
                ["id"] = tool,
                ["tokens"] = tokens,
                ["models"] = models,
            });
        }
        agents.Sort((a, b) => ((long)b["tokens"]!).CompareTo((long)a["tokens"]!));
        return agents;
    }

    private (List<UsageRecord>? Records, string? Error) FetchCursorCsv()
    {
        try
        {
            return FetchCursorCsvAsync().ConfigureAwait(false).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            var msg = ex is AggregateException agg && agg.InnerException is not null
                ? agg.InnerException.Message : ex.Message;
            return (null, "Cursor CSV 请求失败：" + msg);
        }
    }

    private static async Task<(List<UsageRecord>? Records, string? Error)> FetchCursorCsvAsync()
    {
        if (!CursorAuth.TryCookie(out var cookie, out var authErr))
            return (null, authErr);

        async Task<HttpResponseMessage> SendAsync(string url)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("Cookie", cookie);
            req.Headers.TryAddWithoutValidation("Referer", "https://cursor.com/dashboard?tab=usage");
            req.Headers.TryAddWithoutValidation("User-Agent", CursorAuth.UserAgent);
            req.Headers.TryAddWithoutValidation("Accept", "text/csv,text/plain,*/*");
            return await CursorHttp.SendAsync(req).ConfigureAwait(false);
        }

        using var resp0 = await SendAsync(CursorCsvUrl).ConfigureAwait(false);
        HttpResponseMessage resp = resp0;
        HttpResponseMessage? redirected = null;
        if ((int)resp0.StatusCode is >= 300 and < 400 && resp0.Headers.Location is { } loc)
        {
            var next = loc.IsAbsoluteUri ? loc : new Uri(new Uri(CursorCsvUrl), loc);
            redirected = await SendAsync(next.ToString()).ConfigureAwait(false);
            resp = redirected;
        }

        try
        {
            var code = (int)resp.StatusCode;
            if (code is 401 or 403)
                return (null, "Cursor 登录态过期（401/403）：打开 Cursor 重新登录后重扫");
            if (!resp.IsSuccessStatusCode)
                return (null, $"Cursor CSV 接口返回 HTTP {code}");

            var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(body))
                return (new List<UsageRecord>(), null);
            if (body.TrimStart().StartsWith('<'))
                return (null, "Cursor CSV 返回 HTML（登录墙或接口改版）");

            try
            {
                return (UsageParsers.ParseCursorCsv(body).ToList(), null);
            }
            catch (InvalidDataException ex)
            {
                return (null, "Cursor CSV " + ex.Message);
            }
        }
        finally
        {
            redirected?.Dispose();
        }
    }

    // ------------------------------------------------------------------
    // 宠物快照 / 枚举源文件
    // ------------------------------------------------------------------

    /// <summary>桌面宠物用的轻量快照：今日/7 日 token、连续活跃天数、今日会话数、今日模型 Top。
    /// 不加扫描锁：WAL 下读不阻塞写，首扫进行中也能立即出数。</summary>
    public PetSnapshot GetPetSnapshot()
    {
        var today = DateTime.Today;
        var todayKey = today.ToString("yyyy-MM-dd");
        var from7 = today.AddDays(-6).ToString("yyyy-MM-dd");
        using var conn = Open();
        InitSchema(conn);

        long todayTokens = 0, last7d = 0;
        int conversations = 0;
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT
                  COALESCE(SUM(CASE WHEN local_date = $today THEN input_tokens + output_tokens
                    + CASE WHEN tool = 'cursor' THEN cached_input_tokens + cache_write_tokens ELSE 0 END END), 0),
                  COALESCE(SUM(CASE WHEN local_date BETWEEN $from7 AND $today THEN input_tokens + output_tokens
                    + CASE WHEN tool = 'cursor' THEN cached_input_tokens + cache_write_tokens ELSE 0 END END), 0),
                  COUNT(DISTINCT CASE WHEN local_date = $today THEN session_id END)
                FROM usage_records
                """;
            cmd.Parameters.AddWithValue("$today", todayKey);
            cmd.Parameters.AddWithValue("$from7", from7);
            using var r = cmd.ExecuteReader();
            if (r.Read())
            {
                todayTokens = r.GetInt64(0);
                last7d = r.GetInt64(1);
                conversations = r.IsDBNull(2) ? 0 : Convert.ToInt32(r.GetInt64(2));
            }
        }

        var top = new List<PetTopModel>();
        long topSum = 0;
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT model, SUM(input_tokens + output_tokens
                    + CASE WHEN tool = 'cursor' THEN cached_input_tokens + cache_write_tokens ELSE 0 END) AS t
                FROM usage_records WHERE local_date = $today
                GROUP BY model ORDER BY t DESC LIMIT 5
                """;
            cmd.Parameters.AddWithValue("$today", todayKey);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var t = r.GetInt64(1);
                topSum += t;
                top.Add(new PetTopModel(r.GetString(0) ?? "unknown", t, 0));
            }
        }
        if (topSum > 0)
        {
            for (int i = 0; i < top.Count; i++)
                top[i] = top[i] with { Percent = Math.Round(100.0 * top[i].Tokens / topSum, 1) };
        }

        int streak = 0;
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT DISTINCT local_date FROM usage_records
                WHERE input_tokens + output_tokens
                    + CASE WHEN tool = 'cursor' THEN cached_input_tokens + cache_write_tokens ELSE 0 END > 0
                ORDER BY local_date DESC
                """;
            using var r = cmd.ExecuteReader();
            var expected = today;
            while (r.Read())
            {
                if (!DateTime.TryParse(r.GetString(0), out var d)) continue;
                d = d.Date;
                if (d == expected.Date) { streak++; expected = expected.AddDays(-1); }
                else if (d < expected.Date) break;
            }
        }

        return new PetSnapshot(todayTokens, last7d, streak, conversations, top);
    }

    private static IEnumerable<(string File, string Id)> EnumerateCodexSessions()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var root in new[] { Path.Combine(home, ".codex", "sessions"), Path.Combine(home, ".codex", "archived_sessions") })
        {
            if (!Directory.Exists(root)) continue;
            foreach (var f in Directory.EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories))
            {
                var id = CodexProvider.SessionIdFromName(Path.GetFileName(f));
                if (id is not null) yield return (f, id);
            }
        }
    }

    private static IEnumerable<(string File, string Id, bool IsSub)> EnumerateWorkBuddySessions()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".workbuddy", "projects");
        if (!Directory.Exists(root)) yield break;
        foreach (var f in Directory.EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories))
        {
            if (f.Contains($"{Path.DirectorySeparatorChar}tool-results{Path.DirectorySeparatorChar}")) continue;
            var id = Path.GetFileNameWithoutExtension(f);
            bool isSub = f.Contains($"{Path.DirectorySeparatorChar}subagents{Path.DirectorySeparatorChar}");
            if (isSub)
            {
                // 子代理挂到父目录 uuid（probe _session_id 语义）
                var dir = Path.GetDirectoryName(Path.GetDirectoryName(f))!;
                var parent = Path.GetFileName(dir);
                if (Guid.TryParse(parent, out _)) id = parent;
            }
            else if (!Guid.TryParse(id, out _)) continue;   // 心跳等非会话文件
            yield return (f, id, isSub);
        }
    }

    private static IEnumerable<(string File, string Id, string? Project)> EnumerateDshSessions()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh", "sessions");
        if (!Directory.Exists(root)) yield break;
        foreach (var dir in Directory.EnumerateDirectories(root))
            foreach (var sessionDir in Directory.EnumerateDirectories(dir))
            {
                var file = Path.Combine(sessionDir, "session.jsonl.zstd");
                if (!File.Exists(file)) continue;
                yield return (file, DshProvider.NormalizeSessionId(Path.GetFileName(sessionDir)), null);
            }
    }

    // ------------------------------------------------------------------
    // db
    // ------------------------------------------------------------------

    private static SqliteConnection Open()
    {
        var cs = new SqliteConnectionStringBuilder
        {
            DataSource = AgentHubConfig.TokensDbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        };
        var conn = new SqliteConnection(cs.ToString());
        conn.Open();
        return conn;
    }

    private static void InitSchema(SqliteConnection conn)
    {
        // WAL：首扫写事务进行中，读接口（仪表盘/宠物快照）不被 SQLite 互斥阻塞
        try
        {
            using var pragma = conn.CreateCommand();
            pragma.CommandText = "PRAGMA journal_mode=WAL;";
            pragma.ExecuteNonQuery();
        }
        catch (SqliteException) { /* 切换瞬间被并发连接占住则维持现状，下次连接再切 */ }
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS usage_records (
                tool TEXT NOT NULL,
                session_id TEXT NOT NULL,
                request_key TEXT NOT NULL,
                ts_utc TEXT NOT NULL,
                local_date TEXT NOT NULL,
                input_tokens INTEGER NOT NULL,
                output_tokens INTEGER NOT NULL,
                cached_input_tokens INTEGER NOT NULL DEFAULT 0,
                cache_write_tokens INTEGER NOT NULL DEFAULT 0,
                reasoning_tokens INTEGER NOT NULL DEFAULT 0,
                is_subagent INTEGER NOT NULL DEFAULT 0,
                model TEXT NOT NULL DEFAULT 'unknown',
                project TEXT,
                PRIMARY KEY (tool, session_id, request_key)
            );
            CREATE INDEX IF NOT EXISTS idx_usage_date_tool ON usage_records(local_date, tool);
            CREATE INDEX IF NOT EXISTS idx_usage_date_model ON usage_records(local_date, tool, model);
            """;
        cmd.ExecuteNonQuery();
    }
}

public sealed record SourceScanStat(int Files, int Inserted, int Skipped);

public sealed record ScanAllResult(
    int Inserted,
    int Files,
    double Seconds,
    IReadOnlyDictionary<string, SourceScanStat> Sources);

public sealed record PetTopModel(string Name, long Tokens, double Percent);

public sealed record PetSnapshot(
    long TodayTokens,
    long Last7dTokens,
    int StreakDays,
    int Conversations,
    IReadOnlyList<PetTopModel> TopModels);
