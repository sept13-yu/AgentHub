using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentHub.Core.CodexConfigCore;
using AgentHub.Core.Platform;

namespace AgentHub.Core.ProxyCore;

/// <summary>资料中心设置。Skill 路径由应用固定管理，资料目录可配置。</summary>
public sealed class DocsSettings
{
    public static string DefaultLibraryRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Agents");

    public string LibraryRoot { get; set; } = DefaultLibraryRoot;
    public List<string> Exclude { get; set; } = [".workbuddy", "node_modules", ".git"];
    /// <summary>是否把各家规则写成指向共用规则。默认关，不进 /api/settings。</summary>
    public bool UnifiedRules { get; set; }

    public static string NormalizeLibraryRoot(string? raw)
    {
        var value = Environment.ExpandEnvironmentVariables((raw ?? "").Trim());
        if (value.Length == 0) value = DefaultLibraryRoot;
        if (value == "~" || value.StartsWith("~\\", StringComparison.Ordinal)
            || value.StartsWith("~/", StringComparison.Ordinal))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            value = value.Length == 1 ? home : Path.Combine(home, value[2..]);
        }
        if (!Path.IsPathRooted(value))
            throw new ArgumentException("资料目录必须是绝对路径、~ 路径或环境变量路径");
        var full = Path.GetFullPath(value);
        var root = Path.GetPathRoot(full);
        return string.Equals(full, root, StringComparison.OrdinalIgnoreCase)
            ? full
            : full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}

/// <summary>价格表一行：模型名 + 每 100 万 token 的输入/输出单价。输入按总量计价。无效行原样保存，算钱时再跳过。</summary>
public sealed class PriceRow
{
    public string Model { get; set; } = "";
    public double? InputPer1m { get; set; }
    public double? OutputPer1m { get; set; }
    // Cache hit / write per 1M; null => fall back to InputPer1m (LiteLLM/TokenTracker)
    public double? CacheReadPer1m { get; set; }
    public double? CacheWritePer1m { get; set; }
    /// <summary>CNY | USD。保存厂商原币种原价（海外 USD、国内 CNY）；空/非法按 Dashboard.CostCurrency。
    /// 表内不写死折算价；算钱时按设置币种用实时汇率折（汇率拿不到用 FxFallbackRate）。</summary>
    public string Currency { get; set; } = "";
}

/// <summary>仪表盘设置。用量默认含全部会话；成本估算默认关。</summary>
public sealed class DashboardSettings
{
    public static readonly string[] DefaultQuotaOrder =
    [
        "deepseek", "relay", "qoder", "qoder-cn", "trae", "workbuddy", "zcode", "cursor", "codex",
    ];

    public static readonly string[] DefaultAgentOrder =
    [
        "dsh", "trae", "workbuddy", "zcode", "mimocode", "grok", "qoder", "qoder-cn", "cursor", "cursor-cloud", "codex",
        "minimax", "antigravity", "reasonix", "devin", "opencode", "claude-code",
        "gemini-cli", "kiro", "copilot", "kimi-code", "codebuddy", "hermes",
        "openclaw", "every-code", "astudio", "oh-my-pi", "omo", "pi",
    ];

    private static readonly Dictionary<string, string> AgentGroupOf = new(StringComparer.OrdinalIgnoreCase)
    {
        ["dsh"] = "dsh",
        ["trae"] = "trae",
        ["workbuddy"] = "workbuddy",
        ["zcode"] = "zcode",
        ["zcode-5h"] = "zcode",
        ["zcode-week"] = "zcode",
        ["mimocode"] = "mimocode",
        ["grok"] = "grok",
        ["qoder"] = "qoder",
        ["qoder-credits"] = "qoder",
        ["qoder-calls"] = "qoder",
        ["qoder-cn"] = "qoder-cn",
        ["qoder-cn-credits"] = "qoder-cn",
        ["qoder-cn-calls"] = "qoder-cn",
        ["cursor"] = "cursor",
        ["cursor-total"] = "cursor",
        ["cursor-auto"] = "cursor",
        ["cursor-api"] = "cursor",
        ["cursor-grok"] = "cursor",
        ["cursor-cloud"] = "cursor-cloud",
        ["codex"] = "codex",
        ["codex-5h"] = "codex",
        ["codex-7d"] = "codex",
        ["minimax"] = "minimax",
        ["antigravity"] = "antigravity",
        ["reasonix"] = "reasonix",
        ["devin"] = "devin",
        ["opencode"] = "opencode",
        ["claude-code"] = "claude-code",
        ["gemini-cli"] = "gemini-cli",
        ["kiro"] = "kiro",
        ["copilot"] = "copilot",
        ["kimi-code"] = "kimi-code",
        ["codebuddy"] = "codebuddy",
        ["hermes"] = "hermes",
        ["openclaw"] = "openclaw",
        ["every-code"] = "every-code",
        ["astudio"] = "astudio",
        ["oh-my-pi"] = "oh-my-pi",
        ["omo"] = "omo",
        ["pi"] = "pi",
        ["dots"] = "dots",
    };

