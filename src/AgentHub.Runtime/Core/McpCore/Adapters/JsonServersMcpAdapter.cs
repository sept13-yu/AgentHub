using System.IO;
using System.Text.Json.Nodes;
using System.IO;
using AgentHub.Core.ProxyCore;

namespace AgentHub.Core.McpCore.Adapters;

/// <summary>Cursor / Trae / WorkBuddy 风格：根对象 mcpServers + disabled 字段。</summary>
public abstract class JsonServersMcpAdapter : IMcpAdapter
{
    private readonly string[] _serversPath;
    private readonly bool _workBuddyHttp;
    private readonly string _backupAgent;

    protected JsonServersMcpAdapter(string agentId, string displayName, string configPath,
        string[]? serversPath = null, bool workBuddyHttp = false)
    {
        AgentId = agentId;
        DisplayName = displayName;
        ConfigPath = configPath;
        _serversPath = serversPath ?? ["mcpServers"];
        _workBuddyHttp = workBuddyHttp;
        _backupAgent = agentId;
    }

    public string AgentId { get; }
    public string DisplayName { get; }
    public string ConfigPath { get; }
    public virtual bool Detected => File.Exists(ConfigPath) || Directory.Exists(Path.GetDirectoryName(ConfigPath) ?? "");

    public IReadOnlyList<McpServerSpec> List()
    {
        if (!File.Exists(ConfigPath)) return [];
        try
        {
            var root = JsonMcpFile.LoadRoot(ConfigPath);
            var servers = JsonMcpFile.TryGetServers(root, _serversPath);
            if (servers is null) return [];
            var list = new List<McpServerSpec>();
            foreach (var (id, node) in servers)
            {
                if (node is not JsonObject obj) continue;
                list.Add(JsonMcpFile.ParseServer(id, obj, ReadEnabled));
            }
            return list;
        }
        catch
        {
            return [];
        }
    }

    public void Upsert(McpServerSpec spec)
    {
        Backup();
        var root = File.Exists(ConfigPath) ? JsonMcpFile.LoadRoot(ConfigPath) : new JsonObject();
        var servers = JsonMcpFile.GetOrCreateServers(root, _serversPath);
        // 保留未知字段：若已有节点，在其上覆盖已知键
        if (servers[spec.Id] is JsonObject existing)
        {
            var fresh = JsonMcpFile.ToServerNode(spec, WriteEnabled, _workBuddyHttp);
            foreach (var (k, v) in fresh)
                existing[k] = v?.DeepClone();
            // stdio 切到 http 时清掉 command；反之清 url
            if (spec.Transport == McpTransport.Http)
            {
                existing.Remove("command");
                existing.Remove("args");
                existing.Remove("env");
            }
            else
            {
                existing.Remove("url");
                existing.Remove("headers");
                if (!_workBuddyHttp) existing.Remove("type");
            }
            servers[spec.Id] = existing;
        }
        else
        {
            servers[spec.Id] = JsonMcpFile.ToServerNode(spec, WriteEnabled, _workBuddyHttp);
        }
        JsonMcpFile.SaveRoot(ConfigPath, root);
    }

    public void SetEnabled(string id, bool enabled)
    {
        if (!File.Exists(ConfigPath))
            throw new FileNotFoundException("配置文件不存在", ConfigPath);
        Backup();
        var root = JsonMcpFile.LoadRoot(ConfigPath);
        var servers = JsonMcpFile.TryGetServers(root, _serversPath)
            ?? throw new KeyNotFoundException($"没有 MCP：{id}");
        if (servers[id] is not JsonObject node)
            throw new KeyNotFoundException($"没有 MCP：{id}");
        WriteEnabled(node, enabled);
        JsonMcpFile.SaveRoot(ConfigPath, root);
    }

    public void Remove(string id)
    {
        if (!File.Exists(ConfigPath)) return;
        Backup();
        var root = JsonMcpFile.LoadRoot(ConfigPath);
        var servers = JsonMcpFile.TryGetServers(root, _serversPath);
        if (servers is null || !servers.Remove(id)) return;
        JsonMcpFile.SaveRoot(ConfigPath, root);
    }

    protected virtual bool ReadEnabled(JsonObject node)
    {
        if (node["disabled"] is JsonValue dv && dv.TryGetValue<bool>(out var disabled))
            return !disabled;
        return true;
    }

    protected virtual void WriteEnabled(JsonObject node, bool enabled)
    {
        node["disabled"] = !enabled;
    }

    protected void Backup()
    {
        if (!File.Exists(ConfigPath)) return;
        var root = Path.Combine(AgentHubConfig.LocalDataDir, "McpBackups", _backupAgent);
        Directory.CreateDirectory(root);
        var name = $"{DateTime.Now:yyyyMMdd-HHmmss}{Path.GetExtension(ConfigPath)}";
        File.Copy(ConfigPath, Path.Combine(root, name), overwrite: true);
    }
}

public sealed class CursorMcpAdapter : JsonServersMcpAdapter
{
    public CursorMcpAdapter()
        : base("cursor", "Cursor",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cursor", "mcp.json"))
    { }
}

public sealed class TraeMcpAdapter : JsonServersMcpAdapter
{
    public TraeMcpAdapter()
        : base("trae", "Trae",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "TRAE SOLO CN", "User", "mcp.json"))
    { }

    public override bool Detected =>
        File.Exists(ConfigPath)
        || Directory.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TRAE SOLO CN"));
}

public sealed class WorkBuddyMcpAdapter : JsonServersMcpAdapter
{
    public WorkBuddyMcpAdapter()
        : base("workbuddy", "WorkBuddy",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".workbuddy", "mcp.json"),
            workBuddyHttp: true)
    { }
}

/// <summary>ZCode：~/.zcode/cli/config.json → mcp.servers；启用字段为 enable（false 停用）。</summary>
public sealed class ZcodeMcpAdapter : JsonServersMcpAdapter
{
    public ZcodeMcpAdapter()
        : base("zcode", "ZCode",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".zcode", "cli", "config.json"),
            serversPath: ["mcp", "servers"])
    { }

    public override bool Detected =>
        File.Exists(ConfigPath)
        || Directory.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".zcode"));

    protected override bool ReadEnabled(JsonObject node)
    {
        if (node["enable"] is JsonValue ev && ev.TryGetValue<bool>(out var enable))
            return enable;
        return true;
    }

    protected override void WriteEnabled(JsonObject node, bool enabled)
    {
        node.Remove("disabled");
        if (enabled)
            node.Remove("enable"); // 缺省即启用，与现网一致
        else
            node["enable"] = false;
    }
}
