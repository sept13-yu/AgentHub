using AgentHub.Core.ProxyCore;

namespace AgentHub.Core.TokenCore;

/// <summary>Estimate spend from vendor list prices (input, output, cache-read, cache-write).
/// Price table keeps vendor currency (USD overseas / CNY domestic); no hard-coded FX in the table.
/// At estimate time convert to defaultCurrency (costCurrency setting) with fxUsdToCny from caller.
/// Aligns with LiteLLM/TokenTracker: netInput*input + cacheRead*cacheRead + cacheWrite*cacheWrite + output*output;
/// missing cache unit price falls back to input price.</summary>
public static class UsageCost
{
    public static (double? Cost, bool? Partial, string? Currency) Estimate(
        IEnumerable<(string Model, long Input, long Output, long Cached, long CacheWrite)> rows,
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
            var name = row.Model.Trim();
            // Exact hit → PriceAliases → LiteLLM fallback; still missing -> costPartial.
            if (!TryResolve(name, table, out var p))
            {
                missed = true;
                continue;
            }
            any = true;
            var (unitIn, unitOut, unitCr, unitCw) = ConvertUnits(p, targetCny, rate);
            sum += row.Input / 1_000_000d * unitIn
                 + row.Cached / 1_000_000d * unitCr
                 + row.CacheWrite / 1_000_000d * unitCw
                 + row.Output / 1_000_000d * unitOut;
        }
        if (!any) return missed ? (null, true, target) : (null, null, null);
        return (sum, missed, target);
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

    /// <summary>Exact name then <see cref="PriceAliases"/> then LiteLLM fallback; same lookup as <see cref="Estimate"/>.</summary>
    public static bool HasPrice(string? model, IEnumerable<PriceRow> prices, string? defaultCurrency)
        => HasPrice(model, BuildPriceTable(prices, defaultCurrency));

    /// <summary>Exact / alias / LiteLLM fallback against a pre-built table.</summary>
    public static bool HasPrice(
        string? model,
        IReadOnlyDictionary<string, (double Input, double Output, double? CacheRead, double? CacheWrite, bool IsCny)> table)
    {
        // Empty AgentHub table still allows LiteLLM TryGetPrice via TryResolve.
        return TryResolve(model, table, out _);
    }

    /// <summary>Build the price lookup once for repeated <see cref="HasPrice"/> checks.</summary>
    public static Dictionary<string, (double Input, double Output, double? CacheRead, double? CacheWrite, bool IsCny)> BuildPriceTable(
        IEnumerable<PriceRow> prices, string? defaultCurrency)
        => BuildTable(prices, defaultCurrency);

    private static bool TryResolve(
        string? model,
        IReadOnlyDictionary<string, (double Input, double Output, double? CacheRead, double? CacheWrite, bool IsCny)> table,
        out (double Input, double Output, double? CacheRead, double? CacheWrite, bool IsCny) price)
    {
        price = default;
        var name = (model ?? "").Trim();
        if (name.Length == 0) return false;
        if (table.TryGetValue(name, out price)) return true;
        if (PriceAliases.TryMap(name, out var canonical) && table.TryGetValue(canonical, out price))
            return true;
        // Unlisted model: LiteLLM list-price fallback (USD). Keeps settings table lean.
        if (LiteLlmPriceEnricher.TryGetPrice(name, out var lite)
            && lite.InputPer1m is { } inn and > 0
            && lite.OutputPer1m is { } outt and > 0
            && double.IsFinite(inn) && double.IsFinite(outt))
        {
            double? cr = lite.CacheReadPer1m is { } crr && double.IsFinite(crr) ? crr : null;
            double? cw = lite.CacheWritePer1m is { } cww && double.IsFinite(cww) ? cww : null;
            var isCny = string.Equals(lite.Currency, "CNY", StringComparison.OrdinalIgnoreCase);
            price = (inn, outt, cr, cw, isCny);
            return true;
        }
        return false;
    }

    private static Dictionary<string, (double Input, double Output, double? CacheRead, double? CacheWrite, bool IsCny)> BuildTable(
        IEnumerable<PriceRow> prices, string? defaultCurrency)
    {
        var defCny = !string.Equals(defaultCurrency, "USD", StringComparison.OrdinalIgnoreCase);
        var table = new Dictionary<string, (double, double, double?, double?, bool)>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in prices)
        {
            var name = (row.Model ?? "").Trim();
            if (name.Length == 0 || row.InputPer1m is not { } inn || row.OutputPer1m is not { } outt)
                continue;
            if (!double.IsFinite(inn) || !double.IsFinite(outt)) continue;
            double? cr = row.CacheReadPer1m is { } crr && double.IsFinite(crr) ? crr : null;
            double? cw = row.CacheWritePer1m is { } cww && double.IsFinite(cww) ? cww : null;
            var isCny = string.Equals(row.Currency, "CNY", StringComparison.OrdinalIgnoreCase)
                ? true
                : string.Equals(row.Currency, "USD", StringComparison.OrdinalIgnoreCase)
                    ? false
                    : defCny;
            table[name] = (inn, outt, cr, cw, isCny);
        }
        return table;
    }
}
