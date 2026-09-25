namespace AgentHub.Core.TokenCore;

/// <summary>按目录快照估价。ReportedUsd（含 0）优先；其余全部走快照的统一价格路径。</summary>
public static class UsageCost
{
    public static (double? Cost, bool? Partial, string? Currency) Estimate(
        IEnumerable<(string Tool, string Model, long Input, long Output, long Cached, long CacheWrite, long Reasoning, double? ReportedUsd)> rows,
        ModelCatalogSnapshot catalog,
        bool enabled,
        string? defaultCurrency,
        double fxUsdToCny)
    {
        if (!enabled) return (null, null, null);
        var targetCny = !string.Equals(defaultCurrency, "USD", StringComparison.OrdinalIgnoreCase);
        var target = targetCny ? "CNY" : "USD";
        double sum = 0;
        var any = false;
        var missed = false;
        var rate = NormalizeRate(fxUsdToCny);
        foreach (var row in rows)
        {
            if (row.ReportedUsd is not null)
            {
                any = true;
                sum += targetCny ? row.ReportedUsd.Value * rate : row.ReportedUsd.Value;
                continue;
            }
            if (!TryResolve(row.Tool, row.Model, catalog, out var p))
            {
                missed = true;
                continue;
            }
            any = true;
            var (unitIn, unitOut, unitCr, unitCw) = ConvertUnits(p, targetCny, rate);
            sum += row.Input / 1_000_000d * unitIn
                 + row.Cached / 1_000_000d * unitCr
                 + row.CacheWrite / 1_000_000d * unitCw
                 + (row.Output + row.Reasoning) / 1_000_000d * unitOut;
        }
        if (!any) return missed ? (null, true, target) : (null, null, null);
        return (sum, missed, target);
    }

    /// <summary>与 Estimate / noPrice 共用同一快照价格路径。</summary>
    public static bool HasPrice(string? tool, string? model, ModelCatalogSnapshot catalog)
        => TryResolve(tool ?? "", model, catalog, out _);

    private static bool TryResolve(
        string tool, string? model, ModelCatalogSnapshot catalog,
        out (double Input, double Output, double? CacheRead, double? CacheWrite, bool IsCny) price)
    {
        price = default;
        if (!catalog.TryGetPrice(tool, model, out var catalogPrice)) return false;
        price = (catalogPrice.InputPer1m, catalogPrice.OutputPer1m,
                 catalogPrice.CacheReadPer1m, catalogPrice.CacheWritePer1m, catalogPrice.IsCny);
        return true;
    }

    private static (double Input, double Output, double CacheRead, double CacheWrite) ConvertUnits(
        (double Input, double Output, double? CacheRead, double? CacheWrite, bool IsCny) p,
        bool targetCny,
        double rate)
    {
        var cr = p.CacheRead ?? p.Input;
        var cw = p.CacheWrite ?? p.Input;
        if (p.IsCny == targetCny) return (p.Input, p.Output, cr, cw);
        return p.IsCny
            ? (p.Input / rate, p.Output / rate, cr / rate, cw / rate)
            : (p.Input * rate, p.Output * rate, cr * rate, cw * rate);
    }

    private static double NormalizeRate(double rate) => rate > 0 ? rate : 1;
}
