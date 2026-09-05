namespace AgentHub.Core.DocCore;

public enum ManagedSkillState
{
    Enabled,
    Disabled,
    External,
    Modified,
    LegacyLink,
    Conflict,
}

public enum ModifiedResolution
{
    KeepLocalAsStore,
    RestoreFromStore,
}

public sealed record ManagedSkillItem(
    string Name,
    string DisplayName,
    string? Description,
    ManagedSkillState State,
    string PreviewPath,
    string? ActivePath,
    string? StorePath,
    DateTime ModifiedUtc,
    bool CanEnable,
    bool CanDisable,
    bool CanManage,
    bool CanUpdate);

public sealed record SkillOperationResult(bool Ok, string Message, ManagedSkillItem? Item = null);

public sealed record LegacySkillStatus(int LinkCount, int StoreCount, bool CanClean, IReadOnlyList<string> Errors);

public sealed record SkillBatchResult(int Updated, int Skipped, IReadOnlyList<string> Errors);

internal sealed class SkillStateFile
{
    public int Version { get; set; } = 1;
    public Dictionary<string, SkillStateEntry> Skills { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

internal sealed class SkillStateEntry
{
    public bool Enabled { get; set; }
    public string? LastDeployedHash { get; set; }
    public DateTime? LastUpdatedUtc { get; set; }
}
