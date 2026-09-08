using AgentHub.Core.ProxyCore;

namespace AgentHub.Core.TokenCore;

/// <summary>按输入 / 输出单价估算金额。输入按总量（含缓存命中与写入）。
/// 价格行保存厂商原币种原价（海外 USD、国内 CNY），表内不写死折算价。
/// 算钱时按 defaultCurrency（设置 costCurrency）折成展示币种。
/// fxUsdToCny 由调用方先解析（实时汇率，失败用兜底），表内不写死折算价。</summary>
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

        var targetCny = !string.Equals(defaultCurrency, "USD", StringComparison.OrdinalIgnoreCase);
        var target = targetCny ? "CNY" : "USD";
        double sum = 0;
        var any = false;
        var missed = false;
        var rate = NormalizeRate(fxUsdToCny);
        foreach (var row in rows)
        {
            if (!table.TryGetValue(row.Model.Trim(), out var p))
            {
                missed = true;
                continue;
            }
            any = true;
            var (unitIn, unitOut) = ConvertUnits(p.Input, p.Output, p.IsCny, targetCny, rate);
            sum += row.Input / 1_000_000d * unitIn + row.Output / 1_000_000d * unitOut;
        }
        if (!any) return missed ? (null, true, target) : (null, null, null);
        return (sum, missed, target);
    }

    private static (double Input, double Output) ConvertUnits(
        double input, double output, bool rowCny, bool targetCny, double rate)
    {
        if (rowCny == targetCny) return (input, output);
        return rowCny
            ? (input / rate, output / rate)
            : (input * rate, output * rate);
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
