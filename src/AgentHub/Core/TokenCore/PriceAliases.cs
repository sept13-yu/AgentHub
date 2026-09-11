namespace AgentHub.Core.TokenCore;

/// <summary>
/// 用量日志里的别名模型名 → prices.json 里的 canonical。
/// 匹配顺序：价表精确命中（OrdinalIgnoreCase）→ 别名 → 仍没有就算未定价（costPartial）。
/// 厂商官网发了新牌价时，canonical 应先已经在价表里。
/// </summary>
public static class PriceAliases
{
    /// <summary>
    /// 别名 → 正式模型。大小写不敏感。
    /// glm-5.2 / glm-5.2-* 暂与 GLM-5.3 共用国内牌价。
    /// auto / unknown / agent_review / stealth/ox-alpha / codex-auto-review 无稳定牌价，不映射。
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> Map =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            // vision-exp 与正式 flash 同价；正式行在表里，别名兜底旧缓存。
            ["deepseek-v4-flash-vision-exp"] = "deepseek-v4-flash",
            ["kimi-k3-1"] = "kimi-k3",
            ["kimi-k3-high"] = "kimi-k3",
            ["kimi-k3-max"] = "kimi-k3",
            ["hy3"] = "hy4-preview",
            ["hy4-preview-x"] = "hy4-preview",
            ["cursor-grok-4.5-high"] = "cursor-grok-4.6-high",
            ["cursor-grok-4.5-high-fast"] = "cursor-grok-4.6-high-fast",

            ["gpt-5.4-medium"] = "gpt-5.4",
            ["gpt-5.5-medium"] = "gpt-5.5",
            ["gpt-5.6-sol-medium"] = "gpt-5.6-sol",
            ["gpt-5.6-sol-high"] = "gpt-5.6-sol",
            ["gpt-5.6-luna-max"] = "gpt-5.6-luna",
            ["composer-2-fast"] = "composer-2.5-fast",
            ["glm-5.2"] = "GLM-5.3",
            ["glm-5.2-max"] = "GLM-5.3",
            ["glm-5.2-high"] = "GLM-5.3",
            ["claude-4.6-sonnet-medium-thinking"] = "claude-sonnet-4-6",
            ["claude-opus-4-8-thinking-high"] = "claude-opus-4-8",
            ["claude-opus-4-8-thinking-medium"] = "claude-opus-4-8",
            ["claude-4.6-opus-high-thinking"] = "claude-opus-4-6",
            ["claude-fable-5-thinking-high"] = "claude-fable-5",
            ["claude-4.5-sonnet"] = "claude-sonnet-4-5",
        };

    /// <summary>当 model 是别名时返回 true；精确命中应先查价表，不要先走这里。</summary>
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
