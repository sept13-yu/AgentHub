namespace AgentHub.Core.McpCore;

public interface IMcpAdapter
{
    string AgentId { get; }
    string DisplayName { get; }
    string ConfigPath { get; }
    bool Detected { get; }
    IReadOnlyList<McpServerSpec> List();
    void Upsert(McpServerSpec spec);
    void SetEnabled(string id, bool enabled);
    void Remove(string id);
}