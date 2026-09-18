using AgentHub.Core.ProxyCore;

namespace AgentHub.Core.TokenCore;

/// <summary>Estimate spend from vendor list prices (input, output, cache-read, cache-write).
/// Price table keeps vendor currency (USD overseas / CNY domestic); no hard-coded FX in the table.
/// At estimate time convert to defaultCurrency (costCurrency setting) with fxUsdToCny from caller.
/// Aligns with LiteLLM/TokenTracker: netInput*input + cacheRead*cacheRead + cacheWrite*cacheWrite
/// + (output+reasoning)*output; missing cache unit price falls back to input price.
/// Rows with ReportedUsd set (including 0, e.g. Grok costUsdTicks) use provider USD instead of list prices.</summary>
public static class UsageCost
{
    public static (double? Cost, bool? Partial, string? Currency) Estimate(
        IEnumerable<(string Model, long Input, long Output, long Cached, long CacheWrite, long Reasoning, double? ReportedUsd)> rows,
        IEnumerable<PriceRow> prices,
        bool enabled,
        string? defaultCurrency,
        double fxUsdToCny)
    {
        if (!enabled) return (null, null, null);
        var table = BuildTable(prices, defaultCurrency);
        if (table.Count == 0 && !rows.Any(r => r.ReportedUsd is not null)) return (null, null, null);

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
            var name = row.Model.Trim();
            if (name.EndsWith(" · 子代理", StringComparison.Ordinal))
                name = name[..^" · 子代理".Length].Trim();
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
                 + (row.Output + row.Reasoning) / 1_000_000d * unitOut;
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


    
    /// <summary>
    /// 用量名常带厂商/区域前缀：cn:deepseek-v4-flash、qoder/qwen3.8-flash、openai/gpt-6-astra。
    /// 估价时去掉短前缀（或取 / 后段）再匹配价表 / 别名 / LiteLLM；原名优先精确命中。
    /// </summary>
    internal static IEnumerable<string> ModelNameCandidates(string? model)
    {
        var name = (model ?? "").Trim();
        if (name.Length == 0) yield break;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in ExpandModelNameCandidates(name))
        {
            if (seen.Add(c)) yield return c;
        }
    }

    private static IEnumerable<string> ExpandModelNameCandidates(string name)
    {
        yield return name;

        // cn:xxx / us:xxx
        var colon = name.IndexOf(':');
        if (colon is > 0 and <= 8)
        {
            var prefix = name[..colon];
            var rest = name[(colon + 1)..].Trim();
            if (rest.Length > 0
                && prefix.All(c => char.IsLetterOrDigit(c) || c == '-' || c == '_'))
                yield return rest;
        }

        // qoder/qwen3.8-flash、openai/gpt-6-astra：去掉第一段厂商前缀，并再试最后一段
        var slash = name.IndexOf('/');
        if (slash is > 0 and <= 24)
        {
            var vendor = name[..slash];
            var rest = name[(slash + 1)..].Trim();
            if (rest.Length > 0
                && vendor.All(c => char.IsLetterOrDigit(c) || c == '-' || c == '_'))
            {
                yield return rest;
                var last = rest.LastIndexOf('/');
                if (last >= 0 && last < rest.Length - 1)
                    yield return rest[(last + 1)..].Trim();
            }
        }
    }

    private static bool TryResolve(
        string? model,
        IReadOnlyDictionary<string, (double Input, double Output, double? CacheRead, double? CacheWrite, bool IsCny)> table,
        out (double Input, double Output, double? CacheRead, double? CacheWrite, bool IsCny) price)
    {
        price = default;
        foreach (var name in ModelNameCandidates(model))
        {
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
