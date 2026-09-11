using System.IO;
using System.Net.Http;
using System.Text.Json;
using AgentHub.Core.ProxyCore;

namespace AgentHub.Core.TokenCore;

/// <summary>从 LiteLLM model_prices 只补 cache 单价到 AgentHub 价表行。
/// 列表价（input/output/币种/模型名）仍以 AgentHub prices 为准；按 LiteLLM.input 比例缩放到本表 input，
/// 这样 Cursor *-fast 加价会一起反映到 cache 上。GitHub → jsDelivr → 本地缓存。</summary>
public static class LiteLlmPriceEnricher
{
    private static readonly string[] RemoteUrls =
    [
        "https://raw.githubusercontent.com/BerriAI/litellm/main/model_prices_and_context_window.json",
        "https://cdn.jsdelivr.net/gh/BerriAI/litellm@main/model_prices_and_context_window.json",
    ];

    private static readonly string CachePath = Path.Combine(AgentHubConfig.Dir, "litellm.prices.cache.json");
    private static readonly TimeSpan FreshFor = TimeSpan.FromHours(24);

    private static readonly HttpClient Http = CreateHttp();
    private static readonly object Gate = new();

    private static Dictionary<string, LiteRates>? _rates;
    private static string _source = "none";
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
                mappedModels = LiteLlmPriceMap.Map.Count,
                loadedModels = _rates?.Count ?? 0,
            };
        }
    }

    public static void TryLoadCache()
    {
        try
        {
            if (!File.Exists(CachePath)) return;
            var json = File.ReadAllText(CachePath);
            if (!TryParse(json, out var rates)) return;
            lock (Gate)
            {
                _rates = rates;
                _source = "cache";
            }
        }
        catch (Exception)
        {
            // 损坏则等网络刷新
        }
    }

    /// <summary>磁盘缓存未满 24h 则直接用缓存；否则 GitHub → jsDelivr；都失败保留旧缓存。</summary>
    public static async Task RefreshAsync(bool force = false)
    {
        if (!force && IsFreshDiskCache())
        {
            if (_rates is null) TryLoadCache();
            return;
        }

        var errors = new List<string>();
        foreach (var url in RemoteUrls)
        {
            var label = url.Contains("jsdelivr", StringComparison.OrdinalIgnoreCase) ? "jsdelivr" : "github";
            try
            {
                using var resp = await Http.GetAsync(url).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    errors.Add(label + ": HTTP " + (int)resp.StatusCode);
                    continue;
                }

                var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (!TryParse(json, out var rates) || rates.Count == 0)
                {
                    errors.Add(label + ": parse failed");
                    continue;
                }

                WriteCache(json);
                lock (Gate)
                {
                    _rates = rates;
                    _source = label;
                    _lastFetchOk = true;
                    _lastFetchAt = DateTimeOffset.Now;
                    _lastFetchError = null;
                }
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

        if (_rates is null) TryLoadCache();
        lock (Gate)
        {
            _lastFetchOk = false;
            _lastFetchAt = DateTimeOffset.Now;
            _lastFetchError = string.Join("; ", errors);
            if (_rates is not null && _source == "none") _source = "cache";
        }
    }

    /// <summary>克隆列表行并写入缩放后的 cache 单价；未映射或 LiteLLM 缺字段则保留原行 cache。</summary>
    public static IReadOnlyList<PriceRow> Enrich(IReadOnlyList<PriceRow> list)
    {
        Dictionary<string, LiteRates>? rates;
        lock (Gate) rates = _rates;
        if (rates is null || rates.Count == 0 || list.Count == 0)
            return CloneAll(list);

        var result = new List<PriceRow>(list.Count);
        foreach (var row in list)
        {
            var copy = Clone(row);
            if (LiteLlmPriceMap.TryMap(copy.Model, out var key)
                && rates.TryGetValue(key, out var lite)
                && copy.InputPer1m is { } inn and > 0
                && lite.InputPer1m > 0)
            {
                var scale = inn / lite.InputPer1m;
                if (lite.CacheReadPer1m is { } cr && double.IsFinite(cr))
                    copy.CacheReadPer1m = Round(cr * scale);
                if (lite.CacheWritePer1m is { } cw && double.IsFinite(cw))
                    copy.CacheWritePer1m = Round(cw * scale);
            }
            result.Add(copy);
        }
        return result;
    }

    private static bool IsFreshDiskCache()
    {
        try
        {
            if (!File.Exists(CachePath)) return false;
            var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(CachePath);
            return age >= TimeSpan.Zero && age < FreshFor;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool TryParse(string json, out Dictionary<string, LiteRates> rates)
    {
        rates = new Dictionary<string, LiteRates>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Value.ValueKind != JsonValueKind.Object) continue;
                if (!TryReadPer1m(prop.Value, "input_cost_per_token", out var input) || input <= 0)
                    continue;
                double? cacheRead = null;
                double? cacheWrite = null;
                if (TryReadPer1m(prop.Value, "cache_read_input_token_cost", out var cr))
                    cacheRead = cr;
                // cache_creation = write；显式 0 也保留
                if (prop.Value.TryGetProperty("cache_creation_input_token_cost", out var cwEl)
                    && cwEl.ValueKind is JsonValueKind.Number
                    && cwEl.TryGetDouble(out var cwRaw)
                    && double.IsFinite(cwRaw))
                    cacheWrite = cwRaw * 1_000_000d;

                if (cacheRead is null && cacheWrite is null) continue;
                rates[prop.Name] = new LiteRates(input, cacheRead, cacheWrite);
            }
            return rates.Count > 0;
        }
        catch (Exception)
        {
            rates = new Dictionary<string, LiteRates>(StringComparer.OrdinalIgnoreCase);
            return false;
        }
    }

    private static bool TryReadPer1m(JsonElement obj, string name, out double per1m)
    {
        per1m = 0;
        if (!obj.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.Number)
            return false;
        if (!el.TryGetDouble(out var perToken) || !double.IsFinite(perToken))
            return false;
        per1m = perToken * 1_000_000d;
        return double.IsFinite(per1m);
    }

    private static void WriteCache(string json)
    {
        Directory.CreateDirectory(AgentHubConfig.Dir);
        var temp = CachePath + ".tmp";
        File.WriteAllText(temp, json);
        if (File.Exists(CachePath)) File.Replace(temp, CachePath, destinationBackupFileName: null);
        else File.Move(temp, CachePath);
    }

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient(new HttpClientHandler { UseProxy = true })
        {
            Timeout = TimeSpan.FromSeconds(8),
        };
        http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "AgentHub");
        return http;
    }

    private static IReadOnlyList<PriceRow> CloneAll(IReadOnlyList<PriceRow> list)
    {
        var result = new List<PriceRow>(list.Count);
        foreach (var row in list) result.Add(Clone(row));
        return result;
    }

    private static PriceRow Clone(PriceRow row) => new()
    {
        Model = row.Model,
        InputPer1m = row.InputPer1m,
        OutputPer1m = row.OutputPer1m,
        CacheReadPer1m = row.CacheReadPer1m,
        CacheWritePer1m = row.CacheWritePer1m,
        Currency = row.Currency,
    };

    private static double Round(double value) => Math.Round(value, 6, MidpointRounding.AwayFromZero);

    private readonly record struct LiteRates(double InputPer1m, double? CacheReadPer1m, double? CacheWritePer1m);
}
