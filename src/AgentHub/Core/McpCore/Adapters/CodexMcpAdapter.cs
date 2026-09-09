using System.IO;
using System.Text;
using System.IO;
using System.Text.RegularExpressions;
using System.IO;
using AgentHub.Core.CodexConfigCore;
using System.IO;
using AgentHub.Core.ProxyCore;
using System.IO;
using Tomlyn;
using System.IO;
using Tomlyn.Model;

namespace AgentHub.Core.McpCore.Adapters;

/// <summary>Codex：~/.codex/config.toml 的 [mcp_servers.x]；永不删除/覆盖系统项 node_repl / cua_repl。</summary>
public sealed partial class CodexMcpAdapter : IMcpAdapter
{
    public static readonly HashSet<string> SystemNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_repl", "cua_repl",
    };

    public string AgentId => "codex";
    public string DisplayName => "Codex";
    public string ConfigPath { get; }
    public bool Detected => File.Exists(ConfigPath)
        || Directory.Exists(Path.GetDirectoryName(ConfigPath) ?? "");

    public CodexMcpAdapter(string? configPath = null)
    {
        ConfigPath = configPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "config.toml");
    }

    public IReadOnlyList<McpServerSpec> List()
    {
        if (!File.Exists(ConfigPath)) return [];
        try
        {
            var text = File.ReadAllText(ConfigPath);
            var doc = Toml.Parse(text);
            if (doc.Diagnostics.Count > 0) return [];
            var root = doc.ToModel();
            if (!root.TryGetValue("mcp_servers", out var ms) || ms is not TomlTable servers)
                return [];
            var list = new List<McpServerSpec>();
            foreach (var key in servers.Keys)
            {
                if (servers[key] is not TomlTable table) continue;
                list.Add(ParseTable(key, table));
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
        if (SystemNames.Contains(spec.Id))
            throw new InvalidOperationException($"Codex 系统项 {spec.Id} 不可覆盖");
        Backup();
        var text = File.Exists(ConfigPath) ? File.ReadAllText(ConfigPath) : "";
        var rendered = RenderSection(spec);
        var (start, end) = FindSectionSpan(text, spec.Id);
        string next;
        if (start >= 0)
            next = text[..start] + rendered + text[end..];
        else
        {
            var trimmed = text.TrimEnd();
            next = trimmed.Length == 0
                ? rendered
                : trimmed + (trimmed.EndsWith('\n') ? "" : Environment.NewLine) + Environment.NewLine + rendered;
        }
        ValidateToml(next);
        AtomicWrite(ConfigPath, next);
    }

    public void SetEnabled(string id, bool enabled)
    {
        if (SystemNames.Contains(id))
            throw new InvalidOperationException($"Codex 系统项 {id} 不由 AgentHub 启停");
        var list = List();
        var spec = list.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase))
            ?? throw new KeyNotFoundException($"Codex 没有 MCP：{id}");
        spec.Enabled = enabled;
        Upsert(spec);
    }

    public void Remove(string id)
    {
        if (SystemNames.Contains(id))
            throw new InvalidOperationException($"Codex 系统项 {id} 不可删除");
        if (!File.Exists(ConfigPath)) return;
        Backup();
        var text = File.ReadAllText(ConfigPath);
        var (start, end) = FindSectionSpan(text, id);
        if (start < 0) return;
        var next = text[..start] + text[end..];
        ValidateToml(next);
        AtomicWrite(ConfigPath, next);
    }

    private static McpServerSpec ParseTable(string id, TomlTable table)
    {
        var hasUrl = table.ContainsKey("url");
        var spec = new McpServerSpec
        {
            Id = id,
            Transport = hasUrl ? McpTransport.Http : McpTransport.Stdio,
            Command = Str(table, "command"),
            Url = Str(table, "url"),
            Enabled = !table.ContainsKey("enabled") || Bool(table, "enabled"),
        };
        if (table.TryGetValue("args", out var args) && args is TomlArray arr)
            foreach (var a in arr)
                spec.Args.Add(a?.ToString() ?? "");
        if (table.TryGetValue("env", out var env) && env is TomlTable envTable)
            foreach (var key in envTable.Keys)
                spec.Env[key] = envTable[key]?.ToString() ?? "";
        if (table.TryGetValue("http_headers", out var headers) && headers is TomlTable ht)
            foreach (var key in ht.Keys)
                spec.Headers[key] = ht[key]?.ToString() ?? "";
        if (table.TryGetValue("startup_timeout_sec", out var st) && st is long sec)
            spec.StartupTimeoutSec = (int)sec;
        return spec;
    }

    private static string RenderSection(McpServerSpec spec)
    {
        var nl = Environment.NewLine;
        var sb = new StringBuilder();
        sb.Append("[mcp_servers.").Append(spec.Id).Append(']').Append(nl);
        if (spec.Transport == McpTransport.Http)
        {
            sb.Append("url = \"").Append(CodexToml.Esc(spec.Url ?? "")).Append('"').Append(nl);
            sb.Append("enabled = ").Append(spec.Enabled ? "true" : "false").Append(nl);
            if (spec.Headers.Count > 0)
            {
                sb.Append(nl).Append("[mcp_servers.").Append(spec.Id).Append(".http_headers]").Append(nl);
                foreach (var (k, v) in spec.Headers)
                    sb.Append(k).Append(" = \"").Append(CodexToml.Esc(v)).Append('"').Append(nl);
            }
        }
        else
        {
            sb.Append("command = '").Append(EscSingle(spec.Command ?? "")).Append('\'').Append(nl);
            if (spec.Args.Count > 0)
            {
                sb.Append("args = [");
                sb.Append(string.Join(", ", spec.Args.Select(a => "'" + EscSingle(a) + "'")));
                sb.Append(']').Append(nl);
            }
            else
            {
                sb.Append("args = []").Append(nl);
            }
            sb.Append("enabled = ").Append(spec.Enabled ? "true" : "false").Append(nl);
            var timeout = spec.StartupTimeoutSec ?? 30;
            sb.Append("startup_timeout_sec = ").Append(timeout).Append(nl);
            if (spec.Env.Count > 0)
            {
                sb.Append(nl).Append("[mcp_servers.").Append(spec.Id).Append(".env]").Append(nl);
                foreach (var (k, v) in spec.Env)
                    sb.Append(k).Append(" = \"").Append(CodexToml.Esc(v)).Append('"').Append(nl);
            }
        }
        return sb.ToString();
    }

    /// <summary>定位 [mcp_servers.name] 及其子表（.env / .http_headers）直到下一个非本前缀表头。</summary>
    public static (int Start, int End) FindSectionSpan(string text, string id)
    {
        var lines = SplitKeep(text);
        var header = $"[mcp_servers.{id}]";
        var prefix = $"[mcp_servers.{id}.";
        var startLine = -1;
        for (var i = 0; i < lines.Count; i++)
        {
            var t = lines[i].TrimEnd();
            if (t.Equals(header, StringComparison.OrdinalIgnoreCase)
                || t.StartsWith(header + " ", StringComparison.OrdinalIgnoreCase)
                || t.StartsWith(header + "#", StringComparison.OrdinalIgnoreCase))
            {
                startLine = i;
                break;
            }
        }
        if (startLine < 0) return (-1, -1);
        var endLine = lines.Count;
        for (var i = startLine + 1; i < lines.Count; i++)
        {
            var t = lines[i].TrimEnd();
            if (!HeaderLine().IsMatch(t)) continue;
            if (t.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            if (t.Equals(header, StringComparison.OrdinalIgnoreCase)) continue;
            endLine = i;
            break;
        }
        var start = lines.Take(startLine).Sum(l => l.Length);
        var end = lines.Take(endLine).Sum(l => l.Length);
        return (start, end);
    }

    private static List<string> SplitKeep(string text)
    {
        var list = new List<string>();
        var i = 0;
        while (i < text.Length)
        {
            var n = text.IndexOf('\n', i);
            if (n < 0)
            {
                list.Add(text[i..]);
                break;
            }
            list.Add(text[i..(n + 1)]);
            i = n + 1;
        }
        return list;
    }

    private static void ValidateToml(string text)
    {
        var doc = Toml.Parse(text);
        if (doc.Diagnostics.Count > 0)
            throw new InvalidOperationException("Codex config.toml 语法无效：" + doc.Diagnostics[0].Message);
    }

    private void Backup()
    {
        if (!File.Exists(ConfigPath)) return;
        var root = Path.Combine(AgentHubConfig.LocalDataDir, "McpBackups", "codex");
        Directory.CreateDirectory(root);
        File.Copy(ConfigPath, Path.Combine(root, $"{DateTime.Now:yyyyMMdd-HHmmss}.toml"), overwrite: true);
    }

    private static void AtomicWrite(string path, string text)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, text);
        File.Copy(tmp, path, overwrite: true);
        File.Delete(tmp);
    }

    private static string? Str(TomlTable table, string key) =>
        table.TryGetValue(key, out var v) && v is string s ? s : null;
    private static bool Bool(TomlTable table, string key) =>
        table.TryGetValue(key, out var v) && v is bool b && b;

    private static string EscSingle(string value) => value.Replace("'", "''");

    [GeneratedRegex(@"^\s*\[.+\]\s*(#.*)?$")]
    private static partial Regex HeaderLine();
}
