namespace AgentHub.Core.TokenCore;

/// <summary>
/// 用量日志里的变体模型名 → prices.json 已有 canonical。
/// 查找顺序：价格表精确命中（OrdinalIgnoreCase）→ 本表 → 仍没有就算未标价（costPartial）。
/// 不在这里发明新单价；canonical 必须已经在价格表里。
/// </summary>
public static class PriceAliases
{
    /// <summary>
    /// 变体 → 表内已有模型。大小写不敏感。
    /// glm-5.2 / glm-5.2-* 与表内 GLM-5.3 不是同一档牌价，不映射。
    /// auto 是路由名不是某一档模型，宁可不计价也不乱套。
    /// stealth、ox-alpha、codex-auto-review、旧 gpt-5.4/5.5 过稀，不加。
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> Map =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            // vision-exp 另有与 flash 同价的显式行；别名兜底尚未拉到新表的旧缓存。
            ["deepseek-v4-flash-vision-exp"] = "deepseek-v4-flash",
            ["kimi-k3-1"] = "kimi-k3",
            ["kimi-k3-high"] = "kimi-k3",
            ["kimi-k3-max"] = "kimi-k3",
            ["hy3"] = "hy4-preview",
            ["hy4-preview-x"] = "hy4-preview",
            ["cursor-grok-4.5-high"] = "cursor-grok-4.6-high",
            ["cursor-grok-4.5-high-fast"] = "cursor-grok-4.6-high-fast",
        };

    /// <summary>仅当 model 是别名时返回 true。精确名应先查价格表，不要先走这里。</summary>
    public static bool TryMap(string? model, out string canonical)
    {
        canonical = "";
        var name = (model ?? "").Trim();
        if (name.Length == 0) return false;
        if (!Map.TryGetValue(name, out var target) || string.IsNullOrWhiteSpace(target))
            return false;
        canonical = target.Trim();
        return canonical.Length > 0;
    }
}
