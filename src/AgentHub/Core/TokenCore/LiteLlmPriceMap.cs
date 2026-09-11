namespace AgentHub.Core.TokenCore;

/// <summary>AgentHub 价表模型名 → LiteLLM model_prices 键。
/// 未出现的模型（如 composer / hy4）不自动补 cache，沿用价表或回退 input。</summary>
public static class LiteLlmPriceMap
{
    /// <summary>键：AgentHub model（OrdinalIgnoreCase）；值：LiteLLM JSON 顶层键。</summary>
    public static readonly IReadOnlyDictionary<string, string> Map =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["claude-opus-5-thinking-high"] = "claude-opus-5",

            ["gpt-5.6-sol"] = "gpt-5.6-sol",
            ["gpt-5.6-sol-fast"] = "gpt-5.6-sol",
            ["gpt-5.6-terra"] = "gpt-5.6-terra",
            ["gpt-5.6-terra-fast"] = "gpt-5.6-terra",
            ["gpt-5.6-luna"] = "gpt-5.6-luna",
            ["gpt-5.6-luna-fast"] = "gpt-5.6-luna",

            ["gemini-3.7-flash-high"] = "gemini-3.7-flash",

            ["cursor-grok-4.6-xhigh"] = "xai/grok-4.6",
            ["cursor-grok-4.6-high"] = "xai/grok-4.6",
            ["cursor-grok-4.6-xhigh-fast"] = "xai/grok-4.6",
            ["cursor-grok-4.6-high-fast"] = "xai/grok-4.6",
            ["grok-bot-default"] = "xai/grok-4.6",

            ["deepseek-v4.1-flash-expires-on-0910"] = "deepseek-v4-flash",
            ["deepseek-v4-flash"] = "deepseek-v4-flash",
            ["deepseek-v4-flash-vision-exp"] = "deepseek-v4-flash",
            ["deepseek-v4-pro"] = "deepseek-v4-pro",
            ["DeepSeek-V4-Flash 正式版"] = "deepseek-v4-flash",
            ["DeepSeek-V4-Pro 正式版"] = "deepseek-v4-pro",

            ["GLM-5.3"] = "zai/glm-5.3",
            ["GLM-5.3-Flash"] = "zai/glm-5.3-flash",

            // k3 尚无独立条目时，用 k2.6 的 cache 比率（再按本表 input 缩放）
            ["kimi-k3"] = "azure_ai/kimi-k2.6",

            ["Qwen3.8-Max"] = "dashscope/qwen3.8-max",
            ["gpt-5.4"] = "gpt-5.4",
            ["gpt-5.5"] = "gpt-5.5",
            ["claude-sonnet-4-6"] = "claude-sonnet-4-6",
            ["claude-sonnet-4-5"] = "claude-sonnet-4-5",
            ["claude-opus-4-8"] = "claude-opus-4-8",
            ["claude-opus-4-6"] = "claude-opus-4-6",
            ["claude-fable-5"] = "claude-fable-5",
            ["mimo-x-pro-preview"] = "openrouter/xiaomi/mimo-v2.5-pro",
            ["mimo-x-flash-preview"] = "openrouter/xiaomi/mimo-v2-flash",
        };

    public static bool TryMap(string? model, out string liteLlmKey)
    {
        liteLlmKey = "";
        var name = (model ?? "").Trim();
        if (name.Length == 0) return false;
        if (!Map.TryGetValue(name, out var key) || string.IsNullOrWhiteSpace(key))
            return false;
        liteLlmKey = key.Trim();
        return liteLlmKey.Length > 0;
    }
}