    private static readonly Dictionary<string, string> QuotaGroupOf = new(StringComparer.Ordinal)
    {
        ["deepseek"] = "deepseek",
        ["relay"] = "relay",
        ["trae"] = "trae",
        ["workbuddy"] = "workbuddy",
        ["qoder"] = "qoder",
        ["qoder-credits"] = "qoder",
        ["qoder-calls"] = "qoder",
        ["qoder-cn"] = "qoder-cn",
        ["qoder-cn-credits"] = "qoder-cn",
        ["qoder-cn-calls"] = "qoder-cn",
        ["zcode"] = "zcode",
        ["zcode-5h"] = "zcode",
        ["zcode-week"] = "zcode",
        ["cursor"] = "cursor",
        ["cursor-total"] = "cursor",
        ["cursor-auto"] = "cursor",
        ["cursor-api"] = "cursor",
        ["cursor-grok"] = "cursor",
        ["codex"] = "codex",
        ["codex-5h"] = "codex",
        ["codex-7d"] = "codex",
    };

    /// <summary>额度排序键：多账号 Codex 砖（codex:live / codex:auth-…）各自成组，便于拖拽持久化。</summary>
    private static string ResolveQuotaGroup(string id)
    {
        if (id.StartsWith("codex:", StringComparison.Ordinal)) return id;
        if (QuotaGroupOf.TryGetValue(id, out var group)) return group;
        return id;
    }

    private static string? ResolveAgentGroup(string id)
    {
        if (AgentGroupOf.TryGetValue(id, out var group)) return group;
        if (id.StartsWith("codex:", StringComparison.Ordinal)
            || id.StartsWith("codex-", StringComparison.Ordinal))
            return "codex";
        return null;
    }

    public bool CostEstimate { get; set; } = false;
    /// <summary>Token 显示单位。zh = 万/百万/千万/亿；en = K/M/B/T。默认中文。</summary>
    public string TokenUnit { get; set; } = "zh";
    /// <summary>用量重扫间隔（分钟）。默认 15；0 = 只保留启动扫和手动刷新。范围 0–1440。</summary>
    public int ScanIntervalMinutes { get; set; } = 15;
    /// <summary>Agent 表顺序。空或未调过按 DefaultAgentOrder。</summary>
    public List<string> AgentOrder { get; set; } = [];
    /// <summary>额度条目顺序。空或未调过按 DefaultQuotaOrder。</summary>
    public List<string> QuotaOrder { get; set; } = [];
    /// <summary>CNY | USD。展示币种，兼作价格行未标 Currency 时的默认。算钱按实时汇率折到此币种。</summary>
    public string CostCurrency { get; set; } = "CNY";
    /// <summary>汇率兜底：实时 USD→CNY 拿不到（接口不可达/超时/解析失败）时用此值估算。单位：1 USD = ? CNY。</summary>
    public double FxFallbackRate { get; set; } = 7.0;
    /// <summary>历史整表字段。加 JsonIgnore：不再读出也不再写入 config.json。
    /// 算钱走 PriceSyncService.Resolve(PriceOverrides)。</summary>
    [JsonIgnore]
    public List<PriceRow> Prices { get; set; } = [];
    /// <summary>用户显式钉价，按 Model 覆盖远端/内置表。无编辑 UI，只认手写 config.json。</summary>
    public List<PriceRow> PriceOverrides { get; set; } = [];

