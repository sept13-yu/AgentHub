using System.IO;
using System.Net.Http;
using System.Text.Json;
using AgentHub.Core.ProxyCore;

namespace AgentHub.Core.TokenCore;

/// <summary>价格表远端同步：启动与仪表盘手动刷新成功后异步拉一次仓库根 prices.json。
/// 成功 → 更新 Baseline 并落盘 prices.cache.json；失败/超时 → 静默沿用旧缓存；从未成功 → 代码内置表。</summary>
public static class PriceSyncService
{
    private const string RemoteUrl = "https://raw.githubusercontent.com/sept13-yu/AgentHub/main/prices.json";
    private static readonly string CachePath = Path.Combine(AgentHubConfig.Dir, "prices.cache.json");

    private static readonly HttpClient Http = CreateHttp();
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };
    private static readonly object Gate = new();

    /// <summary>与仓库根 prices.json 同源的内置默认表（唯一代码来源）。</summary>
    public static readonly IReadOnlyList<PriceRow> DefaultPrices =
    [
        new() { Model = "cursor-grok-4.6-xhigh-fast", InputPer1m = 2.0, OutputPer1m = 6.0, Currency = "USD" },
        new() { Model = "cursor-grok-4.6-high-fast", InputPer1m = 2.0, OutputPer1m = 6.0, Currency = "USD" },
        new() { Model = "cursor-grok-4.6-xhigh", InputPer1m = 2.0, OutputPer1m = 6.0, Currency = "USD" },
        new() { Model = "cursor-grok-4.6-high", InputPer1m = 2.0, OutputPer1m = 6.0, Currency = "USD" },
        new() { Model = "claude-opus-5-thinking-high", InputPer1m = 5.0, OutputPer1m = 25.0, Currency = "USD" },
        new() { Model = "gemini-3.7-flash-high", InputPer1m = 0.75, OutputPer1m = 3.75, Currency = "USD" },
        new() { Model = "composer-2.5-fast", InputPer1m = 3.0, OutputPer1m = 15.0, Currency = "USD" },
        new() { Model = "gpt-5.6-sol", InputPer1m = 4.0, OutputPer1m = 20.0, Currency = "USD" },
        new() { Model = "gpt-5.6-terra", InputPer1m = 2.0, OutputPer1m = 12.0, Currency = "USD" },
        new() { Model = "gpt-5.6-luna", InputPer1m = 0.2, OutputPer1m = 1.2, Currency = "USD" },
        new() { Model = "GLM-5.3", InputPer1m = 1.4, OutputPer1m = 4.4, Currency = "USD" },
        new() { Model = "GLM-5.3-Flash", InputPer1m = 0.15, OutputPer1m = 0.5, Currency = "USD" },
        new() { Model = "deepseek-v4-flash", InputPer1m = 0.44, OutputPer1m = 1.32, Currency = "USD" },
        new() { Model = "deepseek-v4-pro", InputPer1m = 1.32, OutputPer1m = 3.96, Currency = "USD" },
        new() { Model = "DeepSeek-V4-Flash 正式版", InputPer1m = 0.44, OutputPer1m = 1.32, Currency = "USD" },
        new() { Model = "kimi-k3-1", InputPer1m = 3.0, OutputPer1m = 15.0, Currency = "USD" },
    ];

    private static IReadOnlyList<PriceRow> _baseline = DefaultPrices;

    /// <summary>远端基线：启动时 TryLoadCache()，拉取成功后替换。PriceOverrides 在其上再覆盖。</summary>
    public static IReadOnlyList<PriceRow> Baseline
    {
        get { lock (Gate) return _baseline; }
        private set { lock (Gate) _baseline = value; }
    }

    /// <summary>Baseline 实际变化后通知壳层补刷仪表盘。Core 不引用 App，由 App 挂回调。</summary>
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
            if (!File.Exists(CachePath)) return;
            var json = File.ReadAllText(CachePath);
            if (!TryParse(json, out var rows)) return;
            Baseline = rows;
            lock (Gate) _source = "cache";
        }
        catch (Exception)
        {
            // 缓存损坏：沿用 DefaultPrices
        }
    }

    public static void RefreshInBackground()
    {
        _ = Task.Run(async () =>
        {
            try { await RefreshAsync().ConfigureAwait(false); }
            catch (Exception ex) { MarkFetch(false, ex.GetType().Name); }
        });
    }

    /// <summary>PriceOverrides（按 Model，OrdinalIgnoreCase 覆盖/追加）> Baseline（远端缓存 > DefaultPrices）。</summary>
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
        using var resp = await Http.GetAsync(RemoteUrl).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            MarkFetch(false, "HTTP " + (int)resp.StatusCode);
            return;
        }
        var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
        if (!TryParse(json, out var rows))
        {
            MarkFetch(false, "价格表无法解析");
            return;
        }

        WriteCache(json);
        var previous = Baseline;
        var changed = !SameTable(previous, rows);
        if (changed) Baseline = rows;
        MarkFetch(true, null, "remote");
        if (!changed) return;
        try { OnBaselineChanged?.Invoke(); }
        catch (Exception) { /* 壳层回调失败不影响缓存 */ }
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
