using System.Text.Json.Serialization;

namespace AgentHub.Core.CodexConfigCore;

/// <summary>连接类型。官方订阅走 Codex 内置 provider（无表）；Responses 中转写自定义 [model_providers.OpenAI] 表。</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CodexConnectionKind
{
    /// <summary>ChatGPT 官方订阅：无 base_url，requires_openai_auth，凭据仍在 Codex 的 auth.json。
    /// 账号档案是 AgentHub 对本机登录态的归档，与连接记录分开，不随「应用连接」写入。</summary>
    Official,
    /// <summary>Responses 中转：base_url + auth.command，Key 由 AgentHub DPAPI 保管。</summary>
    ResponsesRelay,
}

/// <summary>
/// 一条 Codex 连接配置。Id 只在 AgentHub 内部使用，绝不写入 Codex 的 Provider 标识。
/// 官方订阅写内置 provider "openai" 且不写任何 [model_providers.*] 表；
/// 中转连接写自定义 provider "OpenAI"（大小写与内置不同，Codex 才允许覆盖）。
/// </summary>
public sealed class CodexConnection
{
    public const string OfficialId = "official";
    /// <summary>中转连接使用的自定义 provider id。Codex 保留内置 id（openai/oss/ollama/lmstudio），
    /// 写 [model_providers.openai] 会直接报 "reserved built-in provider IDs" 拒绝加载；
    /// id 大小写敏感，"OpenAI" 是合法的自定义 provider。</summary>
    public const string FixedProviderId = "OpenAI";
    /// <summary>Codex 内置官方 provider 的 id。官方连接只切顶层 model_provider 到这个值，
    /// 不写表——内置 provider 不允许覆盖。会话 rollout 里记的也是这个小写 id。</summary>
    public const string BuiltInProviderId = "openai";
    /// <summary>配置表里的展示名，Codex 只拿来显示。</summary>
    public const string DisplayName = "OpenAI";

    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public CodexConnectionKind Kind { get; set; } = CodexConnectionKind.ResponsesRelay;
    /// <summary>Responses 中转地址。官方订阅恒为空。</summary>
    public string BaseUrl { get; set; } = "";
    /// <summary>可选。非空时应用连接会改写顶层 model；空则保留 live 现值。</summary>
    public string DefaultModel { get; set; } = "";
    public bool SupportsWebSockets { get; set; }
    /// <summary>中转静态请求头。官方订阅使用 Codex 默认头，两者恒为空。</summary>
    public string UserAgent { get; set; } = "";
    public string Originator { get; set; } = "";
    /// <summary>中转 Key（DPAPI 密文 base64）。官方订阅不保存任何凭据。</summary>
    public string ApiKeyCipher { get; set; } = "";
    /// <summary>可选。余额查询地址，不与推理地址强绑定。</summary>
    public string UsageBaseUrl { get; set; } = "";

    [JsonIgnore]
    public bool IsOfficial => Kind == CodexConnectionKind.Official;

    public static CodexConnection CreateOfficial() => new()
    {
        Id = OfficialId,
        Name = "OpenAI 官方订阅",
        Kind = CodexConnectionKind.Official,
        SupportsWebSockets = true,
    };
}

/// <summary>Codex 配置管理状态（落 AgentHub config.json 的 Codex 节）。</summary>
public sealed class CodexConfigSettings
{
    public List<CodexConnection> Connections { get; set; } = [];
    public string? ActiveConnectionId { get; set; }
    /// <summary>最近一次成功应用后 live config.toml 的 SHA-256，用于外部修改检测。</summary>
    public string? LiveSha256 { get; set; }
}

/// <summary>账号档案索引（codex-auth-profiles/index.json）。不含 token。</summary>
public sealed class CodexAuthProfileIndex
{
    public List<CodexAuthProfileMeta> Profiles { get; set; } = [];
}

/// <summary>一条归档的 ChatGPT 登录展示信息。凭据在同目录 {Id}.json 的 DPAPI 密文里。</summary>
public sealed class CodexAuthProfileMeta
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
    public string Plan { get; set; } = "";
    public string AccountId { get; set; } = "";
    public string Identity { get; set; } = "";
    public string CreatedAt { get; set; } = "";
    public string UpdatedAt { get; set; } = "";
}
