using AgentHub.Core.ProxyCore;
using AgentHub.Core.SessionCore.Providers;

namespace AgentHub.Core.SessionCore;

/// <summary>会话编排：只扫开着且能读的家；子会话并进父行；锁只挡批量删除。</summary>
public sealed class SessionService
{
    private readonly Dictionary<string, IConversationProvider> _providers;
    private readonly SessionIndex _index = new();
    private readonly SessionLockStore _locks = new();
    private readonly AgentHubConfig _config;
    private readonly SemaphoreSlim _rebuild = new(1, 1);
    private readonly Action<string>? _log;
    private bool _scannedOnce;

    /// <summary>各家会话在客户端里随时会被删，索引太久就出"标题在、正文空"的残影，过期必须重扫。</summary>
    private static readonly TimeSpan IndexTtl = TimeSpan.FromMinutes(5);

    public SessionService(TitleOverrideStore titles, AgentHubConfig config, Action<string>? log = null)
    {
        _config = config;
        _log = log;
        _providers = new Dictionary<string, IConversationProvider>(StringComparer.OrdinalIgnoreCase)
        {
            ["cursor"] = new CursorProvider(titles),
            ["codex"] = new CodexProvider(titles, log),
            ["dsh"] = new DshProvider(titles),
            ["workbuddy"] = new WorkBuddyProvider(titles, log),
            ["zcode"] = new ZcodeProvider(titles),
        };
        _index.LoadFromDisk();
    }

    public CursorProvider Cursor => (CursorProvider)_providers["cursor"];
    public int IndexedCount => _index.Count;
    public SessionLockStore Locks => _locks;

    public async Task<SessionPage> QueryPageAsync(
        string? agent, string? q, string range, int offset, int limit, string? project = null)
    {
        await EnsureIndexAsync();
        return _index.Query(agent, q, range, offset, limit, project, AllowedAgents(), _locks.IsLocked);
    }

    public async Task<IReadOnlyList<SessionProject>> ListProjectsAsync(string? agent)
    {
        await EnsureIndexAsync();
        if (string.IsNullOrEmpty(agent) || agent.Equals("all", StringComparison.OrdinalIgnoreCase))
            return [];
        return _index.ListProjects(agent);
    }

    public IReadOnlyList<(string Id, string Name)> Sources()
    {
        var ok = new HashSet<string>(_index.OkAgents, StringComparer.OrdinalIgnoreCase);
        if (ok.Count == 0)
        {
            foreach (var s in AllowedAgents()) ok.Add(s);
            if (Cursor.MissingReason is not null) ok.Remove("cursor");
        }
        var list = new List<(string Id, string Name)>();
        foreach (var id in _config.Dashboard.ResolvedAgentOrder())
        {
            if (!ok.Contains(id) || !_config.Dashboard.SessionReadable(id)) continue;
            if (id.Equals("cursor", StringComparison.OrdinalIgnoreCase) && Cursor.MissingReason is not null)
                continue;
            list.Add((id, DashboardSettings.AgentDisplayName(id)));
        }
        return list;
    }

    public async Task EnsureIndexAsync(bool force = false)
    {
        if (!force && IndexFresh()) return;
        await _rebuild.WaitAsync();
        try
        {
            if (!force && IndexFresh()) return;
            var (all, ok) = await ScanProvidersAsync();
            _index.Replace(all, ok);
            _scannedOnce = true;
        }
        finally { _rebuild.Release(); }
    }

    /// <summary>盘上缓存只够首屏秒开，不算新鲜：本进程扫过一次 + 未过期 + 名单没新增可读家才直接用。</summary>
    private bool IndexFresh() => _scannedOnce && !_index.OlderThan(IndexTtl) && !NeedsRescan();

    /// <summary>名单里新加了能读的家（如 ZCode），旧索引没有，进页补扫一次。</summary>
    private bool NeedsRescan()
    {
        var ok = new HashSet<string>(_index.OkAgents, StringComparer.OrdinalIgnoreCase);
        return AllowedAgents().Any(id => !ok.Contains(id));
    }

    private List<string> AllowedAgents()
    {
        var list = new List<string>();
        foreach (var id in _config.Dashboard.ResolvedAgentOrder())
        {
            if (!_config.Dashboard.SessionReadable(id)) continue;
            if (id.Equals("cursor", StringComparison.OrdinalIgnoreCase) && Cursor.MissingReason is not null)
                continue;
            list.Add(id);
        }
        return list;
    }

