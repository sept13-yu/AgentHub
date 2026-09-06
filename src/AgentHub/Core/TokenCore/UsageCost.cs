using AgentHub.Core.ProxyCore;

namespace AgentHub.Core.TokenCore;

/// <summary>按输入 / 输出单价估算金额。输入按总量（含缓存命中与写入）。
/// 价格行保存厂商原币种原价（海外 USD、国内 CNY），算钱时统一按实时汇率折成 USD
/// （CNY 行 ÷ USD→CNY 汇率；汇率接口拿不到用 fxFallback）。</summary>
public static class UsageCost
{
    public static (double? Cost, bool? Partial, string? Currency) Estimate(
        IEnumerable<(string Model, long Input, long Output)> rows,
        IEnumerable<PriceRow> prices,
        bool enabled,
        string? defaultCurrency,
        double fxUsdToCny)
    {
        if (!enabled) return (null, null, null);
        var table = BuildTable(prices, defaultCurrency);
        if (table.Count == 0) return (null, null, null);

        double sum = 0;
        var any = false;
        var missed = false;
        double? rate = null; // 1 USD = ? CNY；懒加载：只有实际用到 CNY 行才拉汇率
        foreach (var row in rows)
        {
            if (!table.TryGetValue(row.Model.Trim(), out var p))
            {
                missed = true;
                continue;
            }
            any = true;
            var unitIn = p.Input;
            var unitOut = p.Output;
            if (p.IsCny)
            {
                rate ??= NormalizeRate(FxService.UsdToCny(fxUsdToCny));
                unitIn = p.Input / rate.Value;
                unitOut = p.Output / rate.Value;
            }
            sum += row.Input / 1_000_000d * unitIn + row.Output / 1_000_000d * unitOut;
        }
        if (!any) return missed ? (null, true, "USD") : (null, null, null);
        // 结果统一折成 USD：前端只认 $ / ¥ 两个符号
        return (sum, missed, "USD");
    }

    private static double NormalizeRate(double rate) => rate > 0 ? rate : 1;

    private static Dictionary<string, (double Input, double Output, bool IsCny)> BuildTable(
        IEnumerable<PriceRow> prices, string? defaultCurrency)
    {
        var defCny = !string.Equals(defaultCurrency, "USD", StringComparison.OrdinalIgnoreCase);
        var table = new Dictionary<string, (double, double, bool)>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in prices)
        {
            var name = (row.Model ?? "").Trim();
            if (name.Length == 0 || row.InputPer1m is not { } inn || row.OutputPer1m is not { } outt)
                continue;
            if (!double.IsFinite(inn) || !double.IsFinite(outt)) continue;
            var isCny = string.Equals(row.Currency, "CNY", StringComparison.OrdinalIgnoreCase)
                ? true
                : string.Equals(row.Currency, "USD", StringComparison.OrdinalIgnoreCase)
                    ? false
                    : defCny;
            table[name] = (inn, outt, isCny);
        }
        return table;
    }
}