namespace AgentHub.Core.Platform;

/// <summary>
/// 本机装了哪几家 Agent。判据是「数据根里还有不是 AgentHub 写的条目」：
/// 我们会往各家根目录落托管文件（AGENTS.md、rules/、skills 镜像、mcp.json、config.toml），
/// App 卸载后这些照旧留在原地，只看根目录存在会把删掉的家认成在装。
/// 托管名单只维护这一份，App 自己新增的文件不用改代码就能认出来。
/// </summary>
public static class AgentPresence
{
    private static readonly HashSet<string> HubOwned = new(StringComparer.OrdinalIgnoreCase)
    {
        "AGENTS.md",
        "AGENTS.mdc",
        "rules",
        "user_rules",
        "skills",
        "mcp.json",
        "mimocode.jsonc",
        "config.toml",
    };

    /// <summary>根目录不存在，或里面只剩 AgentHub 托管的条目 → 这家没装。</summary>
    public static bool HasOwnFootprint(string root)
    {
        if (!Directory.Exists(root)) return false;
        try
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(root))
                if (!IsHubOwned(Path.GetFileName(entry))) return true;
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;    // 读不动时退回旧口径，宁可把在装的说成在装
        }
    }

    private static bool IsHubOwned(string name)
    {
        // 托管文件的历史备份：mcp.json.bak-movemcp、mimocode.jsonc.bak-qodercn-2026…
        var cut = name.IndexOf(".bak", StringComparison.OrdinalIgnoreCase);
        if (cut > 0) name = name[..cut];
        return HubOwned.Contains(name);
    }

    /// <summary>设置页要画徽标的家。没列在这里的 id（deepseek、relay 这类纯云端）不参与本机探测。</summary>
    private static readonly string[] ProbeIds =
    [
        "dsh", "trae", "workbuddy", "zcode", "mimocode", "grok",
        "qoder", "qoder-cn", "cursor", "cursor-cloud", "codex",
    ];

    /// <summary>按各家 id 探一遍，给设置页排序和灰态徽标用。</summary>
    public static IReadOnlyDictionary<string, bool> Snapshot()
    {
        var map = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in ProbeIds) map[id] = IsInstalled(id);
        return map;
    }

    /// <summary>
    /// 这家的 id → 数据根。Qoder 国际版和 Trae 的 Electron 数据在 %APPDATA%，不在 ~ 下点目录。
    /// 已知 id 之外的家一律按未装处理，避免新加一家时悄悄漏探测。
    /// </summary>
    public static bool IsInstalled(string agentId)
    {
        var appData = PlatformPaths.RoamingAppData;
        return agentId.ToLowerInvariant() switch
        {
            "codex" => HasOwnFootprint(Path.Combine(PlatformPaths.Home, ".codex")),
            "cursor" or "cursor-cloud" => HasOwnFootprint(Path.Combine(PlatformPaths.Home, ".cursor")),
            "dsh" => HasOwnFootprint(Path.Combine(PlatformPaths.Home, ".dsh")),
            "mimocode" => HasOwnFootprint(Path.Combine(PlatformPaths.Home, ".config", "mimocode")),
            "workbuddy" => HasOwnFootprint(Path.Combine(PlatformPaths.Home, ".workbuddy")),
            "zcode" => HasOwnFootprint(Path.Combine(PlatformPaths.Home, ".zcode")),
            "grok" => HasOwnFootprint(Path.Combine(PlatformPaths.Home, ".grok")),
            "trae" => HasOwnFootprint(Path.Combine(PlatformPaths.Home, ".trae-cn"))
                      || Directory.Exists(Path.Combine(appData, "TRAE SOLO CN")),
            "qoder" => Directory.Exists(Path.Combine(appData, "Qoder")),
            "qoder-cn" => HasOwnFootprint(Path.Combine(PlatformPaths.Home, ".qoder-cn"))
                          || Directory.Exists(Path.Combine(appData, "com.qodercn.app.stable")),
            _ => false,
        };
    }
}
