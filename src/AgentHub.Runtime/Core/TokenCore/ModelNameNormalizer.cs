using System;

namespace AgentHub.Core.TokenCore;

/// <summary>
/// 用量/会话里的模型名常带厂商或自定义路径前缀（qoder/...、qoder-custom-uuid/workbuddy/...），
/// 以及脏后缀（deepseek-flash--max）。显示与估价时只留最后一段真实模型 id。
/// </summary>
internal static class ModelNameNormalizer
{
    /// <summary>去掉所有 '/' 前缀路径，只保留最后一段；再剥掉 '--…' 脏后缀；无斜杠则原样 trim。</summary>
    public static string Leaf(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var name = raw.Trim();
        while (name.EndsWith('/'))
            name = name[..^1].TrimEnd();
        if (name.Length == 0) return "";
        var last = name.LastIndexOf('/');
        if (last >= 0 && last < name.Length - 1)
            name = name[(last + 1)..].Trim();
        // deepseek-flash--max → deepseek-flash（双横杠后多为脏后缀）
        var dash = name.IndexOf("--", StringComparison.Ordinal);
        if (dash > 0)
            name = name[..dash].TrimEnd();
        return name;
    }
}
