using System.IO;
using System.Text.Json;
using System.IO;
using System.Text.Json.Serialization;
using System.IO;
using AgentHub.Core.ProxyCore;

namespace AgentHub.Core.McpCore;

/// <summary>母本读写：%LOCALAPPDATA%\AgentHub.Local\mcp-mother.json。空母本默认空列表。</summary>
public sealed class McpMotherStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly string _path;
    private readonly object _gate = new();

    public McpMotherStore(string? localDataRoot = null)
    {
        var root = localDataRoot ?? AgentHubConfig.LocalDataDir;
        Directory.CreateDirectory(root);
        _path = Path.Combine(root, "mcp-mother.json");
    }

    public string MotherPath => _path;

    public McpMotherDocument Load()
    {
        lock (_gate)
        {
            if (!File.Exists(_path))
                return new McpMotherDocument();
            try
            {
                var text = File.ReadAllText(_path);
                if (string.IsNullOrWhiteSpace(text))
                    return new McpMotherDocument();
                var doc = JsonSerializer.Deserialize<McpMotherDocument>(text, JsonOpts) ?? new McpMotherDocument();
                doc.Servers ??= new Dictionary<string, McpMotherServer>(StringComparer.OrdinalIgnoreCase);
                doc.Targets ??= new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                doc.ExcludeNames ??= ["node_repl", "cua_repl"];
                EnsureDefaultTargets(doc);
                return doc;
            }
            catch (JsonException)
            {
                return new McpMotherDocument();
            }
        }
    }

    public void Save(McpMotherDocument doc)
    {
        lock (_gate)
        {
            EnsureDefaultTargets(doc);
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var tmp = _path + ".tmp";
            var json = JsonSerializer.Serialize(doc, JsonOpts);
            File.WriteAllText(tmp, json);
            File.Copy(tmp, _path, overwrite: true);
            File.Delete(tmp);
        }
    }

    public IReadOnlyDictionary<string, McpServerSpec> ListSpecs()
    {
        var doc = Load();
        var map = new Dictionary<string, McpServerSpec>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, s) in doc.Servers)
            map[id] = FromMother(id, s);
        return map;
    }

    public McpServerSpec? Get(string id)
    {
        var doc = Load();
        return doc.Servers.TryGetValue(id, out var s) ? FromMother(id, s) : null;
    }

    public void Upsert(McpServerSpec spec)
    {
        if (string.IsNullOrWhiteSpace(spec.Id))
            throw new ArgumentException("MCP 名称不能为空");
        var doc = Load();
        if (IsExcluded(doc, spec.Id))
            throw new InvalidOperationException($"系统项 {spec.Id} 不能写入母本");
        doc.Servers[spec.Id] = ToMother(spec);
        Save(doc);
    }

    public void SetEnabled(string id, bool enabled)
    {
        var doc = Load();
        if (!doc.Servers.TryGetValue(id, out var s))
            throw new KeyNotFoundException($"母本中没有 {id}");
        s.Enabled = enabled;
        Save(doc);
    }

    public void SetMeta(string id, string? alias, string? note)
    {
        var doc = Load();
        if (!doc.Servers.TryGetValue(id, out var s))
            throw new KeyNotFoundException($"母本中没有 {id}");
        if (alias is not null) s.Alias = alias;
        if (note is not null) s.Note = note;
        Save(doc);
    }

    public void Remove(string id)
    {
        var doc = Load();
        doc.Servers.Remove(id);
        Save(doc);
    }

    public void ReplaceAll(IEnumerable<McpServerSpec> specs, IEnumerable<string>? keepExclude = null)
    {
        var doc = Load();
        var exclude = new HashSet<string>(doc.ExcludeNames, StringComparer.OrdinalIgnoreCase);
        if (keepExclude is not null)
            foreach (var n in keepExclude) exclude.Add(n);
        doc.Servers.Clear();
        foreach (var spec in specs)
        {
            if (string.IsNullOrWhiteSpace(spec.Id) || exclude.Contains(spec.Id)) continue;
            doc.Servers[spec.Id] = ToMother(spec);
        }
        Save(doc);
    }

    public bool IsExcluded(string id)
    {
        var doc = Load();
        return IsExcluded(doc, id);
    }

    public static bool IsExcluded(McpMotherDocument doc, string id) =>
        doc.ExcludeNames.Any(x => string.Equals(x, id, StringComparison.OrdinalIgnoreCase));

    public static McpServerSpec FromMother(string id, McpMotherServer s)
    {
        var transport = string.Equals(s.Transport, "http", StringComparison.OrdinalIgnoreCase)
            ? McpTransport.Http
            : McpTransport.Stdio;
        return new McpServerSpec
        {
            Id = id,
            Transport = transport,
            Command = s.Command,
            Args = s.Args is null ? [] : [.. s.Args],
            Env = s.Env is null
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : new Dictionary<string, string>(s.Env, StringComparer.Ordinal),
            Url = s.Url,
            Headers = s.Headers is null
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : new Dictionary<string, string>(s.Headers, StringComparer.Ordinal),
            Enabled = s.Enabled,
            StartupTimeoutSec = s.StartupTimeoutSec,
            TimeoutMs = s.TimeoutMs,
            Alias = s.Alias,
            Note = s.Note,
            ExplicitType = s.ExplicitType,
        };
    }

    public static McpMotherServer ToMother(McpServerSpec spec) => new()
    {
        Transport = spec.Transport == McpTransport.Http ? "http" : "stdio",
        Command = spec.Command,
        Args = spec.Args.Count == 0 ? null : [.. spec.Args],
        Env = spec.Env.Count == 0 ? null : new Dictionary<string, string>(spec.Env, StringComparer.Ordinal),
        Url = spec.Url,
        Headers = spec.Headers.Count == 0 ? null : new Dictionary<string, string>(spec.Headers, StringComparer.Ordinal),
        Enabled = spec.Enabled,
        StartupTimeoutSec = spec.StartupTimeoutSec,
        TimeoutMs = spec.TimeoutMs,
        Alias = spec.Alias,
        Note = spec.Note,
        ExplicitType = spec.ExplicitType,
    };

    private static void EnsureDefaultTargets(McpMotherDocument doc)
    {
        foreach (var id in new[] { "cursor", "codex", "trae", "workbuddy", "zcode", "mimocode" })
            if (!doc.Targets.ContainsKey(id))
                doc.Targets[id] = true;
    }
}
