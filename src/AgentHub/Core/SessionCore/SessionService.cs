using System.Diagnostics;
using System.Net.Http;
using System.IO;
using AgentHub.Core.ProxyCore;
using AgentHub.Core.SessionCore.Providers;
using AgentHub.Core.TokenCore;

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
    private string? _cloudScanHint;
    private int _cloudEnriching;

    /// <summary>各家会话在客户端里随时会被删，索引太久就出"标题在、正文空"的残影，过期必须重扫。</summary>
    private static readonly TimeSpan IndexTtl = TimeSpan.FromMinutes(5);

    public SessionService(TitleOverrideStore titles, AgentHubConfig config, Action<string>? log = null)
    {
        _config = config;
        _log = log;
        _providers = new Dictionary<string, IConversationProvider>(StringComparer.OrdinalIgnoreCase)
        {
            ["cursor"] = new CursorProvider(titles),
            ["cursor-cloud"] = new CursorCloudProvider(titles, config),
            ["codex"] = new CodexProvider(titles, log),
            ["dsh"] = new DshProvider(titles),
            ["workbuddy"] = new WorkBuddyProvider(titles, log, config),
            ["zcode"] = new ZcodeProvider(titles),
            ["mimocode"] = new MimocodeProvider(titles),
            ["qoder-cn"] = new QoderCnProvider(titles),
        };
        _index.LoadFromDisk();
    }

    public CursorProvider Cursor => (CursorProvider)_providers["cursor"];
    public CursorCloudProvider CursorCloud => (CursorCloudProvider)_providers["cursor-cloud"];
    public int IndexedCount => _index.Count;
    /// <summary>云端扫描失败时的网络提示；成功或未启用云端时为 null。</summary>
    public string? CloudScanHint => _cloudScanHint;
    public SessionLockStore Locks => _locks;

    /// <summary>清各家残留。某一家还在跑就跳过那一家，不挡其余。</summary>
    public ResidueSweepResult SweepResidues(bool vacuum)
    {
        CursorVacuum? vac = null;
        return new(SweepZcodeResidue(), SweepWorkBuddyResidue(), SweepCursorResidue(vacuum, out vac), SweepCodexResidue(), vac);
    }

    private static ResidueSweepAgent SweepZcodeResidue()
    {
        if (ZcodeProvider.ZcodeRunning())
            return new(false, "running", 0, "还在运行，已跳过");
        try
        {
            return new(true, null, ZcodeProvider.SweepOrphanLeftovers(), null);
        }
        catch (Exception ex)
        {
            return new(false, "error", 0, ex.Message);
        }
    }

    private ResidueSweepAgent SweepWorkBuddyResidue()
    {
        if (WorkBuddyProvider.WorkBuddyRunning())
            return new(false, "running", 0, "还在运行，已跳过");
        try
        {
            var cloud = WorkBuddySidebar.SweepCloudDeleted(Dpapi.Unprotect(_config.Credentials.WorkBuddySession));
            return new(true, null, cloud.Ok, cloud.Warning);
        }
        catch (Exception ex)
        {
            return new(false, "error", 0, ex.Message);
        }
    }

    private static ResidueSweepAgent SweepCodexResidue()
    {
        if (CodexDesktopCleanup.CodexRunning())
            return new(false, "running", 0, "还在运行，已跳过");
        try
        {
            return new(true, null, CodexDesktopCleanup.SweepOrphanLeftovers(), null);
        }
        catch (Exception ex)
        {
            return new(false, "error", 0, ex.Message);
        }
    }

    private ResidueSweepAgent SweepCursorResidue(bool vacuum, out CursorVacuum? vac)
    {
        vac = null;
        if (Cursor.MissingReason is not null)
            return new(false, "unavailable", 0, Cursor.MissingReason);
        if (CursorProvider.CursorRunning())
            return new(false, "running", 0, "还在运行，已跳过");
        try
        {
            var shells = Cursor.CleanShells().Count(r => r.Ok);
            var orphans = Cursor.CleanOrphans();
            if (vacuum)
            {
                var v = Cursor.Vacuum();
                vac = new CursorVacuum(v.Ok, v.Error);
            }
            var n = shells + orphans.DeletedRows;
            var detail = orphans.Ok
                ? $"空壳 {shells} · 孤儿 {orphans.DeletedRows} 行"
                : orphans.Error ?? $"空壳 {shells} · 孤儿失败";
            return new(true, orphans.Ok ? null : "error", n, detail);
        }
        catch (Exception ex)
        {
            return new(false, "error", 0, ex.Message);
        }
    }

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
            // 云端并进「Cursor」筛选项，不单独占一个 chip
            if (id.Equals("cursor-cloud", StringComparison.OrdinalIgnoreCase))
                continue;
            if (id.Equals("cursor", StringComparison.OrdinalIgnoreCase))
            {
                var localOk = ok.Contains("cursor")
                    && Cursor.MissingReason is null
                    && _config.Dashboard.SessionReadable("cursor");
                var cloudOk = ok.Contains("cursor-cloud")
                    && CursorCloud.MissingReason is null
                    && _config.Dashboard.SessionReadable("cursor-cloud");
                if (!localOk && !cloudOk) continue;
                list.Add(("cursor", DashboardSettings.AgentDisplayName("cursor")));
                continue;
            }
            if (!ok.Contains(id) || !_config.Dashboard.SessionReadable(id)) continue;
            list.Add((id, DashboardSettings.AgentDisplayName(id)));
        }
        return list;
    }

    public async Task EnsureIndexAsync(bool force = false)
    {
        if (!force && IndexFresh()) return;
        var startCloud = false;
        await _rebuild.WaitAsync();
        try
        {
            if (!force && IndexFresh()) return;
            // 本地优先：先落本地索引，立即返回；云端短超时后台合并，不挡列表。
            var (all, ok) = await ScanLocalProvidersAsync();
            if (CloudEnabled())
            {
                // 保留上次云端缓存，避免离线时列表被掏空；标记 ok 以免 NeedsRescan 死循环。
                if (!ok.Contains("cursor-cloud", StringComparer.OrdinalIgnoreCase))
                    ok.Add("cursor-cloud");
                startCloud = true;
            }
            else
            {
                _cloudScanHint = null;
            }
            _index.Replace(all, ok);
            _scannedOnce = true;
        }
        finally { _rebuild.Release(); }

        if (startCloud)
            StartCloudEnrichInBackground();
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
            if (id.Equals("cursor-cloud", StringComparison.OrdinalIgnoreCase) && CursorCloud.MissingReason is not null)
                continue;
            list.Add(id);
        }
        return list;
    }

    private bool CloudEnabled() =>
        AllowedAgents().Any(id => id.Equals("cursor-cloud", StringComparison.OrdinalIgnoreCase));

    /// <summary>只扫本地家（不含 cursor-cloud），保证 api.cursor.com 不可达时列表仍秒开。</summary>
    private async Task<(List<ConversationSummary> Items, List<string> Ok)> ScanLocalProvidersAsync()
    {
        var lists = new List<ConversationSummary>();
        var ok = new List<string>();
        foreach (var id in AllowedAgents())
        {
            if (id.Equals("cursor-cloud", StringComparison.OrdinalIgnoreCase)) continue;
            if (!_providers.TryGetValue(id, out var p)) continue;
            try
            {
                lists.AddRange(await p.ListAsync());
                ok.Add(id);
            }
            catch (Exception ex)
            {
                // 扫描失败不进 ok：Sources() 不再把故障源画成「可读」
                _log?.Invoke($"[sessions] {p.AgentId} 扫描失败 {ex.GetType().Name}: {ex.Message}");
            }
        }
        return (lists, ok);
    }

    private void StartCloudEnrichInBackground()
    {
        if (Interlocked.CompareExchange(ref _cloudEnriching, 1, 0) != 0) return;
        _ = Task.Run(async () =>
        {
            try
            {
                await EnrichCloudAsync().ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Exchange(ref _cloudEnriching, 0);
            }
        });
    }

    private async Task EnrichCloudAsync()
    {
        if (!_providers.TryGetValue("cursor-cloud", out var p)) return;
        try
        {
            var items = await p.ListAsync().ConfigureAwait(false);
            _index.UpsertAgent("cursor-cloud", items, markOk: true);
            _cloudScanHint = null;
            _log?.Invoke($"[sessions] cursor-cloud 已合并 {items.Count} 条");
        }
        catch (Exception ex)
        {
            // 查不到就不显示云端：清空索引里的 cursor-cloud，不留缓存
            _index.UpsertAgent("cursor-cloud", System.Array.Empty<ConversationSummary>(), markOk: true);
            _cloudScanHint = FriendlyCloudHint(ex);
            _log?.Invoke($"[sessions] cursor-cloud 扫描失败 {ex.GetType().Name}: {ex.Message}（已清空云端）");
        }
    }

    private static string FriendlyCloudHint(Exception ex)
    {
        var msg = ex.Message ?? "";
        if (msg.Contains("api.cursor.com", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("超时", StringComparison.Ordinal)
            || msg.Contains("网络", StringComparison.Ordinal)
            || ex is HttpRequestException or TaskCanceledException or OperationCanceledException
            || ex.InnerException is HttpRequestException or TaskCanceledException)
        {
            return "Cursor 云端暂不可用（无法连接 api.cursor.com）。本地会话已正常显示，可稍后重试刷新。";
        }
        return "Cursor 云端同步失败：" + (string.IsNullOrWhiteSpace(msg) ? ex.GetType().Name : msg);
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
        !agent.Equals("cursor", StringComparison.OrdinalIgnoreCase)
        && !agent.Equals("cursor-cloud", StringComparison.OrdinalIgnoreCase);

    /// <summary>打开会话里出现过的项目目录。不接受索引外的任意路径。</summary>
    public async Task OpenProjectAsync(string path)
    {
        await EnsureIndexAsync();
        string full;
        try { full = Path.GetFullPath(path.Trim()); }
        catch (Exception) { throw new ArgumentException("路径无效"); }
        if (!Directory.Exists(full))
            throw new DirectoryNotFoundException("项目目录不存在");
        var want = SessionIndex.NormalizeProjectPath(full);
        if (want.Length == 0)
            throw new ArgumentException("路径无效");
        var known = _index.ListProjects(null).Any(p =>
            p.Path.Length > 0
            && string.Equals(SessionIndex.NormalizeProjectPath(p.Path), want, StringComparison.OrdinalIgnoreCase));
        if (!known)
            throw new UnauthorizedAccessException("路径不是已知会话项目");
        Process.Start(new ProcessStartInfo(full) { UseShellExecute = true });
    }

    public async Task RenameAsync(string agent, string id, string title)
    {
        if (!_providers.TryGetValue(agent, out var p))
            throw new ArgumentException($"未知 agent：{agent}");
        await p.RenameAsync(id, title);
        _index.UpdateTitle(agent, id, title);
    }

    public void SetLocked(string agent, string id, bool locked) => _locks.Set(agent, id, locked);

    /// <summary>批量删除。已锁的列表行跳过（单条不跳过）。
    /// 子会话在列表里并进父行，不能单独加锁；删父则挂在下面的子会话一起删。</summary>
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
        {
            var gone = r.Ok
                || (r.Error?.Contains("404", StringComparison.Ordinal) == true)
                || (r.Error?.Contains("Not Found", StringComparison.OrdinalIgnoreCase) == true);
            if (gone) _index.Remove(r.AgentId, r.Id);
        }
        return (results, skipped);
    }
}
