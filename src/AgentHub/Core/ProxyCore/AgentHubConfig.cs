using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentHub.Core.CodexConfigCore;

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

/// <summary>价格表一行：模型名 + 每 100 万 token 的输入/输出单价。无效行原样保存，算钱时再跳过。</summary>
public sealed class PriceRow
{
    public string Model { get; set; } = "";
    public double? InputPer1m { get; set; }
    public double? OutputPer1m { get; set; }
    /// <summary>CNY | USD。保存厂商原币种原价（海外 USD、国内 CNY）；空/非法按 Dashboard.CostCurrency。
    /// 算钱时统一按实时汇率折算成 USD（汇率拿不到用 FxFallbackRate）。</summary>
    public string Currency { get; set; } = "";
}

/// <summary>仪表盘设置。用量默认含全部会话；成本估算默认关。</summary>
public sealed class DashboardSettings
{
    public static readonly string[] DefaultQuotaOrder =
    [
        "deepseek", "relay", "trae", "workbuddy", "zcode", "cursor", "codex",
    ];

    public static readonly string[] DefaultAgentOrder =
    [
        "dsh", "trae", "workbuddy", "zcode", "cursor", "codex",
    ];

    public static readonly string[] SessionReadableAgents =
    [
        "codex", "dsh", "cursor", "workbuddy", "zcode",
    ];

    private static readonly Dictionary<string, string> AgentGroupOf = new(StringComparer.OrdinalIgnoreCase)
    {
        ["dsh"] = "dsh",
        ["trae"] = "trae",
        ["workbuddy"] = "workbuddy",
        ["zcode"] = "zcode",
        ["zcode-5h"] = "zcode",
        ["zcode-week"] = "zcode",
        ["cursor"] = "cursor",
        ["cursor-auto"] = "cursor",
        ["cursor-api"] = "cursor",
        ["codex"] = "codex",
        ["codex-5h"] = "codex",
        ["codex-7d"] = "codex",
    };

    private static readonly Dictionary<string, string> QuotaGroupOf = new(StringComparer.Ordinal)
    {
        ["deepseek"] = "deepseek",
        ["relay"] = "relay",
        ["trae"] = "trae",
        ["workbuddy"] = "workbuddy",
        ["zcode"] = "zcode",
        ["zcode-5h"] = "zcode",
        ["zcode-week"] = "zcode",
        ["cursor"] = "cursor",
        ["cursor-auto"] = "cursor",
        ["cursor-api"] = "cursor",
        ["codex"] = "codex",
        ["codex-5h"] = "codex",
        ["codex-7d"] = "codex",
    };

