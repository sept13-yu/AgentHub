using System.Globalization;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.RepresentationModel;

namespace AgentHub.Core.McpCore.Adapters;

/// <summary>只解析 DSH patch 的静态 insert；源码编辑由同目录的 editor 完成。</summary>
internal sealed class DshMcpPatchDocument
{
    internal const string ClientName = "@deepseek-ai/dsh-mcp-client";
    internal string Text { get; }
    internal YamlSequenceNode Root { get; }
    internal IReadOnlyList<DshMcpEntry> Entries { get; }
    internal bool SafeToWrite { get; }

    private DshMcpPatchDocument(string text, YamlSequenceNode root,
        IReadOnlyList<DshMcpEntry> entries, bool safeToWrite)
    {
        Text = text;
        Root = root;
        Entries = entries;
        SafeToWrite = safeToWrite;
    }

    internal static DshMcpPatchDocument Load(string path)
    {
        if (!File.Exists(path)) return Parse("", path, allowEmpty: true);
        return Parse(File.ReadAllText(path), path);
    }

    internal static DshMcpPatchDocument Parse(string text, string path, bool allowEmpty = false)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            if (!allowEmpty) throw Invalid("共享配置为空", path);
            text = "[]";
        }
        YamlStream yaml = new();
        try { yaml.Load(new StringReader(text)); }
        catch (YamlException) { throw Invalid("共享配置语法无效", path); }
        if (yaml.Documents.Count != 1 || yaml.Documents[0].RootNode is not YamlSequenceNode root)
            throw Invalid("共享配置必须是 patch 操作数组", path);

        var entries = new List<DshMcpEntry>();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var safe = root.Children.Count == 0 || root.Style == SequenceStyle.Block;
        foreach (var op in root.Children)
        {
            if (op is not YamlMappingNode map || IsDynamic(op) || map.Style != MappingStyle.Block)
            { safe = false; continue; }
            CheckKeys(map, path);
            foreach (var (key, value) in map.Children)
            {
                if (key is not YamlScalarNode sk || IsDynamic(key) || sk.Value != "insert")
                { safe = false; continue; }
                if (value is not YamlSequenceNode seq || IsDynamic(value) || seq.Style != SequenceStyle.Block)
                { safe = false; continue; }
                foreach (var item in seq.Children)
                {
                    if (item is not YamlMappingNode entry || IsDynamic(item) || entry.Style != MappingStyle.Block)
                    { safe = false; continue; }
                    CheckKeys(entry, path);
                    var idNode = Get(entry, "id");
                    if (idNode is not YamlScalarNode id || !IsString(id) || string.IsNullOrWhiteSpace(id.Value))
                    { safe = false; continue; }
                    if (!ids.Add(id.Value)) throw Invalid("entry id 重复", path);
                    var nameNode = Get(entry, "name");
                    if (nameNode is not YamlScalarNode name || !IsString(name)) { safe = false; continue; }
                    if (name.Value != ClientName) continue;
                    var configNode = Get(entry, "config");
                    if (configNode is not YamlMappingNode config || IsDynamic(config))
                        throw Invalid("MCP config 不是静态映射", path);
                    if (config.Style != MappingStyle.Block) safe = false;
                    CheckKeys(config, path);
                    var serverName = RequiredScalar(config, "serverName", path);
                    if (!names.Add(serverName)) throw Invalid("MCP serverName 重复", path);
                    entries.Add(new DshMcpEntry(entry, config, serverName, id.Value));
                }
            }
        }
        return new DshMcpPatchDocument(text, root, entries, safe);
    }

    internal IReadOnlyList<McpServerSpec> List() => Entries.Select(ParseEntry).ToList();

    internal string Upsert(McpServerSpec spec)
    {
        EnsureSafe();
        _ = List();
        var entry = Find(spec.Id);
        return entry is null ? DshMcpPatchEditor.Add(this, spec) : DshMcpPatchEditor.Update(this, entry, spec);
    }

    internal string SetEnabled(string id, bool enabled)
    {
        EnsureSafe();
        _ = List();
        var entry = Find(id) ?? throw new KeyNotFoundException("DSH MCP 不存在");
        return DshMcpPatchEditor.SetEnabled(this, entry, enabled);
    }

    internal string Remove(string id)
    {
        EnsureSafe();
        _ = List();
        var entry = Find(id);
        return entry is null ? Text : DshMcpPatchEditor.Remove(this, entry);
    }

    private DshMcpEntry? Find(string id) => Entries.FirstOrDefault(e =>
        string.Equals(e.ServerName, id, StringComparison.OrdinalIgnoreCase));

    private void EnsureSafe()
    {
        if (!SafeToWrite || Entries.Any(e => ContainsDynamic(e.Node)))
            throw new InvalidOperationException("该 DSH 配置暂不支持安全编辑");
    }

    private static McpServerSpec ParseEntry(DshMcpEntry entry)
    {
        var config = entry.Config;
        var transport = RequiredScalar(config, "transport", "DSH");
        var spec = new McpServerSpec { Id = entry.ServerName };
        spec.Transport = transport switch
        {
            "stdio" => McpTransport.Stdio,
            "streamable-http" => McpTransport.Http,
            _ => throw Invalid("MCP transport 未知", "DSH"),
        };
        var disabled = Get(entry.Node, "disabled");
        if (disabled is not null)
        {
            if (disabled is not YamlScalarNode { Style: ScalarStyle.Plain } ds || IsDynamic(ds) ||
                !bool.TryParse(ds.Value, out var disabledValue))
                throw Invalid("MCP disabled 类型无效", "DSH");
            spec.Enabled = !disabledValue;
        }
        if (spec.Transport == McpTransport.Stdio)
        {
            spec.Command = RequiredScalar(config, "command", "DSH");
            if (string.IsNullOrWhiteSpace(spec.Command)) throw Invalid("MCP command 为空", "DSH");
            if (Get(config, "url") is not null || Get(config, "headers") is not null)
                throw Invalid("MCP transport 字段冲突", "DSH");
            foreach (var arg in ReadSeq(config, "args")) spec.Args.Add(arg);
            foreach (var (key, value) in ReadMap(config, "env")) spec.Env.Add(key, value);
        }
        else
        {
            spec.Url = RequiredScalar(config, "url", "DSH");
            if (!Uri.TryCreate(spec.Url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
                throw Invalid("MCP url 无效", "DSH");
            if (Get(config, "command") is not null || Get(config, "args") is not null || Get(config, "env") is not null)
                throw Invalid("MCP transport 字段冲突", "DSH");
            foreach (var (key, value) in ReadMap(config, "headers")) spec.Headers.Add(key, value);
        }
        var timeout = Get(config, "toolCallTimeoutMs");
        if (timeout is not null)
        {
            if (timeout is not YamlScalarNode { Style: ScalarStyle.Plain } ts || IsDynamic(ts) ||
                !int.TryParse(ts.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var ms) || ms <= 0)
                throw Invalid("MCP timeout 无效", "DSH");
            spec.TimeoutMs = ms;
        }
        return spec;
    }

    private static IEnumerable<string> ReadSeq(YamlMappingNode map, string key)
    {
        var node = Get(map, key);
        if (node is null) return [];
        if (node is not YamlSequenceNode seq || IsDynamic(seq) || seq.Children.Any(n => n is not YamlScalarNode s || !IsString(s)))
            throw Invalid($"MCP {key} 类型无效", "DSH");
        return seq.Children.Cast<YamlScalarNode>().Select(n => n.Value ?? "").ToArray();
    }

    private static IEnumerable<KeyValuePair<string, string>> ReadMap(YamlMappingNode map, string key)
    {
        var node = Get(map, key);
        if (node is null) return [];
        if (node is not YamlMappingNode values || IsDynamic(values))
            throw Invalid($"MCP {key} 类型无效", "DSH");
        CheckKeys(values, "DSH");
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (k, v) in values.Children)
        {
            if (k is not YamlScalarNode ks || v is not YamlScalarNode vs || !IsString(ks) || !IsString(vs))
                throw Invalid($"MCP {key} 类型无效", "DSH");
            result.Add(ks.Value ?? "", vs.Value ?? "");
        }
        return result;
    }

    internal static YamlNode? Get(YamlMappingNode map, string key) => map.Children
        .FirstOrDefault(p => p.Key is YamlScalarNode s && s.Value == key).Value;

    internal static bool IsDynamic(YamlNode node) => node.NodeType == YamlNodeType.Alias ||
        !node.Tag.IsEmpty || !node.Anchor.IsEmpty;

    private static bool ContainsDynamic(YamlNode node) => IsDynamic(node) || node switch
    {
        YamlMappingNode map => map.Children.Any(pair => ContainsDynamic(pair.Key) || ContainsDynamic(pair.Value)),
        YamlSequenceNode sequence => sequence.Children.Any(ContainsDynamic),
        _ => false,
    };

    private static string RequiredScalar(YamlMappingNode map, string key, string path)
    {
        if (Get(map, key) is not YamlScalarNode node || !IsString(node) || string.IsNullOrWhiteSpace(node.Value))
            throw Invalid($"MCP {key} 无效", path);
        return node.Value;
    }

    private static bool IsString(YamlScalarNode node)
    {
        if (IsDynamic(node) || node.Value is null) return false;
        if (node.Style != ScalarStyle.Plain) return true;
        var value = node.Value;
        if (value == "~" || value.Equals("null", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("false", StringComparison.OrdinalIgnoreCase)) return false;
        return !System.Text.RegularExpressions.Regex.IsMatch(value,
            @"^[-+]?(?:\d+(?:\.\d*)?(?:[eE][-+]?\d+)?|0[xX][0-9a-fA-F]+|0[oO][0-7]+)$");
    }

    private static void CheckKeys(YamlMappingNode map, string path)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in map.Children.Keys)
        {
            if (key is not YamlScalarNode scalar || IsDynamic(key) || scalar.Value == "<<")
                throw Invalid("配置包含动态映射键", path);
            if (!keys.Add(scalar.Value ?? "")) throw Invalid("映射键重复", path);
        }
    }

    private static InvalidOperationException Invalid(string detail, string path) =>
        new($"DSH 配置错误（{Path.GetFileName(path)}）：{detail}");
}

internal sealed record DshMcpEntry(YamlMappingNode Node, YamlMappingNode Config, string ServerName, string EntryId);
