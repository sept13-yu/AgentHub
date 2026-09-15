using System.IO;
using System.Net.Http;
using System.Text.Json;
using AgentHub.Core.ProxyCore;

namespace AgentHub.Core.TokenCore;

/// <summary>从 LiteLLM model_prices 取 cache 单价并按 AgentHub 列表价缩放；
/// 同时提供 <see cref="TryGetPrice"/>：未上架模型在估价时按 LiteLLM 原价（USD）回退，避免 noPrice。
/// 列表价（input/output/币种/模型名）仍以 AgentHub prices 为准；Enrich 只改 cache。
/// 来源：GitHub → jsDelivr → 本地缓存。</summary>
public static class LiteLlmPriceEnricher
{
    private static readonly string[] RemoteUrls =
    [
        "https://raw.githubusercontent.com/BerriAI/litellm/main/model_prices_and_context_window.json",
        "https://cdn.jsdelivr.net/gh/BerriAI/litellm@main/model_prices_and_context_window.json",
    ];

    /// <summary>Cursor / 用量日志常见后缀，从长到短剥离后重试 LiteLLM 键。</summary>
    private static readonly string[] CursorSuffixes =
    [
        "-thinking-high",
        "-thinking-medium",
        "-high-thinking",
        "-medium-thinking",
        "-xhigh-fast",
        "-high-fast",
        "-xhigh",
        "-ultra",
        "-fast",
        "-high",
        "-medium",
        "-max",
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
            // 下次刷新再试
        }
    }

    /// <summary>磁盘缓存未满 24h 则直接用缓存；否则 GitHub → jsDelivr，都失败保留旧缓存。</summary>
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

    /// <summary>克隆列表并写回按比例缩放后的 cache 单价；未映射或 LiteLLM 缺字段则保留原有 cache。</summary>
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

    /// <summary>
    /// 未上架 AgentHub 列表时的 LiteLLM 原价回退（USD）。
    /// 查找顺序：精确键 → LiteLlmPriceMap → PriceAliases → 剥离 Cursor / expires-on 后缀后重试。
    /// TryResolveKey 在精确键未命中时还会尝试提供商前缀（见方法内注释）。
    /// 不把全量 LiteLLM 键并入 UI 列表；仅在估价 / HasPrice 路径使用。
    /// </summary>
    public static bool TryGetPrice(string? model, out PriceRow row)
    {
        row = null!;
        var name = (model ?? "").Trim();
        if (name.Length == 0) return false;

        Dictionary<string, LiteRates>? rates;
        lock (Gate) rates = _rates;
        if (rates is null || rates.Count == 0) return false;

        if (TryResolveKey(rates, name, out row)) return true;

        if (PriceAliases.TryMap(name, out var aliased) && TryResolveKey(rates, aliased, out row))
            return true;

        var cursor = name;
        while (TryStripCursorSuffix(cursor, out var next) || TryStripExpiresOnSuffix(cursor, out next))
        {
            cursor = next;
            if (TryResolveKey(rates, cursor, out row)) return true;
            if (PriceAliases.TryMap(cursor, out aliased) && TryResolveKey(rates, aliased, out row))
                return true;
        }

        return false;
    }

    private static bool TryResolveKey(Dictionary<string, LiteRates> rates, string name, out PriceRow row)
    {
        row = null!;
        if (TryMakeRow(rates, name, name, out row)) return true;
        if (LiteLlmPriceMap.TryMap(name, out var key) && TryMakeRow(rates, key, name, out row))
            return true;

        // 提供商前缀回退（精确/映射都未命中时）：
        // 优先顺序固定，避免随机命中带加价的 reseller：
        //   1) bare name（上面已试）
        //   2) deepseek/{name}
        //   3) openrouter/deepseek/{name}
        //   4) fireworks_ai/{name}
        //   5) 任意键 Equals(name) 忽略大小写，或 EndsWith("/{name}")（稳定：按上述前缀偏好再扫一遍）
        foreach (var prefixed in PrefixedLiteLlmKeys(name))
        {
            if (TryMakeRow(rates, prefixed, name, out row)) return true;
        }
        if (TryFindPrefixedBySuffix(rates, name, out var foundKey) && TryMakeRow(rates, foundKey, name, out row))
            return true;
        return false;
    }

    /// <summary>固定偏好的提供商前缀键（不含 bare；bare 已在 TryResolveKey 先试）。</summary>
    private static IEnumerable<string> PrefixedLiteLlmKeys(string name)
    {
        yield return "deepseek/" + name;
        yield return "openrouter/deepseek/" + name;
        yield return "fireworks_ai/" + name;
    }

    /// <summary>
    /// 扫描 rates：先按 PrefixedLiteLlmKeys 偏好匹配 EndsWith(/name)，再任意 /name 后缀，再 Equals(name)。
    /// </summary>
    private static bool TryFindPrefixedBySuffix(
        Dictionary<string, LiteRates> rates,
        string name,
        out string foundKey)
    {
        foundKey = "";
        var slashName = "/" + name;
        foreach (var preferred in PrefixedLiteLlmKeys(name))
        {
            if (rates.ContainsKey(preferred))
            {
                foundKey = preferred;
                return true;
            }
        }
        string? anySlash = null;
        string? anyEquals = null;
        foreach (var k in rates.Keys)
        {
            if (k.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                anyEquals ??= k;
                continue;
            }
            if (k.EndsWith(slashName, StringComparison.OrdinalIgnoreCase))
                anySlash ??= k;
        }
        // Equals 其实已被 TryMakeRow(bare) 覆盖；这里仅兜底大小写变体键。
        if (anyEquals is not null)
        {
            foundKey = anyEquals;
            return true;
        }
        if (anySlash is not null)
        {
            foundKey = anySlash;
            return true;
        }
        return false;
    }

    private static bool TryMakeRow(
        Dictionary<string, LiteRates> rates,
        string liteKey,
        string modelName,
        out PriceRow row)
    {
        row = null!;
        if (!rates.TryGetValue(liteKey, out var lite)) return false;
        if (lite.InputPer1m <= 0 || lite.OutputPer1m <= 0) return false;
        if (!double.IsFinite(lite.InputPer1m) || !double.IsFinite(lite.OutputPer1m)) return false;

        row = new PriceRow
        {
            Model = modelName,
            InputPer1m = Round(lite.InputPer1m),
            OutputPer1m = Round(lite.OutputPer1m),
            CacheReadPer1m = lite.CacheReadPer1m is { } cr && double.IsFinite(cr) ? Round(cr) : null,
            CacheWritePer1m = lite.CacheWritePer1m is { } cw && double.IsFinite(cw) ? Round(cw) : null,
            Currency = "USD",
        };
        return true;
    }

    private static bool TryStripCursorSuffix(string name, out string stripped)
    {
        stripped = name;
        foreach (var suffix in CursorSuffixes)
        {
            if (name.Length > suffix.Length
                && name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                stripped = name[..^suffix.Length];
                return stripped.Length > 0;
            }
        }
        return false;
    }

    /// <summary>剥离用量日志里的 -expires-on-MMDD / -expires-on-YYYYMMDD 日期后缀。</summary>
    private static bool TryStripExpiresOnSuffix(string name, out string stripped)
    {
        stripped = name;
        const string marker = "-expires-on-";
        var idx = name.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx <= 0) return false;
        var tail = name[(idx + marker.Length)..];
        if (tail.Length == 0) return false;
        foreach (var ch in tail)
        {
            if (!char.IsDigit(ch)) return false;
        }
        stripped = name[..idx];
        return stripped.Length > 0;
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

    /// <summary>
    /// 加载有 input+output 的条目（cache 可选）。无价回退需要 output；
    /// Enrich 仍只在有 cache 字段时覆盖列表 cache。
    /// </summary>
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
                if (!TryReadPer1m(prop.Value, "output_cost_per_token", out var output) || output <= 0)
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

                rates[prop.Name] = new LiteRates(input, output, cacheRead, cacheWrite);
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

    private readonly record struct LiteRates(
        double InputPer1m,
        double OutputPer1m,
        double? CacheReadPer1m,
        double? CacheWritePer1m);
}
