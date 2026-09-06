namespace AgentHub.Core.SessionCore;

/// <summary>会话摘要（列表行）。四源统一形状（方案 §4.1）。</summary>
public sealed record ConversationSummary
{
    public required string AgentId { get; init; }      // cursor / codex / dsh / workbuddy
    public required string Id { get; init; }           // 会话稳定标识（跨扫描不变）
    public required string Title { get; init; }        // 展示标题（源标题/ai-title/覆盖表/首条消息）
    public string? Project { get; init; }              // 工作目录（源内 cwd，非目录名反解）
    public long MessageCount { get; init; }
    public long SizeBytes { get; init; }
    public required DateTime LastActivityUtc { get; init; }
    public bool IsSubagent { get; init; }
    /// <summary>能挂上的父会话 id。挂不上则为空，列表以 OrphanSub 标出。</summary>
    public string? ParentId { get; init; }
    /// <summary>子会话但挂不上父，仍单列。</summary>
    public bool OrphanSub { get; init; }
    /// <summary>标题来源：source=源自身（wb ai-title / cursor name / dsh session/title）；override=本应用覆盖表；derived=首条消息截断。</summary>
    public string TitleSource { get; init; } = "derived";
    /// <summary>相对 HOME 的源路径（排障回溯用）。</summary>
    public required string SourceFile { get; init; }
}

/// <summary>预览消息（正文已脱壳：WorkBuddy 剥 system-reminder，Codex 跳过 developer）。</summary>
public sealed record ConversationMessage
{
    public required string Role { get; init; }         // user / assistant
    public DateTime? TimestampUtc { get; init; }
    public required string Text { get; init; }
}

/// <summary>会话详情（master-detail 右栏）。</summary>
public sealed record ConversationDetail
{
    public required ConversationSummary Summary { get; init; }
    public IReadOnlyList<ConversationMessage> Messages { get; init; } = [];
    /// <summary>截断/部分解压等需要向用户说明的情况（DSH 尾帧截断、Cursor 0 bubble 等）。</summary>
    public string? Note { get; init; }
}

/// <summary>单个删除项的结果。</summary>
public sealed record DeleteItemResult
{
    public required string AgentId { get; init; }
    public required string Id { get; init; }
    public bool Ok { get; init; }
    public string? Error { get; init; }                // 失败原因（须可展示：Cursor 未退出等）
    public long FreedBytes { get; init; }              // 磁盘回收估算（文件/目录大小）
    public string? Note { get; init; }                 // 补充说明（如 Cursor 需 VACUUM 才实际回收）
    public string? Warning { get; init; }              // 非失败提示（如云端未删、请到客户端再删）
}

/// <summary>某 Agent 下的项目空间（给会话页 chip 用）。</summary>
public sealed record SessionProject(string Path, string Label, int Count);

/// <summary>ZCode 产物 / 桌面引用 + WorkBuddy 云端残留标题的清理结果。</summary>
public sealed record HostTitleSweepResult(int ZcodeRemoved, int WorkBuddyAttempted, int WorkBuddyOk, string? Warning);

/// <summary>会话列表分页（索引缓存上的切片）。</summary>
public sealed record SessionPage
{
    public required IReadOnlyList<ConversationSummary> Items { get; init; }
    public int Total { get; init; }
    public int Offset { get; init; }
    public int Limit { get; init; }
    public int IndexedCount { get; init; }
    public DateTimeOffset? IndexedAt { get; init; }
    /// <summary>当前筛选下已锁条数，给删除确认「已跳过 M 条锁定」。</summary>
    public int LockedCount { get; init; }
}

/// <summary>四源统一 Provider 接口（方案 §4.1）。删=全清理，不备份；Cursor 的 vacuum 单独走端点。</summary>
public interface IConversationProvider
{
    string AgentId { get; }
    Task<IReadOnlyList<ConversationSummary>> ListAsync();
    Task<ConversationDetail?> LoadAsync(string id);
    Task RenameAsync(string id, string title);
    Task<IReadOnlyList<DeleteItemResult>> DeleteAsync(IEnumerable<string> ids);
}
