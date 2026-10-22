using System;

namespace AgentHub.Core.TokenCore;

/// <summary>
/// 用量/会话里的模型名常带厂商或自定义路径前缀（qoder/...、qoder-custom-uuid/workbuddy/...），
/// 以及脏后缀（deepseek-flash--max）。显示与估价时只留最后一段真实模型 id。
/// </summary>
internal static class ModelNameNormalizer
{
    /// <summary>去掉所有 '/' 前缀路径，只保留最后一段；再剥掉 @上下文标记与 '--…' 脏后缀。</summary>
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
        name = StripContext(name);
        // deepseek-flash--max → deepseek-flash（双横杠后多为脏后缀）
        var dash = name.IndexOf("--", StringComparison.Ordinal);
        if (dash > 0)
            name = name[..dash].TrimEnd();
        return name;
    }

    /// <summary>grok-4.7@500k 的 @ 后是上下文窗口，不是模型身份。只剥末尾一段，避免误切邮箱。</summary>
    public static string StripContext(string name)
    {
        var at = name.LastIndexOf('@');
        if (at <= 0 || at >= name.Length - 1) return name;
        var suffix = name[(at + 1)..];
        if (!IsContextMarker(suffix)) return name;
        return name[..at].TrimEnd();
    }

    private static bool IsContextMarker(string suffix)
    {
        if (suffix.Length is < 2 or > 12) return false;
        var unit = suffix[^1];
        if (unit is not ('k' or 'K' or 'm' or 'M')) return false;
        for (var i = 0; i < suffix.Length - 1; i++)
        {
            var c = suffix[i];
            if (!char.IsAsciiDigit(c) && c != '.') return false;
        }
        return suffix.Count(static c => c == '.') <= 1 && char.IsAsciiDigit(suffix[0]);
    }
}
