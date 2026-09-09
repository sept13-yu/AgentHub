using System.IO;
using System.Text.Json.Serialization;

namespace AgentHub.Core.McpCore;

public enum McpTransport
{
    Stdio,
    Http,
}

public enum McpPresence
{
    Present,
    Missing,
    System,
    Unsupported,
}

public sealed class McpServerSpec
{
    public string Id { get; set; } = "";
    public McpTransport Transport { get; set; } = McpTransport.Stdio;
    public string? Command { get; set; }
    public List<string> Args { get; set; } = [];
    public Dictionary<string, string> Env { get; set; } = new(StringComparer.Ordinal);
    public string? Url { get; set; }
    public Dictionary<string, string> Headers { get; set; } = new(StringComparer.Ordinal);
    public bool Enabled { get; set; } = true;
    public int? StartupTimeoutSec { get; set; }
    public int? TimeoutMs { get; set; }
    public string? Note { get; set; }
    public string? Alias { get; set; }
    /// <summary>WorkBuddy streamableHttp 等显式类型；空则按 transport 推断。</summary>
    public string? ExplicitType { get; set; }

    public McpServerSpec Clone() => new()
    {
        Id = Id,
        Transport = Transport,
        Command = Command,
        Args = [.. Args],
        Env = new Dictionary<string, string>(Env, StringComparer.Ordinal),
        Url = Url,
        Headers = new Dictionary<string, string>(Headers, StringComparer.Ordinal),
        Enabled = Enabled,
        StartupTimeoutSec = StartupTimeoutSec,
        TimeoutMs = TimeoutMs,
        Note = Note,
        Alias = Alias,
        ExplicitType = ExplicitType,
    };
}

public sealed class McpAgentStatus
{
    public string AgentId { get; set; } = "";
    public McpPresence Presence { get; set; } = McpPresence.Missing;
    public bool? EnabledOnAgent { get; set; }
    public bool Drift { get; set; }
    public string ConfigPath { get; set; } = "";
    public string? Detail { get; set; }
    public bool Detected { get; set; }
}

public sealed class McpManagedItem
{
    public McpServerSpec Spec { get; set; } = new();
    public List<McpAgentStatus> Agents { get; set; } = [];
    public bool InMother { get; set; }
    public bool HasSecretRisk { get; set; }
}

public sealed class McpMotherDocument
{
    public int Version { get; set; } = 1;
    public Dictionary<string, McpMotherServer> Servers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, bool> Targets { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["cursor"] = true,
        ["codex"] = true,
        ["trae"] = true,
        ["workbuddy"] = true,
        ["zcode"] = true,
    };
    public List<string> ExcludeNames { get; set; } = ["node_repl", "cua_repl"];
}

public sealed class McpMotherServer
{
    public string Transport { get; set; } = "stdio";
    public string? Command { get; set; }
    public List<string>? Args { get; set; }
    public Dictionary<string, string>? Env { get; set; }
    public string? Url { get; set; }
    public Dictionary<string, string>? Headers { get; set; }
    public bool Enabled { get; set; } = true;
    public int? StartupTimeoutSec { get; set; }
    public int? TimeoutMs { get; set; }
    public string? Alias { get; set; }
    public string? Note { get; set; }
    public string? ExplicitType { get; set; }
}

public sealed class McpSyncResult
{
    public bool Ok { get; set; }
    public List<McpSyncItemResult> Items { get; set; } = [];
    public string? Error { get; set; }
}

public sealed class McpSyncItemResult
{
    public string Name { get; set; } = "";
    public string Agent { get; set; } = "";
    public bool Ok { get; set; }
    public string? Error { get; set; }
}

public static class McpSecrets
{
    public static bool LooksSecretKey(string key)
    {
        if (string.IsNullOrEmpty(key)) return false;
        var k = key.ToLowerInvariant();
        return k.Contains("password") || k.Contains("token") || k.Contains("secret")
            || k.Contains("apikey") || k.Contains("api_key") || k.Contains("authorization")
            || k.Contains("access_key") || k.Contains("private");
    }

    public static bool LooksSecretValue(string? value)
    {
        if (string.IsNullOrEmpty(value)) return false;
        var v = value.ToLowerInvariant();
        return v.Contains("password=") || v.Contains("token=") || v.Contains("access_token=")
            || v.Contains("bearer ") || v.Contains("secret=");
    }

    public static bool HasSecretRisk(McpServerSpec spec)
    {
        foreach (var kv in spec.Env)
            if (LooksSecretKey(kv.Key) || LooksSecretValue(kv.Value)) return true;
        foreach (var kv in spec.Headers)
            if (LooksSecretKey(kv.Key) || LooksSecretValue(kv.Value)) return true;
        return LooksSecretValue(spec.Url) || LooksSecretValue(spec.Command);
    }

    public static McpServerSpec Mask(McpServerSpec src)
    {
        var s = src.Clone();
        foreach (var key in s.Env.Keys.ToList())
            if (LooksSecretKey(key) || LooksSecretValue(s.Env[key]))
                s.Env[key] = "***";
        foreach (var key in s.Headers.Keys.ToList())
            if (LooksSecretKey(key) || LooksSecretValue(s.Headers[key]))
                s.Headers[key] = "***";
        if (LooksSecretValue(s.Url)) s.Url = MaskUrl(s.Url!);
        return s;
    }

    private static string MaskUrl(string url)
    {
        try
        {
            var uri = new Uri(url);
            var q = uri.Query;
            if (string.IsNullOrEmpty(q)) return url;
            return url.Split('?')[0] + "?***";
        }
        catch
        {
            return "***";
        }
    }
}
