using System.IO;
using AgentHub.Core.McpCore.Adapters;
using AgentHub.Core.Platform;

namespace AgentHub.Core.McpCore;

/// <summary>母本 + 各 Agent 适配器：列表合并、导入、同步、单机启停。</summary>
public sealed partial class McpSyncService
{
    private readonly McpMotherStore _mother;
    private readonly IReadOnlyList<IMcpAdapter> _adapters;
    private readonly Action<string>? _log;
    private readonly object _saveGate = new();

    public McpSyncService(McpMotherStore? mother = null, IEnumerable<IMcpAdapter>? adapters = null, Action<string>? log = null)
    {
        _mother = mother ?? new McpMotherStore();
        _adapters = (adapters ?? DefaultAdapters()).ToList();
        _log = log;
    }

    public McpMotherStore Mother => _mother;
    public IReadOnlyList<IMcpAdapter> Adapters => _adapters;

    public static IEnumerable<IMcpAdapter> DefaultAdapters() =>
    [
        new CursorMcpAdapter(),
        new CodexMcpAdapter(),
        new TraeMcpAdapter(),
        new WorkBuddyMcpAdapter(),
        new ZcodeMcpAdapter(),
        new MimocodeMcpAdapter(),
        new DshMcpAdapter(),
    ];

    /// <summary>编辑统一配置：标准优先；尚未纳管时读取已有端作为起点。</summary>
    public McpServerSpec? GetRaw(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        name = name.Trim();
        var doc = _mother.Load();
        if (CodexMcpAdapter.SystemNames.Contains(name) || McpMotherStore.IsExcluded(doc, name))
            throw new InvalidOperationException("系统项不可编辑");
        var standard = _mother.Get(name);
        if (standard is not null) return standard;
        McpServerSpec? first = null;
        foreach (var adapter in _adapters.OrderBy(a => a.AgentId == "cursor" ? 0 : 1))
        {
            if (!adapter.Detected) continue;
            var hit = adapter.ListStrict().FirstOrDefault(s => s.Id.Equals(name, StringComparison.OrdinalIgnoreCase));
            first ??= hit?.Clone();
        }
        return first;
    }

    /// <summary>统一保存：先写标准，再同步所有已有端及显式新增端；启停状态归各端。</summary>
    public McpSyncResult UpsertToAgents(McpServerSpec spec, IEnumerable<string>? targets)
    {
        lock (_saveGate) return SaveUnified(spec, targets);
    }

    private McpSyncResult SaveUnified(McpServerSpec spec, IEnumerable<string>? targets)
    {
        var result = new McpSyncResult { Ok = false };
        if (string.IsNullOrWhiteSpace(spec.Id))
        {
            result.Error = "需要 name/id";
            return result;
        }
        var name = spec.Id.Trim();
        var targetSet = targets?.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase) ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (targetSet.Any(t => !_adapters.Any(a => a.AgentId.Equals(t, StringComparison.OrdinalIgnoreCase))))
        {
            result.Error = "包含未知 Agent";
            return result;
        }
        if (CodexMcpAdapter.SystemNames.Contains(name))
        {
            result.Error = "系统项不可写入";
            return result;
        }

        var existing = new Dictionary<string, McpServerSpec>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var doc = _mother.Load();
            if (McpMotherStore.IsExcluded(doc, name))
            {
                result.Error = "系统项已排除，不可写入";
                return result;
            }
            foreach (var adapter in _adapters)
            {
                if (!adapter.Detected)
                {
                    if (targetSet.Contains(adapter.AgentId))
                    {
                        result.Error = "所选 Agent 未检测到配置路径";
                        return result;
                    }
                    continue;
                }
                var current = adapter.ListStrict().FirstOrDefault(s => s.Id.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (current is null) continue;
                existing[adapter.AgentId] = current;
                targetSet.Add(adapter.AgentId);
            }
            var standard = spec.Clone();
            standard.Id = name;
            var previous = _mother.Get(name);
            standard.Alias ??= previous?.Alias;
            standard.Note ??= previous?.Note;
            _mother.Upsert(standard);
            Log($"upsert standard {name}");
        }
        catch (Exception ex)
        {
            result.Error = "读取或保存标准配置失败，未同步到各端；请检查配置";
            Log($"upsert standard failed: {ex.GetType().Name}");
            return result;
        }

