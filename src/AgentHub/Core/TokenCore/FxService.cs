using System.Net.Http;
using System.Text.Json;

namespace AgentHub.Core.TokenCore;

/// <summary>汇率服务：USD→CNY。优先拉实时汇率（免费无 key 接口，走系统代理），失败/超时用调用方给的兜底值。
/// 结果缓存 6 小时，避免每次刷新都打外网。</summary>
public static class FxService
{
    private const string PrimaryUrl = "https://open.er-api.com/v6/latest/USD";
    private const string SecondaryUrl = "https://api.frankfurter.dev/v1/latest?base=USD&symbols=CNY";

    private static readonly HttpClient Http = new(new HttpClientHandler { UseProxy = true })
    {
        Timeout = TimeSpan.FromSeconds(5),
    };

    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(6);
    private static readonly object Gate = new();
    private static DateTime _fetchedAt;
    private static double? _cached;

    /// <summary>拿到 1 USD 折合多少 CNY。rate 为兜底值（取配置 FxFallbackRate），接口全挂时用它。</summary>
    public static double UsdToCny(double fallback)
    {
        lock (Gate)
        {
            if (_cached is { } v && DateTime.UtcNow - _fetchedAt < CacheTtl) return v;
        }
        try
        {
            var v = FetchAsync().ConfigureAwait(false).GetAwaiter().GetResult();
            if (v > 0)
            {
                lock (Gate)
                {
                    _cached = v;
                    _fetchedAt = DateTime.UtcNow;
                }
                return v;
            }
        }
        catch (Exception) { /* 接口不可达，走兜底 */ }
        return fallback;
    }

    private static async Task<double> FetchAsync()
    {
        foreach (var url in new[] { PrimaryUrl, SecondaryUrl })
        {
            try
            {
                using var resp = await Http.GetAsync(url).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) continue;
                var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("rates", out var rates)
                    && rates.TryGetProperty("CNY", out var cny)
                    && cny.TryGetDouble(out var v)
                    && v > 0)
                    return v;
            }
            catch (Exception) { /* 试下一个源 */ }
        }
        return 0;
    }
}