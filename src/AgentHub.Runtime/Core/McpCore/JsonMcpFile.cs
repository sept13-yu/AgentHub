using System.IO;
using System.Text.Json;
using System.IO;
using System.Text.Json.Nodes;

namespace AgentHub.Core.McpCore;

/// <summary>通用 JSON MCP 文件读写（Cursor / Trae / WorkBuddy 顶层 mcpServers；ZCode 嵌套 mcp.servers）。</summary>
public static class JsonMcpFile
{
    private static readonly JsonSerializerOptions WriteOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static JsonObject LoadRoot(string path)
    {
        if (!File.Exists(path))
            return new JsonObject();
        var text = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(text))
            return new JsonObject();
        return JsonNode.Parse(text) as JsonObject ?? new JsonObject();
    }

    public static void SaveRoot(string path, JsonObject root)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, root.ToJsonString(WriteOpts));
        File.Copy(tmp, path, overwrite: true);
        File.Delete(tmp);
    }

    public static JsonObject GetOrCreateServers(JsonObject root, string[] pathParts)
    {
        JsonObject cur = root;
        for (var i = 0; i < pathParts.Length; i++)
        {
            var key = pathParts[i];
            if (cur[key] is JsonObject next)
            {
                cur = next;
                continue;
            }
            next = new JsonObject();
            cur[key] = next;
            cur = next;
        }
        return cur;
    }

    public static JsonObject? TryGetServers(JsonObject root, string[] pathParts)
    {
        JsonNode? cur = root;
        foreach (var key in pathParts)
        {
            if (cur is not JsonObject obj || obj[key] is not JsonNode next)
                return null;
            cur = next;
        }
        return cur as JsonObject;
    }

    public static McpServerSpec ParseServer(string id, JsonObject node, Func<JsonObject, bool> readEnabled)
    {
        var hasUrl = node["url"] is JsonValue;
        var type = node["type"]?.GetValue<string>();
        var transport = hasUrl || string.Equals(type, "streamableHttp", StringComparison.OrdinalIgnoreCase)
            || string.Equals(type, "sse", StringComparison.OrdinalIgnoreCase)
            || string.Equals(type, "http", StringComparison.OrdinalIgnoreCase)
            ? McpTransport.Http
            : McpTransport.Stdio;

        var spec = new McpServerSpec
        {
            Id = id,
            Transport = transport,
            Command = node["command"]?.GetValue<string>(),
            Url = node["url"]?.GetValue<string>(),
            Enabled = readEnabled(node),
            ExplicitType = type,
        };

        if (node["args"] is JsonArray args)
        {
            foreach (var a in args)
                if (a is JsonValue v)
                    spec.Args.Add(v.GetValue<string>() ?? "");
        }
        if (node["env"] is JsonObject env)
        {
            foreach (var (k, val) in env)
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
        if (node["startup_timeout_sec"] is JsonValue st && st.TryGetValue<int>(out var sec))
            spec.StartupTimeoutSec = sec;
        return spec;
    }

    public static JsonObject ToServerNode(McpServerSpec spec, Action<JsonObject, bool> writeEnabled, bool workBuddyHttp = false)
    {
        var node = new JsonObject();
        if (spec.Transport == McpTransport.Http)
        {
            if (workBuddyHttp || string.Equals(spec.ExplicitType, "streamableHttp", StringComparison.OrdinalIgnoreCase))
                node["type"] = "streamableHttp";
            else if (!string.IsNullOrWhiteSpace(spec.ExplicitType))
                node["type"] = spec.ExplicitType;
            node["url"] = spec.Url ?? "";
            if (spec.Headers.Count > 0)
            {
                var headers = new JsonObject();
                foreach (var (k, v) in spec.Headers)
                    headers[k] = v;
                node["headers"] = headers;
            }
            if (spec.TimeoutMs is int ms)
                node["timeout"] = ms;
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(spec.ExplicitType) &&
                !string.Equals(spec.ExplicitType, "stdio", StringComparison.OrdinalIgnoreCase))
                node["type"] = spec.ExplicitType;
            node["command"] = spec.Command ?? "";
            if (spec.Args.Count > 0)
            {
                var args = new JsonArray();
                foreach (var a in spec.Args) args.Add(a);
                node["args"] = args;
            }
            if (spec.Env.Count > 0)
            {
                var env = new JsonObject();
                foreach (var (k, v) in spec.Env) env[k] = v;
                node["env"] = env;
            }
        }
        writeEnabled(node, spec.Enabled);
        return node;
    }
}
