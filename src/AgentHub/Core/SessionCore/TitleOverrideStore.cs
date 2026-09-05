using System.IO;
using System.Text.Json;

namespace AgentHub.Core.SessionCore;

/// <summary>Codex / DSH 无稳定标题字段（方案 §0「标题写回」）→ 本应用覆盖表。
/// JSON 落 %APPDATA%\AgentHub\session-titles.json，键 "<agent>:<id>"。</summary>
public sealed class TitleOverrideStore
{
    private readonly string _path;
    private readonly object _gate = new();
    private Dictionary<string, string> _map = new();

    private static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public TitleOverrideStore()
    {
        _path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "AgentHub", "session-titles.json");
        Load();
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_path))
                _map = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_path)) ?? new();
        }
        catch (Exception)
        {
            _map = new();   // 损坏则丢弃（只是显示标题，不值得 fail-fast）
        }
    }

    public string? Get(string agent, string id)
    {
        lock (_gate) return _map.TryGetValue($"{agent}:{id}", out var t) ? t : null;
    }

    public void Set(string agent, string id, string title)
    {
        lock (_gate)
        {
            _map[$"{agent}:{id}"] = title;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                File.WriteAllText(_path, JsonSerializer.Serialize(_map, Opts));
            }
            catch (IOException) { /* 标题写失败不致命，下次写入再试 */ }
        }
    }

    public bool Remove(string agent, string id)
    {
        lock (_gate)
        {
            if (!_map.Remove($"{agent}:{id}")) return false;
            try { File.WriteAllText(_path, JsonSerializer.Serialize(_map, Opts)); }
            catch (IOException) { }
            return true;
        }
    }
}

/// <summary>统一导出 Markdown（方案 §4.1：四源同构输出）。</summary>
public static class MarkdownExporter
{
    public static string Export(ConversationDetail detail)
    {
        var s = detail.Summary;
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"# {s.Title}");
        sb.AppendLine();
        sb.AppendLine($"- 来源：{s.AgentId}");
        sb.AppendLine($"- 项目：{s.Project ?? "（未知）"}");
        sb.AppendLine($"- 最后活动：{s.LastActivityUtc:yyyy-MM-dd HH:mm:ss} UTC");
        sb.AppendLine($"- 消息数：{s.MessageCount}");
        if (detail.Note is { } note)
        {
            sb.AppendLine($"- 说明：{note}");
        }
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();
        foreach (var m in detail.Messages)
        {
            var ts = m.TimestampUtc is { } t ? t.ToString("yyyy-MM-dd HH:mm:ss") + " UTC" : "";
            sb.AppendLine($"## {m.Role}　{ts}");
            sb.AppendLine();
            sb.AppendLine(m.Text);
            sb.AppendLine();
        }
        return sb.ToString();
    }
}