    public bool CursorUsage { get; set; } = true;
    public bool CostEstimate { get; set; } = false;
    /// <summary>Token 显示单位。zh = 万/百万/千万/亿；en = K/M/B/T。默认中文。</summary>
    public string TokenUnit { get; set; } = "zh";
    /// <summary>Trae 用量开关，默认开。</summary>
    public bool TraeUsage { get; set; } = true;
    /// <summary>ZCode 用量开关，默认开。关则不扫本机库。</summary>
    public bool ZcodeUsage { get; set; } = true;
    /// <summary>用量重扫间隔（分钟）。默认 15；0 = 只保留启动扫和手动刷新。范围 0–1440。</summary>
    public int ScanIntervalMinutes { get; set; } = 15;
    public bool ShowQuotaDeepSeek { get; set; } = true;
    public bool ShowQuotaCursor { get; set; } = true;
    public bool ShowQuotaRelay { get; set; } = true;
    public bool ShowQuotaWorkBuddy { get; set; } = true;
    public bool ShowQuotaTrae { get; set; } = true;
    public bool ShowQuotaZcode { get; set; } = true;
    public bool ShowQuotaCodex { get; set; } = true;
    /// <summary>DSH 无额度砖，只控制用量和会话。</summary>
    public bool ShowAgentDsh { get; set; } = true;
    /// <summary>Agent 表顺序。空或未调过按 DefaultAgentOrder。</summary>
    public List<string> AgentOrder { get; set; } = [];
    /// <summary>额度条目顺序。空或未调过按 DefaultQuotaOrder。</summary>
    public List<string> QuotaOrder { get; set; } = [];
    /// <summary>CNY | USD。整表一个默认币种，仅用于价格行未标 Currency 的一侧；算钱结果统一是 USD。</summary>
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
                if (id is null || !QuotaGroupOf.TryGetValue(id, out var group) || !seen.Add(group))
                    continue;
                result.Add(group);
            }
        }
        foreach (var id in DefaultQuotaOrder)
            if (seen.Add(id)) result.Add(id);
        return result;
    }

    public static List<string> NormalizeAgentOrder(IEnumerable<string>? agentOrder, IEnumerable<string>? legacyQuotaOrder = null)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        void Add(string? id)
        {
            if (id is null || !AgentGroupOf.TryGetValue(id, out var group) || !seen.Add(group))
                return;
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

    public List<string> DeriveQuotaOrder()
    {
        var q = new List<string>();
        if (ShowQuotaDeepSeek) q.Add("deepseek");
        if (ShowQuotaRelay) q.Add("relay");
        foreach (var id in ResolvedAgentOrder())
        {
            if (id == "dsh" || !AgentEnabled(id)) continue;
            q.Add(id);
        }
        return q;
    }

    public bool AgentEnabled(string id) => id.ToLowerInvariant() switch
    {
        "dsh" => ShowAgentDsh,
        "trae" => ShowQuotaTrae,
        "workbuddy" => ShowQuotaWorkBuddy,
        "zcode" => ShowQuotaZcode,
        "cursor" => ShowQuotaCursor,
        "codex" => ShowQuotaCodex,
        _ => false,
    };

    public bool SessionReadable(string id) =>
        AgentEnabled(id)
        && SessionReadableAgents.Contains(id, StringComparer.OrdinalIgnoreCase);

    public static string NormalizeCurrency(string? raw) =>
        string.Equals(raw, "USD", StringComparison.OrdinalIgnoreCase) ? "USD" : "CNY";

    public static string NormalizeTokenUnit(string? raw) =>
        string.Equals(raw, "en", StringComparison.OrdinalIgnoreCase) ? "en" : "zh";

    public static string AgentDisplayName(string id) => id.ToLowerInvariant() switch
    {
        "dsh" => "DSH",
        "trae" => "Trae",
        "workbuddy" => "WorkBuddy",
        "zcode" => "ZCode",
        "cursor" => "Cursor",
        "codex" => "Codex",
        _ => id,
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
}

/// <summary>应用级设置。</summary>
public sealed class AppSettings
{
    public bool Autostart { get; set; }
    /// <summary>是否显示桌面宠物。默认关，避免与 TokenTracker 同时出现两只。</summary>
    public bool PetEnabled { get; set; }
    /// <summary>磁盘兼容字段。运行时永远 usage；PUT 忽略。</summary>
    public string PetMode { get; set; } = "usage";
    /// <summary>small | medium | large。默认中，对齐 TokenTracker 三档。</summary>
    public string PetSize { get; set; } = "medium";
    /// <summary>壳层与页面首帧主题：dark | light（UI_RULES §7.2）。页面切换经 theme: 消息写回。</summary>
    public string Theme { get; set; } = "dark";
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

    public static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AgentHub");
    public static string LocalDataDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgentHub");
    public static string ConfigPath => Path.Combine(Dir, "config.json");
    public static string TokensDbPath => Path.Combine(Dir, "tokens.db");

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
        catch (Exception)
        {
            // 损坏配置：备份后回默认（对齐旧 config.py 行为）
            try
            {
                if (File.Exists(ConfigPath))
                    File.Copy(ConfigPath, ConfigPath + ".bak", overwrite: true);
            }
            catch (IOException) { }
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
        Directory.CreateDirectory(Dir);
        var temp = ConfigPath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, JsonOpts));
        // 原子替换（方案 §6.2）：避免「active 标记已前移、配置只写了一半」
        if (File.Exists(ConfigPath)) File.Replace(temp, ConfigPath, destinationBackupFileName: null);
        else File.Move(temp, ConfigPath);
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
