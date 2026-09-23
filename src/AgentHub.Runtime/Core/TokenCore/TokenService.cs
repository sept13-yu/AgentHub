using System.IO;
using System.Net.Http;
using Microsoft.Data.Sqlite;
using AgentHub.Core.ProxyCore;

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

    /// <summary>本地源全量入库（<see cref="UsageSourceRegistry"/>，单事务）。不碰网络。
    /// 主键冲突时更新用量列；新解析出名非 unknown 时回填模型。</summary>
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

                foreach (var source in UsageSourceRegistry.Local)
                {
                    try
                    {
                        var units = source.Units().ToList();
                        sources[source.Id] = units.Count == 0
                            ? new SourceScanStat(0, 0, 0)
                            : Sum(units.Select(u => Ingest(source.Id, u.Label, u.Read)));
                    }
                    catch (Exception ex)
                    {
                        _log?.Invoke($"[tokencore] {source.Id} 枚举失败 {ex.GetType().Name}: {ex.Message}");
                        sources[source.Id] = new SourceScanStat(1, 0, 1);
                    }
                }

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
                // CSV 当日全量替换：清掉同 day 旧主键（含历史 dateRaw 无 model 格式），避免双计
                var days = new HashSet<string>(StringComparer.Ordinal);
                foreach (var rec in recs)
                    days.Add(rec.SessionId);
                foreach (var day in days)
                {
                    using var del = conn.CreateCommand();
                    del.Transaction = tx;
                    del.CommandText = "DELETE FROM usage_records WHERE tool = 'cursor' AND session_id = $sid";
                    del.Parameters.AddWithValue("$sid", day);
                    del.ExecuteNonQuery();
                }
                foreach (var rec in recs)
                    n += InsertRecord(conn, tx, rec);
                tx.Commit();
            }
            return new SourceScanStat(1, n, 0);
        }
    }

    /// <summary>Trae 官网按会话用量入库。网络在锁外；没有登录态则跳过。</summary>
    public SourceScanStat ScanTraeUsage()
    {
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
             cached_input_tokens, cache_write_tokens, reasoning_tokens, reported_cost_usd,
             is_subagent, model, project)
            VALUES ($tool, $sid, $rk, $ts, $ld, $in, $out, $cached, $cw, $reason, $cost,
                    $sub, $model, $project)
            ON CONFLICT(tool, session_id, request_key) DO UPDATE SET
              input_tokens = excluded.input_tokens,
              output_tokens = excluded.output_tokens,
              cached_input_tokens = excluded.cached_input_tokens,
              cache_write_tokens = excluded.cache_write_tokens,
              reasoning_tokens = excluded.reasoning_tokens,
              reported_cost_usd = excluded.reported_cost_usd,
              is_subagent = excluded.is_subagent,
              ts_utc = excluded.ts_utc,
              local_date = excluded.local_date,
              project = excluded.project,
              model = CASE
                WHEN excluded.model <> 'unknown' THEN excluded.model
                ELSE usage_records.model END
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
        cmd.Parameters.AddWithValue("$cost", (object?)r.ReportedCostUsd ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$sub", r.IsSubagent ? 1 : 0);
        cmd.Parameters.AddWithValue("$model", r.Model);
        cmd.Parameters.AddWithValue("$project", (object?)r.Project ?? DBNull.Value);
        return cmd.ExecuteNonQuery();
    }

    /// <summary>互不重叠列合计：input + output + cache 读/写 + reasoning（对齐 TokenTracker total_tokens）。</summary>
    private const string BilledExpr =
        "input_tokens + output_tokens + cached_input_tokens + cache_write_tokens + reasoning_tokens";

    public Dictionary<string, object?> Usage(string range)
    {
        range = UsageRange.Normalize(range);
        var today = DateTime.Today;
        var (from, to) = UsageRange.Current(range, today);
        var (prevFrom, prevTo) = UsageRange.Previous(range, today);

        using var conn = Open();
        InitSchema(conn);

        var rows = ReadModelRows(conn, from, to);
        var prices = PriceSyncService.Resolve(_config.Dashboard.PriceOverrides);
        IReadOnlyDictionary<string, (double Input, double Output, double? CacheRead, double? CacheWrite, bool IsCny)>? priceTable = null;
        if (_config.Dashboard.CostEstimate)
        {
            var built = UsageCost.BuildPriceTable(prices, _config.Dashboard.CostCurrency);
            if (built.Count > 0) priceTable = built;
        }
        var byAgent = BuildByAgent(rows, priceTable);
        var total = byAgent.Sum(a => (long)a["tokens"]!);
        var prev = SumBilled(conn, prevFrom, prevTo);
        var fx = _config.Dashboard.FxFallbackRate;
        if (_config.Dashboard.CostEstimate)
        {
            fx = FxService.UsdToCny(_config.Dashboard.FxFallbackRate);
            if (fx <= 0) fx = _config.Dashboard.FxFallbackRate > 0 ? _config.Dashboard.FxFallbackRate : 7;
        }
        var (cost, partial, currency) = UsageCost.Estimate(
            rows.Select(r => (r.Model, r.Input, r.Output, r.Cached, r.CacheWrite, r.Reasoning, r.ReportedUsd)),
            prices,
            _config.Dashboard.CostEstimate,
            _config.Dashboard.CostCurrency,
            fx);

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
                ["fxUsdToCny"] = cost is not null ? fx : null,
            },
            ["byAgent"] = byAgent,
            ["days"] = ReadDailyDays(conn, HeatmapStart(today), today),
        };
    }

    private const int HeatmapWeeks = 26;

    private static DateTime HeatmapStart(DateTime today)
    {
        var monday = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
        return monday.AddDays(-(HeatmapWeeks - 1) * 7);
    }

    private static List<Dictionary<string, object?>> ReadDailyDays(
        SqliteConnection conn, DateTime from, DateTime to)
    {
        var days = new List<Dictionary<string, object?>>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT local_date,
                   COALESCE(SUM({BilledExpr}), 0)
            FROM usage_records
            WHERE local_date BETWEEN $from AND $to
            GROUP BY local_date
            HAVING SUM({BilledExpr}) > 0
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

    private sealed record ModelRow(
        string Tool, string Model, bool IsSub, long Input, long Output, long Cached, long CacheWrite,
        long Reasoning, double? ReportedUsd)
    {
        public long Tokens => Input + Output + Cached + CacheWrite + Reasoning;
        public string DisplayName
        {
            get
            {
                // 旧库里可能仍是 qoder-custom-.../workbuddy/model；展示时剥路径
                var label = QoderLocal.ResolveChinaModelDisplay(Model);
                return IsSub ? label + " · 子代理" : label;
            }
        }
    }

    private static List<ModelRow> ReadModelRows(SqliteConnection conn, DateTime from, DateTime to)
    {
        var rows = new List<ModelRow>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT tool, model, is_subagent,
                   SUM(input_tokens), SUM(output_tokens),
                   SUM(cached_input_tokens), SUM(cache_write_tokens),
                   SUM(reasoning_tokens), SUM(reported_cost_usd)
            FROM usage_records
            WHERE local_date BETWEEN $from AND $to
            GROUP BY tool, model, is_subagent
            """;
        cmd.Parameters.AddWithValue("$from", from.ToString("yyyy-MM-dd"));
        cmd.Parameters.AddWithValue("$to", to.ToString("yyyy-MM-dd"));
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            double? reported = null;
            if (!r.IsDBNull(8))
                reported = r.GetDouble(8);
            rows.Add(new ModelRow(
                r.GetString(0),
                r.IsDBNull(1) || string.IsNullOrWhiteSpace(r.GetString(1)) ? "unknown" : r.GetString(1),
                !r.IsDBNull(2) && r.GetInt64(2) != 0,
                r.GetInt64(3), r.GetInt64(4), r.GetInt64(5), r.GetInt64(6), r.GetInt64(7),
                reported));
        }
        return rows;
    }

    private static long SumBilled(SqliteConnection conn, DateTime from, DateTime to)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT COALESCE(SUM({BilledExpr}), 0)
            FROM usage_records
            WHERE local_date BETWEEN $from AND $to
            """;
        cmd.Parameters.AddWithValue("$from", from.ToString("yyyy-MM-dd"));
        cmd.Parameters.AddWithValue("$to", to.ToString("yyyy-MM-dd"));
        return (long)cmd.ExecuteScalar()!;
    }

    private static List<Dictionary<string, object?>> BuildByAgent(
        List<ModelRow> rows,
        IReadOnlyDictionary<string, (double Input, double Output, double? CacheRead, double? CacheWrite, bool IsCny)>? priceTable)
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
                .Select(x =>
                {
                    var m = new Dictionary<string, object?> { ["name"] = x.DisplayName, ["tokens"] = x.Tokens };
                    // Cost estimate on + non-empty table: flag models missing from exact/alias lookup.
                    if (priceTable is not null && x.ReportedUsd is null && !UsageCost.HasPrice(x.Model, priceTable))
                        m["noPrice"] = true;
                    return m;
                })
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
        // WAL：首扫写事务进行中，读接口（仪表盘）不被 SQLite 互斥阻塞
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
                reported_cost_usd REAL,
                is_subagent INTEGER NOT NULL DEFAULT 0,
                model TEXT NOT NULL DEFAULT 'unknown',
                project TEXT,
                PRIMARY KEY (tool, session_id, request_key)
            );
            CREATE INDEX IF NOT EXISTS idx_usage_date_tool ON usage_records(local_date, tool);
            CREATE INDEX IF NOT EXISTS idx_usage_date_model ON usage_records(local_date, tool, model);
            """;
        cmd.ExecuteNonQuery();
        TryAddColumn(conn, "reported_cost_usd", "REAL");
    }

    private static void TryAddColumn(SqliteConnection conn, string name, string type)
    {
        try
        {
            using var alter = conn.CreateCommand();
            alter.CommandText = $"ALTER TABLE usage_records ADD COLUMN {name} {type}";
            alter.ExecuteNonQuery();
        }
        catch (SqliteException) { /* 已有列 */ }
    }
}

public sealed record SourceScanStat(int Files, int Inserted, int Skipped);

public sealed record ScanAllResult(
    int Inserted,
    int Files,
    double Seconds,
    IReadOnlyDictionary<string, SourceScanStat> Sources);
