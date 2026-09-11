using System.IO;
using System.Net.Http;
using System.Text.Json;
using AgentHub.Core.ProxyCore;

namespace AgentHub.Core.TokenCore;

/// <summary>价格表远程同步：启动与看板手动刷新成功后异步拉一份仓库 prices.json。
/// 顺序：GitHub → 超时/失败再 Gitee → 再失败保留本地 prices.cache.json / 内置默认表。
/// 列表价以 AgentHub 价表为准；另按映射从 LiteLLM 只补 cache（缩放至本表 input）。</summary>
public static class PriceSyncService
{
    private static readonly string[] RemoteUrls =
    [
        "https://raw.githubusercontent.com/sept13-yu/AgentHub/main/prices.json",
        "https://gitee.com/sept13-yu/AgentHub/raw/main/prices.json",
    ];

    private static readonly string CachePath = Path.Combine(AgentHubConfig.Dir, "prices.cache.json");

    private static readonly HttpClient Http = CreateHttp();
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };
    private static readonly object Gate = new();

    /// <summary>与仓库 prices.json 同源；内置默认表是唯一硬编码源。</summary>
    public static readonly IReadOnlyList<PriceRow> DefaultPrices =
    [
        new() { Model = "cursor-grok-4.6-xhigh-fast", InputPer1m = 4.0, OutputPer1m = 12.0, CacheReadPer1m = 1.0, Currency = "USD" },
        new() { Model = "cursor-grok-4.6-high-fast", InputPer1m = 4.0, OutputPer1m = 12.0, CacheReadPer1m = 1.0, Currency = "USD" },
        new() { Model = "cursor-grok-4.6-xhigh", InputPer1m = 2.0, OutputPer1m = 6.0, CacheReadPer1m = 0.5, Currency = "USD" },
        new() { Model = "grok-bot-default", InputPer1m = 2.0, OutputPer1m = 6.0, CacheReadPer1m = 0.5, Currency = "USD" },
        new() { Model = "cursor-grok-4.6-high", InputPer1m = 2.0, OutputPer1m = 6.0, CacheReadPer1m = 0.5, Currency = "USD" },
        new() { Model = "claude-opus-5-thinking-high", InputPer1m = 5.0, OutputPer1m = 25.0, CacheReadPer1m = 0.5, CacheWritePer1m = 6.25, Currency = "USD" },
        new() { Model = "gemini-3.7-flash-high", InputPer1m = 0.75, OutputPer1m = 3.5, CacheReadPer1m = 0.075, Currency = "USD" },
        new() { Model = "composer-2.5", InputPer1m = 0.5, OutputPer1m = 2.5, Currency = "USD" },
        new() { Model = "composer-2.5-fast", InputPer1m = 3.0, OutputPer1m = 15.0, Currency = "USD" },
        new() { Model = "gpt-5.6-sol", InputPer1m = 4.0, OutputPer1m = 20.0, CacheReadPer1m = 0.4, CacheWritePer1m = 5.0, Currency = "USD" },
        new() { Model = "gpt-5.6-sol-fast", InputPer1m = 8.0, OutputPer1m = 40.0, CacheReadPer1m = 0.8, CacheWritePer1m = 10.0, Currency = "USD" },
        new() { Model = "gpt-5.6-terra", InputPer1m = 2.0, OutputPer1m = 12.0, CacheReadPer1m = 0.2, CacheWritePer1m = 2.5, Currency = "USD" },
        new() { Model = "gpt-5.6-terra-fast", InputPer1m = 4.0, OutputPer1m = 24.0, CacheReadPer1m = 0.4, CacheWritePer1m = 5.0, Currency = "USD" },
        new() { Model = "gpt-5.6-luna", InputPer1m = 0.2, OutputPer1m = 1.2, CacheReadPer1m = 0.02, CacheWritePer1m = 0.25, Currency = "USD" },
        new() { Model = "gpt-5.6-luna-fast", InputPer1m = 0.4, OutputPer1m = 2.4, CacheReadPer1m = 0.04, CacheWritePer1m = 0.5, Currency = "USD" },
        new() { Model = "GLM-5.3", InputPer1m = 8.0, OutputPer1m = 28.0, CacheReadPer1m = 1.485712, CacheWritePer1m = 0.0, Currency = "CNY" },
        new() { Model = "GLM-5.3-Flash", InputPer1m = 0.8, OutputPer1m = 2.8, CacheReadPer1m = 0.16, CacheWritePer1m = 0.0, Currency = "CNY" },
        new() { Model = "deepseek-v4-flash", InputPer1m = 3.0, OutputPer1m = 9.0, CacheReadPer1m = 0.095454, CacheWritePer1m = 0.0, Currency = "CNY" },
        new() { Model = "deepseek-v4-flash-vision-exp", InputPer1m = 3.0, OutputPer1m = 9.0, CacheReadPer1m = 0.095454, CacheWritePer1m = 0.0, Currency = "CNY" },
        new() { Model = "deepseek-v4-pro", InputPer1m = 9.0, OutputPer1m = 27.0, CacheReadPer1m = 0.299997, CacheWritePer1m = 0.0, Currency = "CNY" },
        new() { Model = "DeepSeek-V4-Flash 正式版", InputPer1m = 3.0, OutputPer1m = 9.0, CacheReadPer1m = 0.095454, CacheWritePer1m = 0.0, Currency = "CNY" },
        new() { Model = "DeepSeek-V4-Pro 正式版", InputPer1m = 9.0, OutputPer1m = 27.0, CacheReadPer1m = 0.299997, CacheWritePer1m = 0.0, Currency = "CNY" },
        new() { Model = "kimi-k3", InputPer1m = 20.0, OutputPer1m = 100.0, CacheReadPer1m = 3.36842, Currency = "CNY" },
        new() { Model = "Qwen3.8-Max", InputPer1m = 12.0, OutputPer1m = 36.0, CacheReadPer1m = 1.5, Currency = "CNY" },
        new() { Model = "hy4-preview", InputPer1m = 6.0, OutputPer1m = 18.0, Currency = "CNY" },
        new() { Model = "mimo-x-flash-preview", InputPer1m = 0.1, OutputPer1m = 0.3, CacheReadPer1m = 0.01, CacheWritePer1m = 0.0, Currency = "USD" },
        new() { Model = "mimo-x-pro-preview", InputPer1m = 0.435, OutputPer1m = 0.87, CacheReadPer1m = 0.0036, CacheWritePer1m = 0.0, Currency = "USD" },
        new() { Model = "claude-fable-5", InputPer1m = 10.0, OutputPer1m = 50.0, CacheReadPer1m = 1.0, CacheWritePer1m = 12.5, Currency = "USD" },
        new() { Model = "claude-opus-4-6", InputPer1m = 5.0, OutputPer1m = 25.0, CacheReadPer1m = 0.5, CacheWritePer1m = 6.25, Currency = "USD" },
        new() { Model = "claude-opus-4-8", InputPer1m = 5.0, OutputPer1m = 25.0, CacheReadPer1m = 0.5, CacheWritePer1m = 6.25, Currency = "USD" },
        new() { Model = "claude-sonnet-4-5", InputPer1m = 3.0, OutputPer1m = 15.0, CacheReadPer1m = 0.3, CacheWritePer1m = 3.75, Currency = "USD" },
        new() { Model = "claude-sonnet-4-6", InputPer1m = 3.0, OutputPer1m = 15.0, CacheReadPer1m = 0.3, CacheWritePer1m = 3.75, Currency = "USD" },
        new() { Model = "gpt-5.5", InputPer1m = 5.0, OutputPer1m = 30.0, CacheReadPer1m = 0.5, Currency = "USD" },
        new() { Model = "gpt-5.4", InputPer1m = 2.5, OutputPer1m = 15.0, CacheReadPer1m = 0.25, Currency = "USD" },
    ];

    /// <summary>未做 LiteLLM 补价的 AgentHub 列表价。</summary>
    private static IReadOnlyList<PriceRow> _listBaseline = DefaultPrices;

    private static IReadOnlyList<PriceRow> _baseline = DefaultPrices;

    /// <summary>对外基线：AgentHub 列表价 + LiteLLM cache 补价。PriceOverrides 在其上再覆盖。</summary>
    public static IReadOnlyList<PriceRow> Baseline
    {
        get { lock (Gate) return _baseline; }
        private set { lock (Gate) _baseline = value; }
    }

    /// <summary>Baseline 实际变化时通知上层补刷新看板。Core 不引用 App，由 App 挂回调。</summary>
    public static Action? OnBaselineChanged;

    private static string _source = "builtin";
    private static bool? _lastFetchOk;
    private static DateTimeOffset? _lastFetchAt;
    private static string? _lastFetchError;

    public static object Status()
    {
        lock (Gate)
        {
            return new
            {
                source = _source,
                lastFetchOk = _lastFetchOk,
                lastFetchAt = _lastFetchAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
                lastFetchError = _lastFetchError,
                hasDiskCache = File.Exists(CachePath),
                cachePath = CachePath,
                liteLlm = LiteLlmPriceEnricher.Status(),
            };
        }
    }

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient(new HttpClientHandler { UseProxy = true })
        {
            Timeout = TimeSpan.FromSeconds(5),
        };
        http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "AgentHub");
        return http;
    }

    public static void TryLoadCache()
    {
        try
        {
            if (File.Exists(CachePath))
            {
                var json = File.ReadAllText(CachePath);
                if (TryParse(json, out var rows))
                {
                    lock (Gate)
                    {
                        _listBaseline = rows;
                        _source = "cache";
                    }
                }
            }
        }
        catch (Exception)
        {
            // 缓存损坏：保留 DefaultPrices
        }

        LiteLlmPriceEnricher.TryLoadCache();
        RebaseEnriched(notify: false);
    }

    public static void RefreshInBackground()
    {
        _ = Task.Run(async () =>
        {
            try { await RefreshAsync().ConfigureAwait(false); }
            catch (Exception ex) { MarkFetch(false, ex.GetType().Name); }
        });
    }

    /// <summary>PriceOverrides（按 Model，OrdinalIgnoreCase 覆盖/追加）> Baseline（列表价+LiteLLM cache）> DefaultPrices。
    /// 算钱时的别名映射见 <see cref="PriceAliases"/>；UsageCost 先精确命中再走别名，仍没有就标未定价。</summary>
    public static IReadOnlyList<PriceRow> Resolve(IEnumerable<PriceRow>? overrides)
    {
        var table = new Dictionary<string, PriceRow>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in Baseline)
            TryPut(table, row);
        if (overrides is not null)
        {
            foreach (var row in overrides)
                TryPut(table, row);
        }
        return table.Values.ToList();
    }

    private static async Task RefreshAsync()
    {
        var errors = new List<string>();
        foreach (var url in RemoteUrls)
        {
            var label = url.Contains("gitee.com", StringComparison.OrdinalIgnoreCase) ? "gitee" : "github";
            try
            {
                using var resp = await Http.GetAsync(url).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    errors.Add(label + ": HTTP " + (int)resp.StatusCode);
                    continue;
                }

                var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (!TryParse(json, out var rows))
                {
                    errors.Add(label + ": parse failed");
                    continue;
                }

                WriteCache(json);
                lock (Gate) _listBaseline = rows;
                MarkFetch(true, null, label);
                await AfterListRefreshAsync().ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException)
            {
                errors.Add(label + ": timeout");
            }
            catch (HttpRequestException ex)
            {
                errors.Add(label + ": " + (ex.StatusCode is { } code ? "HTTP " + (int)code : ex.GetType().Name));
            }
            catch (Exception ex)
            {
                errors.Add(label + ": " + ex.GetType().Name);
            }
        }

        // 两边都失败：列表价保持启动时的 disk cache / builtin；仍尝试刷新 LiteLLM cache。
        MarkFetch(false, string.Join("; ", errors));
        await AfterListRefreshAsync().ConfigureAwait(false);
    }

    private static async Task AfterListRefreshAsync()
    {
        try { await LiteLlmPriceEnricher.RefreshAsync().ConfigureAwait(false); }
        catch (Exception)
        {
            // LiteLLM 失败不阻断；沿用旧 LiteLLM 缓存或价表自带 cache
        }
        RebaseEnriched(notify: true);
    }

    private static void RebaseEnriched(bool notify)
    {
        IReadOnlyList<PriceRow> list;
        lock (Gate) list = _listBaseline;
        var enriched = LiteLlmPriceEnricher.Enrich(list);
        var previous = Baseline;
        var changed = !SameTable(previous, enriched);
        if (changed) Baseline = enriched;
        if (!notify || !changed) return;
        try { OnBaselineChanged?.Invoke(); }
        catch (Exception) { /* 上层回调失败不影响缓存 */ }
    }

    private static void MarkFetch(bool ok, string? error, string? source = null)
    {
        lock (Gate)
        {
            _lastFetchOk = ok;
            _lastFetchAt = DateTimeOffset.Now;
            _lastFetchError = error;
            if (source is not null) _source = source;
        }
    }

    private static bool TryParse(string json, out IReadOnlyList<PriceRow> rows)
    {
        rows = DefaultPrices;
        try
        {
            var file = JsonSerializer.Deserialize<PriceFile>(json, JsonOpts);
            if (file?.Prices is null || file.Prices.Count == 0) return false;
            var clean = new List<PriceRow>(file.Prices.Count);
            foreach (var row in file.Prices)
            {
                var name = (row.Model ?? "").Trim();
                if (name.Length == 0 || row.InputPer1m is not { } inn || row.OutputPer1m is not { } outt)
                    continue;
                if (!double.IsFinite(inn) || !double.IsFinite(outt)) continue;
                clean.Add(row);
            }
            if (clean.Count == 0) return false;
            rows = clean;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static void WriteCache(string json)
    {
        Directory.CreateDirectory(AgentHubConfig.Dir);
        var temp = CachePath + ".tmp";
        File.WriteAllText(temp, json);
        if (File.Exists(CachePath)) File.Replace(temp, CachePath, destinationBackupFileName: null);
        else File.Move(temp, CachePath);
    }

    private static void TryPut(Dictionary<string, PriceRow> table, PriceRow row)
    {
        var name = (row.Model ?? "").Trim();
        if (name.Length == 0) return;
        table[name] = row;
    }

    private static bool SameTable(IReadOnlyList<PriceRow> a, IReadOnlyList<PriceRow> b)
    {
        if (a.Count != b.Count) return false;
        var map = new Dictionary<string, PriceRow>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in a)
        {
            var name = (row.Model ?? "").Trim();
            if (name.Length == 0) continue;
            map[name] = row;
        }
        if (map.Count != b.Count) return false;
        foreach (var row in b)
        {
            var name = (row.Model ?? "").Trim();
            if (!map.TryGetValue(name, out var old)) return false;
            if (old.InputPer1m != row.InputPer1m || old.OutputPer1m != row.OutputPer1m
                || old.CacheReadPer1m != row.CacheReadPer1m || old.CacheWritePer1m != row.CacheWritePer1m
                || !string.Equals(old.Currency, row.Currency, StringComparison.OrdinalIgnoreCase))
                return false;
        }
        return true;
    }

    private sealed class PriceFile
    {
        public List<PriceRow>? Prices { get; set; }
    }
}
