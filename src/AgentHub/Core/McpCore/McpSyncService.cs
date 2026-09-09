using System.IO;
using AgentHub.Core.McpCore.Adapters;

namespace AgentHub.Core.McpCore;

/// <summary>母本 + 各 Agent 适配器：列表合并、导入、同步、单机启停。</summary>
public sealed class McpSyncService
{
    private readonly McpMotherStore _mother;
    private readonly IReadOnlyList<IMcpAdapter> _adapters;
    private readonly Action<string>? _log;

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
    ];

    public object ListMasked()
    {
        var doc = _mother.Load();
        var motherSpecs = _mother.ListSpecs();
        var agentLists = _adapters.ToDictionary(
            a => a.AgentId,
            a =>
            {
                try { return a.List(); }
                catch { return (IReadOnlyList<McpServerSpec>)[]; }
            },
            StringComparer.OrdinalIgnoreCase);

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in motherSpecs.Keys) names.Add(id);
        foreach (var list in agentLists.Values)
            foreach (var s in list)
                if (!McpMotherStore.IsExcluded(doc, s.Id))
                    names.Add(s.Id);

        var items = new List<object>();
        foreach (var name in names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
        {
            motherSpecs.TryGetValue(name, out var motherSpec);
            var inMother = motherSpec is not null;
            var baseSpec = motherSpec ?? agentLists.Values.SelectMany(x => x)
                .FirstOrDefault(s => string.Equals(s.Id, name, StringComparison.OrdinalIgnoreCase))
                ?? new McpServerSpec { Id = name };

            // 合并 alias/note：以母本为准
            if (motherSpec is not null)
            {
                baseSpec = motherSpec.Clone();
            }

            var agents = new List<object>();
            foreach (var adapter in _adapters)
            {
                agentLists.TryGetValue(adapter.AgentId, out var list);
                list ??= [];
                var onAgent = list.FirstOrDefault(s => string.Equals(s.Id, name, StringComparison.OrdinalIgnoreCase));
                var isSystem = adapter is CodexMcpAdapter && CodexMcpAdapter.SystemNames.Contains(name);
                McpPresence presence;
                if (!adapter.Detected)
                    presence = McpPresence.Unsupported;
                else if (isSystem && onAgent is not null)
                    presence = McpPresence.System;
                else if (onAgent is not null)
                    presence = McpPresence.Present;
                else
                    presence = McpPresence.Missing;

                var drift = false;
                if (inMother && onAgent is not null && !isSystem)
                    drift = !SpecsEqual(motherSpec!, onAgent);

                agents.Add(new
                {
                    agentId = adapter.AgentId,
                    displayName = adapter.DisplayName,
                    presence = presence.ToString().ToLowerInvariant(),
                    enabledOnAgent = onAgent?.Enabled,
                    drift,
                    configPath = adapter.ConfigPath,
                    detected = adapter.Detected,
                    detail = isSystem ? "系统项" : null,
                });
            }

            var risk = McpSecrets.HasSecretRisk(baseSpec);
            var masked = McpSecrets.Mask(baseSpec);
            items.Add(new
            {
                id = baseSpec.Id,
                alias = baseSpec.Alias,
                note = baseSpec.Note,
                transport = baseSpec.Transport.ToString().ToLowerInvariant(),
                command = masked.Command,
                args = masked.Args,
                env = masked.Env,
                url = masked.Url,
                headers = masked.Headers,
                enabled = baseSpec.Enabled,
                startupTimeoutSec = baseSpec.StartupTimeoutSec,
                timeoutMs = baseSpec.TimeoutMs,
                explicitType = baseSpec.ExplicitType,
                inMother,
                hasSecretRisk = risk,
                agents,
            });
        }

        return new
        {
            motherPath = _mother.MotherPath,
            motherEmpty = motherSpecs.Count == 0,
            targets = doc.Targets,
            excludeNames = doc.ExcludeNames,
            adapters = _adapters.Select(a => new
            {
                agentId = a.AgentId,
                displayName = a.DisplayName,
                configPath = a.ConfigPath,
                detected = a.Detected,
                count = agentLists.TryGetValue(a.AgentId, out var l) ? l.Count : 0,
            }),
            items,
        };
    }

    /// <summary>编辑表单用：未掩码配置。优先 Cursor，其次任一已有端，最后母本缓存。</summary>
    public McpServerSpec? GetRaw(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        name = name.Trim();
        McpServerSpec? cursor = null;
        McpServerSpec? first = null;
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
                if (string.Equals(adapter.AgentId, "cursor", StringComparison.OrdinalIgnoreCase))
                    cursor = hit.Clone();
                else
                    first ??= hit.Clone();
            }
            catch
            {
                // 单个适配器读失败不影响其它源
            }
        }
        if (cursor is not null) return cursor;
        if (first is not null) return first;
        return _mother.Get(name);
    }

    /// <summary>把 spec 直接写入指定端；可选静默写入母本缓存（用户无感知）。</summary>
    public McpSyncResult UpsertToAgents(McpServerSpec spec, IEnumerable<string>? targets, bool ensureMother = true)
    {
        if (string.IsNullOrWhiteSpace(spec.Id))
            throw new ArgumentException("需要 id");
        var name = spec.Id.Trim();
        spec.Id = name;
        if (CodexMcpAdapter.SystemNames.Contains(name))
            throw new InvalidOperationException($"系统项 {name} 不可写入");

        var doc = _mother.Load();
        if (McpMotherStore.IsExcluded(doc, name))
            throw new InvalidOperationException($"系统项 {name} 已排除，不可写入");

        var result = new McpSyncResult { Ok = true };

        if (ensureMother)
        {
            try
            {
                var existing = _mother.Get(name);
                var toMother = spec.Clone();
                if (existing is not null)
                {
                    toMother.Alias = existing.Alias ?? toMother.Alias;
                    toMother.Note = existing.Note ?? toMother.Note;
                }
                _mother.Upsert(toMother);
                Log($"upsert-agents ensure-mother {name}");
            }
            catch (Exception ex)
            {
                Log($"upsert-agents mother-cache fail {name}: {ex.GetType().Name}");
            }
        }

        var targetList = targets?
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t.Trim().ToLowerInvariant())
            .Distinct()
            .ToList() ?? [];
        if (targetList.Count == 0)
            throw new ArgumentException("需要 targets");

        foreach (var agentId in targetList)
        {
            var adapter = _adapters.FirstOrDefault(a =>
                string.Equals(a.AgentId, agentId, StringComparison.OrdinalIgnoreCase));
            if (adapter is null)
            {
                result.Ok = false;
                result.Items.Add(new McpSyncItemResult { Name = name, Agent = agentId, Ok = false, Error = "未知 Agent" });
                continue;
            }
            if (!adapter.Detected)
            {
                result.Ok = false;
                result.Items.Add(new McpSyncItemResult { Name = name, Agent = agentId, Ok = false, Error = "未检测到配置路径" });
                continue;
            }
            try
            {
                var copy = spec.Clone();
                if (adapter is CodexMcpAdapter && copy.Transport == McpTransport.Stdio && copy.StartupTimeoutSec is null)
                    copy.StartupTimeoutSec = 30;
                if (adapter is WorkBuddyMcpAdapter && copy.Transport == McpTransport.Http)
                    copy.ExplicitType ??= "streamableHttp";
                adapter.Upsert(copy);
                result.Items.Add(new McpSyncItemResult { Name = name, Agent = adapter.AgentId, Ok = true });
                Log($"upsert-agents {name} -> {adapter.AgentId}");
            }
            catch (Exception ex)
            {
                result.Ok = false;
                result.Items.Add(new McpSyncItemResult
                {
                    Name = name, Agent = adapter.AgentId, Ok = false, Error = ex.Message,
                });
                Log($"upsert-agents fail {name} -> {adapter.AgentId}: {ex.GetType().Name}");
            }
        }
        return result;
    }

    public void ImportFrom(string sourceAgentId)
    {
        var adapter = _adapters.FirstOrDefault(a =>
            string.Equals(a.AgentId, sourceAgentId, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"未知 Agent：{sourceAgentId}");
        if (!adapter.Detected)
            throw new InvalidOperationException($"{adapter.DisplayName} 未检测到配置");
        var list = adapter.List()
            .Where(s => !CodexMcpAdapter.SystemNames.Contains(s.Id))
            .ToList();
        // 保留已有 alias/note
        var existing = _mother.ListSpecs();
        foreach (var s in list)
        {
            if (existing.TryGetValue(s.Id, out var old))
            {
                s.Alias = old.Alias ?? s.Alias;
                s.Note = old.Note ?? s.Note;
            }
        }
        _mother.ReplaceAll(list);
        Log($"import from {sourceAgentId}: {list.Count} servers");
    }

    public void UpsertMother(McpServerSpec spec)
    {
        _mother.Upsert(spec);
        Log($"upsert mother {spec.Id}");
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
        var doc = _mother.Load();
        var mother = _mother.ListSpecs();
        var nameSet = names?.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()).ToList();
        var targetSet = targets?.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim().ToLowerInvariant()).ToList();

        IEnumerable<string> syncNames = nameSet is { Count: > 0 }
            ? nameSet
            : mother.Keys;
        IEnumerable<IMcpAdapter> syncAdapters = targetSet is { Count: > 0 }
            ? _adapters.Where(a => targetSet.Contains(a.AgentId))
            : _adapters.Where(a => doc.Targets.TryGetValue(a.AgentId, out var on) ? on : true);

        var result = new McpSyncResult { Ok = true };
        foreach (var name in syncNames)
        {
            if (!mother.TryGetValue(name, out var spec))
            {
                result.Items.Add(new McpSyncItemResult { Name = name, Agent = "*", Ok = false, Error = "不在母本中" });
                result.Ok = false;
                continue;
            }
            if (McpMotherStore.IsExcluded(doc, name) || CodexMcpAdapter.SystemNames.Contains(name))
            {
                result.Items.Add(new McpSyncItemResult { Name = name, Agent = "*", Ok = false, Error = "系统项已排除" });
                continue;
            }

            foreach (var adapter in syncAdapters)
            {
                if (!adapter.Detected)
                {
                    result.Items.Add(new McpSyncItemResult
                    {
                        Name = name, Agent = adapter.AgentId, Ok = false, Error = "未检测到配置路径",
                    });
                    result.Ok = false;
                    continue;
                }
                try
                {
                    var copy = spec.Clone();
                    if (adapter is CodexMcpAdapter && copy.Transport == McpTransport.Stdio && copy.StartupTimeoutSec is null)
                        copy.StartupTimeoutSec = 30;
                    if (adapter is WorkBuddyMcpAdapter && copy.Transport == McpTransport.Http)
                        copy.ExplicitType ??= "streamableHttp";
                    adapter.Upsert(copy);
                    result.Items.Add(new McpSyncItemResult { Name = name, Agent = adapter.AgentId, Ok = true });
                    Log($"sync {name} -> {adapter.AgentId}");
                }
                catch (Exception ex)
                {
                    result.Items.Add(new McpSyncItemResult
                    {
                        Name = name, Agent = adapter.AgentId, Ok = false, Error = ex.Message,
                    });
                    result.Ok = false;
                    Log($"sync fail {name} -> {adapter.AgentId}: {ex.GetType().Name}");
                }
            }
        }
        return result;
    }


    /// <summary>
    /// 把 MCP 配置推到指定/缺失端，不要求先导入母本。
    /// 源解析优先级（漂移时同序）：母本 &gt; preferredSource &gt; Cursor &gt; 其它已检测到的适配器（跳过系统项）。
    /// </summary>
    public McpSyncResult Push(string name, IEnumerable<string>? targets, string? preferredSource, bool ensureMother = true)
        => PushNames([name], targets, preferredSource, ensureMother);

    /// <summary>批量 Push；names 与单名 Push 语义相同。</summary>
    public McpSyncResult PushNames(IEnumerable<string> names, IEnumerable<string>? targets, string? preferredSource, bool ensureMother = true)
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
                var one = PushOne(name, targets, preferredSource, ensureMother);
                result.Items.AddRange(one.Items);
                if (!one.Ok) result.Ok = false;
            }
            catch (Exception ex)
            {
                result.Ok = false;
                result.Items.Add(new McpSyncItemResult
                {
                    Name = name, Agent = "*", Ok = false, Error = ex.Message,
                });
            }
        }
        return result;
    }

    private McpSyncResult PushOne(string name, IEnumerable<string>? targets, string? preferredSource, bool ensureMother)
    {
        if (CodexMcpAdapter.SystemNames.Contains(name))
            throw new InvalidOperationException($"系统项 {name} 不可推送");

        var doc = _mother.Load();
        if (McpMotherStore.IsExcluded(doc, name))
            throw new InvalidOperationException($"系统项 {name} 已排除，不可推送");

        var spec = ResolvePushSource(name, preferredSource);
        if (ensureMother)
        {
            var existing = _mother.Get(name);
            var toMother = spec.Clone();
            if (existing is not null)
            {
                // 保留母本已有 alias/note
                toMother.Alias = existing.Alias ?? toMother.Alias;
                toMother.Note = existing.Note ?? toMother.Note;
            }
            _mother.Upsert(toMother);
            Log($"push ensure-mother {name}");
        }

        var adapters = ResolvePushTargets(name, targets);
        var result = new McpSyncResult { Ok = true };
        if (!adapters.Any())
        {
            result.Items.Add(new McpSyncItemResult
            {
                Name = name, Agent = "*", Ok = true, Error = "无缺失端可补齐",
            });
            return result;
        }

        foreach (var adapter in adapters)
        {
            if (!adapter.Detected)
            {
                result.Items.Add(new McpSyncItemResult
                {
                    Name = name, Agent = adapter.AgentId, Ok = false, Error = "未检测到配置路径",
                });
                result.Ok = false;
                continue;
            }
            try
            {
                var copy = spec.Clone();
                copy.Enabled = true;
                if (adapter is CodexMcpAdapter && copy.Transport == McpTransport.Stdio && copy.StartupTimeoutSec is null)
                    copy.StartupTimeoutSec = 30;
                if (adapter is WorkBuddyMcpAdapter && copy.Transport == McpTransport.Http)
                    copy.ExplicitType ??= "streamableHttp";
                adapter.Upsert(copy);
                result.Items.Add(new McpSyncItemResult { Name = name, Agent = adapter.AgentId, Ok = true });
                Log($"push {name} -> {adapter.AgentId}");
            }
            catch (Exception ex)
            {
                result.Items.Add(new McpSyncItemResult
                {
                    Name = name, Agent = adapter.AgentId, Ok = false, Error = ex.Message,
                });
                result.Ok = false;
                Log($"push fail {name} -> {adapter.AgentId}: {ex.GetType().Name}");
            }
        }
        return result;
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
            return _adapters.Where(a => targetSet.Contains(a.AgentId)).ToList();

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
        if (CodexMcpAdapter.SystemNames.Contains(name))
            throw new InvalidOperationException($"系统项 {name} 不可启停");
        var adapter = _adapters.FirstOrDefault(a =>
            string.Equals(a.AgentId, agentId, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"未知 Agent：{agentId}");
        adapter.SetEnabled(name, enabled);
        Log($"agent-enable {agentId}/{name}={enabled}");
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
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = "\"" + dir + "\"",
                    UseShellExecute = true,
                });
                return dir;
            }
            else
                throw new FileNotFoundException("config file missing", path);
        }
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = "/select,\"" + path + "\"",
            UseShellExecute = true,
        });
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
        if (a.Enabled != b.Enabled) return false;
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
        return s.Replace('/', '\\').Trim();
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