    private async Task<(List<ConversationSummary> Items, List<string> Ok)> ScanProvidersAsync()
    {
        var lists = new List<ConversationSummary>();
        var ok = new List<string>();
        foreach (var id in AllowedAgents())
        {
            if (!_providers.TryGetValue(id, out var p)) continue;
            try
            {
                lists.AddRange(await p.ListAsync());
                ok.Add(id);
            }
            catch (Exception ex)
            {
                _log?.Invoke($"[sessions] {p.AgentId} 扫描失败 {ex.GetType().Name}: {ex.Message}");
                ok.Add(id);
            }
        }
        return (lists, ok);
    }

    public async Task<ConversationDetail?> LoadAsync(string agent, string id)
    {
        if (!_providers.TryGetValue(agent, out var p))
            throw new ArgumentException($"未知 agent：{agent}");
        var detail = await p.LoadAsync(id);
        if (detail is null)
        {
            // 源头已无这条会话（多半在客户端里删过）：把缓存残影一并摘掉，列表不再标题空挂。
            _index.Remove(agent, id);
            return null;
        }

        var extras = new List<ConversationMessage>(detail.Messages);
        var kids = _index.ChildOf(agent, id);
        if (kids.Count > 0)
        {
            var subs = await Task.WhenAll(kids.Select(async child =>
            {
                try { return await p.LoadAsync(child.Id); }
                catch (Exception ex)
                {
                    _log?.Invoke($"[sessions] 拼子会话失败 {agent}:{child.Id} {ex.Message}");
                    return null;
                }
            }));
            foreach (var sub in subs)
            {
                if (sub?.Messages is { Count: > 0 } msgs)
                    extras.AddRange(msgs);
            }
        }
        extras = extras
            .OrderBy(m => m.TimestampUtc ?? DateTime.MaxValue)
            .ToList();
        if (extras.Count > 200)
            extras = extras.Skip(extras.Count - 200).ToList();

        var last = detail.Summary.LastActivityUtc;
        long size = detail.Summary.SizeBytes;
        long count = detail.Summary.MessageCount;
        foreach (var kid in kids)
        {
            count += kid.MessageCount;
            size += kid.SizeBytes;
            if (kid.LastActivityUtc > last) last = kid.LastActivityUtc;
        }

        return detail with
        {
            Summary = detail.Summary with
            {
                MessageCount = count,
                SizeBytes = size,
                LastActivityUtc = last,
            },
            Messages = extras,
        };
    }

    public string ExportMarkdown(ConversationDetail detail) => MarkdownExporter.Export(detail);

    public bool CanOpen(string agent) =>
        !agent.Equals("cursor", StringComparison.OrdinalIgnoreCase);

    public async Task RenameAsync(string agent, string id, string title)
    {
        if (!_providers.TryGetValue(agent, out var p))
            throw new ArgumentException($"未知 agent：{agent}");
        await p.RenameAsync(id, title);
        _index.UpdateTitle(agent, id, title);
    }

    public void SetLocked(string agent, string id, bool locked) => _locks.Set(agent, id, locked);

    /// <summary>批量删除。已锁的跳过（单条不跳过）。删父则子集一起删。</summary>
    public async Task<(IReadOnlyList<DeleteItemResult> Results, int Skipped)> DeleteAsync(
        IReadOnlyList<(string Agent, string Id)> items)
    {
        var single = items.Count == 1;
        var skipped = 0;
        var work = new List<(string Agent, string Id)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (agent, id) in items)
        {
            if (!single && _locks.IsLocked(agent, id))
            {
                skipped++;
                continue;
            }
            if (seen.Add($"{agent}:{id}")) work.Add((agent, id));
            foreach (var child in _index.ChildOf(agent, id))
            {
                if (seen.Add($"{agent}:{child.Id}")) work.Add((agent, child.Id));
            }
        }

        var results = new List<DeleteItemResult>();
        foreach (var group in work.GroupBy(x => x.Agent, StringComparer.OrdinalIgnoreCase))
        {
            if (!_providers.TryGetValue(group.Key, out var p))
            {
                results.AddRange(group.Select(x => new DeleteItemResult
                { AgentId = group.Key, Id = x.Id, Ok = false, Error = $"未知 agent：{group.Key}" }));
                continue;
            }
            try
            {
                results.AddRange(await p.DeleteAsync(group.Select(x => x.Id)));
            }
            catch (Exception ex)
            {
                results.AddRange(group.Select(x => new DeleteItemResult
                { AgentId = group.Key, Id = x.Id, Ok = false, Error = ex.Message }));
            }
        }
        foreach (var r in results)
            if (r.Ok) _index.Remove(r.AgentId, r.Id);
        return (results, skipped);
    }
}
