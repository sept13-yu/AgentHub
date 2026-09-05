using System.Text.Json.Serialization;

namespace AgentHub.Core.CodexConfigCore;

/// <summary>连接类型。官方订阅与 Responses 中转都投影到同一个 [model_providers.OpenAI] 表。</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CodexConnectionKind
{
    /// <summary>ChatGPT 官方订阅：无 base_url，requires_openai_auth，凭据归 Codex 自己的 auth.json。</summary>
    Official,
    /// <summary>Responses 中转：base_url + auth.command，Key 由 AgentHub DPAPI 保管。</summary>
    ResponsesRelay,
}

/// <summary>
/// 一条 Codex 连接配置。Id 只在 AgentHub 内部使用，绝不写入 Codex 的 Provider 标识；
/// live 配置的 Provider ID 恒为 "OpenAI"（方案 §4.1 不变量）。
/// </summary>
public sealed class CodexConnection
{
    public const string OfficialId = "official";
    public const string FixedProviderId = "OpenAI";

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