        result.Ok = true;
        foreach (var adapter in _adapters.Where(a => targetSet.Contains(a.AgentId)))
        {
            try
            {
                var copy = spec.Clone();
                copy.Id = existing.TryGetValue(adapter.AgentId, out var current) ? current.Id : name;
                copy.Enabled = current?.Enabled ?? spec.Enabled;
                PrepareForAdapter(copy, adapter);
                adapter.Upsert(copy);
                result.Items.Add(new McpSyncItemResult { Name = name, Agent = adapter.AgentId, Ok = true });
                Log($"upsert standard {name} -> {adapter.AgentId}");
            }
            catch (Exception ex)
            {
                result.Ok = false;
                result.Items.Add(new McpSyncItemResult
                {
                    Name = name, Agent = adapter.AgentId, Ok = false, Error = "同步到该端失败，请检查配置并重试",
                });
                Log($"upsert failed {adapter.AgentId}: {ex.GetType().Name}");
            }
        }
        if (!result.Ok) result.Error = "标准已保存，部分 Agent 同步失败";
        return result;
    }

    private static void PrepareForAdapter(McpServerSpec spec, IMcpAdapter adapter)
    {
        if (adapter is CodexMcpAdapter && spec.Transport == McpTransport.Stdio && spec.StartupTimeoutSec is null)
            spec.StartupTimeoutSec = 30;
        if (adapter is WorkBuddyMcpAdapter && spec.Transport == McpTransport.Http)
            spec.ExplicitType ??= "streamableHttp";
    }

    public void ImportFrom(string sourceAgentId)
    {
        lock (_saveGate)
        {
            var adapter = _adapters.FirstOrDefault(a =>
                string.Equals(a.AgentId, sourceAgentId, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException($"未知 Agent：{sourceAgentId}");
            if (!adapter.Detected)
                throw new InvalidOperationException($"{adapter.DisplayName} 未检测到配置");
            var doc = _mother.Load();
            var list = adapter.ListStrict()
                .Where(s => !CodexMcpAdapter.SystemNames.Contains(s.Id))
                .ToList();
            foreach (var s in list)
            {
                if (McpMotherStore.IsExcluded(doc, s.Id) || _mother.Get(s.Id) is not null) continue;
                var result = UpsertToAgents(s, []);
                if (!result.Ok) throw new InvalidOperationException(result.Error ?? "纳管配置失败");
            }
            Log($"import from {sourceAgentId}: {list.Count} servers");
        }
    }

    public void UpsertMother(McpServerSpec spec)
    {
        var result = UpsertToAgents(spec, []);
        if (!result.Ok) throw new InvalidOperationException(result.Error ?? "统一配置保存失败");
    }

    public void EnableMother(string name, bool enabled)
    {
        _mother.SetEnabled(name, enabled);
        Log($"mother enable {name}={enabled}");
    }

    public void SetMeta(string name, string? alias, string? note)
    {
        _mother.SetMeta(name, alias, note);
    }

    public McpSyncResult Sync(IEnumerable<string>? names, IEnumerable<string>? targets)
    {
        lock (_saveGate) return SyncCore(names, targets);
    }

    private McpSyncResult SyncCore(IEnumerable<string>? names, IEnumerable<string>? targets)
    {
        var doc = _mother.Load();
        var mother = _mother.ListSpecs();
        var nameList = names?.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()).ToList();
        var targetList = targets?.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim()).ToList();
        targetList ??= _adapters.Where(a => a.Detected && (doc.Targets.TryGetValue(a.AgentId, out var on) ? on : true))
            .Select(a => a.AgentId).ToList();
        var result = new McpSyncResult { Ok = true };
        foreach (var name in nameList is { Count: > 0 } ? nameList : mother.Keys.ToList())
        {
            if (!mother.TryGetValue(name, out var spec))
            {
                result.Ok = false;
                result.Items.Add(new McpSyncItemResult { Name = name, Agent = "*", Ok = false, Error = "不在标准配置中" });
                continue;
            }
            var one = UpsertToAgents(spec, targetList);
            result.Items.AddRange(one.Items);
            if (!one.Ok)
            {
                result.Ok = false;
                result.Error = one.Error;
            }
        }
        return result;
    }


    /// <summary>
    /// 把 MCP 配置推到指定/缺失端，不要求先导入母本。
    /// 源解析优先级（漂移时同序）：母本 &gt; preferredSource &gt; Cursor &gt; 其它已检测到的适配器（跳过系统项）。
    /// </summary>
    public McpSyncResult Push(string name, IEnumerable<string>? targets, string? preferredSource)
        => PushNames([name], targets, preferredSource);

    /// <summary>批量 Push；names 与单名 Push 语义相同。</summary>
    public McpSyncResult PushNames(IEnumerable<string> names, IEnumerable<string>? targets, string? preferredSource)
    {
        var nameList = names
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (nameList.Count == 0)
            throw new ArgumentException("需要 name 或 names");

        var result = new McpSyncResult { Ok = true };
        foreach (var name in nameList)
        {
            try
            {
                var one = PushOne(name, targets, preferredSource);
                result.Items.AddRange(one.Items);
                if (!one.Ok)
                {
                    result.Ok = false;
                    result.Error = one.Error;
                }
            }
            catch (Exception)
            {
                result.Ok = false;
                result.Items.Add(new McpSyncItemResult
                {
                    Name = name, Agent = "*", Ok = false, Error = "读取或同步配置失败，请检查配置",
                });
            }
        }
        return result;
    }

    private McpSyncResult PushOne(string name, IEnumerable<string>? targets, string? preferredSource)
    {
        lock (_saveGate)
        {
            if (CodexMcpAdapter.SystemNames.Contains(name))
                throw new InvalidOperationException($"系统项 {name} 不可推送");

            var doc = _mother.Load();
            if (McpMotherStore.IsExcluded(doc, name))
                throw new InvalidOperationException($"系统项 {name} 已排除，不可推送");

            var spec = ResolvePushSource(name, preferredSource);
            spec.Enabled = true;
            var adapters = ResolvePushTargets(name, targets);
            return UpsertToAgents(spec, adapters.Select(a => a.AgentId));
        }
    }

    /// <summary>
    /// 源解析：母本 &gt; preferredSource &gt; Cursor &gt; 其它（跳过系统项）。
    /// </summary>
    private McpServerSpec ResolvePushSource(string name, string? preferredSource)
    {
        var mother = _mother.Get(name);
        if (mother is not null)
            return mother.Clone();

        var found = new List<(IMcpAdapter Adapter, McpServerSpec Spec)>();
        foreach (var adapter in _adapters)
        {
            if (!adapter.Detected) continue;
            try
            {
                var hit = adapter.List()
                    .FirstOrDefault(s => string.Equals(s.Id, name, StringComparison.OrdinalIgnoreCase));
                if (hit is null) continue;
                if (adapter is CodexMcpAdapter && CodexMcpAdapter.SystemNames.Contains(name))
                    continue;
                found.Add((adapter, hit));
            }
            catch
            {
                // 单个适配器读失败不影响其它源
            }
        }

        if (!string.IsNullOrWhiteSpace(preferredSource))
        {
            var pref = found.FirstOrDefault(x =>
                string.Equals(x.Adapter.AgentId, preferredSource.Trim(), StringComparison.OrdinalIgnoreCase));
            if (pref.Spec is not null)
                return pref.Spec.Clone();
        }

        var cursor = found.FirstOrDefault(x =>
            string.Equals(x.Adapter.AgentId, "cursor", StringComparison.OrdinalIgnoreCase));
        if (cursor.Spec is not null)
            return cursor.Spec.Clone();

        if (found.Count > 0)
            return found[0].Spec.Clone();

        throw new InvalidOperationException("找不到可复制的源配置");
    }

    private List<IMcpAdapter> ResolvePushTargets(string name, IEnumerable<string>? targets)
    {
        var targetSet = targets?
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t.Trim().ToLowerInvariant())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (targetSet is { Count: > 0 })
        {
            if (targetSet.Any(t => !_adapters.Any(a => a.AgentId.Equals(t, StringComparison.OrdinalIgnoreCase))))
                throw new ArgumentException("包含未知 Agent");
            return _adapters.Where(a => targetSet.Contains(a.AgentId)).ToList();
        }

        // 默认：默认五端中 presence=missing 的已检测适配器
        var missing = new List<IMcpAdapter>();
        foreach (var adapter in _adapters)
        {
            if (!adapter.Detected) continue;
            try
            {
                var on = adapter.List()
                    .Any(s => string.Equals(s.Id, name, StringComparison.OrdinalIgnoreCase));
                if (!on) missing.Add(adapter);
            }
            catch
            {
                // 读失败则跳过该端
            }
        }
        return missing;
    }

    public void AgentEnable(string agentId, string name, bool enabled)
    {
        lock (_saveGate)
        {
            if (CodexMcpAdapter.SystemNames.Contains(name))
                throw new InvalidOperationException($"系统项 {name} 不可启停");
            var adapter = _adapters.FirstOrDefault(a =>
                string.Equals(a.AgentId, agentId, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException($"未知 Agent：{agentId}");
            if (_mother.IsExcluded(name)) throw new InvalidOperationException("系统项已排除，不可启停");
            if (!adapter.Detected) throw new InvalidOperationException("未检测到该端配置路径");
            if (enabled)
            {
                var spec = _mother.Get(name);
                if (spec is null)
                {
                    spec = GetRaw(name) ?? throw new InvalidOperationException("找不到可启用的配置");
                    var saved = UpsertToAgents(spec, [agentId]);
                    if (!saved.Ok) throw new InvalidOperationException("统一配置保存失败，请检查同步结果");
                }
                var current = adapter.ListStrict().FirstOrDefault(s => s.Id.Equals(name, StringComparison.OrdinalIgnoreCase));
                spec.Id = current?.Id ?? spec.Id;
                spec.Enabled = true;
                PrepareForAdapter(spec, adapter);
                adapter.Upsert(spec);
            }
            else adapter.SetEnabled(name, false);
            Log($"agent-enable {agentId}/{name}={enabled}");
        }
    }

    public string OpenConfig(string agentId)
    {
        var adapter = _adapters.FirstOrDefault(a =>
            string.Equals(a.AgentId, agentId, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"unknown agent: {agentId}");
        return RevealInExplorer(adapter.ConfigPath, createEmptyShell: true);
    }

    public string OpenMotherFile() => RevealInExplorer(_mother.MotherPath, createEmptyShell: true);

    public string RevealInExplorer(string path, bool createEmptyShell = false)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("empty path");
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);
        if (!File.Exists(path))
        {
            if (createEmptyShell)
            {
                var ext = Path.GetExtension(path).ToLowerInvariant();
                var empty = ext == ".json" ? "{}\n" : "";
                File.WriteAllText(path, empty);
            }
            else if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
            {
                ShellLauncher.OpenDirectory(dir);
                return dir;
            }
            else
                throw new FileNotFoundException("config file missing", path);
        }
        ShellLauncher.Reveal(path);
        return path;
    }

    public object GetMother(bool reveal)
    {
        var doc = _mother.Load();
        var servers = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, s) in doc.Servers)
        {
            var spec = McpMotherStore.FromMother(id, s);
            var view = reveal ? spec : McpSecrets.Mask(spec);
            servers[id] = new
            {
                transport = view.Transport.ToString().ToLowerInvariant(),
                command = view.Command,
                args = view.Args,
                env = view.Env,
                url = view.Url,
                headers = view.Headers,
                enabled = view.Enabled,
                startupTimeoutSec = view.StartupTimeoutSec,
                timeoutMs = view.TimeoutMs,
                alias = view.Alias,
                note = view.Note,
                explicitType = view.ExplicitType,
                hasSecretRisk = McpSecrets.HasSecretRisk(spec),
            };
        }
        return new
        {
            path = _mother.MotherPath,
            reveal,
            version = doc.Version,
            servers,
            targets = doc.Targets,
            excludeNames = doc.ExcludeNames,
        };
    }

    public McpSyncResult Remove(string name, bool fromMother = true, IEnumerable<string>? targets = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("name required");
        name = name.Trim();
        if (CodexMcpAdapter.SystemNames.Contains(name))
            throw new InvalidOperationException($"system item {name} cannot be removed");

        var result = new McpSyncResult { Ok = true };
        if (fromMother)
        {
            try
            {
                _mother.Remove(name);
                result.Items.Add(new McpSyncItemResult { Name = name, Agent = "mother", Ok = true });
                Log("remove mother " + name);
            }
            catch (Exception ex)
            {
                result.Ok = false;
                result.Items.Add(new McpSyncItemResult { Name = name, Agent = "mother", Ok = false, Error = ex.Message });
            }
        }

        var targetList = targets?
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t.Trim().ToLowerInvariant())
            .Distinct()
            .ToList();
        if (targetList is not { Count: > 0 })
            return result;

        foreach (var agentId in targetList)
        {
            var adapter = _adapters.FirstOrDefault(a =>
                string.Equals(a.AgentId, agentId, StringComparison.OrdinalIgnoreCase));
            if (adapter is null)
            {
                result.Ok = false;
                result.Items.Add(new McpSyncItemResult { Name = name, Agent = agentId, Ok = false, Error = "unknown agent" });
                continue;
            }
            if (!adapter.Detected)
            {
                result.Ok = false;
                result.Items.Add(new McpSyncItemResult { Name = name, Agent = agentId, Ok = false, Error = "not detected" });
                continue;
            }
            try
            {
                adapter.Remove(name);
                result.Items.Add(new McpSyncItemResult { Name = name, Agent = agentId, Ok = true });
                Log("remove " + name + " from " + agentId);
            }
            catch (Exception ex)
            {
                result.Ok = false;
                result.Items.Add(new McpSyncItemResult { Name = name, Agent = agentId, Ok = false, Error = ex.Message });
                Log("remove fail " + name + " from " + agentId + ": " + ex.GetType().Name);
            }
        }
        return result;
    }

    private static bool SpecsEqual(McpServerSpec a, McpServerSpec b)
    {
        if (a.Transport != b.Transport) return false;
        if (!NormPath(a.Command).Equals(NormPath(b.Command), StringComparison.OrdinalIgnoreCase)) return false;
        if (!NormPath(a.Url).Equals(NormPath(b.Url), StringComparison.OrdinalIgnoreCase)) return false;
        if (!ArgsEqual(a.Args, b.Args)) return false;
        if (!DictEqual(a.Env, b.Env)) return false;
        if (!DictEqual(a.Headers, b.Headers)) return false;
        return true;
    }

    private static string NormPath(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        s = s.Trim();
        return OperatingSystem.IsWindows() ? s.Replace('/', '\\') : s;
    }

    private static bool ArgsEqual(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
            if (!NormPath(a[i]).Equals(NormPath(b[i]), StringComparison.OrdinalIgnoreCase))
                return false;
        return true;
    }

    private static bool DictEqual(IReadOnlyDictionary<string, string> a, IReadOnlyDictionary<string, string> b)
    {
        if (a.Count != b.Count) return false;
        foreach (var (k, v) in a)
        {
            if (!b.TryGetValue(k, out var ov)) return false;
            if (!string.Equals(v, ov, StringComparison.Ordinal)) return false;
        }
        return true;
    }

    private void Log(string msg) => _log?.Invoke("[mcp] " + msg);
}