    public static List<string> NormalizeQuotaOrder(IEnumerable<string>? raw)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();
        if (raw is not null)
        {
            foreach (var id in raw)
            {
                if (id is null) continue;
                var group = ResolveQuotaGroup(id);
                if (!seen.Add(group)) continue;
                result.Add(group);
            }
        }
        foreach (var id in DefaultQuotaOrder)
        {
            // 已有账号级 codex:* 时不再补默认 "codex"，避免双份
            if (id == "codex" && result.Any(x => x.StartsWith("codex:", StringComparison.Ordinal)))
                continue;
            if (seen.Add(id)) result.Add(id);
        }
        return result;
    }

    public static List<string> NormalizeAgentOrder(IEnumerable<string>? agentOrder, IEnumerable<string>? legacyQuotaOrder = null)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        void Add(string? id)
        {
            if (id is null) return;
            var group = ResolveAgentGroup(id);
            if (group is null || !seen.Add(group)) return;
            result.Add(group);
        }
        if (agentOrder is not null)
        {
            foreach (var id in agentOrder) Add(id);
        }
        else if (legacyQuotaOrder is not null)
        {
            foreach (var id in legacyQuotaOrder) Add(id);
        }
        foreach (var id in DefaultAgentOrder)
            if (seen.Add(id)) result.Add(id);
        return result;
    }

    public List<string> ResolvedAgentOrder() =>
        NormalizeAgentOrder(AgentOrder.Count > 0 ? AgentOrder : null, QuotaOrder);

    /// <summary>已手调过用 QuotaOrder；否则 DeepSeek/中转在前，再跟 Agent 表。</summary>
    public List<string> ResolvedQuotaOrder() =>
        QuotaOrder.Count > 0 ? NormalizeQuotaOrder(QuotaOrder) : DeriveQuotaOrder();

    public List<string> DeriveQuotaOrder()
    {
        var q = new List<string>();
        q.AddRange(["deepseek", "relay", "qoder", "qoder-cn"]);
        foreach (var id in ResolvedAgentOrder())
        {
            if (QuotaOmitsAgent(id)) continue;
            q.Add(id);
        }
        return q;
    }

    /// <summary>设置里改 Agent 顺序时，只重排额度砖里的 Agent，DeepSeek/中转位置不动。</summary>
    public static List<string> MergeQuotaOrder(IEnumerable<string>? quotaOrder, IEnumerable<string>? agentOrder)
    {
        var quota = NormalizeQuotaOrder(quotaOrder);
        var agents = NormalizeAgentOrder(agentOrder).Where(id => !QuotaOmitsAgent(id)).ToList();
        var agentSet = new HashSet<string>(agents, StringComparer.Ordinal);
        var qi = 0;
        var result = new List<string>(quota.Count);
        foreach (var id in quota)
        {
            if (agentSet.Contains(id))
            {
                if (qi < agents.Count)
                    result.Add(agents[qi++]);
            }
            else
                result.Add(id);
        }
        while (qi < agents.Count)
            result.Add(agents[qi++]);
        return NormalizeQuotaOrder(result);
    }

    public static string NormalizeCurrency(string? raw) =>
        string.Equals(raw, "USD", StringComparison.OrdinalIgnoreCase) ? "USD" : "CNY";

    public static string NormalizeTokenUnit(string? raw) =>
        string.Equals(raw, "en", StringComparison.OrdinalIgnoreCase) ? "en" : "zh";

    public static string AgentDisplayName(string id) => id.ToLowerInvariant() switch
    {
        "dsh" => "DSH",
        "mimocode" => "MiMo",
        "grok" => "Grok",
        "qoder" => "Qoder",
        "qoder-cn" => "Qoder CN",
        "trae" => "Trae",
        "workbuddy" => "WorkBuddy",
        "zcode" => "ZCode",
        "cursor" => "Cursor",
        "cursor-cloud" => "Cursor 云端",
        "codex" => "Codex",
        "minimax" => "MiniMax Code",
        "antigravity" => "Antigravity",
        "reasonix" => "Reasonix",
        "devin" => "Devin",
        "opencode" => "OpenCode",
        "claude-code" => "Claude Code",
        "gemini-cli" => "Gemini CLI",
        "kiro" => "Kiro",
        "copilot" => "GitHub Copilot",
        "kimi-code" => "Kimi Code",
        "codebuddy" => "CodeBuddy",
        "hermes" => "Hermes",
        "openclaw" => "OpenClaw",
        "every-code" => "Every Code",
        "astudio" => "AStudio",
        "oh-my-pi" => "oh-my-pi",
        "omo" => "OmO",
        "pi" => "pi",
        "dots" => "Dots",
        _ => id,
    };

    /// <summary>只有用量、没有额度砖的家。qoder 自己在额度表最前面，这里从 Agent 顺序里摘掉，避免再插一次。</summary>
    private static bool QuotaOmitsAgent(string id) => id.ToLowerInvariant() switch
    {
        "dsh" or "mimocode" or "grok" or "qoder" or "qoder-cn" or "cursor-cloud"
            or "minimax" or "antigravity" or "reasonix" or "devin" or "opencode" or "claude-code"
            or "gemini-cli" or "kiro" or "copilot" or "kimi-code" or "codebuddy" or "hermes"
            or "openclaw" or "every-code" or "astudio" or "oh-my-pi" or "omo" or "pi" or "dots" => true,
        _ => false,
    };
}

