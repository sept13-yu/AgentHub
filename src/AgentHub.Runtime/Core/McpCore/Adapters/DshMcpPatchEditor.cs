using System.Text;
using System.Text.Json;
using YamlDotNet.Core.Events;
using YamlDotNet.RepresentationModel;

namespace AgentHub.Core.McpCore.Adapters;

/// <summary>按 YAML 节点源码位置改目标字段；未触及的 patch 原文字节保持不变。</summary>
internal static class DshMcpPatchEditor
{
    private sealed record Edit(int Start, int End, string Value);

    internal static string Add(DshMcpPatchDocument doc, McpServerSpec spec)
    {
        var id = "agenthub-mcp-" + Guid.NewGuid().ToString("N");
        var ids = doc.Root.Children.OfType<YamlMappingNode>()
            .SelectMany(m => m.Children.Values.OfType<YamlSequenceNode>())
            .SelectMany(s => s.Children.OfType<YamlMappingNode>())
            .Select(m => DshMcpPatchDocument.Get(m, "id"))
            .OfType<YamlScalarNode>().Select(s => s.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        while (ids.Contains(id)) id = "agenthub-mcp-" + Guid.NewGuid().ToString("N");
        var nl = Newline(doc.Text);
        var body = new StringBuilder();
        body.Append("- insert:").Append(nl)
            .Append("  - id: ").Append(Quote(id)).Append(nl)
            .Append("    name: ").Append(Quote(DshMcpPatchDocument.ClientName)).Append(nl)
            .Append("    config:").Append(nl);
        foreach (var (key, value) in Fields(spec))
            body.Append(RenderField(key, value, 6, nl));
        if (!spec.Enabled) body.Append("    disabled: true").Append(nl);
        if (doc.Root.Children.Count == 0)
        {
            var start = doc.Text.IndexOf('[', (int)doc.Root.Start.Index);
            var end = start < 0 ? -1 : doc.Text.IndexOf(']', start) + 1;
            if (start < 0 || end <= start)
                throw new InvalidOperationException("该 DSH 空配置暂不支持安全编辑");
            return doc.Text[..start] + body + doc.Text[end..];
        }
        var prefix = doc.Text.Length == 0 || doc.Text.EndsWith('\n') ? "" : nl;
        return doc.Text + prefix + body;
    }

    internal static string Update(DshMcpPatchDocument doc, DshMcpEntry entry, McpServerSpec spec)
    {
        var text = doc.Text;
        var nl = Newline(text);
        var edits = new List<Edit>();
        var fields = Fields(spec);
        foreach (var (key, value) in fields)
        {
            var old = DshMcpPatchDocument.Get(entry.Config, key);
            if (old is not null && Equivalent(old, value)) continue;
            if (old is null) continue;
            edits.Add(ReplaceField(text, entry.Config, key, value, nl));
        }
        foreach (var key in new[] { "command", "args", "env", "url", "headers", "toolCallTimeoutMs" })
        {
            if (fields.ContainsKey(key) || DshMcpPatchDocument.Get(entry.Config, key) is null) continue;
            edits.Add(ReplaceField(text, entry.Config, key, null, nl));
        }
        var missing = fields.Where(f => DshMcpPatchDocument.Get(entry.Config, f.Key) is null).ToArray();
        if (missing.Length > 0)
        {
            var configKey = entry.Node.Children.Keys.OfType<YamlScalarNode>().Single(k => k.Value == "config");
            var at = BlockEnd(text, (int)configKey.Start.Index, KeyIndent(entry.Node, "config"));
            var indent = (int)(entry.Config.Children.Keys.OfType<YamlScalarNode>().FirstOrDefault()?.Start.Column ?? 7) - 1;
            var inserted = PrefixAt(text, at, nl) + string.Concat(missing.Select(f => RenderField(f.Key, f.Value, indent, nl)));
            edits.Add(new Edit(at, at, inserted));
        }
        var updated = Apply(text, edits);
        var parsed = DshMcpPatchDocument.Parse(updated, "DSH");
        updated = SetEnabled(parsed, parsed.Entries.Single(e =>
            string.Equals(e.ServerName, spec.Id, StringComparison.OrdinalIgnoreCase)), spec.Enabled);
        return updated;
    }

    internal static string SetEnabled(DshMcpPatchDocument doc, DshMcpEntry entry, bool enabled)
    {
        var old = DshMcpPatchDocument.Get(entry.Node, "disabled");
        if (old is YamlScalarNode scalar)
        {
            var desired = enabled ? "false" : "true";
            if (scalar.Value == desired) return doc.Text;
            return Apply(doc.Text, [new Edit((int)scalar.Start.Index, (int)scalar.End.Index, desired)]);
        }
        if (enabled) return doc.Text;
        var at = BlockEnd(doc.Text, (int)entry.Node.Start.Index, ItemIndent(doc.Text, entry.Node));
        var indent = (int)(entry.Node.Children.Keys.OfType<YamlScalarNode>().FirstOrDefault()?.Start.Column ?? 5) - 1;
        var nl = Newline(doc.Text);
        return Apply(doc.Text, [new Edit(at, at, PrefixAt(doc.Text, at, nl) + new string(' ', indent) + "disabled: true" + nl)]);
    }

    internal static string Remove(DshMcpPatchDocument doc, DshMcpEntry entry)
    {
        var text = doc.Text;
        YamlNode target = entry.Node;
        foreach (var op in doc.Root.Children.OfType<YamlMappingNode>())
            if (DshMcpPatchDocument.Get(op, "insert") is YamlSequenceNode seq &&
                seq.Children.Count == 1 && ReferenceEquals(seq.Children[0], entry.Node))
                target = op;
        var start = LineStart(text, (int)target.Start.Index);
        int end;
        if (ReferenceEquals(target, entry.Node))
            end = BlockEnd(text, (int)entry.Node.Start.Index, ItemIndent(text, entry.Node));
        else
        {
            var index = doc.Root.Children.IndexOf(target);
            end = index + 1 < doc.Root.Children.Count
                ? LineStart(text, (int)doc.Root.Children[index + 1].Start.Index)
                : text.Length;
        }
        var remaining = Apply(text, [new Edit(start, end, "")]);
        if (doc.Root.Children.Count == 1 && !ReferenceEquals(target, entry.Node))
            return remaining + (remaining.EndsWith('\n') || remaining.Length == 0 ? "" : Newline(text)) + "[]" + Newline(text);
        return remaining;
    }

    private static Dictionary<string, string> Fields(McpServerSpec spec)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["serverName"] = Quote(spec.Id),
            ["transport"] = Quote(spec.Transport == McpTransport.Http ? "streamable-http" : "stdio"),
        };
        if (spec.Transport == McpTransport.Stdio)
        {
            fields["command"] = Quote(spec.Command ?? "");
            fields["args"] = RenderSeq(spec.Args);
            if (spec.Env.Count > 0) fields["env"] = RenderMap(spec.Env);
        }
        else
        {
            fields["url"] = Quote(spec.Url ?? "");
            if (spec.Headers.Count > 0) fields["headers"] = RenderMap(spec.Headers);
        }
        if (spec.TimeoutMs is { } ms) fields["toolCallTimeoutMs"] = ms.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return fields;
    }

    private static string RenderSeq(IEnumerable<string> values) => "[" + string.Join(", ", values.Select(Quote)) + "]";
    private static string RenderMap(IEnumerable<KeyValuePair<string, string>> values) =>
        "{" + string.Join(", ", values.Select(p => Quote(p.Key) + ": " + Quote(p.Value))) + "}";
    private static string Quote(string value) => JsonSerializer.Serialize(value);
    private static string RenderField(string key, string value, int indent, string nl) =>
        new string(' ', indent) + key + ": " + value + nl;

    private static bool Equivalent(YamlNode old, string rendered)
    {
        try
        {
            var stream = new YamlStream();
            stream.Load(new StringReader("value: " + rendered));
            var map = (YamlMappingNode)stream.Documents[0].RootNode;
            var desired = DshMcpPatchDocument.Get(map, "value")!;
            return Match(old, desired);
        }
        catch { return false; }
    }

    private static bool Match(YamlNode a, YamlNode b)
    {
        if (a is YamlScalarNode sa && b is YamlScalarNode sb) return sa.Value == sb.Value;
        if (a is YamlSequenceNode qa && b is YamlSequenceNode qb)
            return qa.Children.Count == qb.Children.Count && qa.Children.Zip(qb.Children).All(p => Match(p.First, p.Second));
        if (a is YamlMappingNode ma && b is YamlMappingNode mb)
            return ma.Children.Count == mb.Children.Count && ma.Children.All(p =>
                p.Key is YamlScalarNode key && DshMcpPatchDocument.Get(mb, key.Value ?? "") is { } v && Match(p.Value, v));
        return false;
    }

    private static Edit ReplaceField(string text, YamlMappingNode map, string key, string? value, string nl)
    {
        var keyNode = map.Children.Keys.OfType<YamlScalarNode>().Single(k => k.Value == key);
        var old = map.Children[keyNode];
        var block = old is YamlSequenceNode { Style: SequenceStyle.Block } or
            YamlMappingNode { Style: MappingStyle.Block };
        if (value is not null && !block)
        {
            var (valueStart, valueEnd) = ValueSpan(text, keyNode, old);
            return new Edit(valueStart, valueEnd, value);
        }
        var start = LineStart(text, (int)keyNode.Start.Index);
        var end = block ? BlockEnd(text, (int)keyNode.Start.Index, KeyIndent(map, key))
            : LineEnd(text, (int)old.End.Index);
        var raw = text[start..end];
        if (block && raw.Contains('#'))
            throw new InvalidOperationException("该 DSH 配置字段包含注释，暂不支持安全编辑");
        if (value is null)
        {
            var valueEnd = block ? end : ValueSpan(text, keyNode, old).End;
            var suffix = block ? "" : text[valueEnd..LineEnd(text, valueEnd)];
            var hash = suffix.IndexOf('#');
            return new Edit(start, end, hash < 0 ? "" : new string(' ', KeyIndent(map, key)) + suffix[hash..].TrimEnd('\r', '\n') + nl);
        }
        return new Edit(start, end, RenderField(key, value, KeyIndent(map, key), nl));
    }

    private static int KeyIndent(YamlMappingNode map, string key) =>
        (int)map.Children.Keys.OfType<YamlScalarNode>().Single(k => k.Value == key).Start.Column - 1;
    private static int ItemIndent(string text, YamlNode item)
    {
        var at = LineStart(text, (int)item.Start.Index);
        var dash = at;
        while (dash < text.Length && text[dash] == ' ') dash++;
        if (dash + 1 >= text.Length || text[dash] != '-' || text[dash + 1] != ' ')
            throw new InvalidOperationException("该 DSH 序列项暂不支持安全编辑");
        return dash - at;
    }
    private static (int Start, int End) ValueSpan(string text, YamlScalarNode key, YamlNode value)
    {
        if (value is YamlScalarNode) return ((int)value.Start.Index, (int)value.End.Index);
        var lineEnd = LineEnd(text, (int)key.Start.Index);
        var colon = text.IndexOf(':', (int)key.Start.Index, lineEnd - (int)key.Start.Index);
        if (colon < 0) throw new InvalidOperationException("该 DSH 字段暂不支持安全编辑");
        var start = colon + 1;
        while (start < lineEnd && text[start] == ' ') start++;
        var opening = value is YamlSequenceNode ? '[' : '{';
        var closing = value is YamlSequenceNode ? ']' : '}';
        if (start >= lineEnd || text[start] != opening)
            throw new InvalidOperationException("该 DSH 字段暂不支持安全编辑");
        var depth = 0;
        var quote = '\0';
        var escaped = false;
        for (var i = start; i < lineEnd; i++)
        {
            var c = text[i];
            if (quote != '\0')
            {
                if (escaped) escaped = false;
                else if (c == '\\' && quote == '"') escaped = true;
                else if (c == quote) quote = '\0';
            }
            else if (c is '"' or '\'') quote = c;
            else if (c == opening) depth++;
            else if (c == closing && --depth == 0) return (start, i + 1);
        }
        throw new InvalidOperationException("该 DSH 字段暂不支持安全编辑");
    }
    private static string Newline(string text) => text.Contains("\r\n") ? "\r\n" : "\n";
    private static int LineStart(string text, int index) => text.LastIndexOf('\n', Math.Max(0, index - 1)) + 1;
    private static int LineEnd(string text, int index)
    {
        var next = text.IndexOf('\n', Math.Min(index, text.Length));
        return next < 0 ? text.Length : next + 1;
    }
    private static int BlockEnd(string text, int startIndex, int indent)
    {
        for (var at = LineEnd(text, startIndex); at < text.Length;)
        {
            var next = LineEnd(text, at);
            var line = text[at..next].TrimEnd('\r', '\n');
            var spaces = line.Length - line.TrimStart(' ').Length;
            if (line.Length > spaces && spaces <= indent) return at;
            at = next;
        }
        return text.Length;
    }
    private static string PrefixAt(string text, int at, string nl) =>
        at > 0 && text[at - 1] != '\n' ? nl : "";
    private static string Apply(string text, IEnumerable<Edit> edits)
    {
        foreach (var edit in edits.OrderByDescending(e => e.Start))
            text = text[..edit.Start] + edit.Value + text[edit.End..];
        return text;
    }
}
