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







    /// unknown / agent_review 仍无公开价不映射；mimo-x-*-preview 走官方 mimo-v2.6-*；auto 按 Cursor Grok（grok-bot-default）估价；







    /// stealth/ox-alpha → GLM-5.3-Flash；codex-auto-review → gpt-5.6-luna（OpenAI 自动审后端）。







    /// </summary>







    public static readonly IReadOnlyDictionary<string, string> Map =







        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)







        {







            // vision-exp 与正式 flash 同价；正式行在表里，别名兜底旧缓存。







            // DeepSeek V4.1 Flash：官方 API id deepseek-flash；用量名 deepseek-v4.1-flash / expires-on 变体均走 Flash 列表价。







            ["deepseek-v4.1-flash"] = "deepseek-v4.1-flash",







            ["deepseek-flash"] = "deepseek-v4.1-flash",
            ["deepseek-flash--max"] = "deepseek-flash",







            ["deepseek-v4.1-flash-expires-on-0910"] = "deepseek-v4.1-flash",







            // vision-exp / 旧 flash 名已按 Flash 计费；canonical 用列表中的 deepseek-v4.1-flash。







            ["deepseek-v4-flash-vision-exp"] = "deepseek-v4.1-flash",







            ["deepseek-v4-flash"] = "deepseek-v4.1-flash",







            ["DeepSeek-V4-Flash 正式版"] = "deepseek-v4.1-flash",
            ["DeepSeek-V4-Flash"] = "deepseek-v4.1-flash",
            ["DeepSeek-V4-Pro 正式版"] = "deepseek-v4-pro",
            ["DeepSeek-V4-Pro"] = "deepseek-v4-pro",

            ["qwen3.8-flash"] = "Qwen3.8-Flash",

            ["qoder/qwen3.8-flash"] = "Qwen3.8-Flash",
            ["qfmodel"] = "Qwen3.8-Flash",
            ["qoder/qfmodel"] = "Qwen3.8-Flash",
            ["qmodel_38max"] = "Qwen3.8-Max",
            ["qoder/qmodel_38max"] = "Qwen3.8-Max",







            ["kimi-k3-1"] = "kimi-k3",







            ["kimi-k3-high"] = "kimi-k3",







            ["kimi-k3-max"] = "kimi-k3",







            ["hy3"] = "hy4-preview",







            ["hy4-preview-x"] = "hy4-preview",







            ["cursor-grok-4.5-high"] = "cursor-grok-4.6-high",







            ["cursor-grok-4.5-high-fast"] = "cursor-grok-4.6-high-fast",
            ["cursor-grok-4.7-low"] = "cursor-grok-4.7-high",
            ["cursor-grok-4.7-medium"] = "cursor-grok-4.7-high",
            ["cursor-grok-4.7-max"] = "cursor-grok-4.7-xhigh",
            ["cursor-grok-4.7-low-fast"] = "cursor-grok-4.7-high-fast",
            ["cursor-grok-4.7-medium-fast"] = "cursor-grok-4.7-high-fast",
            ["grok-4.7"] = "cursor-grok-4.7-high",
            ["xai/grok-4.7"] = "cursor-grok-4.7-high",















            ["gpt-5.4-medium"] = "gpt-5.4",







            ["gpt-5.5-medium"] = "gpt-5.5",







            ["gpt-5.6-sol-medium"] = "gpt-5.6-sol",







            ["gpt-5.6-sol-high"] = "gpt-5.6-sol",







            ["gpt-5.6-luna-max"] = "gpt-5.6-luna",
            ["gpt-6-sol-medium"] = "gpt-6-sol",
            ["gpt-6-sol-high"] = "gpt-6-sol",
            ["gpt-6-sol-low"] = "gpt-6-sol",
            ["gpt-6-sol-xhigh"] = "gpt-6-sol",
            ["gpt-6-sol-max"] = "gpt-6-sol",
            ["gpt-6-sol-high-fast"] = "gpt-6-sol-fast",
            ["gpt-6-sol-medium-fast"] = "gpt-6-sol-fast",
            ["gpt-6-sol-low-fast"] = "gpt-6-sol-fast",
            ["gpt-6-sol-xhigh-fast"] = "gpt-6-sol-fast",
            ["gpt-6-sol-max-fast"] = "gpt-6-sol-fast",
            ["openai/gpt-6-sol"] = "gpt-6-sol",
            ["gpt-6-luna-max"] = "gpt-6-luna",
            ["gpt-6-luna-medium"] = "gpt-6-luna",
            ["gpt-6-luna-high"] = "gpt-6-luna",
            ["gpt-6-luna-low"] = "gpt-6-luna",
            ["gpt-6-luna-xhigh"] = "gpt-6-luna",
            ["gpt-6-luna-high-fast"] = "gpt-6-luna-fast",
            ["gpt-6-luna-medium-fast"] = "gpt-6-luna-fast",
            ["gpt-6-luna-low-fast"] = "gpt-6-luna-fast",
            ["gpt-6-luna-xhigh-fast"] = "gpt-6-luna-fast",
            ["gpt-6-luna-max-fast"] = "gpt-6-luna-fast",
            ["openai/gpt-6-luna"] = "gpt-6-luna",







            ["composer-2-fast"] = "composer-2.5-fast",







            ["glm-5.2"] = "GLM-5.3",







            ["glm-5.2-max"] = "GLM-5.3",







            ["glm-5.2-high"] = "GLM-5.3",







            ["claude-4.6-sonnet-medium-thinking"] = "claude-sonnet-4-6",







            ["claude-opus-4-8-thinking-high"] = "claude-opus-4-8",







            ["claude-opus-4-8-thinking-medium"] = "claude-opus-4-8",
            ["claude-opus-5-thinking-medium"] = "claude-opus-5-thinking-high",
            ["claude-opus-5-thinking-low"] = "claude-opus-5-thinking-high",
            ["claude-opus-5-thinking-max"] = "claude-opus-5-thinking-high",
            ["claude-opus-5-thinking-xhigh"] = "claude-opus-5-thinking-high",
            ["claude-opus-5-5"] = "claude-opus-5-5-thinking-high",
            ["claude-opus-5.5"] = "claude-opus-5-5-thinking-high",
            ["claude-opus-5-5-thinking-medium"] = "claude-opus-5-5-thinking-high",
            ["claude-opus-5-5-thinking-low"] = "claude-opus-5-5-thinking-high",
            ["claude-opus-5-5-thinking-max"] = "claude-opus-5-5-thinking-high",
            ["claude-opus-5-5-thinking-xhigh"] = "claude-opus-5-5-thinking-high",
            ["claude-opus-5-5-high"] = "claude-opus-5-5-thinking-high",
            ["claude-opus-5-5-medium"] = "claude-opus-5-5-thinking-high",
            ["claude-opus-5-5-low"] = "claude-opus-5-5-thinking-high",
            ["claude-opus-5-5-max"] = "claude-opus-5-5-thinking-high",
            ["claude-opus-5-5-xhigh"] = "claude-opus-5-5-thinking-high",
            ["anthropic/claude-opus-5-5"] = "claude-opus-5-5-thinking-high",







            ["claude-4.6-opus-high-thinking"] = "claude-opus-4-6",







            ["claude-fable-5-thinking-high"] = "claude-fable-5",







            ["claude-fable-5-1-thinking-high"] = "claude-fable-5-1",







            ["claude-4.5-sonnet"] = "claude-sonnet-4-5",







            ["stealth/ox-alpha"] = "GLM-5.3-Flash",







            ["codex-auto-review"] = "gpt-5.6-luna",















            ["gpt-6-astra-high"] = "gpt-6-astra",







            ["gpt-6-astra-xhigh"] = "gpt-6-astra",







            ["gpt-6-astrahigh"] = "gpt-6-astra",







            ["gpt-6-astra-ultra"] = "gpt-6-astra",







            ["openai/gpt-6-astra"] = "gpt-6-astra",

            // Xiaomi MiMo 邀测 / 桌面展示名 → 官方 API id（价表 canonical 为 mimo-v2.6-*）
            ["mimo-x-flash-preview"] = "mimo-v2.6-flash",
            ["mimo-x-flash"] = "mimo-v2.6-flash",
            ["mimo-x-flash preview"] = "mimo-v2.6-flash",
            ["mimo x flash preview"] = "mimo-v2.6-flash",
            ["X-Flash Preview"] = "mimo-v2.6-flash",
            ["X Flash Preview"] = "mimo-v2.6-flash",
            ["mimo-v2.6-flash-preview"] = "mimo-v2.6-flash",
            ["MiMo V2.6 Flash"] = "mimo-v2.6-flash",
            ["mimo-x-pro-preview"] = "mimo-v2.6-pro",
            ["mimo-x-pro"] = "mimo-v2.6-pro",
            ["mimo-x-pro preview"] = "mimo-v2.6-pro",
            ["mimo x pro preview"] = "mimo-v2.6-pro",
            ["X-Pro Preview"] = "mimo-v2.6-pro",
            ["X Pro Preview"] = "mimo-v2.6-pro",
            ["mimo-v2.6-pro-preview"] = "mimo-v2.6-pro",
            ["MiMo V2.6 Pro"] = "mimo-v2.6-pro",
            ["mimo-x-pro-ultraspeed-preview"] = "mimo-v2.6-pro-ultraspeed",
            ["mimo-x-pro-ultraspeed"] = "mimo-v2.6-pro-ultraspeed",
            ["mimo-x-pro-ultra-speed-preview"] = "mimo-v2.6-pro-ultraspeed",
            ["mimo-v2.6-pro-ultra-speed"] = "mimo-v2.6-pro-ultraspeed",
            ["mimo-v2.6-ultraspeed"] = "mimo-v2.6-pro-ultraspeed",
            ["X-Pro Ultraspeed Preview"] = "mimo-v2.6-pro-ultraspeed",
            ["X-Pro UltraSpeed Preview"] = "mimo-v2.6-pro-ultraspeed",
            ["MiMo V2.6 Pro Ultraspeed"] = "mimo-v2.6-pro-ultraspeed",

            ["grok-bot-automation"] = "grok-bot-default",
            ["grok-bot-cua"] = "grok-bot-default",
            ["auto"] = "grok-bot-default",







        };















    /// <summary>当 model 是别名时返回 true；精确命中应先查价表，不要先走这里。</summary>







    public static bool TryMap(string? model, out string canonical)



    {



        canonical = "";



        var name = (model ?? "").Trim();



        if (name.Length == 0) return false;



        if (Map.TryGetValue(name, out var target) && !string.IsNullOrWhiteSpace(target))



        {



            canonical = target.Trim();



            return canonical.Length > 0;



        }







        // cn:glm-5.3 等：去掉区域前缀后再查别名表



        var colon = name.IndexOf(':');



        if (colon is > 0 and <= 8)



        {



            var rest = name[(colon + 1)..].Trim();



            if (rest.Length > 0



                && Map.TryGetValue(rest, out target)



                && !string.IsNullOrWhiteSpace(target))



            {



                canonical = target.Trim();



                return canonical.Length > 0;



            }



        }







        

        // qoder/qwen3.8-flash 等：去掉厂商前缀后再查别名表

        // 任意 path/.../model：用最后一段查别名（含 qoder-custom-uuid/workbuddy/...）
        var leaf = ModelNameNormalizer.Leaf(name);
        if (leaf.Length > 0
            && !leaf.Equals(name, StringComparison.OrdinalIgnoreCase)
            && Map.TryGetValue(leaf, out target)
            && !string.IsNullOrWhiteSpace(target))
        {
            canonical = target.Trim();
            return canonical.Length > 0;
        }

        // 兼容短厂商前缀仅剥一层：qoder/qwen3.8-flash
        var slash = name.IndexOf('/');
        if (slash is > 0 and <= 24)
        {
            var rest = name[(slash + 1)..].Trim();
            if (rest.Length > 0
                && Map.TryGetValue(rest, out target)
                && !string.IsNullOrWhiteSpace(target))
            {
                canonical = target.Trim();
                return canonical.Length > 0;
            }
        }



        // grok-bot-automation 等：无独立价时一律 grok-bot-default
        // qfmodel 等国内系统路由名：无公开牌价，不映射（HasPrice=false → 仪表盘 noPrice）



        if (name.StartsWith("grok-bot-", StringComparison.OrdinalIgnoreCase)



            && !name.Equals("grok-bot-default", StringComparison.OrdinalIgnoreCase))



        {



            canonical = "grok-bot-default";



            return true;



        }



        return false;



    }



}



