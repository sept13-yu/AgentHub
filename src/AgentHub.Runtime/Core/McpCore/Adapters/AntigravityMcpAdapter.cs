using System.IO;
using System.Text.Json.Nodes;
using AgentHub.Core.Platform;

namespace AgentHub.Core.McpCore.Adapters;

/// <summary>
/// Antigravity：~/.gemini/config/mcp_config.json → mcpServers；disabled 停用。
/// 读取兼容旧键 serverUrl；写入不落 type/timeout，显式 SSE 拒绝并提示。
/// </summary>
public sealed class AntigravityMcpAdapter : JsonServersMcpAdapter
{
    private readonly Func<bool> _detected;

    public AntigravityMcpAdapter(string? configPath = null, Func<bool>? detected = null)
        : base("antigravity", "Antigravity",
            configPath ?? Path.Combine(PlatformPaths.AntigravityConfigDir(), "mcp_config.json"))
    {
        _detected = detected ?? (() => AgentPresence.IsAntigravityInstalled());
    }

    public override bool Detected => _detected();

    protected override McpServerSpec ParseEntry(string id, JsonObject node)
    {
        if (node["url"] is null && node["serverUrl"] is JsonValue legacyUrl)
        {
            var clone = (JsonObject)node.DeepClone();
            clone["url"] = legacyUrl.DeepClone();
            return base.ParseEntry(id, clone);
        }
        return base.ParseEntry(id, node);
    }

    protected override void BeforeUpsert(McpServerSpec spec)
    {
        if (spec.Transport == McpTransport.Http
            && string.Equals(spec.ExplicitType, "sse", StringComparison.OrdinalIgnoreCase))
        {
            throw new McpSafeException("Antigravity 不支持旧版 SSE 传输，请改用 Streamable HTTP URL 或 mcp-remote stdio 桥");
        }
    }

    protected override void AfterBuildNode(McpServerSpec spec, JsonObject node)
    {
        node.Remove("type");
        node.Remove("timeout");
        node.Remove("serverUrl");
    }
}
