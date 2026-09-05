namespace AgentHub.Core.DocCore;

public enum AgentRuleStatus
{
    NotDetected,
    NeedsFirstLaunch,
    Missing,
    Current,
    NeedsSync,
    Busy,
    Unsupported,
    Conflict,
}

public sealed record AgentRuleItem(
    string AgentId,
    string DisplayName,
    bool Detected,
    AgentRuleStatus Status,
    string? RulePath,
    string Message,
    bool CanWrite);

public sealed record AgentRulesStatus(
    string LibraryRoot,
    bool LibraryRootExists,
    string SharedRulesPath,
    AgentRuleStatus SharedRulesStatus,
    IReadOnlyList<AgentRuleItem> Agents,
    bool HasChanges,
    bool HasConflicts,
    bool Enabled);

public sealed record AgentRulesHub(string Path, bool Exists, bool Enabled, string Content);

public sealed record AgentRulesLibraryResult(string Path, bool Moved, IReadOnlyList<string> Notes);

public sealed record AgentRulesPreview(
    IReadOnlyList<string> CreateDirectories,
    IReadOnlyList<string> ModifyFiles,
    IReadOnlyList<string> SkipMessages,
    string BackupRoot);

public sealed record AgentRuleApplyItem(string AgentId, bool Ok, string Message, string? Path = null);
public sealed record AgentRulesApplyResult(bool Ok, IReadOnlyList<AgentRuleApplyItem> Items, string BackupPath);

internal sealed class AgentRuleStateFile
{
    public int Version { get; set; } = 1;
    public Dictionary<string, AgentRuleStateEntry> Agents { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

internal sealed class AgentRuleStateEntry
{
    public string Path { get; set; } = "";
    public string WrittenHash { get; set; } = "";
    public DateTime LastSyncedUtc { get; set; }
}

internal sealed class AgentRuleTransaction
{
    public int Version { get; set; } = 1;
    public string TransactionId { get; set; } = "";
    public string BackupRoot { get; set; } = "";
    public List<AgentRuleTransactionEntry> Writes { get; set; } = [];
}

internal sealed class AgentRuleTransactionEntry
{
    public string AgentId { get; set; } = "";
    public string Path { get; set; } = "";
    public string? BeforeHash { get; set; }
    public string WrittenHash { get; set; } = "";
    public string? BackupPath { get; set; }
    public bool Delete { get; set; }
}