/// <summary>凭据（DPAPI 加密存储，方案 §5.2）。</summary>
public sealed class CredentialsSettings
{
    /// <summary>DeepSeek API Key（DPAPI 密文，base64）。</summary>
    public string DeepSeekKey { get; set; } = "";
    /// <summary>Sub2API 网关 API Key（DPAPI 密文，base64）。查余额并给网关转发。</summary>
    public string RelayKey { get; set; } = "";
    /// <summary>Sub2API 站点根地址。</summary>
    public string RelayPanelBaseUrl { get; set; } = "";
    /// <summary>面板 auth_token（DPAPI 密文）。</summary>
    public string RelayPanelAuthToken { get; set; } = "";
    /// <summary>面板 refresh_token（DPAPI 密文）。</summary>
    public string RelayPanelRefreshToken { get; set; } = "";
    /// <summary>WorkBuddy 网页 cookie `session`（DPAPI 密文）。本机 Cookie / JWT 读不到时才用。</summary>
    public string WorkBuddySession { get; set; } = "";
    /// <summary>Trae 网页 cookie `X-Cloudide-Session`（DPAPI 密文）。本机 storage.json 读不到 JWT 时才用。</summary>
    public string TraeSession { get; set; } = "";
    /// <summary>Cursor Cloud Agents API Key（DPAPI 密文）。Dashboard → API Keys。</summary>
    public string CursorCloudApiKey { get; set; } = "";
}

/// <summary>应用级设置。</summary>
public sealed class AppSettings
{
    public bool Autostart { get; set; }
    /// <summary>壳层与页面首帧主题：dark | light（UI_RULES §7.2）。页面切换经 theme: 消息写回。</summary>
    public string Theme { get; set; } = "dark";
    /// <summary>主窗口上次宽度；缺省用 XAML 默认 1440。</summary>
    public double? WindowWidth { get; set; }
    /// <summary>主窗口上次高度；缺省用 XAML 默认 900。</summary>
    public double? WindowHeight { get; set; }
    /// <summary>主窗口上次 Left（屏幕坐标）。</summary>
    public double? WindowLeft { get; set; }
    /// <summary>主窗口上次 Top（屏幕坐标）。</summary>
    public double? WindowTop { get; set; }
    /// <summary>主窗口是否最大化。</summary>
    public bool WindowMaximized { get; set; }
}

/// <summary>AgentHub 配置：落 %APPDATA%\AgentHub\config.json。</summary>
public sealed class AgentHubConfig
{
    public AppSettings App { get; set; } = new();
    public DocsSettings Docs { get; set; } = new();
    public DashboardSettings Dashboard { get; set; } = new();
    public CredentialsSettings Credentials { get; set; } = new();
    /// <summary>Codex 连接管理（方案 §6）：连接记录存这里，live config.toml 只是当前连接的投影。</summary>
    public CodexConfigSettings Codex { get; set; } = new();

    private readonly object _saveLock = new();

    public static string Dir => Path.Combine(PlatformPaths.RoamingAppData, "AgentHub");
    /// <summary>Velopack 安装根。Setup 只要看到这个目录非空就弹「已安装」。</summary>
    public static string InstallDir => Path.Combine(PlatformPaths.LocalAppData, "AgentHub");
    /// <summary>本机缓存/技能库。必须在安装目录外，否则卸完无法重装。</summary>
    public static string LocalDataDir => Path.Combine(PlatformPaths.LocalAppData, "AgentHub.Local");
    public static string ConfigPath => Path.Combine(Dir, "config.json");
    public static string TokensDbPath => Path.Combine(Dir, "tokens.db");

