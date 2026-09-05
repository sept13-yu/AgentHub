using AgentHub.Core.ProxyCore;

namespace AgentHub.Core.TokenCore;

/// <summary>把各家内部卡收成首页扁平 items。只留 status=ok 的条。</summary>
public static class QuotaPresenter
{
    public static List<Dictionary<string, object?>> Flatten(
        IReadOnlyDictionary<string, Dictionary<string, object?>> sources,
        IEnumerable<string>? groupOrder)
    {
        var bag = new Dictionary<string, Dictionary<string, object?>>(StringComparer.Ordinal);
        AddBalance(bag, sources, "deepseek", "DeepSeek");
        AddBalance(bag, sources, "relay", "Sub2API");
        AddBalance(bag, sources, "trae", "Trae");
        AddBalance(bag, sources, "workbuddy", "WorkBuddy");
        AddCursor(bag, sources);
        AddWindows(bag, sources, "codex", new Dictionary<string, (string Id, string Name)>(StringComparer.Ordinal)
        {
            ["5h"] = ("codex-5h", "Codex 5 小时"),
            ["7d"] = ("codex-7d", "Codex 每周"),
        });
        AddWindows(bag, sources, "zcode", new Dictionary<string, (string Id, string Name)>(StringComparer.Ordinal)
        {
            ["5h"] = ("zcode-5h", "ZCode 5 小时"),
            ["week"] = ("zcode-week", "ZCode 每周"),
        });

        var items = new List<Dictionary<string, object?>>();
        foreach (var group in DashboardSettings.NormalizeQuotaOrder(groupOrder))
        {
            foreach (var id in Expand(group))
            {
                if (bag.TryGetValue(id, out var item))
                    items.Add(item);
            }
        }
        return items;
    }

    private static IEnumerable<string> Expand(string group) => group switch
    {
        "zcode" => ["zcode-5h", "zcode-week"],
        "cursor" => ["cursor-auto", "cursor-api"],
        "codex" => ["codex-5h", "codex-7d"],
        _ => [group],
    };

    private static void AddBalance(
        Dictionary<string, Dictionary<string, object?>> bag,
        IReadOnlyDictionary<string, Dictionary<string, object?>> sources,
        string id, string name)
    {
        if (!TryOk(sources, id, out var card)) return;
        if (!TryNum(card, "balance", out var value)) return;
        var unit = Str(card, "unit") ?? Str(card, "currency") ?? "";
        var item = new Dictionary<string, object?>
        {
            ["id"] = id,
            ["kind"] = "balance",
            ["name"] = name,
            ["value"] = value,
            ["unit"] = unit,
        };
        var plan = Str(card, "plan");
        if (plan is not null) item["plan"] = plan;   // 拿不到套餐就不进响应
        bag[id] = item;
    }

    private static void AddCursor(
        Dictionary<string, Dictionary<string, object?>> bag,
        IReadOnlyDictionary<string, Dictionary<string, object?>> sources)
    {
        if (!TryOk(sources, "cursor", out var card)) return;
        var period = Str(card, "cycleEnd") ?? Str(card, "cycleStart") ?? "";
        var plan = Str(card, "plan");
        if (card.ContainsKey("autoPercent"))
        {
            bag["cursor-auto"] = Remain(
                "cursor-auto", "Cursor Auto",
                100 - ToDouble(card["autoPercent"]), period, plan);
        }
        if (card.ContainsKey("apiPercent"))
        {
            bag["cursor-api"] = Remain(
                "cursor-api", "Cursor API",
                100 - ToDouble(card["apiPercent"]), period, plan);
        }
    }

    private static void AddWindows(
        Dictionary<string, Dictionary<string, object?>> bag,
        IReadOnlyDictionary<string, Dictionary<string, object?>> sources,
        string sourceId,
        Dictionary<string, (string Id, string Name)> map)
    {
        if (!TryOk(sources, sourceId, out var card)) return;
        if (!card.TryGetValue("windows", out var raw) || raw is not IEnumerable<Dictionary<string, object?>> windows)
            return;
        var plan = Str(card, "plan");
        foreach (var w in windows)
        {
            var key = Str(w, "id");
            if (key is null || !map.TryGetValue(key, out var named)) continue;
            if (!TryNum(w, "remainPercent", out var remain)) continue;
            bag[named.Id] = Remain(named.Id, named.Name, remain, Str(w, "resetAt"), plan);
        }
    }

    private static Dictionary<string, object?> Remain(string id, string name, double remainPercent, string? period, string? plan = null) =>
        new()
        {
            ["id"] = id,
            ["kind"] = "remain",
            ["name"] = name,
            ["remainPercent"] = remainPercent,
            ["period"] = period ?? "",
            ["plan"] = plan,
        };

    private static bool TryOk(
        IReadOnlyDictionary<string, Dictionary<string, object?>> sources,
        string id, out Dictionary<string, object?> card)
    {
        card = [];
        return sources.TryGetValue(id, out card!)
            && card.TryGetValue("status", out var st)
            && st is string s
            && s == "ok";
    }

    private static bool TryNum(Dictionary<string, object?> card, string key, out double value)
    {
        value = 0;
        if (!card.TryGetValue(key, out var raw) || raw is null) return false;
        value = ToDouble(raw);
        return true;
    }

    private static double ToDouble(object? raw) => raw switch
    {
        double d => d,
        float f => f,
        decimal m => (double)m,
        int i => i,
        long l => l,
        string s when double.TryParse(s, System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var p) => p,
        _ => Convert.ToDouble(raw, System.Globalization.CultureInfo.InvariantCulture),
    };

    private static string? Str(Dictionary<string, object?> card, string key) =>
        card.TryGetValue(key, out var raw) && raw is string s && s.Length > 0 ? s : null;
}
