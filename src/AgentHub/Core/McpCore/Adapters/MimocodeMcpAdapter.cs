using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentHub.Core.ProxyCore;

namespace AgentHub.Core.McpCore.Adapters;

/// <summary>MiMo Code：~/.config/mimocode/mimocode.jsonc → mcp.{name}。
/// schema 不同于 JsonServersMcpAdapter：command 为数组、env 键为 environment、启用字段为 enabled、type=local|remote。</summary>
public sealed class MimocodeMcpAdapter : IMcpAdapter
{
    private static readonly string[] ServersPath = ["mcp"];
    private static readonly JsonSerializerOptions WriteOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public string AgentId => "mimocode";
    public string DisplayName => "MiMo";
    public string ConfigPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".config", "mimocode", "mimocode.jsonc");

    public bool Detected =>
        File.Exists(ConfigPath)
        || Directory.Exists(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "mimocode"));

    public IReadOnlyList<McpServerSpec> List()
    {
        if (!File.Exists(ConfigPath)) return [];
        try
        {
            var root = LoadRoot(ConfigPath);
            var servers = JsonMcpFile.TryGetServers(root, ServersPath);
            if (servers is null) return [];
            var list = new List<McpServerSpec>();
            foreach (var (id, node) in servers)
            {
                if (node is not JsonObject obj) continue;
                // mcp 下可能夹杂非 server 节点，跳过无 type/command/url 的项
                if (obj["type"] is null && obj["command"] is null && obj["url"] is null) continue;
                list.Add(ParseServer(id, obj));
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
        var root = File.Exists(ConfigPath) ? LoadRoot(ConfigPath) : new JsonObject();
        var servers = JsonMcpFile.GetOrCreateServers(root, ServersPath);
        if (servers[spec.Id] is JsonObject existing)
        {
            var fresh = ToServerNode(spec);
            foreach (var (k, v) in fresh)
                existing[k] = v?.DeepClone();
            if (spec.Transport == McpTransport.Http)
            {
                existing.Remove("command");
                existing.Remove("environment");
                existing.Remove("env");
                existing.Remove("args");
            }
            else
            {
                existing.Remove("url");
                existing.Remove("headers");
                existing.Remove("oauth");
            }
            existing["type"] = spec.Transport == McpTransport.Http ? "remote" : "local";
            servers[spec.Id] = existing;
        }
        else
        {
            servers[spec.Id] = ToServerNode(spec);
        }
        SaveRoot(ConfigPath, root);
    }

    public void SetEnabled(string id, bool enabled)
    {
        if (!File.Exists(ConfigPath))
            throw new FileNotFoundException("配置文件不存在", ConfigPath);
        Backup();
        var root = LoadRoot(ConfigPath);
        var servers = JsonMcpFile.TryGetServers(root, ServersPath)
            ?? throw new KeyNotFoundException($"没有 MCP：{id}");
        if (servers[id] is not JsonObject node)
            throw new KeyNotFoundException($"没有 MCP：{id}");
        node["enabled"] = enabled;
        SaveRoot(ConfigPath, root);
    }

    public void Remove(string id)
    {
        if (!File.Exists(ConfigPath)) return;
        Backup();
        var root = LoadRoot(ConfigPath);
        var servers = JsonMcpFile.TryGetServers(root, ServersPath);
        if (servers is null || !servers.Remove(id)) return;
        SaveRoot(ConfigPath, root);
    }

    private static McpServerSpec ParseServer(string id, JsonObject node)
    {
        var type = node["type"]?.GetValue<string>();
        var hasUrl = node["url"] is JsonValue;
        var isRemote = string.Equals(type, "remote", StringComparison.OrdinalIgnoreCase)
            || (hasUrl && !string.Equals(type, "local", StringComparison.OrdinalIgnoreCase));

        var spec = new McpServerSpec
        {
            Id = id,
            Transport = isRemote ? McpTransport.Http : McpTransport.Stdio,
            ExplicitType = isRemote ? "remote" : "local",
            Enabled = true,
            Url = node["url"]?.GetValue<string>(),
        };

        if (node["enabled"] is JsonValue ev && ev.TryGetValue<bool>(out var enabled))
            spec.Enabled = enabled;

        if (node["command"] is JsonArray cmdArr)
        {
            var parts = new List<string>();
            foreach (var a in cmdArr)
                if (a is JsonValue v)
                    parts.Add(v.GetValue<string>() ?? "");
            if (parts.Count > 0)
            {
                spec.Command = parts[0];
                if (parts.Count > 1)
                    spec.Args.AddRange(parts.Skip(1));
            }
        }
        else if (node["command"] is JsonValue cmdStr)
        {
            spec.Command = cmdStr.GetValue<string>();
            if (node["args"] is JsonArray args)
            {
                foreach (var a in args)
                    if (a is JsonValue v)
                        spec.Args.Add(v.GetValue<string>() ?? "");
            }
        }

        var envNode = node["environment"] as JsonObject ?? node["env"] as JsonObject;
        if (envNode is not null)
        {
            foreach (var (k, val) in envNode)
                if (val is JsonValue jv)
                    spec.Env[k] = jv.GetValue<string>() ?? "";
        }
        if (node["headers"] is JsonObject headers)
        {
            foreach (var (k, val) in headers)
                if (val is JsonValue jv)
                    spec.Headers[k] = jv.GetValue<string>() ?? "";
        }
        if (node["timeout"] is JsonValue to && to.TryGetValue<int>(out var ms))
            spec.TimeoutMs = ms;
        return spec;
    }

    private static JsonObject ToServerNode(McpServerSpec spec)
    {
        var node = new JsonObject();
        if (spec.Transport == McpTransport.Http)
        {
            node["type"] = "remote";
            node["url"] = spec.Url ?? "";
            if (spec.Headers.Count > 0)
            {
                var headers = new JsonObject();
                foreach (var (k, v) in spec.Headers)
                    headers[k] = v;
                node["headers"] = headers;
            }
        }
        else
        {
            node["type"] = "local";
            var cmd = new JsonArray();
            if (!string.IsNullOrEmpty(spec.Command))
                cmd.Add(spec.Command);
            foreach (var a in spec.Args)
                cmd.Add(a);
            node["command"] = cmd;
            if (spec.Env.Count > 0)
            {
                var env = new JsonObject();
                foreach (var (k, v) in spec.Env)
                    env[k] = v;
                node["environment"] = env;
            }
        }
        node["enabled"] = spec.Enabled;
        if (spec.TimeoutMs is int ms)
            node["timeout"] = ms;
        return node;
    }

    private void Backup()
    {
        if (!File.Exists(ConfigPath)) return;
        var root = Path.Combine(AgentHubConfig.LocalDataDir, "McpBackups", "mimocode");
        Directory.CreateDirectory(root);
        var name = $"{DateTime.Now:yyyyMMdd-HHmmss}{Path.GetExtension(ConfigPath)}";
        File.Copy(ConfigPath, Path.Combine(root, name), overwrite: true);
    }

    private static JsonObject LoadRoot(string path)
    {
        if (!File.Exists(path))
            return new JsonObject();
        var text = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(text))
            return new JsonObject();
        var cleaned = StripJsonc(text);
        return JsonNode.Parse(cleaned) as JsonObject ?? new JsonObject();
    }

    private static void SaveRoot(string path, JsonObject root)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, root.ToJsonString(WriteOpts));
        File.Copy(tmp, path, overwrite: true);
        File.Delete(tmp);
    }

    /// <summary>去掉 // 与 /* */ 注释、对象/数组尾逗号，便于 JsonNode.Parse JSONC。</summary>
    internal static string StripJsonc(string text)
    {
        var sb = new StringBuilder(text.Length);
        var i = 0;
        var inStr = false;
        var escape = false;
        while (i < text.Length)
        {
            var c = text[i];
            if (inStr)
            {
                sb.Append(c);
                if (escape) escape = false;
                else if (c == '\\') escape = true;
                else if (c == '"') inStr = false;
                i++;
                continue;
            }
            if (c == '"')
            {
                inStr = true;
                sb.Append(c);
                i++;
                continue;
            }
            if (c == '/' && i + 1 < text.Length)
            {
                var n = text[i + 1];
                if (n == '/')
                {
                    i += 2;
                    while (i < text.Length && text[i] is not ('\n' or '\r'))
                        i++;
                    continue;
                }
                if (n == '*')
                {
                    i += 2;
                    while (i + 1 < text.Length && !(text[i] == '*' && text[i + 1] == '/'))
                        i++;
                    i = Math.Min(i + 2, text.Length);
                    continue;
                }
            }
            if (c == ',')
            {
                var j = i + 1;
                while (j < text.Length && char.IsWhiteSpace(text[j]))
                    j++;
                if (j < text.Length && text[j] is '}' or ']')
                {
                    i++;
                    continue;
                }
            }
            sb.Append(c);
            i++;
        }
        return sb.ToString();
    }
}
