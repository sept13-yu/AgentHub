using AgentHub.Core.McpCore.Adapters;

namespace AgentHub.Core.McpCore;

public sealed partial class McpSyncService
{
    private sealed record AdapterRead(IReadOnlyList<McpServerSpec> Servers, string? Error);

    public McpListResponse ListMasked()
    {
        var doc = _mother.Load();
        var motherSpecs = _mother.ListSpecs();
        var reads = new Dictionary<string, AdapterRead>(StringComparer.OrdinalIgnoreCase);
        foreach (var adapter in _adapters)
        {
            if (!adapter.Detected)
            {
                reads[adapter.AgentId] = new AdapterRead([], null);
                continue;
            }
            try
            {
                reads[adapter.AgentId] = new AdapterRead(adapter.ListStrict(), null);
            }
            catch (Exception ex)
            {
                // DSH 适配器只抛安全消息；其它适配器异常可能携带配置原文。
                var error = adapter is DshMcpAdapter && !string.IsNullOrWhiteSpace(ex.Message)
                    ? ex.Message
                    : $"读取配置失败（{ex.GetType().Name}）";
                reads[adapter.AgentId] = new AdapterRead([], error);
                Log($"list {adapter.AgentId} failed: {ex.GetType().Name}");
            }
        }

        var names = new HashSet<string>(motherSpecs.Keys, StringComparer.OrdinalIgnoreCase);
        foreach (var read in reads.Values)
            foreach (var spec in read.Servers)
                if (!McpMotherStore.IsExcluded(doc, spec.Id))
                    names.Add(spec.Id);

        var items = new List<McpListItem>();
        foreach (var name in names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
        {
            motherSpecs.TryGetValue(name, out var motherSpec);
            var inMother = motherSpec is not null;
            var baseSpec = motherSpec?.Clone() ?? reads.Values.SelectMany(r => r.Servers)
                .FirstOrDefault(s => string.Equals(s.Id, name, StringComparison.OrdinalIgnoreCase))
                ?? new McpServerSpec { Id = name };

            var agents = new List<McpAgentInfo>();
            foreach (var adapter in _adapters)
            {
                var read = reads[adapter.AgentId];
                var onAgent = read.Servers.FirstOrDefault(s => string.Equals(s.Id, name, StringComparison.OrdinalIgnoreCase));
                var isSystem = adapter is CodexMcpAdapter && CodexMcpAdapter.SystemNames.Contains(name);
                var presence = !adapter.Detected ? McpPresence.Unsupported
                    : read.Error is not null ? McpPresence.Error
                    : isSystem && onAgent is not null ? McpPresence.System
                    : onAgent is not null ? McpPresence.Present
                    : McpPresence.Missing;
                var drift = inMother && onAgent is not null && !isSystem && !SpecsEqual(motherSpec!, onAgent);
                agents.Add(new McpAgentInfo(
                    adapter.AgentId, adapter.DisplayName, presence.ToString().ToLowerInvariant(),
                    onAgent?.Enabled, drift, adapter.ConfigPath, adapter.Detected,
                    read.Error ?? (isSystem ? "系统项" : null)));
            }

            var risk = McpSecrets.HasSecretRisk(baseSpec);
            var masked = McpSecrets.Mask(baseSpec);
            items.Add(new McpListItem(
                baseSpec.Id, baseSpec.Alias, baseSpec.Note, baseSpec.Transport.ToString().ToLowerInvariant(),
                masked.Command, masked.Args, masked.Env, masked.Url, masked.Headers, baseSpec.Enabled,
                baseSpec.StartupTimeoutSec, baseSpec.TimeoutMs, baseSpec.ExplicitType,
                inMother, risk, agents));
        }

        var adapters = _adapters.Select(a =>
        {
            var read = reads[a.AgentId];
            return new McpAdapterInfo(a.AgentId, a.DisplayName, a.ConfigPath, a.Detected,
                read.Servers.Count, read.Error);
        }).ToList();
        return new McpListResponse(_mother.MotherPath, motherSpecs.Count == 0,
            doc.Targets, doc.ExcludeNames, adapters, items);
    }
}
