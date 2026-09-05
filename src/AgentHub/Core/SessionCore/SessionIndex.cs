using System.IO;
using System.Text.Json;
using AgentHub.Core.ProxyCore;

namespace AgentHub.Core.SessionCore;

/// <summary>会话摘要索引：扫四源一次后落盘，列表/筛选走缓存；手动刷新才重扫；删除按 id 改缓存。</summary>
internal sealed class SessionIndex
{
    private readonly object _gate = new();
    private List<ConversationSummary> _items = [];
    private List<string> _okAgents = [];
    private DateTimeOffset? _builtAt;

    private static string PathFile => System.IO.Path.Combine(AgentHubConfig.Dir, "session-index.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public bool Ready
    {
        get { lock (_gate) return _builtAt is not null; }
    }

    /// <summary>索引是否比 ttl 旧。盘上缓存载入的 BuiltAt 可能是几天前，天然触发重扫。</summary>
    public bool OlderThan(TimeSpan ttl)
    {
        lock (_gate) return _builtAt is not { } at || DateTimeOffset.UtcNow - at > ttl;
    }

    public int Count
    {
        get { lock (_gate) return _items.Count; }
    }

    public void LoadFromDisk()
    {
        try
        {
            if (!File.Exists(PathFile)) return;
            var dto = JsonSerializer.Deserialize<IndexFile>(File.ReadAllText(PathFile), JsonOpts);
            if (dto?.Items is null) return;
            lock (_gate)
            {
                _items = dto.Items;
                _okAgents = dto.OkAgents ?? [];
                _builtAt = dto.BuiltAt;
            }
        }
        catch (Exception) { }
    }

    public IReadOnlyList<string> OkAgents
    {
        get { lock (_gate) return _okAgents.ToList(); }
    }

    public void Replace(IReadOnlyList<ConversationSummary> items, IReadOnlyList<string>? okAgents = null)
    {
        lock (_gate)
        {
            _items = items.ToList();
            if (okAgents is not null) _okAgents = okAgents.ToList();
            _builtAt = DateTimeOffset.UtcNow;
        }
        Save();
    }

    public IReadOnlyList<ConversationSummary> ChildOf(string agent, string id)
    {
        lock (_gate)
        {
            return _items.Where(s =>
                s.AgentId.Equals(agent, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrEmpty(s.ParentId)
                && s.ParentId.Equals(id, StringComparison.OrdinalIgnoreCase)).ToList();
        }
    }

    public void Remove(string agent, string id)
    {
        lock (_gate)
        {
            _items.RemoveAll(s =>
                s.AgentId.Equals(agent, StringComparison.OrdinalIgnoreCase)
                && s.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        }
        Save();
    }

    public void UpdateTitle(string agent, string id, string title)
    {
        lock (_gate)
        {
            for (var i = 0; i < _items.Count; i++)
            {
                var s = _items[i];
                if (!s.AgentId.Equals(agent, StringComparison.OrdinalIgnoreCase)
                    || !s.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
                    continue;
                _items[i] = s with { Title = title, TitleSource = "override" };
                break;
            }
        }
        Save();
    }

    public SessionPage Query(
        string? agent, string? q, string range, int offset, int limit,
        string? project, IReadOnlyCollection<string> allowedAgents,
        Func<string, string, bool>? isLocked = null)
    {
        List<ConversationSummary> snapshot;
        DateTimeOffset? at;
        lock (_gate)
        {
            snapshot = _items.ToList();
            at = _builtAt;
        }

        var allow = new HashSet<string>(allowedAgents, StringComparer.OrdinalIgnoreCase);
        var merged = MergeSubs(snapshot.Where(s => allow.Contains(s.AgentId)).ToList());
        IEnumerable<ConversationSummary> seq = merged;

        if (!string.IsNullOrEmpty(agent) && !agent.Equals("all", StringComparison.OrdinalIgnoreCase))
            seq = seq.Where(s => s.AgentId.Equals(agent, StringComparison.OrdinalIgnoreCase));

        var single = !string.IsNullOrEmpty(agent) && !agent.Equals("all", StringComparison.OrdinalIgnoreCase);
        if (single && project is not null)
        {
            var want = NormalizeProjectPath(project);
            seq = seq.Where(s => string.Equals(NormalizeProjectPath(s.Project), want, ProjectComparison));
        }
        if (!string.IsNullOrEmpty(q))
        {
            seq = seq.Where(s =>
                s.Title.Contains(q, StringComparison.OrdinalIgnoreCase)
                || (s.Project?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)
                || s.Id.Contains(q, StringComparison.OrdinalIgnoreCase));
        }

        var weekStart = WeekStartLocal();
        range = range is "before" or "all" ? range : "week";
        if (range == "week")
            seq = seq.Where(s => s.LastActivityUtc.ToLocalTime() >= weekStart);
        else if (range == "before")
            seq = seq.Where(s => s.LastActivityUtc.ToLocalTime() < weekStart);

        var list = seq.OrderByDescending(s => s.LastActivityUtc).ToList();
        offset = Math.Max(0, offset);
        limit = Math.Clamp(limit <= 0 ? 40 : limit, 1, 200);
        var lockedCount = isLocked is null ? 0 : list.Count(s => isLocked(s.AgentId, s.Id));
        return new SessionPage
        {
            Items = list.Skip(offset).Take(limit).ToList(),
            Total = list.Count,
            Offset = offset,
            Limit = limit,
            IndexedCount = snapshot.Count,
            IndexedAt = at,
            LockedCount = lockedCount,
        };
    }

    public static DateTime WeekStartLocal()
    {
        var today = DateTime.Today;
        return today.AddDays(-((int)today.DayOfWeek + 6) % 7);
    }

    internal static List<ConversationSummary> MergeSubs(IReadOnlyList<ConversationSummary> raw)
    {
        var byKey = new Dictionary<string, ConversationSummary>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in raw) byKey[$"{s.AgentId}:{s.Id}"] = s;

        var attached = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var kids = new Dictionary<string, List<ConversationSummary>>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in raw)
        {
            if (string.IsNullOrEmpty(s.ParentId) && !s.IsSubagent) continue;
            if (string.IsNullOrEmpty(s.ParentId)) continue;
            var pk = $"{s.AgentId}:{s.ParentId}";
            if (!byKey.TryGetValue(pk, out var parent) || parent.IsSubagent) continue;
            if (!kids.TryGetValue(pk, out var list)) kids[pk] = list = [];
            list.Add(s);
            attached.Add($"{s.AgentId}:{s.Id}");
        }

        var display = new List<ConversationSummary>();
        foreach (var s in raw)
        {
            var k = $"{s.AgentId}:{s.Id}";
            if (attached.Contains(k)) continue;
            var orphan = (s.IsSubagent || !string.IsNullOrEmpty(s.ParentId)) && !attached.Contains(k);
            long msg = s.MessageCount, size = s.SizeBytes;
            var last = s.LastActivityUtc;
            if (kids.TryGetValue(k, out var children))
            {
                foreach (var kid in children)
                {
                    msg += kid.MessageCount;
                    size += kid.SizeBytes;
                    if (kid.LastActivityUtc > last) last = kid.LastActivityUtc;
                }
            }
            display.Add(s with
            {
                MessageCount = msg,
                SizeBytes = size,
                LastActivityUtc = last,
                OrphanSub = orphan,
            });
        }
        return display;
    }

    public IReadOnlyList<SessionProject> ListProjects(string? agent)
    {
        List<ConversationSummary> snapshot;
        lock (_gate) snapshot = _items.ToList();

        IEnumerable<ConversationSummary> seq = MergeSubs(snapshot);
        if (!string.IsNullOrEmpty(agent))
            seq = seq.Where(s => s.AgentId.Equals(agent, StringComparison.OrdinalIgnoreCase));

        var cmp = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        return seq
            .GroupBy(s => NormalizeProjectPath(s.Project), cmp)
            .Select(g => new SessionProject(g.Key, ProjectLabel(g.Key), g.Count()))
            .OrderBy(p => p.Path.Length == 0 ? 1 : 0)
            .ThenBy(p => p.Label, cmp)
            .ToList();
    }

    internal static string NormalizeProjectPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        var s = path.Trim().Replace('/', '\\');
        while (s.Length > 0 && s.EndsWith('\\'))
        {
            if (s.Length == 3 && char.IsLetter(s[0]) && s[1] == ':') break;
            s = s[..^1];
        }
        return s;
    }

    private static string ProjectLabel(string path)
    {
        if (path.Length == 0) return "未知";
        var name = Path.GetFileName(path.TrimEnd('\\', '/'));
        return string.IsNullOrEmpty(name) ? path : name;
    }

    private static StringComparison ProjectComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private void Save()
    {
        IndexFile dto;
        lock (_gate)
        {
            dto = new IndexFile { BuiltAt = _builtAt ?? DateTimeOffset.UtcNow, Items = _items, OkAgents = _okAgents };
        }
        try
        {
            Directory.CreateDirectory(AgentHubConfig.Dir);
            var tmp = PathFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(dto, JsonOpts));
            File.Copy(tmp, PathFile, overwrite: true);
            File.Delete(tmp);
        }
        catch (Exception) { }
    }

    private sealed class IndexFile
    {
        public DateTimeOffset BuiltAt { get; set; }
        public List<ConversationSummary> Items { get; set; } = [];
        public List<string> OkAgents { get; set; } = [];
    }
}
