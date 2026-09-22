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
        if (CodexHasAccountList(sources))
            AddCodexAccounts(bag, sources);
        else
        {
            // 没有账号列表的旧单卡才展开 5h/7d
            AddWindows(bag, sources, "codex", new Dictionary<string, (string Id, string Name)>(StringComparer.Ordinal)
            {
                ["5h"] = ("codex-5h", "Codex 5 小时"),
                ["7d"] = ("codex-7d", "Codex 每周"),
            });
        }
        AddWindows(bag, sources, "zcode", new Dictionary<string, (string Id, string Name)>(StringComparer.Ordinal)
        {
            ["5h"] = ("zcode-5h", "ZCode 5 小时"),
            ["week"] = ("zcode-week", "ZCode 每周"),
        });
        AddWindows(bag, sources, "qoder", new Dictionary<string, (string Id, string Name)>(StringComparer.Ordinal)
        {
            ["credits"] = ("qoder-credits", "Qoder 额度"),
            ["calls"] = ("qoder-calls", "Qoder 体验"),
        });
        AddWindows(bag, sources, "qoder-cn", new Dictionary<string, (string Id, string Name)>(StringComparer.Ordinal)
        {
            ["credits"] = ("qoder-cn-credits", "Qoder CN 额度"),
            ["calls"] = ("qoder-cn-calls", "Qoder CN 体验"),
        });

        var items = new List<Dictionary<string, object?>>();
        var emitted = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in DashboardSettings.NormalizeQuotaOrder(groupOrder))
        {
            // 旧组名 "codex" 吐出全部账号窗；账号级 "codex:key" 只吐自己
            IEnumerable<string> ids = group == "codex"
                ? bag.Keys.Where(k => k.StartsWith("codex-", StringComparison.Ordinal)
                                   || k.StartsWith("codex:", StringComparison.Ordinal)).ToList()
                : Expand(group);
            foreach (var id in ids)
            {
                if (emitted.Add(id) && bag.TryGetValue(id, out var item))
                    items.Add(item);
            }
        }
        return items;
    }

    private static IEnumerable<string> Expand(string group) => group switch
    {
        "zcode" => ["zcode-5h", "zcode-week"],
        "cursor" => ["cursor-total", "cursor-auto", "cursor-api", "cursor-grok"],
        "codex" => ["codex-5h", "codex-7d"],
        "qoder" => ["qoder-credits", "qoder-calls"],
        "qoder-cn" => ["qoder-cn-credits", "qoder-cn-calls"],
        _ when group.StartsWith("codex:", StringComparison.Ordinal) =>
            [$"{group}:5h", $"{group}:7d"],
        _ => [group],
    };

    /// <summary>多 ChatGPT 账号：只写入 status=ok 且有窗口的 5h/7d。失败账号不进首页。</summary>
    private static void AddCodexAccounts(
        Dictionary<string, Dictionary<string, object?>> bag,
        IReadOnlyDictionary<string, Dictionary<string, object?>> sources)
    {
        if (!sources.TryGetValue("codex", out var card)) return;
        if (!card.TryGetValue("accounts", out var raw) || raw is not System.Collections.IEnumerable seq)
            return;
        foreach (var entry in seq)
        {
            if (entry is not Dictionary<string, object?> acc) continue;
            var key = Str(acc, "key");
            if (key is null || key.Length == 0) continue;
            if (!acc.TryGetValue("status", out var st) || st is not string status || status != "ok") continue;
            if (!acc.TryGetValue("windows", out var wraw) || wraw is not System.Collections.IEnumerable windows)
                continue;
            var label = Str(acc, "label") ?? key;
            var plan = Str(acc, "plan");
            var tile = "codex:" + key;
            foreach (var wObj in windows)
            {
                if (wObj is not Dictionary<string, object?> w) continue;
                var wid = Str(w, "id");
                if (wid is not ("5h" or "7d")) continue;
                if (!TryNum(w, "remainPercent", out var remain)) continue;
                var id = tile + ":" + wid;
                var item = Remain(id, wid == "5h" ? "5 小时" : "每周", remain, Str(w, "resetAt"), plan);
                item["tile"] = tile;
                item["label"] = label;
                bag[id] = item;
            }
        }
    }

    /// <summary>新合同带 accounts。有这个键就不再展开旧的单卡窗口，查不到也不造砖。</summary>
    private static bool CodexHasAccountList(
        IReadOnlyDictionary<string, Dictionary<string, object?>> sources)
    {
        if (!sources.TryGetValue("codex", out var card)) return false;
        return card.TryGetValue("accounts", out var raw) && raw is System.Collections.IEnumerable;
    }

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
        if (TryNum(card, "usedPercent", out var used) || TryNum(card, "total", out used))
        {
            bag["cursor-total"] = Remain(
                "cursor-total", "Cursor 总用量",
                100 - used, period, plan);
        }
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
        if (card.ContainsKey("grokPercent"))
        {
            bag["cursor-grok"] = Remain(
                "cursor-grok", "Cursor Grok Bot",
                100 - ToDouble(card["grokPercent"]),
                Str(card, "grokResetAt") ?? period, plan);
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