    private static readonly string[] InstallDirUserDataNames =
    [
        "WebView2",
        // 宠物功能已下线，但老版本安装目录里可能残留这两项，必须继续挪出安装目录，否则卸载后目录非空、Velopack Setup 会误判「已安装」。
        "WebView2Pet",
        "pet-placement.json",
        "SkillStore",
        "skills-state.json",
        "SkillStaging",
        "SkillBackups",
        "AgentRuleBackups",
        "agent-rules-state.json",
        "agent-rules-transaction.json",
    ];

    /// <summary>把误写进安装目录的用户数据搬到 LocalDataDir，让卸载能删干净。</summary>
    public static void RelocateLocalDataFromInstallDir()
    {
        var install = InstallDir;
        var destRoot = LocalDataDir;
        if (!Directory.Exists(install)) return;
        if (string.Equals(
                Path.GetFullPath(install).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(destRoot).TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
            return;

        foreach (var name in InstallDirUserDataNames)
        {
            var src = Path.Combine(install, name);
            var dest = Path.Combine(destRoot, name);
            try
            {
                if (Directory.Exists(src))
                {
                    if (Directory.Exists(dest) || File.Exists(dest)) continue;
                    Directory.CreateDirectory(destRoot);
                    Directory.Move(src, dest);
                }
                else if (File.Exists(src))
                {
                    if (File.Exists(dest) || Directory.Exists(dest)) continue;
                    Directory.CreateDirectory(destRoot);
                    File.Move(src, dest);
                }
            }
            catch (Exception ex)
            {
                HubLog.Write("[config] 安装目录迁移跳过 " + name + "：" + ex.Message);
            }
        }
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static AgentHubConfig Load()
    {
        AgentHubConfig cfg;
        string raw;
        try
        {
            if (!File.Exists(ConfigPath)) return new AgentHubConfig();
            raw = File.ReadAllText(ConfigPath);
            cfg = JsonSerializer.Deserialize<AgentHubConfig>(raw, JsonOpts)
                ?? new AgentHubConfig();
        }
        catch (Exception ex)
        {
            HubLog.Write("[config] 配置损坏，已尝试备份后回默认：" + ex.Message);
            try
            {
                if (File.Exists(ConfigPath))
                    File.Copy(ConfigPath, ConfigPath + ".bak", overwrite: true);
            }
            catch (IOException io)
            {
                HubLog.Write("[config] 损坏配置备份失败：" + io.Message);
            }
            return new AgentHubConfig();
        }

        try
        {
            var changed = NormalizeAndMigrateLibraryRoot(cfg);
            changed |= MigrateRelayPanelBaseUrl(cfg, raw);
            if (changed) cfg.Save();
        }
        catch (Exception)
        {
            // 路径本身无效时保留其它配置与原文件，设置页会给出明确校验错误。
        }
        return cfg;
    }

    private static bool MigrateRelayPanelBaseUrl(AgentHubConfig cfg, string raw)
    {
        if (!string.IsNullOrWhiteSpace(cfg.Credentials.RelayPanelBaseUrl)) return false;
        using var doc = JsonDocument.Parse(raw);
        if (!doc.RootElement.TryGetProperty("Privacy", out var privacy)
            || !privacy.TryGetProperty("Upstream", out var upstream)
            || upstream.ValueKind != JsonValueKind.String)
            return false;
        var value = upstream.GetString()?.Trim() ?? "";
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https"))
            return false;
        cfg.Credentials.RelayPanelBaseUrl = value.TrimEnd('/');
        return true;
    }

    public void Save()
    {
        lock (_saveLock)
        {
            Directory.CreateDirectory(Dir);
            var json = JsonSerializer.Serialize(this, JsonOpts);
            var temp = ConfigPath + ".tmp";
            File.WriteAllText(temp, json);
            // 原子替换（方案 §6.2）：避免「active 标记已前移、配置只写了一半」
            if (File.Exists(ConfigPath)) File.Replace(temp, ConfigPath, destinationBackupFileName: null);
            else File.Move(temp, ConfigPath);
        }
    }

    /// <summary>规范化资料根路径；空值回落到默认用户目录，不改用户已配置的其它绝对路径。</summary>
    private static bool NormalizeAndMigrateLibraryRoot(AgentHubConfig cfg)
    {
        var current = cfg.Docs.LibraryRoot ?? "";
        var normalized = DocsSettings.NormalizeLibraryRoot(current);
        if (string.Equals(normalized, current, StringComparison.Ordinal)) return false;
        cfg.Docs.LibraryRoot = normalized;
        return true;
    }
}
